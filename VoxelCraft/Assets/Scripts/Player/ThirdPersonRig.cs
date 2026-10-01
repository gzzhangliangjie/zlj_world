using UnityEngine;
using VoxelCraft.Art;

namespace VoxelCraft.Player
{
    /// <summary>
    /// Blocky third-person player model (Steve-style boxes with painted skin)
    /// plus the V-key first/third person camera toggle. The model animates
    /// legs and arms from the player's actual movement speed.
    /// </summary>
    public class ThirdPersonRig : MonoBehaviour
    {
        public PlayerMotor motor;
        public new Camera camera;
        public KeyCode toggleKey = KeyCode.V;
        public float thirdPersonDistance = 3.4f;

        // Minecraft-style camera cycling: 1st person -> 3rd behind ->
        // 3rd FRONT (mirrored pitch) -> back to 1st. Extra presses of V
        // while already in front view keep flipping to the opposite side
        // (like MC's per-press F5 flip).
        public bool FrontView { get; private set; }

        /// <summary>Right-hand attach point for the held item view.</summary>
        public Transform hand { get; private set; }

        private Transform modelRoot;
        private readonly Transform[] legs = new Transform[2];
        private readonly Transform[] arms = new Transform[2];
        private bool thirdPerson;
        private float animPhase;
        private Vector3 lastPos;

        public bool IsThirdPerson => thirdPerson;

        /// <summary>Builds the player model. Call once after references are set.</summary>
        public void BuildModel()
        {
            modelRoot = new GameObject("PlayerModel").transform;
            modelRoot.SetParent(transform, false);

            // Real Minecraft-format skin sheet when present (64x32 classic layout).
            var skin = CreatureTextureFactory.GetSkinMaterial("player_skin");
            if (skin != null)
            {
                var headNet = BoxBuilder.McNet(0, 0, 8, 8, 8);
                var bodyNet = BoxBuilder.McNet(16, 16, 8, 12, 4);
                var armNet = BoxBuilder.McNet(40, 16, 4, 12, 4);
                var legNet = BoxBuilder.McNet(0, 16, 4, 12, 4);

                BoxBuilder.SkinnedBox(modelRoot, "Body", new Vector3(0f, 1.12f, 0f), new Vector3(0.5f, 0.75f, 0.25f),
                    skin, bodyNet, 64, 32);
                // Head is 2mm slimmer in X than the torso half plane: an exact
                // shared side plane with the torso would z-fight on a 15mm strip.
                BoxBuilder.SkinnedBox(modelRoot, "Head", new Vector3(0f, 1.73f, 0f), new Vector3(0.48f, 0.5f, 0.5f),
                    skin, headNet, 64, 32);

                for (int i = 0; i < 2; i++)
                {
                    float side = i == 0 ? -0.375f : 0.375f;
                    var hip = new GameObject("Hip" + i).transform;
                    hip.SetParent(modelRoot, false);
                    hip.localPosition = new Vector3(side * 0.6f, 0.75f, 0f);
                    // Limb Z is 24cm vs the torso 25cm: their front/back planes
                    // must never coincide (z-fight strips on the lower torso).
                    BoxBuilder.SkinnedBox(hip, "Leg", new Vector3(0f, -0.375f, 0f), new Vector3(0.25f, 0.75f, 0.24f),
                        skin, legNet, 64, 32);
                    legs[i] = hip;

                    // Shoulder at 0.362: the arm inner face lands 12mm INSIDE the
                    // torso (half width 0.25) instead of exactly on it.
                    var shoulder = new GameObject("Shoulder" + i).transform;
                    shoulder.SetParent(modelRoot, false);
                    shoulder.localPosition = new Vector3(i == 0 ? -0.362f : 0.362f, 1.44f, 0f);
                    BoxBuilder.SkinnedBox(shoulder, "Arm", new Vector3(0f, -0.34f, 0f), new Vector3(0.25f, 0.72f, 0.24f),
                        skin, armNet, 64, 32);
                    arms[i] = shoulder;
                }
            }
            else
            {
                // Procedural fallback (original look).
                BoxBuilder.Box(modelRoot, "Body", new Vector3(0f, 1.12f, 0f), new Vector3(0.5f, 0.72f, 0.26f),
                    CreatureTextureFactory.Get("player", "body"));
                BoxBuilder.Box(modelRoot, "Head", new Vector3(0f, 1.72f, 0f), Vector3.one * 0.46f,
                    CreatureTextureFactory.Get("player", "face"));
                for (int i = 0; i < 2; i++)
                {
                    float side = i == 0 ? -0.36f : 0.36f;
                    var hip = new GameObject("Hip" + i).transform;
                    hip.SetParent(modelRoot, false);
                    hip.localPosition = new Vector3(side * 0.75f, 0.78f, 0f);
                    BoxBuilder.Box(hip, "Leg", new Vector3(0f, -0.39f, 0f), new Vector3(0.24f, 0.78f, 0.24f),
                        CreatureTextureFactory.Get("player", "legs"));
                    legs[i] = hip;

                    var shoulder = new GameObject("Shoulder" + i).transform;
                    shoulder.SetParent(modelRoot, false);
                    shoulder.localPosition = new Vector3(side, 1.42f, 0f);
                    BoxBuilder.Box(shoulder, "Arm", new Vector3(0f, -0.32f, 0f), new Vector3(0.2f, 0.66f, 0.2f),
                        CreatureTextureFactory.Get("player", "arm"));
                    arms[i] = shoulder;
                }
            }

            // Right-hand tip: held-item anchor follows the arm swing.
            if (arms[1] != null)
            {
                hand = new GameObject("Hand").transform;
                hand.SetParent(arms[1], false);
                hand.localPosition = new Vector3(0f, -0.7f, 0.02f);
            }

            modelRoot.gameObject.SetActive(false);
            lastPos = transform.position;
        }

