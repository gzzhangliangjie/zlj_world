using System.Collections.Generic;
using UnityEngine;
using VoxelCraft.Core;
using VoxelCraft.Creatures;
using VoxelCraft.World;

namespace VoxelCraft.Editor
{
    /// <summary>
    /// M34 close-up delivery shot: entities are DRIVEN by the control system
    /// (DrivableVehicle.Tick) before rendering - cars drop onto their
    /// suspension, the horse gallops mid-stride. Proves the logic, not just
    /// the models.
    /// </summary>
    public static class M34VehicleCloseup
    {
        public static void Run()
        {
            Shader.SetGlobalFloat("_VoxelDayBrightness", 1f);
            Shader.SetGlobalVector("_VoxelFogRange", new Vector4(400f, 900f, 0f, 0f));
            Shader.SetGlobalColor("_VoxelFogColor", new Color(0.68f, 0.80f, 0.92f));

            var go = new GameObject("M34C");
            var atlas = Art.TextureFactory.Build();
            var sim = new WorldSim(1337);
            sim.dataRadius = 3; sim.meshRadius = 3;
            var rects = new Rect[36];
            for (int i = 0; i < 36; i++) rects[i] = atlas.TileRect((TileId)i);
            sim.tileRects = rects;
            var rem = new List<Chunk>();
            sim.Step(0, 0, 100000f, 100000f, rem, null);

            // terrain backdrop
            var blocksShader = Resources.Load<Shader>("Shaders/BlocksShader");
            var solidMat = new Material(blocksShader) { mainTexture = atlas.atlas };
            foreach (var ch in rem)
            {
                var mgo = new GameObject($"C{ch.cx}_{ch.cz}");
                mgo.transform.SetParent(go.transform);
                mgo.transform.localPosition = new Vector3(ch.cx * 16, 0, ch.cz * 16);
                mgo.AddComponent<MeshFilter>().sharedMesh =
                    ch.solidMeshData != null ? ch.solidMeshData.ToMesh(null) : null;
                mgo.AddComponent<MeshRenderer>().sharedMaterial = solidMat;
            }

            var stubGo = new GameObject("StubWorld");
            var stub = stubGo.AddComponent<WorldRoot>();
            typeof(WorldRoot).GetProperty("sim").SetValue(stub, sim);

            var ents = new List<DrivableVehicle>();
            // cars on the flat sand terrace the mounts used (x 18..30, z 20)
            for (int i = 0; i < 2; i++)
            {
                string name = i == 0 ? "car1" : "police1";
                int gx = 18 + i * 6, gz = 20;
                int g0 = sim.SurfaceHeight(gx, gz, true);
                var vgo = new GameObject($"C_{name}");
                vgo.transform.SetParent(go.transform);
                vgo.transform.position = new Vector3(gx + 0.5f, g0 + 2.2f, gz + 0.5f);
                vgo.transform.rotation = Quaternion.Euler(0f, 90f, 0f); // face +x
                var v = vgo.AddComponent<DrivableVehicle>();
                v.world = stub; v.vehicleName = name;
                v.chassis = DrivableVehicle.ChassisType.Wheels;
                v.BuildModel();
                v.Enter(null);
                ents.Add(v);
            }
            // horse mid-gallop behind the cars
            {
                int gx = 18 + 12, gz = 20;
                int g0 = sim.SurfaceHeight(gx, gz, true);
                var vgo = new GameObject("C_horse");
                vgo.transform.SetParent(go.transform);
                vgo.transform.position = new Vector3(gx + 0.5f, g0 + 1.02f, gz + 0.5f);
                vgo.transform.rotation = Quaternion.Euler(0f, 90f, 0f);
                var v = vgo.AddComponent<DrivableVehicle>();
                v.world = stub; v.vehicleName = "horse";
                v.chassis = DrivableVehicle.ChassisType.Mount;
                v.mountCreature = true;
                v.BuildModel();
                v.Enter(null);
                ents.Add(v);
            }

            var dt = 1f / 60f;
            // 0.8 s scripted drive: cars land + roll forward, horse gallops
            for (int f = 0; f < 48; f++)
            {
                for (int i = 0; i < ents.Count; i++)
                {
                    var v = ents[i];
                    if (v.chassis == DrivableVehicle.ChassisType.Mount)
                    { v.throttleIn = 1f; v.gallopIn = true; }
                    else v.throttleIn = 1f;
                    v.Tick(dt);
                }
            }
            Debug.Log($"[M34CU] car1 speed={ents[0].Speed:F2} comp={ents[0].AvgCompression:F2} grounded={ents[0].AnyWheelGrounded}");
            Debug.Log($"[M34CU] police1 speed={ents[1].Speed:F2} comp={ents[1].AvgCompression:F2} grounded={ents[1].AnyWheelGrounded}");
            Debug.Log($"[M34CU] horse speed={ents[2].Speed:F2}");

            // tight bounds camera over the three entities
            var b = new Bounds(ents[0].transform.position, Vector3.zero);
            foreach (var e in ents) b.Encapsulate(e.transform.position);
            foreach (Transform e in ents[2].transform) { } // no-op keeps compiler calm
            b.Expand(4f);
            Vector3 c = b.center;
            float radius = Mathf.Max(b.extents.magnitude, 7f);
            var camGo = new GameObject("Cam");
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.68f, 0.80f, 0.92f);
            cam.transform.position = c + new Vector3(-radius * 0.1f, radius * 0.7f, radius * 1.5f);
            cam.transform.LookAt(c);
            Render(cam, 1280, 720, "m34_closeup.jpg");
            Debug.Log("M34CLOSEUP RESULT: DONE");
        }

        static void Render(Camera cam, int w, int h, string name)
        {
            var rt = new RenderTexture(w, h, 24);
            cam.targetTexture = rt;
            cam.Render();
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            tex.Apply();
            System.IO.File.WriteAllBytes("D:/zlj_world/_shots/" + name, tex.EncodeToJPG(90));
            RenderTexture.active = prev;
            cam.targetTexture = null;
            Object.DestroyImmediate(rt);
            Object.DestroyImmediate(tex);
        }
    }
}
