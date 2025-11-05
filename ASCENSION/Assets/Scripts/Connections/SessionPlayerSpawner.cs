// SessionPlayerSpawner.cs
using System.Collections;
using UnityEngine;
using Photon.Pun;
using Photon.Realtime;
using ExitGames.Client.Photon; // Hashtable
#if UNITY_EDITOR
using UnityEditor;
#endif

public class SessionPlayerSpawner : MonoBehaviourPunCallbacks
{
    [Header("Prefab (drag a prefab here or set name)")]
    [Tooltip("Optional: assign the player prefab directly in the inspector. It still needs to be available to Photon (i.e., inside a Resources folder) unless you set up a custom PrefabPool.")]
    [SerializeField] private GameObject playerPrefab; // optional inspector assignment

    [Tooltip("Name of the player prefab file located in Assets/Resources (without path). Used if no prefab is assigned.")]
    [SerializeField] private string playerPrefabName = "PlayerPrefab";

    [Header("Optional spawn points (order used by ActorNumber to distribute)")]
    [SerializeField] private Transform[] spawnPoints;

    [Header("Assignable prefabs (index -> prefab)")]
    [Tooltip("Assign one prefab per character. The selector sets an index; this array maps that index to the prefab to spawn.")]
    [SerializeField] private GameObject[] prefabPrefabs = new GameObject[0];

    [Header("Spawn behavior")]
    [Tooltip("If true, when connected to Photon the spawner will wait up to WaitForPropTimeout seconds for the Photon player property to appear. Otherwise it immediately falls back to PlayerPrefs.")]
    [SerializeField] private bool waitForPhotonPropIfConnected = true;
    [Tooltip("How many seconds to wait for the Photon LocalPlayer custom property before falling back.")]
    [SerializeField] private float waitForPropTimeout = 3.0f;

    // Instance guard so we don't spawn multiple times if Start + OnJoinedRoom both fire
    private bool hasSpawned = false;
    private Coroutine spawnRoutine;

    void Start()
    {
        TrySpawnPlayer();
    }

    public override void OnJoinedRoom()
    {
        TrySpawnPlayer();
    }

    void OnDestroy()
    {
        if (spawnRoutine != null) StopCoroutine(spawnRoutine);
    }

    private void TrySpawnPlayer()
    {
        if (hasSpawned) return;
        if (!PhotonNetwork.InRoom) return;
        if (spawnRoutine != null) return;
        spawnRoutine = StartCoroutine(SpawnWhenReady());
    }

    public override void OnPlayerPropertiesUpdate(Player targetPlayer, ExitGames.Client.Photon.Hashtable changedProps)
    {
        base.OnPlayerPropertiesUpdate(targetPlayer, changedProps);

        if (targetPlayer == null) return;
        if (!targetPlayer.IsLocal) return;
        if (hasSpawned) return;

        if (changedProps.ContainsKey(PhotonKeys.PROP_CHARACTER_INDEX) ||
            changedProps.ContainsKey(PhotonKeys.PROP_TRIAD) ||
            changedProps.ContainsKey(PhotonKeys.PROP_CHARACTER_PREFAB))
        {
            Debug.Log("SessionPlayerSpawner: Detected local player property update — retrying spawn attempt.");
            TrySpawnPlayer();
        }
    }

