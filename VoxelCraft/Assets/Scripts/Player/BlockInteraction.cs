using System.Collections.Generic;
using UnityEngine;
using VoxelCraft.Core;
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

            if (Input.GetMouseButton(0) && Time.time >= nextBreakTime)
            {
                var target = world.sim.GetBlock(hit.x, hit.y, hit.z);
                if (!BlockDatabase.Get(target).unbreakable)
                {
                    OnBreak?.Invoke(target);
                    world.SetBlockAndApply(hit, BlockType.Air);
                    nextBreakTime = Time.time + breakInterval;
                }
            }

            if (Input.GetMouseButtonDown(1))
            {
                var current = world.sim.GetBlock(place.x, place.y, place.z);
                if ((current == BlockType.Air || current == BlockType.Water) && !OverlapsPlayer(place))
                {
                    world.SetBlockAndApply(place, SelectedBlock);
                    OnPlace?.Invoke(SelectedBlock);
                }
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
