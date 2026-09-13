using System.Collections.Generic;
using UnityEngine;
using VoxelCraft.Core;
using VoxelCraft.Items;
using VoxelCraft.World;

namespace VoxelCraft.Player
{
    /// <summary>
    /// First-person block editing: DDA targeting with a wireframe highlight,
    /// hold-to-break (LMB), place with player-overlap guard (RMB), 9-slot hotbar
    /// driven by number keys and the mouse wheel.
    /// </summary>
    public class BlockInteraction : MonoBehaviour
    {
        public Camera viewCamera;
        public WorldRoot world;
        public Transform playerBody;
        public VoxelCraft.Environment.DayNightCycle dayNight;

        public BlockType[][] hotbarPages =
        {
            new[]
            {
                BlockType.Grass, BlockType.Dirt, BlockType.Stone, BlockType.Sand, BlockType.Log,
                BlockType.Plank, BlockType.Cobble, BlockType.Glass, BlockType.Brick,
            },
            new[]
            {
                BlockType.Snow, BlockType.Gravel, BlockType.Ice, BlockType.Obsidian, BlockType.MossyCobble,
                BlockType.StoneBrick, BlockType.CoalOre, BlockType.IronOre, BlockType.GoldOre,
            },
        };
        public int activePage;
        public int selectedIndex;
        public ToolType currentTool = ToolType.Hand;

        /// <summary>Transient hint shown by the HUD when an action was refused.</summary>
        public string denyHint;
        private float denyHintUntil;

        /// <summary>Raised with the block type right before it is destroyed.</summary>
        public event System.Action<BlockType> OnBreak;

        /// <summary>Raised with the block type right after it is placed.</summary>
        public event System.Action<BlockType> OnPlace;

        public float reach = 6f;
        public float breakInterval = 0.22f;

        private float nextBreakTime;
        private readonly List<LineRenderer> highlightLines = new List<LineRenderer>();
        private Transform highlightRoot;
        private CharacterController cachedController;

        public CharacterController Controller
        {
            get
            {
                if (cachedController == null && playerBody != null)
                {
                    cachedController = playerBody.GetComponent<CharacterController>();
                }
                return cachedController;
            }
        }

        private const float H = 0.502f; // half-size plus a hair to avoid z-fighting

        private static readonly Vector3[] CornerOffsets =
        {
            new Vector3(-H, -H, -H), new Vector3(H, -H, -H), new Vector3(H, -H, H), new Vector3(-H, -H, H),
            new Vector3(-H, H, -H), new Vector3(H, H, -H), new Vector3(H, H, H), new Vector3(-H, H, H),
        };

        private static readonly int[,] Edges =
        {
            {0,1},{1,2},{2,3},{3,0},{4,5},{5,6},{6,7},{7,4},{0,4},{1,5},{2,6},{3,7},
        };

        public BlockType[] hotbar => hotbarPages[activePage];

        public BlockType SelectedBlock => hotbar[selectedIndex];

        private void Awake()
        {
            // playerBody is assigned after AddComponent; Controller resolves lazily.
            BuildHighlight();
        }

        /// <summary>Raised whenever the player uses/swings the held item
        /// (creature attack, block break attempt, placement).</summary>
        public event System.Action OnUse;