    private IEnumerator SpawnWhenReady()
    {
        if (hasSpawned)
        {
            spawnRoutine = null;
            yield break;
        }

        int chosenIndex = -1;
        string prefabNameToUse = null;
        GameObject selectedPrefab = null;

        int tri0 = -1, tri1 = -1, tri2 = -1;

        bool havePhoton = PhotonNetwork.IsConnected && PhotonNetwork.LocalPlayer != null;
        float waitDeadline = Time.realtimeSinceStartup + waitForPropTimeout;

        if (havePhoton && waitForPhotonPropIfConnected)
        {
            bool foundIndex = false;
            while (Time.realtimeSinceStartup <= waitDeadline)
            {
                var props = PhotonNetwork.LocalPlayer.CustomProperties;
                if (props != null && props.TryGetValue(PhotonKeys.PROP_CHARACTER_INDEX, out object objIndex))
                {
                    if (objIndex is int) chosenIndex = (int)objIndex;
                    else int.TryParse(objIndex?.ToString() ?? "-1", out chosenIndex);

                    foundIndex = true;
                }

                if (props != null && props.TryGetValue(PhotonKeys.PROP_CHARACTER_PREFAB, out object objPrefab))
                {
                    prefabNameToUse = objPrefab?.ToString();
                }

                if (props != null && props.TryGetValue(PhotonKeys.PROP_TRIAD, out object objTriad))
                {
                    ParseTriadObject(objTriad, ref tri0, ref tri1, ref tri2);
                }

                if (foundIndex && (tri0 >= 0 || tri1 >= 0 || tri2 >= 0))
                    break;

                yield return null;
            }

            // final quick read after wait loop
            if (!foundIndex)
            {
                if (PhotonNetwork.LocalPlayer.CustomProperties != null &&
                    PhotonNetwork.LocalPlayer.CustomProperties.TryGetValue(PhotonKeys.PROP_CHARACTER_INDEX, out object objIndex2))
                {
                    if (objIndex2 is int) chosenIndex = (int)objIndex2;
                    else int.TryParse(objIndex2?.ToString() ?? "-1", out chosenIndex);
                }
            }

            if ((tri0 < 0 && tri1 < 0 && tri2 < 0) && PhotonNetwork.LocalPlayer.CustomProperties != null &&
                PhotonNetwork.LocalPlayer.CustomProperties.TryGetValue(PhotonKeys.PROP_TRIAD, out object objTriadFinal))
            {
                ParseTriadObject(objTriadFinal, ref tri0, ref tri1, ref tri2);
            }
        }
        else if (havePhoton)
        {
            if (PhotonNetwork.LocalPlayer.CustomProperties != null &&
                PhotonNetwork.LocalPlayer.CustomProperties.TryGetValue(PhotonKeys.PROP_CHARACTER_INDEX, out object objIndex))
            {
                if (objIndex is int) chosenIndex = (int)objIndex;
                else int.TryParse(objIndex?.ToString() ?? "-1", out chosenIndex);
            }
            if (PhotonNetwork.LocalPlayer.CustomProperties != null &&
                PhotonNetwork.LocalPlayer.CustomProperties.TryGetValue(PhotonKeys.PROP_CHARACTER_PREFAB, out object objPrefab))
            {
                prefabNameToUse = objPrefab?.ToString();
            }
            if (PhotonNetwork.LocalPlayer.CustomProperties != null &&
                PhotonNetwork.LocalPlayer.CustomProperties.TryGetValue(PhotonKeys.PROP_TRIAD, out object objTriadImmediate))
            {
                ParseTriadObject(objTriadImmediate, ref tri0, ref tri1, ref tri2);
            }
        }

        // If we still don't have a chosenIndex, fall back to PlayerPrefs (local machine fallback)
        if (chosenIndex < 0)
        {
            if (PlayerPrefs.HasKey(PhotonKeys.PREF_CHARACTER_INDEX))
                chosenIndex = PlayerPrefs.GetInt(PhotonKeys.PREF_CHARACTER_INDEX, -1);
        }

        // triad fallback from PlayerPrefs (CSV string)
        if (PlayerPrefs.HasKey(PhotonKeys.PREF_KEY_TRIAD))
        {
            string triCsv = PlayerPrefs.GetString(PhotonKeys.PREF_KEY_TRIAD, null);
            if (!string.IsNullOrEmpty(triCsv))
            {
                var parts = triCsv.Split(',');
                if (parts.Length >= 1) int.TryParse(parts[0], out tri0);
                if (parts.Length >= 2) int.TryParse(parts[1], out tri1);
                if (parts.Length >= 3) int.TryParse(parts[2], out tri2);
            }

            // If Photon connected and LocalPlayer props lack triad, push PlayerPrefs triad into LocalPlayer props
            if (havePhoton && PhotonNetwork.LocalPlayer != null &&
                (PhotonNetwork.LocalPlayer.CustomProperties == null || !PhotonNetwork.LocalPlayer.CustomProperties.ContainsKey(PhotonKeys.PROP_TRIAD)))
            {
                object[] triObj = new object[] { tri0, tri1, tri2 };
                PhotonNetwork.LocalPlayer.SetCustomProperties(new ExitGames.Client.Photon.Hashtable { { PhotonKeys.PROP_TRIAD, triObj } });
                Debug.Log($"SessionPlayerSpawner: pushed PlayerPrefs triad into LocalPlayer custom props ({tri0},{tri1},{tri2}).");
            }
        }

        // --- Pick the prefab by index (preferred) ---
        if (chosenIndex >= 0 && prefabPrefabs != null && chosenIndex < prefabPrefabs.Length)
        {
            selectedPrefab = prefabPrefabs[chosenIndex];
            if (selectedPrefab != null) prefabNameToUse = selectedPrefab.name;
        }
        else if (chosenIndex >= 0 && string.IsNullOrEmpty(prefabNameToUse))
        {
            Debug.LogWarning($"SessionPlayerSpawner: chosen index {chosenIndex} out of range of prefabPrefabs. Falling back to inspector defaults.");
        }

        // If prefabNameToUse still null, use inspector assignment or string name
        if (string.IsNullOrEmpty(prefabNameToUse))
        {
            if (playerPrefab != null)
            {
                selectedPrefab = playerPrefab;
                prefabNameToUse = playerPrefab.name;
            }
            else
            {
                prefabNameToUse = playerPrefabName;
            }
        }

        if (string.IsNullOrEmpty(prefabNameToUse))
        {
            Debug.LogError("SessionPlayerSpawner: No player prefab assigned and no selection found to spawn.");
            spawnRoutine = null;
            yield break;
        }

        // Optional: quick Resources existence check to warn about Photon requirements
        var resCheck = Resources.Load<GameObject>(prefabNameToUse);
        if (resCheck == null)
        {
            Debug.LogWarning($"SessionPlayerSpawner: Prefab named '{prefabNameToUse}' was NOT found under any Resources folder. PhotonNetwork.Instantiate will fail unless you register a PrefabPool that can provide this prefab.");
        }

        // Compute spawn position/rotation
        Vector3 spawnPos = Vector3.zero;
        Quaternion spawnRot = Quaternion.identity;
        if (spawnPoints != null && spawnPoints.Length > 0 && PhotonNetwork.LocalPlayer != null)
        {
            int idx = (PhotonNetwork.LocalPlayer.ActorNumber - 1) % spawnPoints.Length;
            spawnPos = spawnPoints[idx].position;
            spawnRot = spawnPoints[idx].rotation;
        }

        // Instantiate via Photon and pass the chosenIndex and triad indices as instantiationData
        GameObject player = null;
        try
        {
            object[] instantiationData = new object[] { chosenIndex, tri0, tri1, tri2 };
            player = PhotonNetwork.Instantiate(prefabNameToUse, spawnPos, spawnRot, 0, instantiationData);
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"SessionPlayerSpawner: PhotonNetwork.Instantiate failed for '{prefabNameToUse}'. Exception: {ex.Message}\nEnsure the prefab is available to Photon (Resources or PrefabPool).");
        }

