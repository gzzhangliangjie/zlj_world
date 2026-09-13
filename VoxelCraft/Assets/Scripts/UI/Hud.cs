using UnityEngine;
using VoxelCraft.Art;
using VoxelCraft.Core;
using VoxelCraft.Environment;
using VoxelCraft.Items;
using VoxelCraft.Player;
using VoxelCraft.World;

namespace VoxelCraft.UI
{
    /// <summary>
    /// IMGUI overlay: crosshair, hotbar with block icons, tool panel, clock/weather
    /// status, survival inventory strip, transient deny hints, the paused
    /// controls panel, and the T/E tool-bag screen (pick tools, view backpack).
    /// Owns the Escape / click-to-resume cursor lock flow.
    /// </summary>
    public class Hud : MonoBehaviour
    {
        public Game game;
        public BlockInteraction interaction;
        public DayNightCycle dayNight;
        public WeatherSystem weather;

        private static readonly ToolType[] BagTools =
        {
            ToolType.Hand, ToolType.Sword, ToolType.Axe, ToolType.Pickaxe, ToolType.Clock,
        };

        private int fps;
        private int frames;
        private float fpsWindow;
        private bool bagOpen;

        private GUIStyle labelStyle;
        private GUIStyle boxStyle;
        private GUIStyle titleStyle;
        private GUIStyle hintStyle;
        private bool stylesBuilt;

        private void Update()
        {
            frames++;
            fpsWindow += Time.unscaledDeltaTime;
            if (fpsWindow >= 0.5f)
            {
                fps = Mathf.RoundToInt(frames / fpsWindow);
                frames = 0;
                fpsWindow = 0f;
            }

            if (Input.GetKeyDown(KeyCode.Escape))
            {
                if (bagOpen)
                {
                    CloseBag();
                }
                else
                {
                    Cursor.lockState = CursorLockMode.None;
                    Cursor.visible = true;
                }
            }
            else if (Input.GetKeyDown(KeyCode.T) || Input.GetKeyDown(KeyCode.E))
            {
                bagOpen = !bagOpen;
                if (bagOpen)
                {
                    Cursor.lockState = CursorLockMode.None;
                    Cursor.visible = true;
                }
                else
                {
                    LockCursor();
                }
            }
            else if (!bagOpen && Cursor.lockState == CursorLockMode.None && Input.GetMouseButtonDown(0))
            {
                LockCursor();
            }

            // While the bag is open the number keys pick a tool directly.
            if (bagOpen)
            {
                for (int i = 0; i < BagTools.Length; i++)
                {
                    if (Input.GetKeyDown(KeyCode.Alpha1 + i))
                    {
                        SelectTool(BagTools[i]);
                        break;
                    }
                }
            }
        }

        private void LockCursor()
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }

        private void CloseBag()
        {
            bagOpen = false;
            LockCursor();
        }

        private void SelectTool(ToolType tool)
        {
            if (interaction != null)
            {
                interaction.currentTool = tool;
            }
            CloseBag();
        }

        private void OnGUI()
        {
            BuildStyles();
            if (game == null || interaction == null)
            {
                return;
            }

            // Corner build id: lets anyone verify which build the browser is
            // actually running (Pages caches index.html for up to 10 minutes).
            GUI.Label(new Rect(Screen.width - 150f, Screen.height - 22f, 140f, 18f),
                "build " + Core.Game.BuildId, labelStyle);

            if (bagOpen)
            {
                DrawToolBag();
            }
            else if (Cursor.lockState == CursorLockMode.Locked)
            {
                DrawCrosshair();
                DrawHotbar();
                DrawTool();
                DrawStatus();
                DrawInventory();
                DrawDenyHint();
            }
            else
            {
                DrawPausedPanel();
            }
        }