        private void Update()
        {
            if (Cursor.lockState != CursorLockMode.Locked || viewCamera == null || world == null || world.sim == null)
            {
                SetHighlight(false);
                return;
            }

            Ray ray = new Ray(viewCamera.transform.position, viewCamera.transform.forward);
            bool hasTarget = world.sim.Raycast(ray.origin, ray.direction, reach, out Vector3Int hit, out Vector3Int place);
            SetHighlight(hasTarget);
            if (hasTarget)
            {
                PositionHighlight(hit);
            }
            else
            {
                return;
            }

            // Clock tool: hold LMB to fast-forward time, R to jump between
            // dawn / noon / dusk / midnight.
            if (currentTool == ToolType.Clock && dayNight != null)
            {
                if (Input.GetMouseButton(0))
                {
                    dayNight.timeOfDay = (dayNight.timeOfDay + Time.deltaTime * 0.16f) % 1f;
                }
                if (Input.GetKeyDown(KeyCode.R))
                {
                    float[] anchors = { 0.02f, 0.25f, 0.48f, 0.75f };
                    float target = anchors[0];
                    foreach (float anchor in anchors)
                    {
                        if (anchor > dayNight.timeOfDay + 0.02f)
                        {
                            target = anchor;
                            break;
                        }
                    }
                    dayNight.timeOfDay = target;
                    ShowDeny("Time jumped to " + dayNight.ClockText);
                }
            }

            // Sword: attack creatures first (physics ray against animal colliders),
            // fall through to block breaking rules when no creature is targeted.
            bool creatureAttacked = false;
            if (currentTool == ToolType.Sword && Input.GetMouseButton(0) && Time.time >= nextBreakTime)
            {
                var creatureHits = Physics.RaycastAll(
                    new Ray(viewCamera.transform.position, viewCamera.transform.forward), 3.8f);
                float nearest = float.PositiveInfinity;
                Creatures.BlockyAnimal target = null;
                foreach (var h in creatureHits)
                {
                    var animal = h.collider != null ? h.collider.GetComponentInParent<Creatures.BlockyAnimal>() : null;
                    if (animal != null && !animal.dead && h.distance < nearest)
                    {
                        nearest = h.distance;
                        target = animal;
                    }
                }
                if (target != null)
                {
                    target.TakeHit(viewCamera.transform.forward, ToolType.Sword);
                    nextBreakTime = Time.time + 0.45f;
                    creatureAttacked = true;
                    OnUse?.Invoke();
                }
            }

            bool cropHandled = false;
            if (!creatureAttacked && currentTool != ToolType.Clock && Input.GetMouseButton(0) && Time.time >= nextBreakTime)
            {
                OnUse?.Invoke();
                var target = world.sim.GetBlock(hit.x, hit.y, hit.z);

                // Wheat harvest: pickaxe-only, yields carrots + seeds.
                if (Items.Crops.IsWheat(target))
                {
                    cropHandled = true;
                    if (currentTool == ToolType.Pickaxe)
                    {
                        Items.Crops.Harvest(target);
                        Items.Crops.Forget(hit);
                        world.SetBlockAndApply(hit, BlockType.Air);
                        nextBreakTime = Time.time + 0.15f;
                    }
                    else
                    {
                        ShowDeny("Harvest wheat with the PICKAXE (press T)");
                        nextBreakTime = Time.time + 0.25f;
                    }
                }
                else
                {
                    var (allowed, interval) = ToolRules.BreakRule(currentTool, target);
                    if (allowed)
                    {
                        OnBreak?.Invoke(target);
                        ItemDrops.Spawn(Inventory.DropFor(target),
                            new Vector3(hit.x + 0.5f, hit.y + 0.5f, hit.z + 0.5f));
                        if (target == BlockType.Grass && Random.value < Items.Crops.SeedChanceFromGrass)
                        {
                            Inventory.AddSeeds(1);
                        }
                        world.SetBlockAndApply(hit, BlockType.Air);
                        nextBreakTime = Time.time + interval;
                    }
                    else
                    {
                        ShowDeny(BlockDatabase.Get(target).unbreakable
                            ? "Bedrock cannot be broken"
                            : "This block needs a PICKAXE (press T)");
                        nextBreakTime = Time.time + 0.25f;
                    }
                }
            }

            if (!cropHandled && Input.GetMouseButtonDown(1))
            {
                OnUse?.Invoke();
                var current = world.sim.GetBlock(place.x, place.y, place.z);
                if ((current == BlockType.Air || current == BlockType.Water) && !OverlapsPlayer(place))
                {
                    if (Inventory.TryConsume(SelectedBlock))
                    {
                        world.SetBlockAndApply(place, SelectedBlock);
                        OnPlace?.Invoke(SelectedBlock);
                    }
                    else
                    {
                        ShowDeny("No " + BlockDatabase.Get(SelectedBlock).name + " left - break blocks to collect (G = creative)");
                    }
                }
            }

            // NOTE: T (tool bag) is owned by the Hud - it must work while the
            // cursor is released, which this Update no longer handles then.

            // Q: plant seeds on the targeted soil block (crop grows above it).
            if (Input.GetKeyDown(KeyCode.Q) && Inventory.seeds > 0)
            {
                var soil = world.sim.GetBlock(hit.x, hit.y, hit.z);
                var above = new Vector3Int(hit.x, hit.y + 1, hit.z);
                if (Items.Crops.IsSoil(soil) && world.sim.GetBlock(above.x, above.y, above.z) == BlockType.Air)
                {
                    if (Inventory.TryConsumeSeeds(1))
                    {
                        world.SetBlockAndApply(above, BlockType.Wheat0);
                        Items.Crops.Track(above);
                    }
                }
                else
                {
                    ShowDeny("Seeds need grass or dirt with open sky above");
                }
            }
            if (Input.GetKeyDown(KeyCode.G))
            {
                Inventory.creative = !Inventory.creative;
                ShowDeny(Inventory.creative ? "Creative mode ON (infinite blocks)" : "Creative mode OFF");
            }

            if (Input.GetKeyDown(KeyCode.Tab))
            {
                activePage = (activePage + 1) % hotbarPages.Length;
                selectedIndex = Mathf.Clamp(selectedIndex, 0, hotbar.Length - 1);
            }

            for (int i = 0; i < hotbar.Length; i++)
            {
                if (Input.GetKeyDown(KeyCode.Alpha1 + i))
                {
                    selectedIndex = i;
                }
            }
            float wheel = Input.GetAxis("Mouse ScrollWheel");
            if (wheel > 0.01f)
            {
                selectedIndex = (selectedIndex + 1) % hotbar.Length;
            }
            else if (wheel < -0.01f)
            {
                selectedIndex = (selectedIndex - 1 + hotbar.Length) % hotbar.Length;
            }
        }

