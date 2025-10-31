using UnityEngine;
using Photon.Pun;

[RequireComponent(typeof(Camera))]
public class CameraHead : MonoBehaviour
{
    [Header("Head / Neck")]
    public Transform headAnchor; // assign an empty Transform under Player at neck/head
    public Vector3 targetLocalPosition = Vector3.zero; // desired camera offset relative to headAnchor (usually (0,0,0))
    public float smoothTime = 0.03f;

    [Header("Collision")]
    public LayerMask obstructionMask = ~0; // layers that can obstruct camera
    public float sphereCastRadius = 0.18f; // radius for head spherecast
    public float collisionOffset = 0.05f;  // keep a little distance from the hit surface

    Vector3 currentVelocity = Vector3.zero;
    PhotonView parentPhotonView;
    Camera cam;
    AudioListener audioListener;

    void Awake()
    {
        parentPhotonView = GetComponentInParent<PhotonView>();
        cam = GetComponent<Camera>();
        audioListener = GetComponent<AudioListener>();
    }

    void Start()
    {
        bool isMine = parentPhotonView == null ? true : parentPhotonView.IsMine;

        if (!isMine)
        {
            // Remote players: disable camera (and audio listener) so they don't render
            if (cam != null) cam.enabled = false;
            if (audioListener != null) audioListener.enabled = false;

            // Also disable this script for remote players
            enabled = false;
            return;
        }

        // Local player: keep camera enabled (already enabled by default)
        if (cam != null) cam.enabled = true;
        if (audioListener != null) audioListener.enabled = true;
    }

    void LateUpdate()
    {
        if (headAnchor == null) return;

        Vector3 desiredWorld = headAnchor.TransformPoint(targetLocalPosition);
        Vector3 dir = desiredWorld - headAnchor.position;
        float dist = dir.magnitude;
        Vector3 desiredPos = desiredWorld;

        if (dist > 0.0001f)
        {
            RaycastHit hit;
            Vector3 rayDir = dir.normalized;
            if (Physics.SphereCast(headAnchor.position, sphereCastRadius, rayDir, out hit, dist, obstructionMask, QueryTriggerInteraction.Ignore))
            {
                desiredPos = hit.point - rayDir * collisionOffset;
            }
        }

        transform.position = Vector3.SmoothDamp(transform.position, desiredPos, ref currentVelocity, smoothTime);
    }

    void OnDrawGizmosSelected()
    {
        if (headAnchor == null) return;
        Gizmos.color = Color.green;
        Vector3 desiredWorld = headAnchor.TransformPoint(targetLocalPosition);
        Gizmos.DrawLine(headAnchor.position, desiredWorld);
        Gizmos.DrawWireSphere(desiredWorld, sphereCastRadius);
    }
}
