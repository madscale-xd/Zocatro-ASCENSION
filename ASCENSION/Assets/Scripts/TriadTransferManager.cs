// TriadTransferManager.cs
using System.Collections;
using UnityEngine;
using Photon.Pun;
using ExitGames.Client.Photon;
using UnityEngine.SceneManagement;

/// <summary>
/// Responsible for locking the triad at the moment you transition Room -> Session.
/// Call LockTriadAndLoad("SessionSceneName", usePhotonLoad: true) from Room when you want to transfer.
/// This will:
/// - read triad from CharacterSelector in the **current scene**,
/// - write it to PlayerPrefs and Photon LocalPlayer custom props,
/// - wait briefly for confirmation (timeout),
/// - then load the session scene.
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

    /// <summary>
    /// Ensure there is an instance (create if missing) and return it.
    /// Use this if you want to call from code but aren't sure an instance was placed in the scene.
    /// </summary>
    public static TriadTransferManager EnsureInstance()
    {
        if (Instance != null) return Instance;
        var go = new GameObject("TriadTransferManager");
        Instance = go.AddComponent<TriadTransferManager>();
        DontDestroyOnLoad(go);
        return Instance;
    }

    /// <summary>
    /// Main entry: lock triad from the active scene (Room) and load sceneName.
    /// If usePhotonLoad=true and Photon is connected, it will call PhotonNetwork.LoadLevel(sceneName).
    /// </summary>
    public void LockTriadAndLoad(string sceneName, bool usePhotonLoad = false)
    {
        if (string.IsNullOrEmpty(sceneName))
        {
            Debug.LogWarning("TriadTransferManager: sceneName empty; aborting.");
            return;
        }
        StartCoroutine(LockTriadAndLoadRoutine(sceneName, usePhotonLoad));
    }

    private IEnumerator LockTriadAndLoadRoutine(string sceneName, bool usePhotonLoad)
    {
        // Find CharacterSelector in the current (Room) scene
        CharacterSelectorUIButtonPhoton selector = FindObjectOfType<CharacterSelectorUIButtonPhoton>();
        int[] tri = null;
        int chosenIndex = -1;
        string prefabName = null;

        if (selector != null)
        {
            tri = selector.GetTriadIndices();
            chosenIndex = selector.GetCurrentIndex();
            // do NOT call selector.SaveSelectionToPhoton() here - we'll explicitly write what we need below
        }

        // fallback to PlayerPrefs or generate if needed
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
            else
            {
                // generate fallback unique triad (safe default)
                tri = new int[3] { Random.Range(0, 10), Random.Range(0, 10), Random.Range(0, 10) };
                if (tri[1] == tri[0]) tri[1] = (tri[1] + 1) % 10;
                if (tri[2] == tri[0] || tri[2] == tri[1]) tri[2] = (tri[2] + 2) % 10;
            }
        }

        if (chosenIndex < 0 && PlayerPrefs.HasKey(PhotonKeys.PREF_CHARACTER_INDEX))
            chosenIndex = PlayerPrefs.GetInt(PhotonKeys.PREF_CHARACTER_INDEX, -1);

        if (PlayerPrefs.HasKey(PhotonKeys.PREF_CHARACTER_PREFAB))
            prefabName = PlayerPrefs.GetString(PhotonKeys.PREF_CHARACTER_PREFAB, null);

        // Persist to PlayerPrefs (local fallback) — so spawner finds it even if props race
        PlayerPrefs.SetString(PhotonKeys.PREF_KEY_TRIAD, $"{tri[0]},{tri[1]},{tri[2]}");
        if (chosenIndex >= 0) PlayerPrefs.SetInt(PhotonKeys.PREF_CHARACTER_INDEX, chosenIndex);
        if (!string.IsNullOrEmpty(prefabName)) PlayerPrefs.SetString(PhotonKeys.PREF_CHARACTER_PREFAB, prefabName);
        PlayerPrefs.Save();

        // If Photon connected, write triad + index into LocalPlayer custom props
        if (PhotonNetwork.IsConnected && PhotonNetwork.LocalPlayer != null)
        {
            object[] triObj = new object[] { tri[0], tri[1], tri[2] };
            ExitGames.Client.Photon.Hashtable props = new ExitGames.Client.Photon.Hashtable();
            props[PhotonKeys.PROP_TRIAD] = triObj;
            if (chosenIndex >= 0) props[PhotonKeys.PROP_CHARACTER_INDEX] = chosenIndex;
            if (!string.IsNullOrEmpty(prefabName)) props[PhotonKeys.PROP_CHARACTER_PREFAB] = prefabName;

            PhotonNetwork.LocalPlayer.SetCustomProperties(props);
            Debug.Log($"TriadTransferManager: Wrote triad -> LocalPlayer props ({tri[0]},{tri[1]},{tri[2]}) idx={chosenIndex}");
        }

        // Wait until LocalPlayer props contain triad or timeout
        float deadline = Time.realtimeSinceStartup + syncTimeout;
        if (PhotonNetwork.IsConnected && PhotonNetwork.LocalPlayer != null)
        {
            while (Time.realtimeSinceStartup < deadline)
            {
                var props = PhotonNetwork.LocalPlayer.CustomProperties;
                if (props != null && props.ContainsKey(PhotonKeys.PROP_TRIAD))
                    break;
                yield return null;
            }
        }

        // Finally load the scene
        if (usePhotonLoad && PhotonNetwork.IsConnected)
        {
            PhotonNetwork.LoadLevel(sceneName);
        }
        else
        {
            SceneManager.LoadScene(sceneName);
        }
    }
}
