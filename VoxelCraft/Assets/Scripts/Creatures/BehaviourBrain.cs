using System.Collections.Generic;
using UnityEngine;
using VoxelCraft.Creatures;

namespace VoxelCraft.Creatures
{
    /// <summary>
    /// Ambient behaviour state machine layered on top of BlockyAnimal's
    /// locomotion: species-specific idle behaviours played through the
    /// official bedrock clips (wolf sit/shake, sheep graze, fox sit/sleep).
    /// Locomotion (walking) always wins; behaviours run only while idle.
    /// Values below are the vanilla animation names from bedrock-samples.
    /// </summary>
    public class BehaviourBrain : MonoBehaviour
    {
        private enum State { Locomotion, Behaviour }

        private struct BehDef
        {
            public string clip;       // official bedrock clip name
            public bool absolute;     // pose-style (setup/sitting) clip?
            public float minT, maxT;  // duration range (s)
        }

        private BlockyAnimal ani;
        private BedrockAnimationPlayer player;
        private State state = State.Locomotion;
        private string currentClip;
        private bool currentAbsolute;
        private float stateTimer;
        private float behaviourCooldown;

        // Species behaviour tables come from the data registry
        // (Resources/Registry/creatures.json) - clips are official bedrock
        // names; BehaviourBrain only picks WHICH clip, never invents poses.
        private static Dictionary<string, BehDef[]> table;
        private static Dictionary<string, BehDef[]> BuildTable()
        {
            var t = new Dictionary<string, BehDef[]>();
            string[] known = { "wolf", "sheep", "fox", "chicken", "cow", "goat", "mooshroom", "pig" };
            foreach (var sp in known)
            {
                var reg = CreatureRegistry.Get(sp);
                if (reg == null || reg.behaviours == null || reg.behaviours.Count == 0) continue;
                var list = new List<BehDef>();
                foreach (var b in reg.behaviours)
                    list.Add(new BehDef { clip = b.clip, absolute = b.absolute, minT = b.minT, maxT = b.maxT });
                t[sp] = list.ToArray();
            }
            return t;
        }

        private void Update()
        {
            if (ani == null)
            {
                ani = GetComponent<BlockyAnimal>();
                if (ani == null) return;
            }
            // Batch verification drives time via Tick(dt) - Update is a
            // no-op there (Time.deltaTime == 0).
            if (Time.deltaTime <= 0f) return;
            Tick(Time.deltaTime);
        }

        /// <summary>Batch/tests drive the brain manually.</summary>
        public void Tick(float dt)
        {
            if (ani == null)
            {
                ani = GetComponent<BlockyAnimal>();
                if (ani == null) return;
            }
            if (player == null)
            {
                player = GetComponent<BedrockAnimationPlayer>();
                if (player == null) return;
            }
            if (table == null) table = BuildTable();
            if (!table.TryGetValue(ani.species, out var behs)) return;

            stateTimer -= dt;
            behaviourCooldown -= dt;

            bool idle = !ani.walking;
            switch (state)
            {
                case State.Locomotion:
                    if (idle && behaviourCooldown <= 0f && behs.Length > 0)
                    {
                        var b = behs[Random.Range(0, behs.Length)];
                        if (player.Play(b.clip, b.absolute))
                        {
                            currentClip = b.clip;
                            currentAbsolute = b.absolute;
                            stateTimer = Random.Range(b.minT, b.maxT);
                            state = State.Behaviour;
                        }
                        else behaviourCooldown = 3f; // clip missing; back off
                    }
                    break;

                case State.Behaviour:
                    // Interrupt: animal starts walking -> drop the pose.
                    if (!idle)
                    {
                        if (currentClip != null) player.Stop(currentClip);
                        currentClip = null;
                        behaviourCooldown = Random.Range(4f, 10f);
                        state = State.Locomotion;
                    }
                    else if (stateTimer <= 0f)
                    {
                        if (currentClip != null) player.Stop(currentClip);
                        currentClip = null;
                        behaviourCooldown = Random.Range(5f, 15f);
                        state = State.Locomotion;
                    }
                    break;
            }
        }

        /// <summary>Test/verification introspection.</summary>
        public string ActiveClip => currentClip;
        public bool InBehaviour => state == State.Behaviour;
    }
}