        private void BuildStyles()
        {
            if (stylesBuilt)
            {
                return;
            }
            stylesBuilt = true;
            labelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                alignment = TextAnchor.UpperLeft,
                normal = { textColor = new Color(1f, 1f, 1f, 0.92f) },
            };
            boxStyle = new GUIStyle(GUI.skin.box);
            titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 26,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(1f, 1f, 1f, 0.95f) },
            };
            hintStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 15,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(1f, 0.85f, 0.4f, 0.95f) },
            };
        }

        private void DrawCrosshair()
        {
            Texture2D white = game.atlas.white;
            if (white == null)
            {
                return;
            }
            float cx = Screen.width * 0.5f;
            float cy = Screen.height * 0.5f;
            var prev = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, 0.85f);
            GUI.DrawTexture(new Rect(cx - 1f, cy - 8f, 2f, 16f), white);
            GUI.DrawTexture(new Rect(cx - 8f, cy - 1f, 16f, 2f), white);
            GUI.color = prev;
        }

        private static string ToolHint(ToolType tool)
        {
            switch (tool)
            {
                case ToolType.Sword: return "LMB attacks animals for meat";
                case ToolType.Axe: return "fast wood cutting";
                case ToolType.Pickaxe: return "breaks stone, harvests wheat";
                case ToolType.Clock: return "hold LMB: time +, R: jump";
                default: return "breaks soft blocks";
            }
        }

        private void DrawTool()
        {
            var tool = interaction.currentTool;
            float x = 14f;
            float y = Screen.height - 132f;
            GUI.Box(new Rect(x, y, 168f, 52f), GUIContent.none, boxStyle);
            var icon = ToolIcons.Get(tool);
            if (icon != null)
            {
                GUI.DrawTexture(new Rect(x + 8f, y + 8f, 36f, 36f), icon);
            }
            GUI.Label(new Rect(x + 52f, y + 8f, 112f, 20f), ToolRules.DisplayName(tool), labelStyle);
            GUI.Label(new Rect(x + 52f, y + 26f, 112f, 26f), ToolHint(tool), labelStyle);
            GUI.Label(new Rect(x + 8f, y - 18f, 160f, 18f), "Tool  [T: tool bag]", labelStyle);
        }

        private void DrawHotbar()
        {
            const int slot = 46;
            const int gap = 4;
            int count = interaction.hotbar.Length;
            int total = count * (slot + gap) - gap;
            float x0 = (Screen.width - total) * 0.5f;
            float y0 = Screen.height - slot - 14f;

            var selected = interaction.hotbar[interaction.selectedIndex];
            var prev = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, 0.65f);
            string pageNote = interaction.hotbarPages.Length > 1
                ? $"   [Page {interaction.activePage + 1}/{interaction.hotbarPages.Length} - Tab]"
                : "";
            string stock = Inventory.creative ? "" : $" x{Inventory.Get(selected)}";
            GUI.Label(new Rect(x0, y0 - 24f, total + 240f, 20f),
                BlockDatabase.Get(selected).name + stock + pageNote, labelStyle);
            GUI.color = prev;

            for (int i = 0; i < count; i++)
            {
                Rect rect = new Rect(x0 + i * (slot + gap), y0, slot, slot);
                GUI.Box(rect, GUIContent.none, boxStyle);
                if (game.atlas.icons.TryGetValue(interaction.hotbar[i], out Texture2D icon))
                {
                    var dimPrev = GUI.color;
                    if (!Inventory.creative && Inventory.Get(interaction.hotbar[i]) <= 0)
                    {
                        GUI.color = new Color(1f, 1f, 1f, 0.35f);
                    }
                    GUI.DrawTexture(new Rect(rect.x + 7f, rect.y + 7f, slot - 14f, slot - 14f), icon);
                    GUI.color = dimPrev;
                }
                if (i == interaction.selectedIndex)
                {
                    GUI.Box(new Rect(rect.x - 2f, rect.y - 2f, slot + 4f, slot + 4f), GUIContent.none, boxStyle);
                }
                GUI.Label(new Rect(rect.x + 3f, rect.y + 1f, 14f, 14f), (i + 1).ToString(), labelStyle);
            }
        }

        private void DrawStatus()
        {
            var pos = interaction.playerBody != null ? interaction.playerBody.position : Vector3.zero;
            int chunks = game.worldRoot != null && game.worldRoot.sim != null ? game.worldRoot.sim.chunks.Count : 0;
            bool fly = game.playerMotor != null && game.playerMotor.IsFlying;
            string timeLine = "";
            if (dayNight != null)
            {
                timeLine = dayNight.ClockText;
                if (weather != null)
                {
                    timeLine += "  " + weather.Describe;
                }
            }
            string text =
                $"FPS {fps}   Chunks {chunks}   {timeLine}\n" +
                $"XYZ {pos.x:F1} {pos.y:F1} {pos.z:F1}   Seed {game.seed}\n" +
                (fly ? "Flying (F)" : "Walking (F to fly)") +
                (Inventory.creative ? "   [CREATIVE]" : "");
            GUI.Box(new Rect(8f, 8f, 300f, 62f), GUIContent.none, boxStyle);
            GUI.Label(new Rect(14f, 12f, 292f, 56f), text, labelStyle);
        }

        private void DrawInventory()
        {
            int used = 0;
            foreach (var kv in Inventory.counts)
            {
                if (kv.Value > 0)
                {
                    used++;
                }
            }
            if (Inventory.meat > 0)
            {
                used++;
            }
            if (Inventory.creative || used == 0)
            {
                return;
            }
            float y = 8f;
            float x = Screen.width - 176f;
            GUI.Box(new Rect(x, y, 168f, 30f + used * 24f), GUIContent.none, boxStyle);
            GUI.Label(new Rect(x + 8f, y + 6f, 152f, 18f), "Backpack (break to collect)", labelStyle);
            float row = y + 26f;
            if (Inventory.meat > 0)
            {
                var meatIcon = ToolIcons.GetItem("meat");
                if (meatIcon != null)
                {
                    GUI.DrawTexture(new Rect(x + 8f, row, 18f, 18f), meatIcon);
                }
                GUI.Label(new Rect(x + 32f, row + 1f, 128f, 18f), "Meat x" + Inventory.meat, labelStyle);
                row += 24f;
            }
            if (Inventory.seeds > 0)
            {
                var seedIcon = ToolIcons.GetItem("seeds");
                if (seedIcon != null)
                {
                    GUI.DrawTexture(new Rect(x + 8f, row, 18f, 18f), seedIcon);
                }
                GUI.Label(new Rect(x + 32f, row + 1f, 128f, 18f), "Seeds x" + Inventory.seeds + "  (Q to plant)", labelStyle);
                row += 24f;
            }
            if (Inventory.carrots > 0)
            {
                var carrotIcon = ToolIcons.GetItem("carrot");
                if (carrotIcon != null)
                {
                    GUI.DrawTexture(new Rect(x + 8f, row, 18f, 18f), carrotIcon);
                }
                GUI.Label(new Rect(x + 32f, row + 1f, 128f, 18f), "Carrots x" + Inventory.carrots, labelStyle);
                row += 24f;
            }
            foreach (var kv in Inventory.counts)
            {
                if (kv.Value <= 0)
                {
                    continue;
                }
                if (game.atlas.icons.TryGetValue(kv.Key, out Texture2D icon))
                {
                    GUI.DrawTexture(new Rect(x + 8f, row, 18f, 18f), icon);
                }
                GUI.Label(new Rect(x + 32f, row + 1f, 128f, 18f),
                    BlockDatabase.Get(kv.Key).name + " x" + kv.Value, labelStyle);
                row += 24f;
            }
        }

        private void DrawDenyHint()
        {
            string hint = interaction.ActiveDeny;
            if (string.IsNullOrEmpty(hint))
            {
                return;
            }
            float w = 460f;
            var rect = new Rect((Screen.width - w) * 0.5f, Screen.height * 0.62f, w, 24f);
            GUI.Label(rect, hint, hintStyle);
        }

        /// <summary>
        /// The T/E tool bag: all tools with icons and usage hints (click or 1-5 to
        /// pick) plus the backpack contents. Shown while the cursor is released;
        /// block interaction is suspended because it requires cursor lock.
        /// </summary>
        private void DrawToolBag()
        {
            var prev = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.6f);
            GUI.Box(new Rect(0f, 0f, Screen.width, Screen.height), GUIContent.none, boxStyle);
            GUI.color = prev;

            float w = 560f;
            float h = 396f;
            var rect = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);
            GUI.Box(rect, GUIContent.none, boxStyle);
            GUI.Label(new Rect(rect.x, rect.y + 12f, rect.width, 30f), "TOOL BAG", titleStyle);

            // Tools column.
            GUI.Label(new Rect(rect.x + 24f, rect.y + 54f, 300f, 20f), "TOOLS  (click or 1-5)", labelStyle);
            float rowY = rect.y + 78f;
            foreach (var tool in BagTools)
            {
                var rowRect = new Rect(rect.x + 24f, rowY, 302f, 44f);
                bool selected = interaction.currentTool == tool;
                var rowPrev = GUI.color;
                GUI.color = selected ? new Color(1f, 0.85f, 0.4f, 0.28f) : new Color(1f, 1f, 1f, 0.08f);
                GUI.Box(rowRect, GUIContent.none, boxStyle);
                GUI.color = rowPrev;

                var icon = ToolIcons.Get(tool);
                if (icon != null)
                {
                    GUI.DrawTexture(new Rect(rowRect.x + 6f, rowRect.y + 6f, 32f, 32f), icon);
                }
                GUI.Label(new Rect(rowRect.x + 48f, rowRect.y + 4f, 230f, 18f),
                    $"{(int)tool + 1}. {ToolRules.DisplayName(tool)}{(selected ? "  <" : "")}", labelStyle);
                GUI.Label(new Rect(rowRect.x + 48f, rowRect.y + 22f, 250f, 18f), ToolHint(tool), labelStyle);
                if (GUI.Button(rowRect, GUIContent.none, GUIStyle.none))
                {
                    SelectTool(tool);
                }
                rowY += 50f;
            }

            // Backpack column.
            float bx = rect.x + 344f;
            GUI.Label(new Rect(bx, rect.y + 54f, 210f, 20f), "BACKPACK  (click: hold)", labelStyle);
            float by = rect.y + 78f;
            if (Inventory.creative)
            {
                GUI.Label(new Rect(bx, by, 200f, 18f), "[CREATIVE - infinite blocks]", labelStyle);
                by += 22f;
            }
            bool anyItem = false;
            if (Inventory.meat > 0)
            {
                anyItem = true;
                DrawBagRow(bx, ref by, ToolIcons.GetItem("meat"), "Meat x" + Inventory.meat);
            }
            if (Inventory.seeds > 0)
            {
                anyItem = true;
                DrawBagRow(bx, ref by, ToolIcons.GetItem("seeds"), "Seeds x" + Inventory.seeds + " (Q: plant)");
            }
            if (Inventory.carrots > 0)
            {
                anyItem = true;
                DrawBagRow(bx, ref by, ToolIcons.GetItem("carrot"), "Carrots x" + Inventory.carrots);
            }
            foreach (var kv in Inventory.counts)
            {
                if (kv.Value <= 0)
                {
                    continue;
                }
                anyItem = true;
                game.atlas.icons.TryGetValue(kv.Key, out Texture2D icon);
                DrawBagBlockRow(bx, ref by, icon, kv.Key, kv.Value);
            }
            if (!anyItem)
            {
                GUI.Label(new Rect(bx, by, 200f, 36f), "Empty - break blocks\nto collect them", labelStyle);
            }

            GUI.Label(new Rect(rect.x, rect.y + rect.height - 26f, rect.width, 20f),
                "T / E / Esc: close     Tool + held block are shown in game (3D)", labelStyle);
        }

        private void DrawBagRow(float x, ref float y, Texture2D icon, string text)
        {
            if (icon != null)
            {
                GUI.DrawTexture(new Rect(x, y, 18f, 18f), icon);
            }
            GUI.Label(new Rect(x + 24f, y + 1f, 190f, 18f), text, labelStyle);
            y += 22f;
        }

        /// <summary>Backpack block row; clicking equips the block to the current
        /// hotbar slot (and to the hand, via the hand tool).</summary>
        private void DrawBagBlockRow(float x, ref float y, Texture2D icon, BlockType type, int count)
        {
            var rowRect = new Rect(x - 2f, y - 1f, 194f, 21f);
            bool selected = interaction != null && interaction.SelectedBlock == type
                && interaction.currentTool == ToolType.Hand;
            if (selected)
            {
                var prevColor = GUI.color;
                GUI.color = new Color(1f, 0.85f, 0.4f, 0.25f);
                GUI.Box(rowRect, GUIContent.none, boxStyle);
                GUI.color = prevColor;
            }
            if (icon != null)
            {
                GUI.DrawTexture(new Rect(x, y, 18f, 18f), icon);
            }
            GUI.Label(new Rect(x + 24f, y + 1f, 168f, 18f),
                BlockDatabase.Get(type).name + " x" + count + (selected ? "  <" : ""), labelStyle);
            if (GUI.Button(rowRect, GUIContent.none, GUIStyle.none))
            {
                EquipBlock(type);
            }
            y += 22f;
        }

        /// <summary>Equips a backpack block: hand tool + current hotbar slot,
        /// so the held-item view shows the block immediately.</summary>
        private void EquipBlock(BlockType type)
        {
            if (interaction != null)
            {
                interaction.currentTool = ToolType.Hand;
                interaction.hotbar[interaction.selectedIndex] = type;
            }
            CloseBag();
        }

        private void DrawPausedPanel()
        {
            var prev = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.55f);
            GUI.Box(new Rect(0f, 0f, Screen.width, Screen.height), GUIContent.none, boxStyle);
            GUI.color = prev;

            float w = 470f;
            float h = 340f;
            var rect = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);
            GUI.Box(rect, GUIContent.none, boxStyle);
            GUI.Label(new Rect(rect.x, rect.y + 16f, rect.width, 34f), "PAUSED", titleStyle);
            string help =
                "WASD  Move        Shift  Sprint\n" +
                "Space Jump / fly up   Ctrl  Fly down\n" +
                "F     Toggle fly mode\n" +
                "V     First / third person\n" +
                "T / E Tool bag: pick tool (1-5), view backpack\n" +
                "      Sword attacks animals (meat!), Axe=wood, Pick=stone\n" +
                "      Clock: hold LMB fast-forwards time, R jumps\n" +
                "G     Toggle creative (infinite blocks)\n" +
                "Tab   Hotbar page    1-9 / Wheel  Select block\n" +
                "Q     Plant seeds on grass/dirt (30s/stage, 4 stages)\n" +
                "      Harvest mature wheat with the PICKAXE\n" +
                "LMB   Break (tool rules apply)\n" +
                "RMB   Place (consumes from backpack)\n" +
                "Esc   Pause / release cursor\n\n" +
                "Click anywhere to resume.";
            GUI.Label(new Rect(rect.x + 30f, rect.y + 64f, rect.width - 60f, rect.height - 80f), help, labelStyle);
        }
    }
}
