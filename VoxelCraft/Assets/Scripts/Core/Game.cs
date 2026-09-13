using UnityEngine;
using VoxelCraft.Art;
using VoxelCraft.Core;
using VoxelCraft.Gen;
using VoxelCraft.Player;
using VoxelCraft.World;

namespace VoxelCraft.Core
{
    /// <summary>Entry point. The scene stays empty; everything is bootstrapped from code.</summary>
    public static class GameBoot
    {
        private static bool booted;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        public static void Boot()
        {
            if (booted)
            {
                return;
            }
            booted = true;
            if (Object.FindObjectOfType<Game>() != null)
            {
                return;
            }
            var root = new GameObject("VoxelGameRoot");
            root.AddComponent<Game>();
        }
    }

    /// <summary>
    /// Composition root: atlas/materials, sky+fog, player rig with camera,
    /// sun, and the streaming world (pre-warmed around the spawn point).
    /// </summary>
    public class Game : MonoBehaviour
    {
        /// <summary>Bumped every published change; shown in the HUD corner so
        /// a stale cached page is instantly recognizable.</summary>
        public const string BuildId = "2026-09-13f";

        [Header("World")]
        public int seed = 1337;
        public int viewRadius = 7;

        [Header("Sky")]
        public Color skyColor = new Color(0.68f, 0.80f, 0.92f);

        public TextureFactory.AtlasResult atlas { get; private set; }
        public WorldRoot worldRoot { get; private set; }
        public PlayerMotor playerMotor { get; private set; }
        public Camera mainCamera { get; private set; }
        public Material solidMaterial { get; private set; }
        public Material waterMaterial { get; private set; }

