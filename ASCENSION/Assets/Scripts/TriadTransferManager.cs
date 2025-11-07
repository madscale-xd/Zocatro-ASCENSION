// TriadTransferManager.cs
using System.Collections;
using UnityEngine;
using Photon.Pun;
using ExitGames.Client.Photon;
using UnityEngine.SceneManagement;

/// <summary>
/// Manager that can (A) lock triad locally (write PlayerPrefs + LocalPlayer custom props),
/// and (B) lock+load (used by master in some flows). The important method LockTriadLocal()
/// now *returns* the triad it wrote so callers (RPC handlers) can report it quickly.
///
/// New behavior:
/// - If a room-level authoritative mapping "triad_<actor>" exists, use that as authoritative.
///   Do NOT overwrite that mapping from the client side.
/// - When master receives reports, it will write the room mapping only if none exists.
/// - LockTriadLocal will persist the authoritative triad into LocalPlayer props and PlayerPrefs
///   so subsequent runtime code reads the same authoritative values.
/// </summary>
public class TriadTransferManager : MonoBehaviourPun
{
    public static TriadTransferManager Instance { get; private set; }

    [Tooltip("Seconds to wait for LocalPlayer custom props to appear before falling back to PlayerPrefs")]
    public float syncTimeout = 2.0f;

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this.gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(this.gameObject);
    }

    public static TriadTransferManager EnsureInstance()
    {
        if (Instance != null) return Instance;
        var go = new GameObject("TriadTransferManager");
        Instance = go.AddComponent<TriadTransferManager>();
        DontDestroyOnLoad(go);
        return Instance;
    }

    /// <summary>
    /// Read authoritative triad mapping from the room for the given actorNumber.
    /// Returns int[3] or null if none exists.
    /// </summary>
    public int[] GetAuthoritativeTriadFromRoom(int actorNumber)
    {
        if (!PhotonNetwork.InRoom || PhotonNetwork.CurrentRoom == null) return null;
        string key = "triad_" + actorNumber;
        var roomProps = PhotonNetwork.CurrentRoom.CustomProperties;
        if (roomProps != null && roomProps.TryGetValue(key, out object obj))
        {
            // parse object into int[3]
            int a = -1, b = -1, c = -1;
            if (obj is object[] oarr)
            {
                if (oarr.Length > 0) int.TryParse(oarr[0]?.ToString() ?? "-1", out a);
                if (oarr.Length > 1) int.TryParse(oarr[1]?.ToString() ?? "-1", out b);
                if (oarr.Length > 2) int.TryParse(oarr[2]?.ToString() ?? "-1", out c);
            }
            else if (obj is int[] iarr)
            {
                if (iarr.Length > 0) a = iarr[0];
                if (iarr.Length > 1) b = iarr[1];
                if (iarr.Length > 2) c = iarr[2];
            }
            else if (obj is long[] larr)
            {
                if (larr.Length > 0) a = (int)larr[0];
                if (larr.Length > 1) b = (int)larr[1];
                if (larr.Length > 2) c = (int)larr[2];
            }
            else
            {
                // fallback: parse string
                var s = obj.ToString();
                if (!string.IsNullOrEmpty(s))
                {
                    var parts = s.Split(',');
                    if (parts.Length > 0) int.TryParse(parts[0], out a);
                    if (parts.Length > 1) int.TryParse(parts[1], out b);
                    if (parts.Length > 2) int.TryParse(parts[2], out c);
                }
            }

            return new int[] { a, b, c };
        }
        return null;
    }

    /// <summary>
    /// Lock triad on this local client: read triad from CharacterSelector in the active scene (Room),
    /// or if the room contains an authoritative mapping for this actor, use that. Persist into
    /// PlayerPrefs + LocalPlayer custom props (if connected). Returns the triad as an int[3].
    /// This is synchronous — used when master tells clients to lock locally via RPC.
    /// </summary>
    public int[] LockTriadLocal()
    {
        int localActor = -1;
        if (PhotonNetwork.IsConnected && PhotonNetwork.LocalPlayer != null)
            localActor = PhotonNetwork.LocalPlayer.ActorNumber;

        // 1) If the room already has an authoritative triad for this actor, use that (do not attempt to overwrite room props).
        if (localActor >= 0 && PhotonNetwork.InRoom && PhotonNetwork.CurrentRoom != null)
        {
            int[] roomTri = GetAuthoritativeTriadFromRoom(localActor);
            if (roomTri != null)
            {
                // persist locally so LocalPlayer props/PlayerPrefs reflect authoritative triad
                PersistTriadLocally(roomTri, -1, null);
                Debug.Log($"TriadTransferManager.LockTriadLocal: authoritative room triad found for actor {localActor} -> ({roomTri[0]},{roomTri[1]},{roomTri[2]}). Using it locally.");
                return roomTri;
            }
        }

        // 2) Otherwise, attempt to read triad from local CharacterSelector (if present)
        CharacterSelectorUIButtonPhoton selector = FindObjectOfType<CharacterSelectorUIButtonPhoton>();
        int[] tri = null;
        int chosenIndex = -1;
        string prefabName = null;

        if (selector != null)
        {
            tri = selector.GetTriadIndices();
            chosenIndex = selector.GetCurrentIndex();
            // Try to read prefab name mapping if available (best-effort)
            try
            {
                var field = typeof(CharacterSelectorUIButtonPhoton).GetField("prefabResourceNames", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
                if (field != null)
                {
                    var list = field.GetValue(selector) as System.Collections.IList;
                    if (list != null && chosenIndex >= 0 && chosenIndex < list.Count)
                        prefabName = list[chosenIndex] as string;
                }
            }
            catch { /* ignore */ }
        }

        // fallback: try PlayerPrefs if selector not present or tri is null
        if (tri == null)
        {
            if (PlayerPrefs.HasKey(PhotonKeys.PREF_KEY_TRIAD))
            {
                string triCsv = PlayerPrefs.GetString(PhotonKeys.PREF_KEY_TRIAD, null);
                if (!string.IsNullOrEmpty(triCsv))
                {
                    var parts = triCsv.Split(',');
                    tri = new int[3] { -1, -1, -1 };
                    if (parts.Length >= 1) int.TryParse(parts[0], out tri[0]);
                    if (parts.Length >= 2) int.TryParse(parts[1], out tri[1]);
                    if (parts.Length >= 3) int.TryParse(parts[2], out tri[2]);
                }
            }
        }

        // final fallback: generate a unique-ish triad locally (shouldn't normally happen)
        if (tri == null)
        {
            tri = new int[3] { Random.Range(0, 10), Random.Range(0, 10), Random.Range(0, 10) };
            if (tri[1] == tri[0]) tri[1] = (tri[1] + 1) % 10;
            if (tri[2] == tri[0] || tri[2] == tri[1]) tri[2] = (tri[2] + 2) % 10;
            Debug.LogWarning($"TriadTransferManager.LockTriadLocal: No selector or PlayerPrefs triad found — generated local fallback ({tri[0]},{tri[1]},{tri[2]}).");
        }

        // Persist the triad locally (PlayerPrefs + LocalPlayer props)
        PersistTriadLocally(tri, chosenIndex, prefabName);
        Debug.Log($"TriadTransferManager.LockTriadLocal: local triad written ({tri[0]},{tri[1]},{tri[2]}) idx={chosenIndex}");

        return tri;
    }

    /// <summary>
    /// Persist triad to PlayerPrefs and LocalPlayer custom properties (if connected).
    /// This does NOT write room-level mapping — only master writes room-level mapping.
    /// </summary>
    private void PersistTriadLocally(int[] tri, int chosenIndex, string prefabName)
    {
        if (tri == null || tri.Length < 3) return;

        PlayerPrefs.SetString(PhotonKeys.PREF_KEY_TRIAD, $"{tri[0]},{tri[1]},{tri[2]}");
        if (chosenIndex >= 0) PlayerPrefs.SetInt(PhotonKeys.PREF_CHARACTER_INDEX, chosenIndex);
        if (!string.IsNullOrEmpty(prefabName)) PlayerPrefs.SetString(PhotonKeys.PREF_CHARACTER_PREFAB, prefabName);
        PlayerPrefs.Save();

        // If Photon connected, set LocalPlayer custom props to mirror authoritative local triad
        if (PhotonNetwork.IsConnected && PhotonNetwork.LocalPlayer != null)
        {
            object[] triObj = new object[] { tri[0], tri[1], tri[2] };
            ExitGames.Client.Photon.Hashtable props = new ExitGames.Client.Photon.Hashtable
            {
                { PhotonKeys.PROP_TRIAD, triObj }
            };
            if (chosenIndex >= 0) props[PhotonKeys.PROP_CHARACTER_INDEX] = chosenIndex;
            if (!string.IsNullOrEmpty(prefabName)) props[PhotonKeys.PROP_CHARACTER_PREFAB] = prefabName;

            PhotonNetwork.LocalPlayer.SetCustomProperties(props);
        }
    }

    /// <summary>
    /// Legacy helper: lock triad locally then load the scene (call this only on master if you want a combined operation).
    /// Kept for compatibility but server-driven multi-client flow uses RPC_RequestLockTriadLocal + RPC_ReportTriadLocked instead.
    /// </summary>
    public void LockTriadAndLoad(string sceneName, bool usePhotonLoad = false)
    {
        StartCoroutine(LockTriadAndLoadRoutine(sceneName, usePhotonLoad));
    }

    private IEnumerator LockTriadAndLoadRoutine(string sceneName, bool usePhotonLoad)
    {
        int[] tri = LockTriadLocal();

        // Wait a tiny bit for props to propagate locally
        float waitUntil = Time.realtimeSinceStartup + 0.15f;
        while (Time.realtimeSinceStartup < waitUntil) yield return null;

        if (usePhotonLoad && PhotonNetwork.IsConnected)
            PhotonNetwork.LoadLevel(sceneName);
        else
            SceneManager.LoadScene(sceneName);
    }
}
