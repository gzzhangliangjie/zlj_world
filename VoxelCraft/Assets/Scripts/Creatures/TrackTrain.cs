using System.Collections.Generic;
using UnityEngine;
using VoxelCraft.Core;
using VoxelCraft.World;

namespace VoxelCraft.Creatures
{
    /// <summary>Anything the player can right-click to ride: cars (DrivableVehicle)
    /// and track trains (TrackTrain). BlockInteraction + PlayerMotor speak only
    /// to this, so new rideables need zero player-side changes.</summary>
    public interface IRideable
    {
        bool Enter(Transform player);
        void Exit();
        Transform Seat { get; }
        Transform Transform { get; }
        float Speed { get; }
    }

    /// <summary>
    /// Multi-car train running on RAIL blocks (official rail textures; cars are
    /// mmmm veh_train/veh_wagon geo models at the M34 player scale).
    ///
    /// Motion model: the track is a CELL PATH walked on demand (Rail = runs Z,
    /// RailX = runs X; L-corners followed), and every car sits at a fixed arc
    /// offset behind the locomotive along that path. The locomotive arc `s` is
    /// a float, so motion is sub-cell smooth; the path extends forward as the
    /// loco approaches its end and clamps at the tail, which makes dead ends
    /// stop the train and reverse work with zero special cases.
    ///
    /// Driving: right-click the locomotive (IRideable) to ride; W/S throttle,
    /// F exits. Input is read in Update; motion integrates in Tick(dt) so
    /// batch harnesses drive it deterministically.
    /// </summary>
    public class TrackTrain : MonoBehaviour, IRideable
    {
        public WorldRoot world;
        public string locomotiveName = "train";
        public string[] carNames = { "wagon1", "wagon2", "wagon3", "wagon4" };
        public float maxSpeed = 7f;         // blocks/s
        public float accel = 1.6f;
        public float brake = 3.2f;
        public float carGap = 0.35f;        // coupler gap (u)
        /// <summary>Model scale: mmmm geo needs the M34 1.585 vehicle
        /// multiplier; official Bedrock geo (minecart) is already 1/16 u per
        /// px, so it runs at 1.0.</summary>
        public float unitScale = 1.585f;

        // driver input (Update forwards; Tick consumes)
        public float throttleIn;            // -1..1
        public bool Occupied { get; private set; }
        public Transform Seat { get; private set; }
        public Transform Transform => transform;
        public float Speed => speed;
        public Vector2Int HeadCell => PathCellAt(s);
        public float DebugS => s;
        public int DebugPathCount => path.Count;
        /// <summary>Expected centre-to-centre spacing for each consecutive car pair.</summary>
        public float[] CouplerSpacings()
        {
            var r = new float[cumBehind.Count - 1];
            for (int i = 1; i < cumBehind.Count; i++) r[i - 1] = cumBehind[i] - cumBehind[i - 1];
            return r;
        }
        public bool OnTrack => path.Count > 1;
        /// <summary>Rail cells currently under the consist.</summary>
        public int RailsUnder { get; private set; }

        float speed;                        // signed blocks/s along the path
        float s;                            // arc position of the LOCOMOTIVE (blocks)
        readonly List<Vector3> path = new List<Vector3>();   // cell centres, path[0] = anchor
        readonly List<Vector2Int> cells = new List<Vector2Int>();
        Vector2Int pathHeading = new Vector2Int(0, 1);       // heading at the path END
        readonly List<Transform> cars = new List<Transform>();
        readonly List<float> carHalf = new List<float>();
        readonly List<float> cumBehind = new List<float>(); // arc distance from loco centre to each car centre
        Transform bodyRoot;
        float tailArc;

        void EnsureSeat()
        {
            if (Seat != null) return;
            Seat = new GameObject("Seat").transform;
            Seat.SetParent(transform, false);
            Seat.localPosition = new Vector3(0f, 1.4f * unitScale, 0f);
        }

