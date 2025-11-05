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
    /// Lock triad on this local client: read triad from CharacterSelector in the active scene (Room),
    /// write PlayerPrefs fallback and Photon LocalPlayer custom properties (if connected).
    /// Returns the triad as an int[3] (values -1 if missing).
    /// This is synchronous — used when master tells clients to lock locally via RPC.
    /// </summary>
    public int[] LockTriadLocal()
    {
        // Find CharacterSelector in the active (room) scene
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
        }

        // Persist to PlayerPrefs (local fallback)
        PlayerPrefs.SetString(PhotonKeys.PREF_KEY_TRIAD, $"{tri[0]},{tri[1]},{tri[2]}");
        if (chosenIndex >= 0) PlayerPrefs.SetInt(PhotonKeys.PREF_CHARACTER_INDEX, chosenIndex);
        if (!string.IsNullOrEmpty(prefabName)) PlayerPrefs.SetString(PhotonKeys.PREF_CHARACTER_PREFAB, prefabName);
        PlayerPrefs.Save();

        // If Photon connected, set LocalPlayer custom props
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
            Debug.Log($"TriadTransferManager.LockTriadLocal: local triad written ({tri[0]},{tri[1]},{tri[2]}) idx={chosenIndex}");
        }
        else
        {
            Debug.Log($"TriadTransferManager.LockTriadLocal: wrote PlayerPrefs triad ({tri[0]},{tri[1]},{tri[2]}); Photon not connected or local player missing.");
        }

        return tri;
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
