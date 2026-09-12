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
                BoxBuilder.SkinnedBox(modelRoot, "Head", new Vector3(0f, 1.73f, 0f), Vector3.one * 0.5f,
                    skin, headNet, 64, 32);

                for (int i = 0; i < 2; i++)
                {
                    float side = i == 0 ? -0.375f : 0.375f;
                    var hip = new GameObject("Hip" + i).transform;
                    hip.SetParent(modelRoot, false);
                    hip.localPosition = new Vector3(side * 0.6f, 0.75f, 0f);
                    BoxBuilder.SkinnedBox(hip, "Leg", new Vector3(0f, -0.375f, 0f), new Vector3(0.25f, 0.75f, 0.25f),
                        skin, legNet, 64, 32);
                    legs[i] = hip;

                    var shoulder = new GameObject("Shoulder" + i).transform;
                    shoulder.SetParent(modelRoot, false);
                    shoulder.localPosition = new Vector3(side, 1.44f, 0f);
                    BoxBuilder.SkinnedBox(shoulder, "Arm", new Vector3(0f, -0.34f, 0f), new Vector3(0.25f, 0.72f, 0.25f),
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

            modelRoot.gameObject.SetActive(false);
            lastPos = transform.position;
        }

        private void Update()
        {
            if (Input.GetKeyDown(toggleKey))
            {
                thirdPerson = !thirdPerson;
                if (modelRoot != null)
                {
                    modelRoot.gameObject.SetActive(thirdPerson);
                }
            }

            if (camera != null)
            {
                Vector3 target = thirdPerson ? new Vector3(0f, 0.38f, -thirdPersonDistance) : Vector3.zero;
                camera.transform.localPosition = Vector3.Lerp(camera.transform.localPosition, target, Time.deltaTime * 10f);
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