        private void Update()
        {
            if (Input.GetKeyDown(toggleKey))
            {
                if (!thirdPerson)
                {
                    thirdPerson = true;          // 1st -> 3rd behind
                    FrontView = false;
                }
                else if (!FrontView)
                {
                    FrontView = true;            // 3rd behind -> 3rd front
                }
                else
                {
                    thirdPerson = false;         // 3rd front -> 1st
                    FrontView = false;
                }
                if (modelRoot != null)
                {
                    modelRoot.gameObject.SetActive(thirdPerson);
                }
            }

            if (camera != null)
            {
                // Behind view: camera sits back along the head's -Z (which
                // follows the yaw). Front view: +Z ahead of the player and
                // yawed 180 degrees so mouse-right still turns the view
                // naturally around the character.
                Vector3 target = thirdPerson
                    ? new Vector3(0f, 0.38f, FrontView ? thirdPersonDistance : -thirdPersonDistance)
                    : Vector3.zero;
                camera.transform.localPosition = Vector3.Lerp(camera.transform.localPosition, target, Time.deltaTime * 10f);
                // yaw flip lives on the head pivot so MouseLook (which owns
                // head pitch) stays consistent: front view mirrors pitch by
                // rotating the head 180 and letting MouseLook pitch invert
                // relative to the camera.
                float yawOff = FrontView ? 180f : 0f;
                if (camera.transform.parent != null)
                    camera.transform.localRotation = Quaternion.Euler(0f, yawOff, 0f);
            }

            Vector3 delta = transform.position - lastPos;
            delta.y = 0f;
            lastPos = transform.position;
            float speed = delta.magnitude / Mathf.Max(Time.deltaTime, 0.0001f);
            bool moving = speed > 0.4f;

            animPhase += Time.deltaTime * (5f + speed * 1.6f);
            float swing = moving ? Mathf.Sin(animPhase) * 0.7f : 0f;
            legs[0].localRotation = Quaternion.Euler(swing * 57.3f, 0f, 0f);
            legs[1].localRotation = Quaternion.Euler(-swing * 57.3f, 0f, 0f);
            arms[0].localRotation = Quaternion.Euler(-swing * 57.3f, 0f, 0f);
            arms[1].localRotation = Quaternion.Euler(swing * 57.3f, 0f, 0f);
        }
    }
}