        private void ShowDeny(string message)
        {
            denyHint = message;
            denyHintUntil = Time.time + 2.2f;
        }

        /// <summary>HUD reads this; null when the hint expired.</summary>
        public string ActiveDeny => Time.time < denyHintUntil ? denyHint : null;

        private bool OverlapsPlayer(Vector3Int voxel)
        {
            var controller = Controller;
            if (controller == null)
            {
                return false;
            }
            var voxelBounds = new Bounds(new Vector3(voxel.x + 0.5f, voxel.y + 0.5f, voxel.z + 0.5f), Vector3.one * 0.98f);
            return controller.bounds.Intersects(voxelBounds);
        }

        private void BuildHighlight()
        {
            highlightRoot = new GameObject("Highlight").transform;
            highlightRoot.SetParent(transform, false);
            // Self-contained shader from Resources: survives build stripping.
            var shader = Resources.Load<Shader>("Shaders/LineShader");
            var material = shader != null
                ? new Material(shader)
                : new Material(Shader.Find("Sprites/Default")) { color = new Color(0f, 0f, 0f, 0.85f) };
            for (int e = 0; e < Edges.GetLength(0); e++)
            {
                var lineGo = new GameObject("Edge" + e);
                lineGo.transform.SetParent(highlightRoot, false);
                var line = lineGo.AddComponent<LineRenderer>();
                line.positionCount = 2;
                line.startWidth = 0.02f;
                line.endWidth = 0.02f;
                line.sharedMaterial = material;
                if (shader != null)
                {
                    line.startColor = new Color(0f, 0f, 0f, 0.85f);
                    line.endColor = new Color(0f, 0f, 0f, 0.85f);
                }
                line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                line.useWorldSpace = true;
                highlightLines.Add(line);
            }
            highlightRoot.gameObject.SetActive(false);
        }

        private void SetHighlight(bool active)
        {
            if (highlightRoot != null && highlightRoot.gameObject.activeSelf != active)
            {
                highlightRoot.gameObject.SetActive(active);
            }
        }

        private void PositionHighlight(Vector3Int voxel)
        {
            var center = new Vector3(voxel.x + 0.5f, voxel.y + 0.5f, voxel.z + 0.5f);
            for (int e = 0; e < Edges.GetLength(0); e++)
            {
                var line = highlightLines[e];
                line.SetPosition(0, center + CornerOffsets[Edges[e, 0]]);
                line.SetPosition(1, center + CornerOffsets[Edges[e, 1]]);
            }
        }
    }
}
