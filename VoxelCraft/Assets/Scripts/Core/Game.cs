using System.Collections.Generic;
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
        public const string BuildId = "2026-09-20a";

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

            // Drivable vehicles near spawn (right-click to enter, F to exit):
            // horse + donkey mounts reuse the animated BlockyAnimal models;
            // car1 + police1 are vox_to_creature.py geo vehicles.
            for (int vi = 0; vi < 4; vi++)
            {
                bool isMount = vi < 2;
                string vn = isMount ? (vi == 0 ? "horse" : "donkey") : (vi == 2 ? "car1" : "police1");
                var vgo = new GameObject($"Vehicle_{vn}");
                int vx = 8 + vi * 4, vz = 14;
                int vg = worldRoot.sim.SurfaceHeight(vx, vz, ignoreTrees: true);
                vgo.transform.position = new Vector3(vx + 0.5f, vg + 1f, vz + 0.5f);
                var veh = vgo.AddComponent<Creatures.DrivableVehicle>();
                veh.world = worldRoot;
                veh.vehicleName = vn;
                veh.mountCreature = isMount;
                if (isMount) { veh.maxSpeed = 11f; veh.turnSpeed = 110f; }
                veh.BuildModel();
            }

            // Demo track: a straight rail line beside spawn (official rail
            // textures) + a five-car consist parked on it. Right-click the
            // locomotive to drive; W/S throttle, F exits.
            var trackGo = new GameObject("DemoTrack");
            var track = trackGo.AddComponent<Creatures.TrackTrain>();
            track.world = worldRoot;
            track.locomotiveName = "train";
            // wagons 1-4 turned out to be reskinned car1 bodies; the real
            // passenger coaches live inside scene_train.vox (carved out as
            // coach1/coach2 geo, M44)
            track.carNames = new[] { "coach3", "coach3" };
            int trackZ = 24;
            int trackY = worldRoot.sim.SurfaceHeight(6, trackZ, true) + 1;
            for (int rx = 6; rx < 60; rx++)
            {
                int ry = worldRoot.sim.SurfaceHeight(rx, trackZ, true) + 1;
                var remeshed2 = new List<World.Chunk>();
                worldRoot.sim.SetBlock(rx, ry, trackZ, Core.BlockType.RailX, remeshed2);
            }
            track.BuildConsist(new Vector2Int(20, trackZ));

            // Second demo track one block south: official minecart geo (1.12
            // unit scale — NO 1.585 vehicle multiplier) hauling cargo carts.
            var cartTrack = new GameObject("MinecartTrack");
            var carts = cartTrack.AddComponent<Creatures.TrackTrain>();
            carts.world = worldRoot;
            carts.locomotiveName = "minecart";
            carts.carNames = new[] { "minecart", "minecart" };
            carts.unitScale = 0.5f;        // bedrock renders minecart at half scale (1.8 double-space geo)
            carts.carGap = 0.15f;
            carts.maxSpeed = 4.5f;
            int cartZ = 28;
            for (int rx2 = 6; rx2 < 60; rx2++)
            {
                int ry2 = worldRoot.sim.SurfaceHeight(rx2, cartZ, true) + 1;
                var remeshed3 = new List<World.Chunk>();
                worldRoot.sim.SetBlock(rx2, ry2, cartZ, Core.BlockType.RailX, remeshed3);
            }
            carts.BuildConsist(new Vector2Int(24, cartZ));

            // Third demo track (z=32): the diesel locomotive train2 hauling
            // one passenger coach — same geo pipeline, loco swapped (M45).
            var dTrack = new GameObject("DieselTrack");
            var diesel = dTrack.AddComponent<Creatures.TrackTrain>();
            diesel.world = worldRoot;
            diesel.locomotiveName = "train2";
            diesel.carNames = new[] { "coach3", "train3" };   // coach + flatcar (63-vox deck)
            diesel.carGap = 0.15f;
            int dieselZ = 32;
            for (int rx3 = 6; rx3 < 60; rx3++)
            {
                int ry3 = worldRoot.sim.SurfaceHeight(rx3, dieselZ, true) + 1;
                var remeshed4 = new List<World.Chunk>();
                worldRoot.sim.SetBlock(rx3, ry3, dieselZ, Core.BlockType.RailX, remeshed4);
            }
            diesel.BuildConsist(new Vector2Int(22, dieselZ));

            // Fourth demo track (z=36): the Shinkansen-style bullet train
            // (veh_bullettrain.vox, converted from a CC-BY poly.pizza GLB).
            var bTrack = new GameObject("BulletTrack");
            var bullet = bTrack.AddComponent<Creatures.TrackTrain>();
            bullet.world = worldRoot;
            bullet.locomotiveName = "bullettrain";
            bullet.carNames = new[] { "traincar" };        // jeremy CC-BY trailer (poly.pizza), same livery family
            bullet.carGap = 0.15f;
            int bulletZ = 36;
            for (int rx4 = 6; rx4 < 60; rx4++)
            {
                int ry4 = worldRoot.sim.SurfaceHeight(rx4, bulletZ, true) + 1;
                var remeshed5 = new List<World.Chunk>();
                worldRoot.sim.SetBlock(rx4, ry4, bulletZ, Core.BlockType.RailX, remeshed5);
            }
            bullet.BuildConsist(new Vector2Int(20, bulletZ));

            // Sixth demo track (z=44): CRH2 "Hexie" EMU head car
            // (procedural voxel shell, white body + navy band, long nose)
            var crhTrack = new GameObject("Track_CRH2");
            var crh = crhTrack.AddComponent<Creatures.TrackTrain>();
            crh.world = worldRoot;
            crh.locomotiveName = "crh2";
            crh.carNames = new string[] { };
            crh.carGap = 0.15f;
            crh.unitScale = 1.585f;
            int crhZ = 44;
            for (int rx6 = 4; rx6 < 40; rx6++)
            {
                int ry6 = worldRoot.sim.SurfaceHeight(rx6, crhZ, true) + 1;
                var remeshed6 = new List<World.Chunk>();
                worldRoot.sim.SetBlock(rx6, ry6, crhZ, Core.BlockType.RailX, remeshed6);
            }
            crh.BuildConsist(new Vector2Int(20, crhZ));


            // Fifth demo track (z=40): the HXD3D electric locomotive — real-world
            // CR HXD3D, ripped from a CC-BY Sketchfab model via Tools/sketchfab_rip.py
            // (sketchfang decrypt pipeline) then voxelized + converted (M48).
            var hxdTrack = new GameObject("HxdTrack");
            var hxd = hxdTrack.AddComponent<Creatures.TrackTrain>();
            hxd.world = worldRoot;
            hxd.locomotiveName = "hxd3d";
            hxd.carNames = new string[] { };            // single loco, no consist
            hxd.carGap = 0.15f;
            int hxdZ = 40;
            for (int rx5 = 6; rx5 < 60; rx5++)
            {
                int ry5 = worldRoot.sim.SurfaceHeight(rx5, hxdZ, true) + 1;
                var remeshed6 = new List<World.Chunk>();
                worldRoot.sim.SetBlock(rx5, ry5, hxdZ, Core.BlockType.RailX, remeshed6);
            }
            hxd.BuildConsist(new Vector2Int(20, hxdZ));

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

            // Build tool (WorldEdit-style selection/fill/copy/paste, B key).
            var buildTool = playerGo.AddComponent<BuildTools.BuildToolController>();
            buildTool.world = worldRoot;
            buildTool.viewCamera = mainCamera;
            buildTool.playerBody = playerGo.transform;
            buildTool.interaction = interaction;
            interaction.buildTool = buildTool;

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
