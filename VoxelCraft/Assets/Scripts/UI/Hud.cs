using UnityEngine;
using VoxelCraft.Core;
using VoxelCraft.Player;
using VoxelCraft.World;

namespace VoxelCraft.UI
{
    /// <summary>
    /// IMGUI overlay: crosshair, hotbar with block icons, status line, and a
    /// paused/controls panel while the cursor is unlocked. Also owns the
    /// Escape / click-to-resume cursor lock flow.
    /// </summary>
    public class Hud : MonoBehaviour
    {
        public Game game;
        public BlockInteraction interaction;

        private int fps;
        private int frames;
        private float fpsWindow;

        private GUIStyle labelStyle;
        private GUIStyle boxStyle;
        private GUIStyle titleStyle;
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
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
            else if (Cursor.lockState == CursorLockMode.None && Input.GetMouseButtonDown(0))
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
        }

        private void OnGUI()
        {
            BuildStyles();
            if (game == null || interaction == null)
            {
                return;
            }

            if (Cursor.lockState == CursorLockMode.Locked)
            {
                DrawCrosshair();
                DrawHotbar();
                DrawStatus();
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
            GUI.Label(new Rect(x0, y0 - 24f, total + 200f, 20f), BlockDatabase.Get(selected).name + pageNote, labelStyle);
            GUI.color = prev;

            for (int i = 0; i < count; i++)
            {
                Rect rect = new Rect(x0 + i * (slot + gap), y0, slot, slot);
                GUI.Box(rect, GUIContent.none, boxStyle);
                if (game.atlas.icons.TryGetValue(interaction.hotbar[i], out Texture2D icon))
                {
                    GUI.DrawTexture(new Rect(rect.x + 7f, rect.y + 7f, slot - 14f, slot - 14f), icon);
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
            string text =
                $"FPS {fps}   Chunks {chunks}\n" +
                $"XYZ {pos.x:F1} {pos.y:F1} {pos.z:F1}   Seed {game.seed}\n" +
                (fly ? "Flying (F to toggle)" : "Walking (F to fly)");
            GUI.Box(new Rect(8f, 8f, 250f, 62f), GUIContent.none, boxStyle);
            GUI.Label(new Rect(14f, 12f, 240f, 56f), text, labelStyle);
        }

        private void DrawPausedPanel()
        {
            var prev = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.55f);
            GUI.Box(new Rect(0f, 0f, Screen.width, Screen.height), GUIContent.none, boxStyle);
            GUI.color = prev;

            float w = 460f;
            float h = 300f;
            var rect = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);
            GUI.Box(rect, GUIContent.none, boxStyle);
            GUI.Label(new Rect(rect.x, rect.y + 16f, rect.width, 34f), "PAUSED", titleStyle);
            string help =
                "WASD  Move        Shift  Sprint\n" +
                "Space Jump / fly up   Ctrl  Fly down\n" +
                "F     Toggle fly mode\n" +
                "V     First / third person\n" +
                "Tab   Hotbar page\n" +
                "LMB   Break block (hold)\n" +
                "RMB   Place block\n" +
                "1-9 / Wheel  Select hotbar slot\n" +
                "Esc   Pause / release cursor\n\n" +
                "Click anywhere to resume.";
            GUI.Label(new Rect(rect.x + 30f, rect.y + 64f, rect.width - 60f, rect.height - 80f), help, labelStyle);
        }
    }
}
