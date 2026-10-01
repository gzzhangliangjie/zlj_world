using System.Collections.Generic;
using UnityEngine;
using VoxelCraft.Core;
using VoxelCraft.Gen;
using VoxelCraft.World;

namespace VoxelCraft.Creatures
{
    /// <summary>
    /// Vehicle control system v2 (M34). Two chassis types share one driver API:
    ///
    ///  - WHEELS (voxel cars): ArcadeCarPhysics-style raycast suspension
    ///    trimmed for voxel terrain. Wheel rays query the voxel height field
    ///    (WorldSim.SurfaceHeight) instead of Physics.Raycast - no physics
    ///    step needed, works in batch mode, and matches how animals
    ///    ground-clamp. Per-wheel spring compression averages into the body
    ///    ride height; front/rear and left/right compression deltas produce
    ///    visual pitch/roll, giving cars a real suspension feel.
    ///
    ///  - MOUNT (horse/donkey): Schvedov-style mount economy - no rigidbody.
    ///    The mount reuses BlockyAnimal's official bedrock walk animation:
    ///    vehicle speed feeds the animation player (moving + walkSpeedRef),
    ///    so the horse trots faster as the player speeds up (Shift gallops).
    ///
    /// All clocks integrate inside Tick(dt); Update only forwards realtime
    /// input, so the editor harness can step the physics deterministically
    /// (unity-batch-verification rule 1: batch mode never runs Update).
    /// </summary>
    public class DrivableVehicle : MonoBehaviour
    {
        public enum ChassisType { Wheels, Mount }

        public WorldRoot world;
        public string vehicleName = "car1";
        public bool mountCreature;          // legacy alias: true => Mount chassis
        public ChassisType chassis = ChassisType.Wheels;

        // ---- driver input (set by player or harness; read in Tick) ----
        public float throttleIn;            // -1..1
        public float steerIn;               // -1..1
        public bool handbrakeIn;
        public bool gallopIn;               // mount: hold Shift

        // ---- longitudinal ----
        public float maxSpeed = 9f;         // blocks/s (~32 km/h)
        public float maxReverse = 3.6f;
        public float accel = 7f;
        public float brake = 14f;
        public float drag = 3f;             // coast deceleration blocks/s^2
        public float turnSpeed = 70f;       // deg/s at full speed

        // ---- wheels (voxel-space, 4 corners) ----
        public Vector2 wheelbase = new Vector2(1.15f, 0.75f); // hub offsets: z ±17-19px, x ±12px (2x geo)
        public float wheelRadius = 0.3125f;              // tire half-height: 10px/16
        public float suspRest = 0.20f;      // hub travel range (hub sits 0.3125u over ground)
        public float staticSag = 0.50f;     // resting compression (spring sag)
        public float bodyRollDeg = 4.5f;    // visual roll at full comp delta
        public float bodyPitchDeg = 2.6f;

        // ---- mount ----
        public float walkSpeed = 3.2f;      // mount trot
        public float gallopSpeed = 8.2f;    // mount gallop (Shift)
        public float mountTurn = 95f;
        public float seatExitOffset = 2.5f;

        // ---- readouts (harness asserts on these) ----
        public float Speed => speed;
        public bool Occupied { get; private set; }
        public Transform Seat => seat;
        public float AvgCompression { get; private set; }
        public bool AnyWheelGrounded { get; private set; }

        float speed;                        // signed blocks/s
        float steerSmooth;
        readonly float[] compression = new float[4]; // FL FR RL RR
        Transform seat;
        Transform bodyRoot;
        Transform modelPivot;               // visual roll/pitch pivot
        readonly Transform[] wheelBones = new Transform[4]; // FL FR RL RR
        float wheelSpin;                    // accumulated roll radians
        string mountWalkClip;               // fallback when no controller asset
        BlockyAnimal mountAnim;
        BedrockAnimationPlayer mountPlayer; // gait clock (moving/walkSpeedRef)
        float mountSpeedRef;

        void Awake()
        {
            seat = new GameObject("Seat").transform;
            seat.SetParent(transform, false);
            seat.localPosition = new Vector3(0f, 1.1f, 0f);
        }

