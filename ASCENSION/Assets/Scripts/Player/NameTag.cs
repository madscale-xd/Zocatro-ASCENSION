using UnityEngine;
using Photon.Pun;
using TMPro;

[RequireComponent(typeof(PhotonView))]
public class NameTag : MonoBehaviour
{
    [Header("UI (optional)")]
    [Tooltip("Optional: drag a TextMeshPro (3D) component here. If left empty the script will create one.")]
    public TextMeshPro uiText;

    [Header("Placement")]
    [Tooltip("Local offset from the player root where the name will appear (usually above head).")]
    public Vector3 localOffset = new Vector3(0f, 2.1f, 0f);

    [Tooltip("Scale for the auto-created TextMeshPro (3D) object.")]
    public float textScale = 0.02f;

    [Header("Text appearance")]
    [Tooltip("Base font size for the 3D text (mesh units).")]
    public float fontSize = 3f;

    [Header("Behavior")]
    [Tooltip("Whether to show the local player's name above their own head.")]
    public bool showLocalPlayerName = true;

    [Tooltip("Distance alpha fade start (meters) — set 0 to disable fading.")]
    public float fadeStartDistance = 10f;
    [Tooltip("Distance alpha fade end (meters) — at this distance name is invisible.")]
    public float fadeEndDistance = 25f;

    PhotonView pv;
    Camera mainCam;
    string currentName = "";

    void Awake()
    {
        pv = GetComponent<PhotonView>();
        // avoid repeated Camera.main calls; fallback to first camera if null
        mainCam = Camera.main;
        if (mainCam == null && Camera.allCamerasCount > 0)
            mainCam = Camera.allCameras[0];
    }

    void Start()
    {
        // If no uiText assigned, create a 3D TextMeshPro child
        if (uiText == null)
            Create3DTextMeshPro();

        UpdateNameImmediate();
    }

    void Update()
    {
        if (uiText == null) return;

        // keep local offset (parent may be root transform or object containing the mesh)
        uiText.transform.localPosition = localOffset;
        uiText.transform.localScale = Vector3.one * textScale;

        // Billboard: face the camera
        if (mainCam != null)
        {
            Vector3 dir = uiText.transform.position - mainCam.transform.position;
            if (dir.sqrMagnitude > 0.000001f)
                uiText.transform.rotation = Quaternion.LookRotation(dir, Vector3.up);
        }

        // Update name if changed (owner name could be set after instantiate)
        string newName = ResolvePlayerName();
        if (newName != currentName)
        {
            currentName = newName;
            uiText.text = currentName;
        }

        // Optionally hide local player's name
        bool isLocal = pv.IsMine;
        uiText.enabled = showLocalPlayerName || !isLocal;

        // Optional distance fade
        if (mainCam != null && fadeStartDistance > 0f && fadeEndDistance > fadeStartDistance)
        {
            float dist = Vector3.Distance(mainCam.transform.position, uiText.transform.position);
            float t = Mathf.InverseLerp(fadeStartDistance, fadeEndDistance, dist);
            float alpha = Mathf.Clamp01(1f - t);
            Color c = uiText.color;
            c.a = alpha;
            uiText.color = c;
        }
    }

    void UpdateNameImmediate()
    {
        if (uiText == null) return;
        currentName = ResolvePlayerName();
        uiText.text = currentName;
    }

    string ResolvePlayerName()
    {
        if (pv != null && pv.Owner != null)
        {
            if (!string.IsNullOrEmpty(pv.Owner.NickName))
                return pv.Owner.NickName;
            return $"Player {pv.Owner.ActorNumber}";
        }

        if (pv != null && pv.IsMine)
        {
            if (!string.IsNullOrEmpty(PhotonNetwork.NickName))
                return PhotonNetwork.NickName;
        }

        return "Player";
    }

    void Create3DTextMeshPro()
    {
        // Create a child GameObject to hold the TextMeshPro mesh
        GameObject textGO = new GameObject("NameText_TMP_3D");
        textGO.transform.SetParent(transform, false);
        textGO.transform.localPosition = localOffset;
        textGO.transform.localRotation = Quaternion.identity;
        textGO.transform.localScale = Vector3.one * textScale;

        // Add TextMeshPro (3D) component
        TextMeshPro tmp = textGO.AddComponent<TextMeshPro>();
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.fontSize = fontSize;
        tmp.enableWordWrapping = false;
        tmp.color = Color.white;
        tmp.raycastTarget = false; // not needed for 3D text but kept for parity
        tmp.GetComponent<Renderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        tmp.GetComponent<Renderer>().receiveShadows = false;

        uiText = tmp;
    }
}
