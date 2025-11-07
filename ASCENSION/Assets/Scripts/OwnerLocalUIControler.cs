using System.Collections;
using UnityEngine;
using Photon.Pun;
using Photon.Realtime;

/// <summary>
/// OwnerLocalUIController
/// Attach to your player root (same object as PhotonView / CharacterSkills / TarotSelection).
/// Purpose: ensure "GameUI" / HUD objects are active only on the owning client (local effect only).
/// - On start: activates assigned UI roots only if this client owns the player (or if offline).
/// - On remote clients the UI roots are deactivated (local-only visuals).
/// - Provides an RPC the owner can receive to toggle their UI (useful for external triggers).
/// </summary>
[RequireComponent(typeof(PhotonView))]
public class OwnerLocalUIController : MonoBehaviourPun
{
    [Header("UI Roots (owner-only)")]
    [Tooltip("GameObjects to enable only on the owner client (leave empty to auto-find child named 'GameUI' or canvases).")]
    public GameObject[] uiRoots;

    [Header("Auto-Find Options")]
    [Tooltip("If true and uiRoots is empty, tries to auto-find a child GameObject named 'GameUI'.")]
    public bool autoFindByName = true;
    [Tooltip("If true and uiRoots is still empty, will gather Canvas components under this player and use their GameObjects.")]
    public bool autoFindCanvases = true;

    [Header("Behavior")]
    [Tooltip("If true, initial activation is applied automatically on Start (deferred one frame).")]
    public bool applyAtStart = true;

    // cached photon view
    private PhotonView pv;

    void Awake()
    {
        pv = GetComponent<PhotonView>();
    }

    void Start()
    {
        if (!applyAtStart) return;
        // Defer one frame to allow other components (that might clone UI etc.) to finish Awake.
        StartCoroutine(DeferredInitialApply());
    }

    IEnumerator DeferredInitialApply()
    {
        yield return null; // wait one frame
        EnsureUiRootsExist();
        ApplyOwnerLocalState();
    }

    /// <summary>
    /// Finds UI roots automatically if the inspector list is empty.
    /// </summary>
    private void EnsureUiRootsExist()
    {
        if (uiRoots != null && uiRoots.Length > 0)
            return;

        // Try to find exact-named child "GameUI" (recursive)
        if (autoFindByName)
        {
            Transform found = FindChildRecursive(transform, "GameUI");
            if (found != null)
            {
                uiRoots = new GameObject[] { found.gameObject };
                return;
            }
        }

        // Fallback: collect Canvas children under this transform
        if (autoFindCanvases)
        {
            var canvases = GetComponentsInChildren<Canvas>(true);
            if (canvases != null && canvases.Length > 0)
            {
                uiRoots = new GameObject[canvases.Length];
                for (int i = 0; i < canvases.Length; i++) uiRoots[i] = canvases[i].gameObject;
                return;
            }
        }

        // Still nothing: leave uiRoots empty (script will be no-op)
    }

    /// <summary>
    /// Sets UI GameObjects active only on the local owner (or if offline).
    /// </summary>
    public void ApplyOwnerLocalState()
    {
        bool isLocalOwner = (pv == null) || !PhotonNetwork.InRoom || pv.IsMine;

        if (uiRoots == null || uiRoots.Length == 0)
        {
            Debug.LogWarning($"[OwnerLocalUIController] No uiRoots configured / found on '{name}'. Nothing to toggle.");
            return;
        }

        for (int i = 0; i < uiRoots.Length; i++)
        {
            var go = uiRoots[i];
            if (go == null) continue;
            // local-only: we only change activation on the local client; this does not network any state.
            go.SetActive(isLocalOwner);
        }
    }

    /// <summary>
    /// Public helper to explicitly set UI active/disabled on the owner client.
    /// If called locally on owner it toggles immediately; if called on another client it can ask the owner via RPC.
    /// </summary>
    /// <param name="active">desired active state</param>
    /// <param name="askOwnerWhenRemote">if true and this is not the owner, send an RPC to the owner asking them to change UI</param>
    public void SetOwnerUIActive(bool active, bool askOwnerWhenRemote = false)
    {
        bool isLocalOwner = (pv == null) || !PhotonNetwork.InRoom || pv.IsMine;
        if (isLocalOwner)
        {
            // We are the owner or offline: set now
            if (uiRoots == null || uiRoots.Length == 0) EnsureUiRootsExist();
            if (uiRoots != null)
            {
                foreach (var go in uiRoots) if (go != null) go.SetActive(active);
            }
        }
        else
        {
            // Not owner: optionally request the owner to toggle their UI (owner-only RPC)
            if (askOwnerWhenRemote && pv != null && pv.Owner != null)
            {
                try
                {
                    pv.RPC(nameof(RPC_SetLocalUIActive), pv.Owner, active);
                }
                catch
                {
                    // ignore network errors
                }
            }
            else
            {
                // Otherwise just ensure our local copy remains deactivated
                if (uiRoots != null)
                {
                    foreach (var go in uiRoots) if (go != null) go.SetActive(false);
                }
            }
        }
    }

    /// <summary>
    /// RPC executed on the owner client to toggle local UI roots.
    /// Targeting this RPC at the owner ensures it runs only on the owner's client.
    /// </summary>
    /// <param name="active"></param>
    [PunRPC]
    public void RPC_SetLocalUIActive(bool active)
    {
        // Only the owner should actually do the toggle locally.
        bool isLocalOwner = (pv == null) || !PhotonNetwork.InRoom || pv.IsMine;
        if (!isLocalOwner) return;

        if (uiRoots == null || uiRoots.Length == 0) EnsureUiRootsExist();
        if (uiRoots != null)
        {
            foreach (var go in uiRoots) if (go != null) go.SetActive(active);
        }
    }

    /// <summary>
    /// Utility: recursive child search by exact name (case-sensitive).
    /// </summary>
    private Transform FindChildRecursive(Transform parent, string name)
    {
        if (parent == null) return null;
        foreach (Transform child in parent)
        {
            if (child.name == name) return child;
            var deeper = FindChildRecursive(child, name);
            if (deeper != null) return deeper;
        }
        return null;
    }

    // Editor / debug helper
#if UNITY_EDITOR
    private void OnValidate()
    {
        // do not automatically change runtime state in editor unless play mode
        if (!Application.isPlaying) return;
        // keep behaviour consistent in play mode
        StartCoroutine(DelayedValidateApply());
    }

    IEnumerator DelayedValidateApply()
    {
        yield return null;
        ApplyOwnerLocalState();
    }
#endif
}