        /// <summary>Builds the model (call once after vehicleName set). Mount
        /// creatures (horse/donkey) reuse BlockyAnimal's animated geo; voxel
        /// vehicles build their single-bone geo directly.</summary>
        public void BuildModel()
        {
            if (bodyRoot != null) return;
            if (mountCreature) chassis = ChassisType.Mount;
            var go = new GameObject("BodyRoot");
            bodyRoot = go.transform;
            bodyRoot.SetParent(transform, false);

            if (chassis == ChassisType.Mount)
            {
                var ani = go.AddComponent<BlockyAnimal>();
                ani.world = world;
                ani.species = vehicleName;
                ani.playerRef = null;
                ani.walking = false;        // player-driven: AI wandering off
                ani.BuildModel();
                mountAnim = ani;
                mountPlayer = ani.GetComponent<BedrockAnimationPlayer>();
                if (mountPlayer == null)
                    mountPlayer = ani.GetComponentInChildren<BedrockAnimationPlayer>();
                ani.enabled = false;        // kill the AI Update loop (mounted)
                // Horse/donkey: registry says controllers:true but the
                // vanilla horse.animation_controllers asset does not exist
                // locally, so TickControllers no-ops and NOTHING schedules
                // the walk clip (legs froze at 0 deg - M34 bug 3). Drive the
                // clip directly + enable the horseGait synthesizer.
                var reg = CreatureRegistry.Get(vehicleName);
                if (reg != null && reg.controllers)
                {
                    var acTa = Resources.Load<TextAsset>(
                        "AnimControllers/" + vehicleName + ".animation_controllers");
                    if (acTa == null)
                    {
                        mountPlayer.horseGait = true;
                        mountWalkClip = reg.walkClip;
                        mountPlayer.Play(mountWalkClip);
                    }
                }
            }
            else
            {
                var pivotGo = new GameObject("ModelPivot");
                modelPivot = pivotGo.transform;
                modelPivot.SetParent(bodyRoot, false);
                var skin = Art.CreatureTextureFactory.GetSkinMaterial(vehicleName + "_skin");
                var geoAsset = Resources.Load<TextAsset>("Geo/" + vehicleName + ".geo");
                if (skin != null && geoAsset != null)
                {
                    BedrockGeoImporter.Build(modelPivot, geoAsset, null, skin, 0f, null,
                        out _, out _, out _, out _);
                    // rotatable wheel bones (vox_to_creature vehicle mode)
                    string[] wn = { "wheelFL", "wheelFR", "wheelRL", "wheelRR" };
                    for (int wi = 0; wi < 4; wi++)
                        wheelBones[wi] = FindDeep(modelPivot, wn[wi]);
                    if (wheelBones[0] != null)
                    {
                        // tire bottom sits at geo y=0, so hub height == radius
                        wheelRadius = wheelBones[0].localPosition.y;
                        suspRest = Mathf.Max(0.16f, wheelRadius - 0.03f);
                        staticSag = 0.5f;
                    }
                }
            }
            var col = gameObject.AddComponent<BoxCollider>();
            // 2x geo px->units: car 30x22x64px => 1.9 x 1.4 x 4.0 u
            col.size = new Vector3(1.9f, 1.4f, 4.0f);
            col.center = new Vector3(0f, 0.7f, 0f);
        }

        public bool Enter(Transform player)
        {
            if (Occupied) return false;
            Occupied = true;
            return true;
        }

        public void Exit()
        {
            Occupied = false;
            speed = 0f;
            throttleIn = 0f;
            steerIn = 0f;
        }

        void Update()
        {
            if (!Occupied) return;
            // realtime input forwarding only; all physics integrates in Tick
            throttleIn = Input.GetAxisRaw("Vertical");
            steerIn = Input.GetAxisRaw("Horizontal");
            handbrakeIn = Input.GetKey(KeyCode.Space);
            gallopIn = Input.GetKey(KeyCode.LeftShift);
            Tick(Time.deltaTime);
        }

