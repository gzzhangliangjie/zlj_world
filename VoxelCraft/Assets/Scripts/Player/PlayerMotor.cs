using UnityEngine;
using VoxelCraft.Core;
using VoxelCraft.World;

namespace VoxelCraft.Player
{
    /// <summary>
    /// First-person character movement over a CharacterController:
    /// walk/sprint/jump, toggleable flight (F), and simple swimming.
    /// Freezes while the cursor is unlocked (menu state owned by Hud/Game).
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public class PlayerMotor : MonoBehaviour
    {
        public float walkSpeed = 4.3f;
        public float sprintSpeed = 7f;
        public float flySpeed = 11f;
        public float jumpSpeed = 8f;      // jump height = v^2 / 2g ~ 1.28 blocks
        public float gravity = 25f;
        public float swimGravity = 4f;
        public float swimUpSpeed = 4.5f;
        public float swimMaxSink = 3f;

        public Transform head;
        public WorldRoot world;

        private CharacterController controller;
        private bool flying;
        private float verticalVelocity;

        public bool IsFlying => flying;

        private void Awake()
        {
            controller = GetComponent<CharacterController>();
        }

        public void ToggleFly()
        {
            flying = !flying;
            verticalVelocity = 0f;
        }

        private void Update()
        {
            if (Cursor.lockState != CursorLockMode.Locked)
            {
                return;
            }

            float dt = Time.deltaTime;
            float horizontal = Input.GetAxis("Horizontal");
            float forward = Input.GetAxis("Vertical");
            bool sprint = Input.GetKey(KeyCode.LeftShift);
            bool ascend = Input.GetKey(KeyCode.Space);
            bool descend = Input.GetKey(KeyCode.LeftControl);

            bool inWater = InWater();

            Vector3 wish = transform.right * horizontal + transform.forward * forward;
            wish = Vector3.ClampMagnitude(wish, 1f);
            float speed = flying ? flySpeed : sprint ? sprintSpeed : walkSpeed;
            if (inWater && !flying)
            {
                speed *= 0.6f;
            }
            Vector3 motion = wish * speed;

            if (flying)
            {
                verticalVelocity = 0f;
                if (ascend)
                {
                    motion.y += flySpeed;
                }
                if (descend)
                {
                    motion.y -= flySpeed;
                }
            }
            else if (inWater)
            {
                float v = verticalVelocity - swimGravity * dt;
                if (ascend)
                {
                    v = swimUpSpeed;
                }
                v = Mathf.Max(v, -swimMaxSink);
                verticalVelocity = v;
                motion.y += v;
            }
            else
            {
                float v = verticalVelocity - gravity * dt;
                if (controller.isGrounded)
                {
                    v = ascend ? jumpSpeed : -2f;
                }
                verticalVelocity = v;
                motion.y += v;
            }

            controller.Move(motion * dt);
        }

        private bool InWater()
        {
            if (world == null || world.sim == null)
            {
                return false;
            }
            var p = transform.position;
            var block = world.sim.GetBlock(
                Mathf.FloorToInt(p.x),
                Mathf.FloorToInt(p.y + 0.4f),
                Mathf.FloorToInt(p.z));
            return BlockDatabase.IsLiquid(block);
        }
    }
}
