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

        /// <summary>Non-null while riding a DrivableVehicle: player input is
        /// forwarded to the vehicle, the character controller is disabled.</summary>
        public Creatures.DrivableVehicle riding;

        private void Awake()
        {
            controller = GetComponent<CharacterController>();
        }

        /// <summary>Mount/dismount helper. Exiting places the player beside the vehicle.</summary>
        public void Ride(Creatures.DrivableVehicle vehicle)
        {
            if (vehicle == null || riding != null) return;
            riding = vehicle;
            if (!vehicle.Enter(transform)) { riding = null; return; }
            controller.enabled = false;
            verticalVelocity = 0f;
        }

        public void Dismount()
        {
            if (riding == null) return;
            var v = riding;
            riding = null;
            v.Exit();
            controller.enabled = true;
            // step out sideways, on top of ground
            Vector3 outPos = v.transform.position + v.transform.right * v.seatExitOffset;
            int wx = Mathf.FloorToInt(outPos.x), wz = Mathf.FloorToInt(outPos.z);
            int g = world != null && world.sim != null ? world.sim.SurfaceHeight(wx, wz, true) : Mathf.FloorToInt(v.transform.position.y);
            transform.position = new Vector3(outPos.x, g + 1.05f, outPos.z);
            verticalVelocity = 0f;
        }

        public void ToggleFly()
        {
            flying = !flying;
            verticalVelocity = 0f;
        }

        private void Update()
        {
            // Riding: input goes to the vehicle, not the character.
            if (riding != null)
            {
                if (Input.GetKeyDown(KeyCode.F)) Dismount();
                transform.position = riding.Seat.position;
                transform.rotation = Quaternion.Euler(0f, riding.transform.eulerAngles.y, 0f);
                return;
            }
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
