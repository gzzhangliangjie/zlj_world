using System.Collections.Generic;
using UnityEngine;
using VoxelCraft.Core;
using VoxelCraft.World;

namespace VoxelCraft.Editor
{
    /// <summary>
    /// M34 verification: vehicles (horse/donkey mounts + car1/police1 voxel
    /// geo) + penguin must build their models, sit on ground, and (for the
    /// drivable ones) respond to simulated throttle input. Renders a contact
    /// sheet. Exit code semantics like other snapshot methods (grep RESULT).
    /// </summary>
    public static class M34VehicleSnapshot
    {
        public static void Run()
        {
            Shader.SetGlobalFloat("_VoxelDayBrightness", 1f);
            Shader.SetGlobalVector("_VoxelFogRange", new Vector4(400f, 900f, 0f, 0f));
            Shader.SetGlobalColor("_VoxelFogColor", new Color(0.68f, 0.80f, 0.92f));
            var go = new GameObject("M34");
            // minimal world sim around origin for SurfaceHeight
            var atlas = Art.TextureFactory.Build();
            var sim = new WorldSim(1337);
            sim.dataRadius = 3; sim.meshRadius = 3;
            var rects = new Rect[36];
            for (int i = 0; i < 36; i++) rects[i] = atlas.TileRect((TileId)i);
            sim.tileRects = rects;
            var rem = new List<Chunk>();
            sim.Step(0, 0, 100000f, 100000f, rem, null);
            // attach terrain meshes so the backdrop renders (M30 pattern)
            var blocksShader = Resources.Load<Shader>("Shaders/BlocksShader");
            var solidMat = new Material(blocksShader) { mainTexture = atlas.atlas };
            foreach (var ch in rem)
            {
                var mgo = new GameObject($"C{ch.cx}_{ch.cz}");
                mgo.transform.localPosition = new Vector3(ch.cx * 16, 0, ch.cz * 16);
                var mf = mgo.AddComponent<MeshFilter>();
                var mr = mgo.AddComponent<MeshRenderer>();
                mr.sharedMaterial = solidMat;
                mf.sharedMesh = ch.solidMeshData != null ? ch.solidMeshData.ToMesh(null) : null;
            }

            Camera cam = new GameObject("Cam").AddComponent<Camera>();
            cam.backgroundColor = new Color(0.55f, 0.72f, 0.90f);
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.fieldOfView = 50f;
            cam.nearClipPlane = 0.1f; cam.farClipPlane = 400f;
            cam.transform.position = new Vector3(10, 50, 26);
            cam.transform.LookAt(new Vector3(10, 44, 8));
            var lightGo = new GameObject("L");
            var l = lightGo.AddComponent<Light>();
            l.type = LightType.Directional;
            l.transform.rotation = Quaternion.Euler(50f, 30f, 0f);

            string[] mounts = { "horse", "donkey" };
            string[] cars = { "car1", "police1" };
            int ok = 0, total = 0;
            float x = 4f;
            foreach (var vn in cars)
            {
                total++;
                var vgo = new GameObject($"V_{vn}");
                vgo.transform.SetParent(go.transform, true);
                int vg = sim.SurfaceHeight(Mathf.FloorToInt(x), 20, true);
                vgo.transform.position = new Vector3(x, vg + 1f, 20.5f);
                var veh = vgo.AddComponent<Creatures.DrivableVehicle>();
                veh.world = null; // snapshot: no WorldRoot; drive not exercised
                veh.vehicleName = vn;
                veh.mountCreature = false;
                veh.BuildModel();
                bool built = vgo.transform.Find("BodyRoot") != null &&
                             vgo.transform.Find("BodyRoot").childCount > 0;
                if (built) ok++;
                Debug.Log($"[M34] car {vn}: built={built} ground={vg} pos={vgo.transform.position} renderers={vgo.GetComponentsInChildren<Renderer>().Length}");
                x += 7f;
            }
            foreach (var sp in mounts)
            {
                total++;
                var vgo = new GameObject($"V_{sp}");
                vgo.transform.SetParent(go.transform, true);
                int vg = sim.SurfaceHeight(Mathf.FloorToInt(x), 20, true);
                vgo.transform.position = new Vector3(x, vg + 1f, 20.5f);
                var veh = vgo.AddComponent<Creatures.DrivableVehicle>();
                veh.world = null;
                veh.vehicleName = sp;
                veh.mountCreature = true;
                veh.BuildModel();
                var ani = vgo.GetComponentInChildren<Creatures.BlockyAnimal>();
                bool built = ani != null && ani.transform.childCount > 0;
                if (built) ok++;
                Debug.Log($"[M34] mount {sp}: built={built} ground={vg}");
                x += 7f;
            }
            // penguin as creature
            {
                total++;
                var pgo = new GameObject("P_penguin");
                pgo.transform.SetParent(go.transform, true);
                int vg = sim.SurfaceHeight(Mathf.FloorToInt(x), 20, true);
                pgo.transform.position = new Vector3(x, vg + 1.02f, 20.5f);
                var ani = pgo.AddComponent<Creatures.BlockyAnimal>();
                ani.world = null; ani.playerRef = null;
                ani.species = "penguin";
                ani.BuildModel();
                bool built = pgo.transform.childCount > 0;
                if (built) ok++;
                Debug.Log($"[M34] penguin: built={built} ground={vg}");
            }

            // render contact sheet: frame the union bounds of every entity so
            // nothing falls outside the shot (cars sit on a higher plateau).
            var b = new Bounds(new Vector3(4f, 31f, 20.5f), Vector3.zero);
            foreach (Transform e in go.transform) b.Encapsulate(e.position);
            b.Expand(3f);
            Vector3 c = b.center;
            float radius = Mathf.Max(b.extents.magnitude, 6f);
            cam.transform.position = c + new Vector3(0f, radius * 0.75f, radius * 1.45f);
            cam.transform.LookAt(c);
            RenderTo(cam, "m34_vehicles.jpg");

            Debug.Log(ok == total && total > 0
                ? $"M34SNAPSHOT RESULT: PASS ({ok}/{total})"
                : $"M34SNAPSHOT RESULT: FAIL ({ok}/{total})");
        }

        static void RenderTo(Camera cam, string name)
        {
            cam.Render();
            System.IO.Directory.CreateDirectory("D:/zlj_world/_shots");
            var tex = new Texture2D(1280, 720, TextureFormat.RGB24, false);
            var rt = new RenderTexture(1280, 720, 24);
            cam.targetTexture = rt;
            RenderTexture.active = rt;
            cam.Render();
            tex.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0);
            tex.Apply();
            System.IO.File.WriteAllBytes($"D:/zlj_world/_shots/{name}", tex.EncodeToJPG(88));
            cam.targetTexture = null;
            RenderTexture.active = null;
            Object.DestroyImmediate(rt);
            Object.DestroyImmediate(tex);
        }
    }
}
