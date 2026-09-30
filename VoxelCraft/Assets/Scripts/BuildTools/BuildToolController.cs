using System.Collections.Generic;
using UnityEngine;
using VoxelCraft.Core;
using VoxelCraft.Player;
using VoxelCraft.World;

namespace VoxelCraft.BuildTools
{
    /// <summary>
    /// WorldEdit-style in-game editing: B toggles build mode; while active, LMB
    /// picks selection corners on the targeted block face, then keys run ops:
    /// F fill, R replace-surface... see KeyHelp. Ops apply instantly, undo with Z.
    /// Selection is axis-aligned between two picked block positions.
    /// </summary>
    public class BuildToolController : MonoBehaviour
    {
        public WorldRoot world;
        public Camera viewCamera;
        public Transform playerBody;

        [Header("Selection visual")]
        public Color lineColor = new Color(1f, 0.6f, 0.1f, 0.9f);

        private bool active;
        private Vector3Int? cornerA;
        private Vector3Int? cornerB;
        private readonly List<LineRenderer> boxLines = new List<LineRenderer>();
        private Transform boxRoot;

        // Undo stack: world pos -> previous block (0 = air). Each op pushes its own dict.
        private readonly List<Dictionary<Vector3Int, BlockType>> undoStack = new List<Dictionary<Vector3Int, BlockType>>();
        private const int MaxUndo = 32;

        private string statusText;
        private float statusUntil;

