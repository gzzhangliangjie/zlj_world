using UnityEngine;
using VoxelCraft.World;

namespace VoxelCraft.Environment
{
    public enum WeatherKind : byte
    {
        Clear = 0,
        Overcast = 1,
        Rain = 2,
        Snow = 3,
    }

    /// <summary>
    /// Weather: four states (Clear / Overcast / Rain / Snow). Rain and Snow share
    /// the particle layer (Rain becomes Snow automatically in cold biomes).
    /// Overcast and precipitation grey out the sky (GrayFactor) which the
    /// DayNightCycle blends into sky/fog color, and dim the world brightness.
    /// </summary>
    public class WeatherSystem : MonoBehaviour
    {
        public Transform player;
        public WorldRoot world;

        public float minStateSeconds = 70f;
        public float maxStateSeconds = 200f;
        public WeatherKind Kind { get; private set; } = WeatherKind.Clear;

        /// <summary>0 = clear skies, 1 = fully overcast (rain/snow); smoothly interpolated.</summary>
        public float GrayFactor { get; private set; }

        private ParticleSystem particles;
        private ParticleSystem.EmissionModule emission;
        private float stateTimer;
        private float targetRate;
        private bool precipitating;
        private bool snowMode;

        public string Describe
        {
            get
            {
                switch (Kind)
                {
                    case WeatherKind.Overcast: return "Overcast";
                    case WeatherKind.Rain: return "Rain";
                    case WeatherKind.Snow: return "Snow";
                    default: return "Clear";
                }
            }
        }

        public void Setup(Transform playerTransform, WorldRoot worldRoot)
        {
            player = playerTransform;
            world = worldRoot;
            Kind = WeatherKind.Clear;
            stateTimer = Random.Range(minStateSeconds, maxStateSeconds);

            var go = new GameObject("WeatherParticles");
            go.transform.SetParent(transform, false);
            particles = go.AddComponent<ParticleSystem>();
            emission = particles.emission;
            emission.enabled = true;
            emission.rateOverTime = 0f;

            var main = particles.main;
            main.loop = true;
            main.playOnAwake = true;
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
            main.maxParticles = 1500;

            var shape = particles.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(24f, 1f, 24f);

            ApplyParticleMode(isSnow: false);

            var renderer = go.GetComponent<ParticleSystemRenderer>();
            var shader = Resources.Load<Shader>("Shaders/ParticleShader");
            var material = new Material(shader) { mainTexture = PaintFlake() };
            renderer.sharedMaterial = material;
            renderer.renderMode = ParticleSystemRenderMode.Stretch;
            renderer.lengthScale = 6f;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        private void Update()
        {
            if (particles == null || player == null)
            {
                return;
            }

            stateTimer -= Time.deltaTime;
            if (stateTimer <= 0f)
            {
                PickNext();
            }

            float rate = emission.rateOverTime.constant;
            rate = Mathf.Lerp(rate, targetRate, Time.deltaTime * 0.6f);
            emission.rateOverTime = rate;

            float grayTarget = Kind == WeatherKind.Overcast ? 0.5f : precipitating ? 1f : 0f;
            GrayFactor = Mathf.Lerp(GrayFactor, grayTarget, Time.deltaTime * 0.25f);

            // Follow the player so the weather box is always overhead.
            var pos = particles.transform.position;
            pos.x = player.position.x;
            pos.y = player.position.y + 10f;
            pos.z = player.position.z;
            particles.transform.position = pos;
        }

        private void PickNext()
        {
            bool cold = IsColdAtPlayer();
            switch (Kind)
            {
                case WeatherKind.Clear:
                    Kind = WeatherKind.Overcast;
                    break;
                case WeatherKind.Overcast:
                {
                    float r = Random.value;
                    if (r < 0.45f) { Kind = WeatherKind.Rain; }
                    else if (cold && r < 0.65f) { Kind = WeatherKind.Snow; }
                    else if (r < 0.85f) { Kind = WeatherKind.Overcast; }
                    else { Kind = WeatherKind.Clear; }
                    break;
                }
                case WeatherKind.Rain:
                case WeatherKind.Snow:
                    Kind = Random.value < 0.55f ? WeatherKind.Overcast : WeatherKind.Clear;
                    break;
            }

            precipitating = Kind == WeatherKind.Rain || Kind == WeatherKind.Snow;
            snowMode = Kind == WeatherKind.Snow;
            if (precipitating)
            {
                ApplyParticleMode(isSnow: snowMode);
                targetRate = snowMode ? 260f : 750f;
            }
            else
            {
                targetRate = 0f;
            }
            stateTimer = Random.Range(minStateSeconds, maxStateSeconds);
        }

        private bool IsColdAtPlayer()
        {
            if (world == null || world.sim == null || player == null)
            {
                return false;
            }
            var p = player.position;
            return world.sim.generator.BiomeAt(Mathf.FloorToInt(p.x), Mathf.FloorToInt(p.z)) < 0.35f;
        }

        private void ApplyParticleMode(bool isSnow)
        {
            var main = particles.main;
            var vel = particles.velocityOverLifetime;
            vel.enabled = true;

            if (isSnow)
            {
                main.startLifetime = 6.5f;
                main.startSpeed = 0f;
                main.startSize = 0.08f;
                main.gravityModifier = 0.035f;
                vel.x = 0.25f;
                vel.y = -1.1f;
                vel.z = 0.1f;
                main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.95f, 0.97f, 1f, 0.9f));
                var renderer = particles.GetComponent<ParticleSystemRenderer>();
                renderer.renderMode = ParticleSystemRenderMode.Billboard;
            }
            else
            {
                main.startLifetime = 0.65f;
                main.startSpeed = 0f;
                main.startSize = 0.045f;
                main.gravityModifier = 2.4f;
                vel.x = 0f;
                vel.y = -14f;
                vel.z = 0f;
                main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.55f, 0.7f, 1f, 0.42f));
                var renderer = particles.GetComponent<ParticleSystemRenderer>();
                renderer.renderMode = ParticleSystemRenderMode.Stretch;
                renderer.lengthScale = 6f;
            }
        }

        private static Texture2D PaintFlake()
        {
            int size = 16;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var px = new Color32[size * size];
            float c = (size - 1) * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) / c;
                    float alpha = d > 1f ? 0f : Mathf.Pow(1f - d, 1.6f);
                    px[y * size + x] = new Color(1f, 1f, 1f, alpha);
                }
            }
            tex.SetPixels32(px);
            tex.filterMode = FilterMode.Bilinear;
            tex.Apply(false, false);
            return tex;
        }
    }
}
