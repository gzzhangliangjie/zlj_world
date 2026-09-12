using System.Collections.Generic;
using UnityEngine;

namespace VoxelCraft.Art
{
    /// <summary>
    /// Loads the bundled CC BY-SA sound set from Resources/Sounds and groups it
    /// by BlockDatabase soundGroup for dig / place / step playback.
    /// </summary>
    public static class AudioLibrary
    {
        public sealed class Group
        {
            public AudioClip[] dig = System.Array.Empty<AudioClip>();
            public AudioClip[] place = System.Array.Empty<AudioClip>();
            public AudioClip[] step = System.Array.Empty<AudioClip>();
        }

        public static readonly Dictionary<string, Group> groups = new Dictionary<string, Group>();
        public static int totalClips { get; private set; }

        public static void Build()
        {
            groups.Clear();
            AudioClip[] all = Resources.LoadAll<AudioClip>("Sounds");
            totalClips = all.Length;

            var map = new Dictionary<string, AudioClip>();
            foreach (var clip in all)
            {
                if (clip != null && !map.ContainsKey(clip.name))
                {
                    map[clip.name] = clip;
                }
            }

            Group Get(string key)
            {
                if (!groups.TryGetValue(key, out var g))
                {
                    g = new Group();
                    groups[key] = g;
                }
                return g;
            }

            // dig / break sounds per group
            Get("grass").dig = Pick(map, "default_dig_crumbly");
            Get("dirt").dig = Pick(map, "default_dig_crumbly");
            Get("sand").dig = Pick(map, "default_dig_crumbly");
            Get("stone").dig = Pick(map, "default_dig_cracky.1", "default_dig_cracky.2", "default_dig_cracky.3");
            Get("wood").dig = Pick(map, "default_dig_choppy.1", "default_dig_choppy.2", "default_dig_choppy.3");
            Get("glass").dig = Pick(map, "default_break_glass.1", "default_break_glass.2", "default_break_glass.3");
            Get("snow").dig = Pick(map, "default_dig_snappy");
            Get("ice").dig = Pick(map, "default_ice_dig.1", "default_ice_dig.2", "default_ice_dig.3");

            // place sounds
            var normalPlace = Pick(map, "default_place_node.1", "default_place_node.2", "default_place_node.3");
            var hardPlace = Pick(map, "default_place_node_hard.1", "default_place_node_hard.2");
            foreach (var pair in groups)
            {
                pair.Value.place = pair.Key == "stone" ? hardPlace : normalPlace;
            }

            // footsteps
            Get("grass").step = Pick(map, "default_grass_footstep.1", "default_grass_footstep.2", "default_grass_footstep.3");
            Get("dirt").step = Pick(map, "default_dirt_footstep.1", "default_dirt_footstep.2");
            Get("sand").step = Pick(map, "default_sand_footstep.1", "default_sand_footstep.2", "default_sand_footstep.3");
            Get("stone").step = Pick(map, "default_hard_footstep.1", "default_hard_footstep.2", "default_hard_footstep.3");
            Get("wood").step = Pick(map, "default_wood_footstep.1", "default_wood_footstep.2");
            Get("glass").step = Pick(map, "default_glass_footstep");
            Get("snow").step = Pick(map, "default_snow_footstep.1", "default_snow_footstep.2");
            Get("ice").step = Pick(map, "default_ice_footstep.1", "default_ice_footstep.2");
        }

        public static Group GetGroup(string name)
        {
            return groups.TryGetValue(name, out var g) ? g : null;
        }

        private static AudioClip[] Pick(Dictionary<string, AudioClip> map, params string[] names)
        {
            var list = new List<AudioClip>(names.Length);
            foreach (string n in names)
            {
                if (map.TryGetValue(n, out var clip) && clip != null)
                {
                    list.Add(clip);
                }
            }
            return list.ToArray();
        }
    }
}
