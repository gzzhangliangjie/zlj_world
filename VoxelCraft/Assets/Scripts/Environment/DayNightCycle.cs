using System;
using UnityEngine;
using VoxelCraft.Art;

namespace VoxelCraft.Environment
{
    /// <summary>
    /// Day/night cycle: rotates the sun and moon (lights + billboard discs),
    /// blends sky/fog color through night-dusk-day gradients, and drives the
    /// global _VoxelDayBrightness multiplier used by all world shaders.
    /// timeOfDay: 0 = sunrise, 0.25 = noon, 0.5 = sunset, 0.75 = midnight.
    /// </summary>
    public class DayNightCycle : MonoBehaviour
    {
        public float dayLengthSeconds = 480f;
        [Range(0f, 1f)] public float timeOfDay = 0.12f; // start shortly after sunrise
        public WeatherSystem weather;                   // optional, set by Game

        private Camera cam;
        private Light sunLight;
        private Light moonLight;
        private Transform sunDisc;
        private Transform moonDisc;

        private static readonly Color DaySky = new Color(0.68f, 0.80f, 0.92f);
        private static readonly Color DuskSky = new Color(0.93f, 0.58f, 0.38f);
        private static readonly Color NightSky = new Color(0.012f, 0.02f, 0.055f);

        /// <summary>Clock string for the HUD, e.g. "07:25".</summary>
        public string ClockText
        {
            get
            {
                float hours = timeOfDay * 24f;
                int h = Mathf.FloorToInt(hours);
                int m = Mathf.FloorToInt((hours - h) * 60f);
                return $"{h:00}:{m:00}";
            }
        }

        public float SunBrightness { get; private set; }

        public void Setup(Camera camera, Light sun)
        {
            cam = camera;
            sunLight = sun;

            var moonGo = new GameObject("Moon");
            moonLight = moonGo.AddComponent<Light>();
            moonLight.type = LightType.Directional;
            moonLight.intensity = 0f;
            moonLight.shadows = LightShadows.None;
            moonLight.color = new Color(0.65f, 0.72f, 1f);

            sunDisc = CreateDisc("SunDisc", PaintSun(), new Color(1f, 0.95f, 0.75f), 42f);
            moonDisc = CreateDisc("MoonDisc", PaintMoon(), new Color(0.9f, 0.93f, 1f), 26f);
        }

        private void Update()
        {
            if (cam == null || sunLight == null)
            {
                return;
            }

            timeOfDay += Time.deltaTime / dayLengthSeconds;
            if (timeOfDay >= 1f)
            {
                timeOfDay -= 1f;
            }

            float a = timeOfDay * Mathf.PI * 2f;             // 0 = east horizon
            var sunDir = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0.18f).normalized;
            float elevation = sunDir.y;                       // +1 noon, -1 midnight
            SunBrightness = Mathf.Clamp01(elevation * 2.5f);  // fast ramp after sunrise

            sunLight.transform.rotation = Quaternion.LookRotation(-sunDir);
            sunLight.intensity = Mathf.Clamp01(elevation * 3f) * 1.1f;
            moonLight.transform.rotation = Quaternion.LookRotation(sunDir);
            moonLight.intensity = Mathf.Clamp01(-elevation * 3f) * 0.22f;

            // Sky gradient: night -> dusk band -> day.
            float day01 = Mathf.SmoothStep(0f, 1f, SunBrightness);
            Color sky = Color.Lerp(NightSky, DaySky, day01);
            float dusk = Mathf.Clamp01(1f - Mathf.Abs(elevation) * 4.5f) * (1f - day01 * 0.35f);
            sky = Color.Lerp(sky, DuskSky, dusk * 0.75f);

            // Weather greys the sky and dampens brightness (overcast half, full precip).
            float brightness = 0.22f + 0.78f * day01;
            if (weather != null)
            {
                sky = Color.Lerp(sky, new Color(0.32f, 0.34f, 0.38f), weather.GrayFactor);
                brightness *= 1f - 0.2f * weather.GrayFactor;
            }