        /// <summary>Build the consist. startCell must hold a Rail/RailX block;
        /// the consist is placed straddling it (cars trail backward).</summary>
        public void BuildConsist(Vector2Int startCell)
        {
            EnsureSeat();
            var rootGo = new GameObject("ConsistRoot");
            bodyRoot = rootGo.transform;
            // NOT parented to this transform: cars are placed in WORLD space
            // and this transform tracks the locomotive (seat / collider / player).

            var names = new List<string> { locomotiveName };
            names.AddRange(carNames);
            foreach (var n in names)
            {
                var carGo = new GameObject("Car_" + n);
                carGo.transform.SetParent(bodyRoot, false);
                var skin = Art.CreatureTextureFactory.GetSkinMaterial(n + "_skin");
                var geoAsset = Resources.Load<TextAsset>("Geo/" + n + ".geo");
                if (skin != null && geoAsset != null)
                {
                    // Body wrapper survives PlaceConsist's t.rotation writes:
                    // the wrapper carries the geo, the yaw fix lives inside.
                    var bodyGo = new GameObject("Body");
                    bodyGo.transform.SetParent(carGo.transform, false);
                    BedrockGeoImporter.Build(bodyGo.transform, geoAsset, null, skin, 0f, null,
                        out _, out _, out _, out _);
                    carGo.transform.localScale = Vector3.one * unitScale;
                    // Bedrock v1.8 cart geo (minecart) is authored with its
                    // LONG axis on X while TrackTrain orients cars along the
                    // rail with Unity Z-forward — yaw the body a quarter turn
                    // so the tub sits square on the track.
                    if (n == "minecart") bodyGo.transform.localRotation = Quaternion.Euler(0f, 90f, 0f);
                    var rends = carGo.GetComponentsInChildren<Renderer>();
                    if (rends.Length > 0)
                    {
                        var b = rends[0].bounds;
                        foreach (var r in rends) b.Encapsulate(r.bounds);
                        carHalf.Add(b.size.z / 2f);
                    }
                    else carHalf.Add(2f);
                }
                else
                {
                    Debug.LogError("[Train] missing geo/skin for " + n);
                    carHalf.Add(2f);
                }
                // Official minecart geo ships WITHOUT wheels (5 plates only —
                // vanilla fakes it with the dark under-plate). Stamp four small
                // hub wheels so the cart visibly rolls on the rail.
                if (n == "minecart")
                {
                    const float R = 0.09f;       // wheel radius (u)
                    // body yaws +90° (X↔Z swap), wheels parent to the car and
                    // pre-swap their offsets so they land under the tub corners
                    float wx = 0.40f, wz0 = carHalf[carHalf.Count - 1] * 0.45f;
                    var wheelMat = Art.CreatureTextureFactory.GetSkinMaterial("minecart_skin");
                    Vector3[] wpos =
                    {
                        new Vector3(-wz0, R, -wx), new Vector3(wz0, R, -wx),
                        new Vector3(-wz0, R,  wx), new Vector3(wz0, R,  wx),
                    };
                    for (int wi = 0; wi < 4; wi++)
                    {
                        var wh = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                        wh.name = "wheel" + wi;
                        Object.Destroy(wh.GetComponent<Collider>());
                        wh.transform.SetParent(carGo.transform, false);
                        wh.transform.localPosition = wpos[wi];
                        wh.transform.localRotation = Quaternion.Euler(90f, 0f, 0f); // axle across track
                        wh.transform.localScale = new Vector3(R * 2f, 0.045f, R * 2f);
                        if (wheelMat != null) wh.GetComponent<Renderer>().sharedMaterial = wheelMat;
                    }
                }
                cars.Add(carGo.transform);
            }

            // coupler chain: car i centre sits cumBehind[i] behind the loco
            cumBehind.Add(0f);
            for (int i = 1; i < cars.Count; i++)
            {
                cumBehind.Add(cumBehind[i - 1] + carHalf[i - 1] + carGap + carHalf[i]);
            }
            tailArc = cumBehind[cumBehind.Count - 1] + carHalf[carHalf.Count - 1];

            var col = gameObject.AddComponent<BoxCollider>();
            col.size = new Vector3(1.7f * unitScale / 1.585f, 1.6f * unitScale / 1.585f, carHalf[0] * 2f + 0.6f);
            col.center = new Vector3(0f, 1.2f, 0f);

            // seed the path at the anchor and extend for the whole consist
            pathHeading = RailDirAt(startCell) ?? new Vector2Int(0, 1);
            AppendCell(startCell);
            ExtendPath(tailArc + 4f);        // cover the whole consist + headway
            s = Mathf.Min(tailArc, Mathf.Max(0.25f, path.Count - 1 - 0.25f));
            PlaceConsist();
        }