        /// <summary>
        /// Advance the vehicle by dt seconds. Deterministic, callable from
        /// the editor harness (batch mode never runs Update).
        /// </summary>
        public void Tick(float dt)
        {
            if (dt <= 0f) return;
            if (!Occupied) return;

            if (chassis == ChassisType.Mount) { TickMount(dt); return; }

            // ---------- longitudinal ----------
            if (Mathf.Abs(throttleIn) > 0.05f)
            {
                bool opposing = Mathf.Sign(throttleIn) != Mathf.Sign(speed) && Mathf.Abs(speed) > 0.05f;
                float a = opposing ? brake : accel;
                float before = speed;
                speed += throttleIn * a * dt;
                // braking crossing zero stops AT zero: reverse needs an
                // explicit second intent (throttle held from standstill)
                if (opposing && Mathf.Sign(speed) != Mathf.Sign(before)) speed = 0f;
            }
            else
            {
                speed = Mathf.MoveTowards(speed, 0f, drag * dt);
            }
            if (handbrakeIn) speed = Mathf.MoveTowards(speed, 0f, 22f * dt);
            speed = Mathf.Clamp(speed, -maxReverse, maxSpeed);

            // ---------- steering (less angle at speed, none at standstill) ----------
            steerSmooth = Mathf.Lerp(steerSmooth, steerIn, 10f * dt);
            if (Mathf.Abs(speed) > 0.25f)
            {
                float dir = Mathf.Sign(speed);
                transform.Rotate(0f,
                    -steerSmooth * turnSpeed * dt * dir * Mathf.Clamp01(0.4f + 0.6f * Mathf.Abs(speed) / maxSpeed),
                    0f);
            }

            // ---------- suspension: 4 wheel probes on the voxel height field ----------
            AvgCompression = 0f;
            bool grounded = false;
            Vector3[] local = WheelPoints();
            for (int i = 0; i < 4; i++)
            {
                Vector3 wp = transform.TransformPoint(local[i]);
                int gx = Mathf.FloorToInt(wp.x), gz = Mathf.FloorToInt(wp.z);
                int ground = world != null && world.sim != null
                    ? world.sim.SurfaceHeight(gx, gz, ignoreTrees: true)
                    : Mathf.FloorToInt(wp.y - suspRest - wheelRadius);
                // kinematic suspension: at ride height the hub sits exactly
                // wheelRadius over the block top => suspLen 0. Positive
                // suspLen = wheel dangling over a drop (comp 0); negative =
                // terrain pushes the hub up (comp 1 at suspRest intrusion).
                float suspLen = wp.y - wheelRadius - (ground + 1f);
                float comp = Mathf.Clamp01(-suspLen / Mathf.Max(suspRest, 0.01f));
                compression[i] = comp;
                AvgCompression += comp * 0.25f;
                if (suspLen < suspRest) grounded = true;
            }
            AnyWheelGrounded = grounded;

            // visual roll/pitch from compression deltas (left/right, front/rear)
            float rollIn  = (compression[0] + compression[2]) - (compression[1] + compression[3]);
            float pitchIn = (compression[0] + compression[1]) - (compression[2] + compression[3]);
            float rollAngle  = Mathf.Clamp(rollIn * 0.5f, -1f, 1f) * bodyRollDeg;
            float pitchAngle = Mathf.Clamp(pitchIn * 0.5f, -1f, 1f) * bodyPitchDeg;
            // airborne: relax visual lean toward neutral

            // ---------- move + ride height ----------
            Vector3 pos = transform.position + transform.forward * (speed * dt);
            int cwx = Mathf.FloorToInt(pos.x), cwz = Mathf.FloorToInt(pos.z);
            int cground = world.sim.SurfaceHeight(cwx, cwz, ignoreTrees: true);
            // 2x geo: tire bottom at geo y=0, but ride half a tire higher
            // (0.15u) so the wheels read visually under the arches.
            float rideY = cground + 1f + 0.15f;
            // refuse to climb >1 block walls (head-on into a cliff)
            if (rideY - transform.position.y > 1.35f && speed > 0f)
            {
                speed = 0f;
            }
            else
            {
                if (pos.y > rideY + 0.02f)
                {
                    // fall rigidly: catches drops instantly, no float
                    pos.y = Mathf.Max(rideY, pos.y - 22f * dt);
                    if (pos.y - rideY < 0.35f) pos.y = Mathf.Lerp(pos.y, rideY, 12f * dt);
                }
                else pos.y = Mathf.Lerp(pos.y, rideY, 12f * dt);
                transform.position = pos;
                if (modelPivot != null)
                {
                    var lp = modelPivot.localPosition;
                    modelPivot.localPosition = new Vector3(lp.x, Mathf.Clamp(AvgCompression, -1f, 1f) * 0.16f, lp.z);
                    modelPivot.localRotation = Quaternion.Euler(pitchAngle, 0f, -rollAngle);
                }
                // wheel roll: radians = distance / radius. Geo frame x is
                // flipped (x' = -x_world), so forward roll is NEGATIVE around
                // the bone's local x.
                wheelSpin += speed * dt / Mathf.Max(wheelRadius, 0.05f);
                for (int wi = 0; wi < 4; wi++)
                    if (wheelBones[wi] != null)
                        wheelBones[wi].localRotation = Quaternion.Euler(-wheelSpin * Mathf.Rad2Deg, 0f, 0f);
            }
        }

