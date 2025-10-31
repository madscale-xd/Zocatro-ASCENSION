using UnityEngine;
using Photon.Pun;

public class MouseLook : MonoBehaviour
{
    [Header("Mouse Look")]
    public float mouseSensitivity = 100f;
    public Transform playerBody; // assign the Player transform (parent)
    public bool lockCursor = true;
    public float minPitch = -85f;
    public float maxPitch = 85f;

    float xRotation = 0f;
    PhotonView parentPhotonView;

    void Awake()
    {
        // find PhotonView on parents (player root usually has it)
        parentPhotonView = GetComponentInParent<PhotonView>();
    }

    void Start()
    {
        if (playerBody == null)
            Debug.LogWarning("MouseLook: playerBody not assigned. Assign the Player transform.");

        // If we couldn't find a PhotonView, assume single-player / local-only
        bool isMine = parentPhotonView == null ? true : parentPhotonView.IsMine;

        if (!isMine)
        {
            // disable for remote instances
            enabled = false;
            return;
        }

        if (lockCursor)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
    }

    void Update()
    {
        // Only runs for local player (script disabled otherwise)
        float mouseX = Input.GetAxis("Mouse X") * mouseSensitivity * Time.deltaTime;
        float mouseY = Input.GetAxis("Mouse Y") * mouseSensitivity * Time.deltaTime;

        if (playerBody != null)
            playerBody.Rotate(Vector3.up * mouseX);

        xRotation -= mouseY;
        xRotation = Mathf.Clamp(xRotation, minPitch, maxPitch);
        transform.localRotation = Quaternion.Euler(xRotation, 0f, 0f);

        if (Input.GetKeyDown(KeyCode.Escape))
        {
            if (Cursor.lockState == CursorLockMode.Locked)
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
            else
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
        }
    }
}