        // ---------- rail queries ----------
        BlockType RailBlockAt(Vector2Int c)
        {
            // The rail sits ON the old surface, but SurfaceHeight treats the
            // rail itself as the new surface (non-air, non-liquid), so both the
            // surface y and y+1 must be checked to find the plate.
            int g = world.sim.SurfaceHeight(c.x, c.y, true);
            for (int dy = 0; dy <= 1; dy++)
            {
                var b = world.sim.GetBlock(c.x, g + dy, c.y);
                if (b == BlockType.Rail || b == BlockType.RailX) return b;
            }
            return BlockType.Air;
        }

        /// <summary>Direction implied by a rail block: RailX runs X; Rail runs
        /// Z. Both ends are valid, so the caller picks the sense.</summary>
        Vector2Int? RailDirAt(Vector2Int c)
        {
            var b = RailBlockAt(c);
            if (b == BlockType.RailX) return new Vector2Int(1, 0);
            if (b == BlockType.Rail) return new Vector2Int(0, 1);
            return null;
        }

        void AppendCell(Vector2Int c)
        {
            cells.Add(c);
            // ride height: the rail block's own top (g+1) + clearance. If the
            // rail is the surface block itself (placed rail), SurfaceHeight
            // returns the rail y, so its top is g+1 either way.
            int g = world.sim.SurfaceHeight(c.x, c.y, true);
            var b0 = world.sim.GetBlock(c.x, g, c.y);
            // The rail is a 1/16 plate at the BOTTOM of its own block (its
            // visible face sits at g + 1/16). SurfaceHeight returns the rail
            // block itself, so ride height = rail block y + plate thickness.
            float y = g + 1f / 16f + 0.02f;
            path.Add(new Vector3(c.x + 0.5f, y, c.y + 0.5f));
        }

        /// <summary>Grow the path forward until it covers `need` arc length or
        /// the track runs out (dead end kept as-is).</summary>
        void ExtendPath(float need)
        {
            int guard = 0;
            while (path.Count < need && guard++ < 512)
            {
                var head = cells[cells.Count - 1];
                var cand = head + pathHeading;
                if (RailBlockAt(cand) != BlockType.Air)
                {
                    AppendCell(cand);
                    continue;
                }
                // L-corner: turn left or right (prefer keeping the same sense)
                var left = new Vector2Int(-pathHeading.y, pathHeading.x);
                var right = new Vector2Int(pathHeading.y, -pathHeading.x);
                if (RailBlockAt(head + left) != BlockType.Air) { pathHeading = left; AppendCell(head + left); }
                else if (RailBlockAt(head + right) != BlockType.Air) { pathHeading = right; AppendCell(head + right); }
                else break; // dead end
            }
        }

        Vector2Int PathCellAt(float arc)
        {
            int i = Mathf.Clamp(Mathf.RoundToInt(arc), 0, cells.Count - 1);
            return cells[i];
        }

        // ---------- ride ----------
        public bool Enter(Transform player)
        {
            if (Occupied) return false;
            Occupied = true;
            return true;
        }

