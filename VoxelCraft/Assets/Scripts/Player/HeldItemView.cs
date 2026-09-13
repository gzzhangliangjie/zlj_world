using UnityEngine;
using VoxelCraft.Art;
using VoxelCraft.Core;
using VoxelCraft.Items;

namespace VoxelCraft.Player
{
    /// <summary>
    /// The held-item view: shows the active tool (or the selected block while
    /// the hand tool is active) as a 3D pixel model in front of the camera
    /// (first person) and on the third-person model's right hand. Swings when
    /// the player uses the item, bobs while walking, and follows the V-key
    /// view mode.
    /// </summary>
    public class HeldItemView : MonoBehaviour
    {
        public BlockInteraction interaction;
        public Camera viewCamera;
        public ThirdPersonRig rig;
        public TextureFactory.AtlasResult atlas;

        public bool AnchorsReady
        {
            get { return fpAnchor != null && tpAnchor != null; }
        }

        private Transform fpAnchor;   // child of the camera
        private Transform tpAnchor;   // child of the third-person right hand
        private GameObject shown;
        private ToolType lastTool = ToolType.Hand;
        private BlockType lastBlock = BlockType.Air;
        private bool lastShowTool;
        private bool lastTp;
        private float swingT = 60f;   // seconds since swing start; SwingTime = done
        private float bobPhase;
        private Vector3 lastPlayerPos;
        private bool posSeeded;

        private const float SwingTime = 0.26f;
        private static readonly Vector3 FpPos = new Vector3(0.44f, -0.37f, 0.62f);
        private static readonly Quaternion FpRot = Quaternion.Euler(-14f, 26f, -8f);
        private static readonly Vector3 TpPos = new Vector3(0.02f, 0.04f, 0.1f);
        private static readonly Quaternion TpRot = Quaternion.Euler(-60f, 60f, 20f);

        /// <summary>Creates the two anchors. Call once after the refs are set.</summary>
        public void Build()
        {
            fpAnchor = new GameObject("HeldItemFP").transform;
            if (viewCamera != null)
            {
                fpAnchor.SetParent(viewCamera.transform, false);
            }
            fpAnchor.localPosition = FpPos;
            fpAnchor.localRotation = FpRot;

            tpAnchor = new GameObject("HeldItemTP").transform;
            if (rig != null && rig.hand != null)
            {
                tpAnchor.SetParent(rig.hand, false);
                tpAnchor.localPosition = TpPos;
                tpAnchor.localRotation = TpRot;
            }
        }

        /// <summary>Triggered by BlockInteraction.OnUse.</summary>
        public void Swing()
        {
            swingT = 0f;
        }

        private void Update()
        {
            if (interaction == null || fpAnchor == null)
            {
                return;
            }

            bool tp = rig != null && rig.IsThirdPerson && rig.hand != null;
            ToolType tool = interaction.currentTool;
            BlockType block = interaction.SelectedBlock;
            bool showTool = tool != ToolType.Hand;
            bool changed = tp != lastTp || showTool != lastShowTool
                || (showTool ? tool != lastTool : block != lastBlock);
            if (changed)
            {
                Show(showTool ? ItemModelFactory.BuildTool(tool) : ItemModelFactory.BuildBlock(atlas, block), tp);
                lastTool = tool;
                lastBlock = block;
                lastShowTool = showTool;
                lastTp = tp;
            }

            // swing decay
            if (swingT < SwingTime)
            {
                swingT += Time.deltaTime;
            }
            float s = swingT < SwingTime ? Mathf.Sin((swingT / SwingTime) * Mathf.PI) : 0f;

            // walk bob, driven by the player's real horizontal speed
            Vector3 playerPos = interaction.transform.position;
            if (!posSeeded)
            {
                lastPlayerPos = playerPos;
                posSeeded = true;
            }
            float speed = (playerPos - lastPlayerPos).magnitude / Mathf.Max(Time.deltaTime, 0.0001f);
            lastPlayerPos = playerPos;
            bobPhase += Time.deltaTime * (3f + speed * 1.7f);
            float bob = Mathf.Sin(bobPhase) * Mathf.Min(speed * 0.004f, 0.02f);

            fpAnchor.localPosition = FpPos + new Vector3(0f, bob - s * 0.05f, -s * 0.04f);
            fpAnchor.localRotation = FpRot * Quaternion.Euler(-s * 55f, 0f, -s * 8f);
            tpAnchor.localPosition = TpPos + new Vector3(0f, -s * 0.06f, 0f);
            tpAnchor.localRotation = TpRot * Quaternion.Euler(-s * 70f, 0f, 0f);
        }

        private void Show(GameObject model, bool tp)
        {
            if (shown != null)
            {
                shown.SetActive(false);
                shown = null;
            }
            if (model == null)
            {
                return;
            }
            var anchor = tp && rig != null && rig.hand != null ? tpAnchor : fpAnchor;
            model.transform.SetParent(anchor, false);
            model.transform.localPosition = Vector3.zero;
            model.transform.localRotation = Quaternion.identity;
            model.SetActive(true);
            shown = model;
        }
    }
}
