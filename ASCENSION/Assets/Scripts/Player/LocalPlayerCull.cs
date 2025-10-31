using UnityEngine;
using Photon.Pun;

[RequireComponent(typeof(PhotonView))]
public class LocalLayerCullSafe : MonoBehaviour
{
    [Header("Assign in Inspector (explicit)")]
    [Tooltip("Root of the third-person body (only the meshes you want hidden from the local player).")]
    public Transform bodyRoot;

    [Tooltip("Head transform. Useful if you want to hide the head as well (optional).")]
    public Transform headRoot;

    [Header("Layer / Behavior")]
    [Tooltip("Layer name to move the hidden parts onto. Create it in Tags & Layers first.")]
    public string hideLayerName = "InvisibleToLocal";

    [Tooltip("If true, headRoot will also be moved to the hide layer. Otherwise head stays visible.")]
    public bool hideHeadToo = false;

    PhotonView pv;
    Camera localCam;
    int prevCullingMask;
    bool cullingModified = false;

    void Awake()
    {
        pv = GetComponent<PhotonView>();
    }

    void Start()
    {
        // Only run on the local player's instance
        if (!pv.IsMine) return;

        // Fallback find if inspector fields left empty
        if (bodyRoot == null)
        {
            var t = transform.Find("Body");
            if (t != null) bodyRoot = t;
            else bodyRoot = transform; // fallback — be careful not to assign the whole player if you don't want that.
        }

        if (headRoot == null)
        {
            // common head names; adjust to your prefab naming
            var h = transform.Find("Head");
            if (h == null) h = transform.Find("head");
            if (h != null) headRoot = h;
            // else leave null if not found
        }

        int layer = LayerMask.NameToLayer(hideLayerName);
        if (layer == -1)
        {
            Debug.LogWarning($"LocalLayerCullSafe: Layer '{hideLayerName}' not found. Create it in Project Settings > Tags & Layers.");
            return;
        }

        // Apply layer only to the chosen parts (not whole player unless that's what you assigned)
        if (bodyRoot != null)
            SetLayerRecursivelySafe(bodyRoot, layer);

        if (hideHeadToo && headRoot != null)
            SetLayerRecursivelySafe(headRoot, layer);

        // Only change the local camera's culling mask (store previous so we can restore)
        localCam = Camera.main;
        if (localCam != null)
        {
            prevCullingMask = localCam.cullingMask;
            localCam.cullingMask &= ~(1 << layer);
            cullingModified = true;
            Debug.Log($"LocalLayerCullSafe: removed layer '{hideLayerName}' from local camera culling mask.");
        }
        else
        {
            Debug.LogWarning("LocalLayerCullSafe: Camera.main is null. Local camera culling was not modified.");
        }
    }

    // Restore the camera culling mask if we changed it
    void OnDisable()
    {
        RestoreCameraCulling();
    }

    void OnDestroy()
    {
        RestoreCameraCulling();
    }

    void RestoreCameraCulling()
    {
        if (cullingModified && localCam != null)
        {
            localCam.cullingMask = prevCullingMask;
            cullingModified = false;
            Debug.Log("LocalLayerCullSafe: restored camera culling mask.");
        }
    }

    // Safety: skip cameras/audio listeners and UI canvases while changing layers
    void SetLayerRecursivelySafe(Transform root, int layer)
    {
        if (root == null) return;

        // Skip if this object holds a Camera/AudioListener/Canvas — likely not part of the mesh
        if (root.GetComponent<Camera>() != null || root.GetComponent<AudioListener>() != null || root.GetComponent<Canvas>() != null)
            return;

        root.gameObject.layer = layer;
        foreach (Transform child in root)
        {
            SetLayerRecursivelySafe(child, layer);
        }
    }
}