        public void Exit()
        {
            Occupied = false;
            throttleIn = 0f;
        }

        void Update()
        {
            if (!Occupied) return;
            throttleIn = Input.GetAxisRaw("Vertical");
            Tick(Time.deltaTime);
        }

        /// <summary>Deterministic advance (batch-verifiable).</summary>
        public void Tick(float dt)
        {
            if (dt <= 0f || !Occupied) return;

            if (Mathf.Abs(throttleIn) > 0.05f)
            {
                bool opposing = Mathf.Sign(throttleIn) != Mathf.Sign(speed) && Mathf.Abs(speed) > 0.05f;
                speed += throttleIn * (opposing ? brake : accel) * dt;
            }
            else
            {
                speed = Mathf.MoveTowards(speed, 0f, 0.8f * dt);
            }
            speed = Mathf.Clamp(speed, -maxSpeed * 0.6f, maxSpeed);

            float ds = speed * dt;
            if (Mathf.Abs(ds) > 1e-7f)
            {
                ExtendPath(s + ds + Mathf.Abs(speed) + 3f); // look past this step
                float maxS = Mathf.Max(0f, path.Count - 1 - 0.25f); // keep loco centre on rails
                bool hitEnd = (s + ds) > maxS + 1e-6f;      // explicit bound test
                bool hitTail = (s + ds) < 0.25f - 1e-6f;
                if (hitEnd || hitTail) speed = 0f;  // buffer stop
                s = Mathf.Clamp(s + ds, 0.25f, maxS);
            }
            PlaceConsist();
        }

        // ---------- placement ----------
        void PlaceConsist()
        {
            if (cars.Count == 0 || path.Count == 0) return;
            RailsUnder = 0;
            for (int i = 0; i < cars.Count; i++)
            {
                float arc = Mathf.Max(s - cumBehind[i], 0f);
                var (pos, fwd) = PathSample(arc);
                var t = cars[i];
                if (fwd.sqrMagnitude > 1e-6f)
                {
                    // flatten heading: rails are flat plates, the consist must
                    // stay level (a pitched forward vector tipped carts 26°)
                    var flatFwd = new Vector3(fwd.x, 0f, fwd.z);
                    t.rotation = Quaternion.LookRotation(flatFwd.sqrMagnitude > 1e-8f ? flatFwd.normalized : Vector3.forward, Vector3.up);
                }
                t.position = pos;
                int ci = Mathf.Clamp(Mathf.RoundToInt(arc), 0, cells.Count - 1);
                if (RailBlockAt(cells[ci]) != BlockType.Air) RailsUnder++;
                if (i == 0)
                {
                    var ff = new Vector3(fwd.x, 0f, fwd.z);
                    transform.SetPositionAndRotation(pos,
                        ff.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(ff.normalized, Vector3.up) : transform.rotation);
                }
            }
        }

        /// <summary>Position + forward samples at an arc length.</summary>
        (Vector3, Vector3) PathSample(float arc)
        {
            if (path.Count == 0) return (transform.position, transform.position + Vector3.forward);
            arc = Mathf.Clamp(arc, 0f, path.Count - 1);
            int i0 = Mathf.Clamp(Mathf.FloorToInt(arc), 0, path.Count - 1);
            int i1 = Mathf.Clamp(i0 + 1, 0, path.Count - 1);
            float f = arc - i0;
            var a = Vector3.Lerp(path[i0], path[i1], f);
            float arcF = Mathf.Min(arc + 0.25f, path.Count - 1);
            int j0 = Mathf.Clamp(Mathf.FloorToInt(arcF), 0, path.Count - 1);
            int j1 = Mathf.Clamp(j0 + 1, 0, path.Count - 1);
            float fj = arcF - j0;
            var b = Vector3.Lerp(path[j0], path[j1], fj);
            if ((b - a).sqrMagnitude < 1e-6f) b = a + Vector3.forward;
            return (a, b);
        }
    }
}