        private void Start()
        {
            Application.targetFrameRate = 60;

            atlas = TextureFactory.Build();
            AudioLibrary.Build();
            solidMaterial = new Material(Resources.Load<Shader>("Shaders/BlocksShader"))
            {
                mainTexture = atlas.atlas,
            };
            waterMaterial = new Material(Resources.Load<Shader>("Shaders/WaterShader"))
            {
                mainTexture = atlas.atlas,
            };

            float fogEnd = viewRadius * 16f - 8f;
            float fogStart = fogEnd * 0.55f;
            Shader.SetGlobalVector("_VoxelFogRange", new Vector4(fogStart, fogEnd, 0f, 0f));
            Shader.SetGlobalColor("_VoxelFogColor", skyColor);

            // ---- spawn search: first dry, non-desert grass column near origin ----
            var spawnGen = new TerrainGenerator(seed);
            int sx = 8, sz = 8, spawnH = spawnGen.HeightAt(sx, sz);
            for (int r = 0; r <= 96; r += 8)
            {
                bool found = false;
                for (int a = 0; a < 8 && !found; a++)
                {
                    int px = (r * Mathf.RoundToInt(Mathf.Cos(a * Mathf.PI / 4f))) + 8;
                    int pz = (r * Mathf.RoundToInt(Mathf.Sin(a * Mathf.PI / 4f))) + 8;
                    int h = spawnGen.HeightAt(px, pz);
                    if (h > VoxelMath.SeaLevel + 1 && h < TerrainGenerator.SnowLine - 2 && !spawnGen.IsDesert(px, pz))
                    {
                        sx = px;
                        sz = pz;
                        spawnH = h;
                        found = true;
                    }
                }
                if (found)
                {
                    break;
                }
            }

            // ---- player rig ----
            var playerGo = new GameObject("Player");
            playerGo.transform.position = new Vector3(sx + 0.5f, spawnH + 2.2f, sz + 0.5f);
            var cc = playerGo.AddComponent<CharacterController>();
            cc.height = 1.8f;
            cc.radius = 0.35f;
            cc.center = new Vector3(0f, 0.9f, 0f);
            cc.slopeLimit = 50f;
            cc.stepOffset = 0.4f;
            cc.skinWidth = 0.03f;

            var headGo = new GameObject("Head");
            headGo.transform.SetParent(playerGo.transform, false);
            headGo.transform.localPosition = new Vector3(0f, 1.62f, 0f);

            var camGo = new GameObject("Main Camera");
            camGo.tag = "MainCamera";
            camGo.transform.SetParent(headGo.transform, false);
            camGo.transform.localPosition = Vector3.zero;
            camGo.transform.localRotation = Quaternion.identity;
            mainCamera = camGo.AddComponent<Camera>();
            mainCamera.clearFlags = CameraClearFlags.SolidColor;
            mainCamera.backgroundColor = skyColor;
            mainCamera.fieldOfView = 72f;
            mainCamera.nearClipPlane = 0.06f;
            mainCamera.farClipPlane = 600f;
            camGo.AddComponent<AudioListener>();
            var look = camGo.AddComponent<MouseLook>();
            look.yawTransform = playerGo.transform;
            look.pitchTransform = headGo.transform;

            playerMotor = playerGo.AddComponent<PlayerMotor>();
            playerMotor.head = headGo.transform;

            var interaction = playerGo.AddComponent<BlockInteraction>();
            interaction.viewCamera = mainCamera;
            interaction.playerBody = playerGo.transform;
            // NOTE: interaction.world is assigned AFTER worldRoot exists (line below) -
            // wiring order matters, a null world silently disables all interaction.

            var blockAudio = playerGo.AddComponent<Player.BlockAudio>();
            blockAudio.interaction = interaction;
            blockAudio.motor = playerMotor;
            blockAudio.world = worldRoot;

            // ---- sun ----
            var sunGo = new GameObject("Sun");
            var sun = sunGo.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.intensity = 1.05f;
            sun.shadows = LightShadows.None;
            sunGo.transform.rotation = Quaternion.Euler(52f, -35f, 0f);

            // ---- world (viewer = player) ----
            var worldGo = new GameObject("World");
            worldRoot = worldGo.AddComponent<WorldRoot>();
            worldRoot.Init(seed, viewRadius, atlas, solidMaterial, waterMaterial, playerGo.transform);
            playerMotor.world = worldRoot;
            interaction.world = worldRoot; // FIX: assigned only once the world actually exists

            // Build spawn area synchronously so the player never falls through.
            worldRoot.PrewarmAround(playerGo.transform.position);

            // HUD owns cursor lock/unlock and the pause panel from here on.
            var hudGo = new GameObject("Hud");
            var hud = hudGo.AddComponent<UI.Hud>();
            hud.game = this;
            hud.interaction = interaction;

            // Third-person model + camera mode (V key).
            var rig = playerGo.AddComponent<ThirdPersonRig>();
            rig.motor = playerMotor;
            rig.camera = mainCamera;
            rig.BuildModel();

            // Held item: 3D tool/block model in the camera's view + on the TP hand.
            var held = playerGo.AddComponent<Player.HeldItemView>();
            held.interaction = interaction;
            held.viewCamera = mainCamera;
            held.rig = rig;
            held.atlas = atlas;
            held.Build();
            interaction.OnUse += held.Swing;

            // Blocky animal population around the player.
            var spawner = worldGo.AddComponent<Creatures.CreatureSpawner>();
            spawner.world = worldRoot;
            spawner.player = playerGo.transform;

            // Day/night cycle + weather, wired into the interaction (clock tool).
            var envGo = new GameObject("Environment");
            var dayNight = envGo.AddComponent<Environment.DayNightCycle>();
            dayNight.Setup(mainCamera, sun);
            var weather = envGo.AddComponent<Environment.WeatherSystem>();
            weather.Setup(playerGo.transform, worldRoot);
            dayNight.weather = weather;
            interaction.dayNight = dayNight;

            // World-space item drops (context for spawning + pickup target).
            Items.ItemDrops.world = worldRoot;
            Items.ItemDrops.player = playerGo.transform;
            Items.ItemDrops.iconOf = t => atlas.icons.TryGetValue(t, out var icon) ? icon : null;

            hud.dayNight = dayNight;
            hud.weather = weather;

            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F) && playerMotor != null)
            {
                playerMotor.ToggleFly();
            }
        }
    }
}
