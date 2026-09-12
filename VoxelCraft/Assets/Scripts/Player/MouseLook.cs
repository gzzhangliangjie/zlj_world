using UnityEngine;

namespace VoxelCraft.Player
{
    /// <summary>
    /// Pointer-locked mouse look: yaw on the player body, pitch on the head pivot.
    /// Attached to the camera object; inactive while the cursor is unlocked.
    /// </summary>
    public class MouseLook : MonoBehaviour
    {
        public Transform yawTransform;
        public Transform pitchTransform;
        public float sensitivity = 2.2f;

        private float pitch;

        private void Update()
        {
            if (Cursor.lockState != CursorLockMode.Locked)
            {
                return;
            }
            float mx = Input.GetAxis("Mouse X") * sensitivity;
            float my = Input.GetAxis("Mouse Y") * sensitivity;
            if (yawTransform != null)
            {
                yawTransform.Rotate(0f, mx, 0f);
            }
            pitch = Mathf.Clamp(pitch - my, -89f, 89f);
            if (pitchTransform != null)
            {
                pitchTransform.localRotation = Quaternion.Euler(pitch, 0f, 0f);
            }
        }
    }
}