            cam.backgroundColor = sky;
            Shader.SetGlobalColor("_VoxelFogColor", sky);
            Shader.SetGlobalFloat("_VoxelDayBrightness", brightness);

            PlaceDisc(sunDisc, sunDir, 430f);
            PlaceDisc(moonDisc, -sunDir, 430f);
            sunDisc.gameObject.SetActive(elevation > -0.12f);
            moonDisc.gameObject.SetActive(elevation < 0.12f);
        }

        private void PlaceDisc(Transform disc, Vector3 dir, float distance)
        {
            Vector3 pos = cam.transform.position + dir * distance;
            disc.position = pos;
            disc.LookAt(cam.transform.position);
        }

        private Transform CreateDisc(string name, Texture2D texture, Color tint, float size)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            var filter = go.AddComponent<MeshFilter>();
            filter.sharedMesh = BuildQuad();
            var renderer = go.AddComponent<MeshRenderer>();
            var shader = Resources.Load<Shader>("Shaders/SkyBodyShader");
            var material = new Material(shader) { mainTexture = texture };
            material.SetColor("_Color", tint);
            renderer.sharedMaterial = material;
            go.transform.localScale = new Vector3(size, size, 1f);
            return go.transform;
        }

        private static Mesh BuildQuad()
        {
            var mesh = new Mesh
            {
                vertices = new[]
                {
                    new Vector3(-0.5f, -0.5f, 0f),
                    new Vector3(0.5f, -0.5f, 0f),
                    new Vector3(-0.5f, 0.5f, 0f),
                    new Vector3(0.5f, 0.5f, 0f),
                },
                uv = new[]
                {
                    new Vector2(0f, 0f), new Vector2(1f, 0f),
                    new Vector2(0f, 1f), new Vector2(1f, 1f),
                },
                triangles = new[] { 0, 1, 2, 2, 1, 3 },
            };
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Texture2D PaintSun()
        {
            return PaintDisc(32, r => new Color(1f, 0.92f, 0.55f, 1f), r => new Color(1f, 1f, 0.9f, 1f));
        }

        private static Texture2D PaintMoon()
        {
            var tex = PaintDisc(32, r => new Color(0.75f, 0.8f, 0.92f, 1f), r => new Color(0.95f, 0.97f, 1f, 1f));
            // craters
            var px = tex.GetPixels32();
            void Spot(int cx, int cy, int rad)
            {
                for (int y = cy - rad; y <= cy + rad; y++)
                {
                    for (int x = cx - rad; x <= cx + rad; x++)
                    {
                        if (x < 0 || y < 0 || x >= 32 || (x - cx) * (x - cx) + (y - cy) * (y - cy) > rad * rad)
                        {
                            continue;
                        }
                        px[y * 32 + x] = new Color32(160, 170, 190, 255);
                    }
                }
            }
            Spot(12, 18, 3);
            Spot(20, 11, 2);
            Spot(17, 22, 2);
            tex.SetPixels32(px);
            tex.Apply(false, false);
            return tex;
        }

        private static Texture2D PaintDisc(int size, Func<float, Color> edge, Func<float, Color> core)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var px = new Color32[size * size];
            float c = (size - 1) * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) / c;
                    if (d > 1f)
                    {
                        px[y * size + x] = new Color32(0, 0, 0, 0);
                        continue;
                    }
                    float t = Mathf.Clamp01(d * 1.25f);
                    Color col = Color.Lerp(core(d), edge(d), t);
                    float alpha = d > 0.86f ? Mathf.Clamp01((1f - d) / 0.14f) : 1f;
                    col.a = alpha;
                    px[y * size + x] = col;
                }
            }
            tex.SetPixels32(px);
            tex.filterMode = FilterMode.Bilinear;
            tex.Apply(false, false);
            return tex;
        }
    }
}