        if (player != null)
        {
            hasSpawned = true;
            Debug.Log($"SessionPlayerSpawner: Spawned local player '{prefabNameToUse}' with chosenIndex={chosenIndex} triad=({tri0},{tri1},{tri2}).");
            // Start coroutine to wait for TarotSelection readiness and acquire a single tarot
            StartCoroutine(AcquireInitialTarotOnLocalPlayer(player, waitForPropTimeout)); // you can tune timeout
        }
        else
        {
            Debug.LogError($"SessionPlayerSpawner: Failed to instantiate '{prefabNameToUse}'. Ensure the prefab is available to Photon (Resources or PrefabPool).");
        }

        spawnRoutine = null;
    }

    // Utility: parse triad object from Photon custom properties (supports int[], object[] or comma string)
    private void ParseTriadObject(object obj, ref int a, ref int b, ref int c)
    {
        if (obj == null) return;

        if (obj is int[])
        {
            var arr = (int[])obj;
            if (arr.Length > 0) a = arr.Length > 0 ? arr[0] : -1;
            if (arr.Length > 1) b = arr.Length > 1 ? arr[1] : -1;
            if (arr.Length > 2) c = arr.Length > 2 ? arr[2] : -1;
            return;
        }

        if (obj is object[])
        {
            var oarr = (object[])obj;
            if (oarr.Length > 0) int.TryParse(oarr[0]?.ToString(), out a);
            if (oarr.Length > 1) int.TryParse(oarr[1]?.ToString(), out b);
            if (oarr.Length > 2) int.TryParse(oarr[2]?.ToString(), out c);
            return;
        }

        // fallback: try parse comma-separated string
        var s = obj.ToString();
        if (!string.IsNullOrEmpty(s))
        {
            var parts = s.Split(',');
            if (parts.Length > 0) int.TryParse(parts[0], out a);
            if (parts.Length > 1) int.TryParse(parts[1], out b);
            if (parts.Length > 2) int.TryParse(parts[2], out c);
            return;
        }
    }

