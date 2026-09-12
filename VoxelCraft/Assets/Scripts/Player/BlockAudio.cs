using UnityEngine;
using VoxelCraft.Art;
using VoxelCraft.Core;
using VoxelCraft.World;

namespace VoxelCraft.Player
{
    /// <summary>
    /// Plays dig/place sounds for block edits (subscribed to BlockInteraction
    /// events) plus material footsteps while walking on the world.
    /// </summary>
    public class BlockAudio : MonoBehaviour
    {
        public BlockInteraction interaction;
        public PlayerMotor motor;
        public WorldRoot world;

        public float volume = 0.45f;
        public float stepInterval = 0.42f;

        private AudioSource source;
        private float stepTimer;
        private Vector3 lastPos;
        private CharacterController cachedController;
        private bool hooked;

        private void Awake()
        {
            source = gameObject.AddComponent<AudioSource>();
            source.spatialBlend = 0f;
            source.playOnAwake = false;
        }

        private void OnDisable()
        {
            if (hooked && interaction != null)
            {
                interaction.OnBreak -= HandleBreak;
                interaction.OnPlace -= HandlePlace;
                hooked = false;
            }
        }

        private CharacterController Controller
        {
            get
            {
                if (cachedController == null && motor != null)
                {
                    cachedController = motor.GetComponent<CharacterController>();
                }
                return cachedController;
            }
        }

        private void HandleBreak(BlockType block)
        {
            PlayOne(BlockDatabase.Get(block).soundGroup, a => a.dig);
        }

        private void HandlePlace(BlockType block)
        {
            PlayOne(BlockDatabase.Get(block).soundGroup, a => a.place);
        }

        private void Update()
        {
            if (!hooked && interaction != null)
            {
                interaction.OnBreak += HandleBreak;
                interaction.OnPlace += HandlePlace;
                hooked = true;
            }
            if (motor == null || world == null || world.sim == null)
            {
                return;
            }

            Vector3 pos = motor.transform.position;
            Vector3 delta = pos - lastPos;
            delta.y = 0f;
            float moved = delta.magnitude;
            lastPos = pos;

            var cc = Controller;
            bool grounded = cc != null && cc.isGrounded;
            if (!grounded || moved < 0.02f)
            {
                return;
            }

            stepTimer -= Time.deltaTime * Mathf.Clamp(moved / (motor.walkSpeed * Time.deltaTime), 0.5f, 2.5f);
            if (stepTimer <= 0f)
            {
                stepTimer = stepInterval;
                var under = world.sim.GetBlock(
                    Mathf.FloorToInt(pos.x),
                    Mathf.FloorToInt(pos.y - 0.2f),
                    Mathf.FloorToInt(pos.z));
                if (under != BlockType.Air)
                {
                    PlayOne(BlockDatabase.Get(under).soundGroup, a => a.step);
                }
            }
        }

        private void PlayOne(string group, System.Func<AudioLibrary.Group, AudioClip[]> select)
        {
            var set = AudioLibrary.GetGroup(group);
            if (set == null)
            {
                return;
            }
            var clips = select(set);
            if (clips == null || clips.Length == 0)
            {
                return;
            }
            source.PlayOneShot(clips[Random.Range(0, clips.Length)], volume);
        }
    }
}