        void TickMount(float dt)
        {
            // Schvedov-style: accelerate toward walk/gallop target, turn,
            // ground-clamp; animation follows actual speed.
            float targetSpeed = 0f;
            if (throttleIn > 0.1f) targetSpeed = gallopIn ? gallopSpeed : walkSpeed;
            else if (throttleIn < -0.1f) targetSpeed = -maxReverse;
            float rate = Mathf.Abs(targetSpeed) > Mathf.Abs(speed) ? accel : brake;
            speed = Mathf.MoveTowards(speed, targetSpeed, rate * dt);
            speed = Mathf.Clamp(speed, -maxReverse, gallopSpeed);

            if (Mathf.Abs(speed) > 0.25f)
            {
                float dir = Mathf.Sign(speed);
                transform.Rotate(0f,
                    -steerIn * mountTurn * dt * dir * Mathf.Clamp01(0.4f + 0.6f * Mathf.Abs(speed) / gallopSpeed),
                    0f);
            }

            Vector3 pos = transform.position + transform.forward * (speed * dt);
            int gx = Mathf.FloorToInt(pos.x), gz = Mathf.FloorToInt(pos.z);
            int ground = world.sim.SurfaceHeight(gx, gz, ignoreTrees: true);
            float rideY = ground + 1.14f;   // +0.12 hoof-swing clearance (leg pivots swing hooves below y=0)
            if (rideY - transform.position.y > 1.3f && speed > 0f) speed = 0f;
            else
            {
                pos.y = Mathf.Lerp(pos.y, rideY, 12f * dt);
                transform.position = pos;
            }
            DriveMountAnimation(dt);
        }

        void DriveMountAnimation(float dt)
        {
            if (mountPlayer == null) return;
            bool moving = Mathf.Abs(speed) > 0.15f;
            mountPlayer.moving = moving;
            // batch/editor: BlockyAnimal.Update is disabled on mounts, so the
            // vanilla controller state machine + clip scheduler advance here,
            // then the player integrates the gait clock and APPLIES POSES.
            if (mountAnim != null)
                mountAnim.TickControllers(dt, moving);
            if (!Application.isPlaying)
                mountPlayer.Tick(dt);   // batch/editor: the player's own
                                        // Update never runs here; in play
                                        // mode Update ticks it (no double)
            if (mountWalkClip != null)
            {
                // keep the walk clip's play state in sync with `moving`:
                // stopped => Stop (rest pose), moving => (re)Play
                if (moving) mountPlayer.Play(mountWalkClip);
                else mountPlayer.Stop(mountWalkClip);
            }
            // gait cadence scales with actual speed: slow trot .. gallop
            mountSpeedRef = Mathf.Lerp(mountSpeedRef,
                moving ? 0.55f + 1.65f * Mathf.Clamp01(Mathf.Abs(speed) / gallopSpeed) : 0f,
                4f * dt);
            mountPlayer.walkSpeedRef = mountSpeedRef;
            mountPlayer.gaitWeight = Mathf.MoveTowards(
                mountPlayer.gaitWeight, moving ? 0.55f : 0f, dt * 3f);
        }

        static Transform FindDeep(Transform t, string name)
        {
            if (t.name == name) return t;
            foreach (Transform c in t)
            {
                var r = FindDeep(c, name);
                if (r != null) return r;
            }
            return null;
        }

        Vector3[] WheelPoints()
        {
            // FL, FR, RL, RR in local space (z+ = forward)
            // hub height in geo space: wheel pivot y (5 for the 2x cars)
            float hubY = wheelBones[0] != null ? wheelBones[0].localPosition.y : 0.1f;
            return new[]
            {
                new Vector3(-wheelbase.y, hubY,  wheelbase.x),
                new Vector3( wheelbase.y, hubY,  wheelbase.x),
                new Vector3(-wheelbase.y, hubY, -wheelbase.x),
                new Vector3( wheelbase.y, hubY, -wheelbase.x)
            };
        }
    }
}