#if UNITY_EDITOR
    private void OnValidate()
    {
        if (prefabPrefabs != null)
        {
            for (int i = 0; i < prefabPrefabs.Length; i++)
            {
                var p = prefabPrefabs[i];
                if (p == null) continue;
                string path = AssetDatabase.GetAssetPath(p);
                if (!string.IsNullOrEmpty(path) && !path.Contains("/Resources/"))
                {
                    Debug.LogWarning($"SessionPlayerSpawner: prefabPrefabs[{i}] '{p.name}' is not inside a Resources folder. PhotonNetwork.Instantiate will fail at runtime unless you use a custom PrefabPool.");
                }
            }
        }

        if (playerPrefab != null)
        {
            string path = AssetDatabase.GetAssetPath(playerPrefab);
            if (!path.Contains("/Resources/"))
                Debug.LogWarning($"SessionPlayerSpawner: assigned playerPrefab '{playerPrefab.name}' is not under a Resources folder. PhotonNetwork.Instantiate will not find it at runtime unless you use a PrefabPool.");
            playerPrefabName = playerPrefab.name;
        }
    }
#endif
    private IEnumerator AcquireInitialTarotOnLocalPlayer(GameObject player, float timeoutSeconds = 1.0f)
    {
        if (player == null) yield break;

        // try to find TarotSelection component (owner instance) on the spawned player
        float deadline = Time.realtimeSinceStartup + timeoutSeconds;
        TarotSelection ts = null;

        while (Time.realtimeSinceStartup < deadline)
        {
            ts = player.GetComponentInChildren<TarotSelection>(true);
            if (ts != null)
            {
                // check whether triad data is present (triadIndices first element >= 0)
                int[] idx = ts.GetTriadIndices();
                if (idx != null && (idx.Length >= 1 && idx[0] >= 0 || idx.Length >= 2 && idx[1] >= 0 || idx.Length >= 3 && idx[2] >= 0))
                    break; // ready
            }
            yield return null;
        }

        // final attempt if not found earlier
        if (ts == null)
            ts = player.GetComponentInChildren<TarotSelection>(true);

        if (ts == null)
        {
            Debug.LogWarning("[SessionPlayerSpawner] AcquireInitialTarot: no TarotSelection found on spawned player.");
            yield break;
        }

        // Acquire exactly one tarot card for the local player
        if (ts.AcquireNextTarot(out var acquired))
        {
            Debug.Log($"[SessionPlayerSpawner] AcquireInitialTarot: Local player acquired {acquired}.");
        }
        else
        {
            Debug.LogWarning("[SessionPlayerSpawner] AcquireInitialTarot: no tarot could be acquired (triad/deck empty).");
        }
    }
}