        public bool IsActive => active;
        public string Status => Time.time < statusUntil ? statusText : null;
        public string SelectionText =>
            cornerA == null ? "no corners" :
            cornerB == null ? $"A={cornerA.Value} (pick B)" :
            $"A={cornerA.Value} B={cornerB.Value}";

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.B) && world != null && world.sim != null)
            {
                active = !active;
                if (!active)
                {
                    cornerA = null;
                    cornerB = null;
                    SetBox(false);
                }
                ShowStatus(active
                    ? "Build mode ON — LMB pick corners, F fill / X clear / C copy / V paste / Z undo / B off"
                    : "Build mode OFF");
            }
            if (!active || Cursor.lockState != CursorLockMode.Locked || viewCamera == null || world == null || world.sim == null)
            {
                return;
            }

            var ray = new Ray(viewCamera.transform.position, viewCamera.transform.forward);
            bool hasTarget = world.sim.Raycast(ray.origin, ray.direction, 10f, out Vector3Int hit, out _);

            if (Input.GetMouseButtonDown(0) && hasTarget)
            {
                if (cornerA == null || (cornerA != null && cornerB != null))
                {
                    cornerA = hit;
                    cornerB = null;
                    ShowStatus($"corner A = {hit}");
                }
                else
                {
                    cornerB = hit;
                    ShowStatus($"corner B = {hit} — F fill / X clear / C copy / V paste");
                }
            }

            if (cornerA != null && cornerB != null)
            {
                PositionBox(cornerA.Value, cornerB.Value);
                SetBox(true);
            }
            else
            {
                SetBox(false);
            }

            if (cornerA == null || cornerB == null)
            {
                return;
            }

            if (Input.GetKeyDown(KeyCode.F)) OpFill();
            if (Input.GetKeyDown(KeyCode.X)) OpClear();
            if (Input.GetKeyDown(KeyCode.C)) OpCopy();
            if (Input.GetKeyDown(KeyCode.V)) OpPaste();
        }

        private void LateUpdate()
        {
            if (Input.GetKeyDown(KeyCode.Z) && active)
            {
                OpUndo();
            }
        }

        private static Vector3Int Min(Vector3Int a, Vector3Int b) => new Vector3Int(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Min(a.z, b.z));
        private static Vector3Int Max(Vector3Int a, Vector3Int b) => new Vector3Int(Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y), Mathf.Max(a.z, b.z));

        // ---- clipboard ----
        private static BlockType[,,] clipboard;
        private static Vector3Int clipboardSize;

        /// <summary>Fill uses the block selected in the player hotbar (via interaction).</summary>
        public BlockInteraction interaction;

        private BlockType FillBlock =>
            interaction != null ? interaction.SelectedBlock : BlockType.Plank;

        private IEnumerable<Vector3Int> SelectionVolume()
        {
            var lo = Min(cornerA.Value, cornerB.Value);
            var hi = Max(cornerA.Value, cornerB.Value);
            for (int y = lo.y; y <= hi.y; y++)
                for (int z = lo.z; z <= hi.z; z++)
                    for (int x = lo.x; x <= hi.x; x++)
                        yield return new Vector3Int(x, y, z);
        }

        private void Record(IEnumerable<Vector3Int> cells)
        {
            var snapshot = new Dictionary<Vector3Int, BlockType>();
            foreach (var c in cells)
            {
                snapshot[c] = world.sim.GetBlock(c.x, c.y, c.z);
            }
            undoStack.Add(snapshot);
            if (undoStack.Count > MaxUndo)
            {
                undoStack.RemoveAt(0);
            }
        }

        private void Apply(Dictionary<Vector3Int, BlockType> changes)
        {
            foreach (var kv in changes)
            {
                world.SetBlockAndApply(kv.Key, kv.Value);
            }
        }

        private void OpFill()
        {
            var cells = new List<Vector3Int>(SelectionVolume());
            if (!GuardVolume(cells.Count)) return;
            Record(cells);
            foreach (var c in cells)
            {
                world.SetBlockAndApply(c, FillBlock);
            }
            ShowStatus($"filled {cells.Count} blocks with {FillBlock}");
        }

        private void OpClear()
        {
            var cells = new List<Vector3Int>(SelectionVolume());
            if (!GuardVolume(cells.Count)) return;
            Record(cells);
            foreach (var c in cells)
            {
                world.SetBlockAndApply(c, BlockType.Air);
            }
            ShowStatus($"cleared {cells.Count} blocks");
        }

        private void OpCopy()
        {
            var lo = Min(cornerA.Value, cornerB.Value);
            var hi = Max(cornerA.Value, cornerB.Value);
            int sx = hi.x - lo.x + 1, sy = hi.y - lo.y + 1, sz = hi.z - lo.z + 1;
            if (!GuardVolume(sx * sy * sz)) return;
            clipboard = new BlockType[sx, sy, sz];
            for (int y = 0; y < sy; y++)
                for (int z = 0; z < sz; z++)
                    for (int x = 0; x < sx; x++)
                        clipboard[x, y, z] = world.sim.GetBlock(lo.x + x, lo.y + y, lo.z + z);
            clipboardSize = new Vector3Int(sx, sy, sz);
            ShowStatus($"copied {sx}x{sy}x{sz} ({sx * sy * sz} cells)");
        }

        private void OpPaste()
        {
            if (clipboard == null)
            {
                ShowStatus("clipboard empty — copy first (C)");
                return;
            }
            var lo = Min(cornerA.Value, cornerB.Value);
            int sx = clipboardSize.x, sy = clipboardSize.y, sz = clipboardSize.z;
            if (!GuardVolume(sx * sy * sz)) return;
            var cells = new List<Vector3Int>(sx * sy * sz);
            for (int y = 0; y < sy; y++)
                for (int z = 0; z < sz; z++)
                    for (int x = 0; x < sx; x++)
                        cells.Add(new Vector3Int(lo.x + x, lo.y + y, lo.z + z));
            Record(cells);
            for (int y = 0; y < sy; y++)
                for (int z = 0; z < sz; z++)
                    for (int x = 0; x < sx; x++)
                    {
                        var t = clipboard[x, y, z];
                        if (t != BlockType.Air)
                        {
                            world.SetBlockAndApply(new Vector3Int(lo.x + x, lo.y + y, lo.z + z), t);
                        }
                    }
            ShowStatus($"pasted {sx}x{sy}x{sz} at {lo}");
        }

        private bool GuardVolume(int count)
        {
            if (count > 32 * 32 * 32)
            {
                ShowStatus("selection too large (max 32^3 = 32768 blocks)");
                return false;
            }
            return true;
        }

        private void OpUndo()
        {
            if (undoStack.Count == 0)
            {
                ShowStatus("nothing to undo");
                return;
            }
            var snap = undoStack[undoStack.Count - 1];
            undoStack.RemoveAt(undoStack.Count - 1);
            foreach (var kv in snap)
            {
                world.SetBlockAndApply(kv.Key, kv.Value);
            }
            ShowStatus($"undo {snap.Count} blocks");
        }

        private void ShowStatus(string msg)
        {
            statusText = msg;
            statusUntil = Time.time + 3f;
        }

        // ---- selection box visuals ----
        private static readonly Vector3[] BoxCorners =
        {
            new Vector3(0,0,0), new Vector3(1,0,0), new Vector3(1,0,1), new Vector3(0,0,1),
            new Vector3(0,1,0), new Vector3(1,1,0), new Vector3(1,1,1), new Vector3(0,1,1),
        };
        private static readonly int[,] BoxEdges =
        {
            {0,1},{1,2},{2,3},{3,0},{4,5},{5,6},{6,7},{7,4},{0,4},{1,5},{2,6},{3,7},
        };

        private void EnsureBox()
        {
            if (boxRoot != null) return;
            boxRoot = new GameObject("BuildSelectionBox").transform;
            boxRoot.SetParent(transform, false);
            var shader = Resources.Load<Shader>("Shaders/LineShader");
            var material = shader != null
                ? new Material(shader)
                : new Material(Shader.Find("Sprites/Default"));
            for (int e = 0; e < BoxEdges.GetLength(0); e++)
            {
                var go = new GameObject("BoxEdge" + e);
                go.transform.SetParent(boxRoot, false);
                var line = go.AddComponent<LineRenderer>();
                line.positionCount = 2;
                line.startWidth = 0.08f;
                line.endWidth = 0.08f;
                line.sharedMaterial = material;
                line.startColor = lineColor;
                line.endColor = lineColor;
                line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                line.useWorldSpace = true;
                boxLines.Add(line);
            }
            boxRoot.gameObject.SetActive(false);
        }

        private void PositionBox(Vector3Int a, Vector3Int b)
        {
            EnsureBox();
            var lo = Min(a, b);
            var hi = Max(a, b);
            var loF = new Vector3(lo.x, lo.y, lo.z);
            var size = new Vector3(hi.x - lo.x + 1, hi.y - lo.y + 1, hi.z - lo.z + 1);
            for (int e = 0; e < BoxEdges.GetLength(0); e++)
            {
                var line = boxLines[e];
                line.SetPosition(0, loF + Vector3.Scale(BoxCorners[BoxEdges[e, 0]], size));
                line.SetPosition(1, loF + Vector3.Scale(BoxCorners[BoxEdges[e, 1]], size));
            }
        }

        private void SetBox(bool on)
        {
            if (boxRoot != null && boxRoot.gameObject.activeSelf != on)
            {
                boxRoot.gameObject.SetActive(on);
            }
        }

        /// <summary>Editor-capture hook: build + show the selection box without Update.</summary>
        public void BuildBoxForCapture()
        {
            if (cornerA != null && cornerB != null)
            {
                PositionBox(cornerA.Value, cornerB.Value);
                SetBox(true);
            }
        }
    }
}
