// SessionPlayerSpawner.cs (with wait-for-player-prop fix)
using System.Collections;
using UnityEngine;
using Photon.Pun;
using Photon.Realtime;
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
    [SerializeField] private float waitForPropTimeout = 1.5f;

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

        // start coroutine to wait for Photon prop (if desired), then spawn
        if (spawnRoutine != null) StopCoroutine(spawnRoutine);
        spawnRoutine = StartCoroutine(SpawnWhenReady());
    }

    private IEnumerator SpawnWhenReady()
    {
        // guard: don't spawn twice
        if (hasSpawned) yield break;

        int chosenIndex = -1;
        string prefabNameToUse = null;
        GameObject selectedPrefab = null;

        bool havePhoton = PhotonNetwork.IsConnected && PhotonNetwork.LocalPlayer != null;
        float waitDeadline = Time.realtimeSinceStartup + waitForPropTimeout;

        // If connected and configured to wait: poll for the player custom property for a short time
        if (havePhoton && waitForPhotonPropIfConnected)
        {
            bool found = false;
            while (Time.realtimeSinceStartup <= waitDeadline)
            {
                if (PhotonNetwork.LocalPlayer.CustomProperties != null &&
                    PhotonNetwork.LocalPlayer.CustomProperties.TryGetValue(SessionPlayerSpawnerCharacterKeys.PROP_CHARACTER_INDEX, out object objIndex))
                {
                    if (objIndex is int) chosenIndex = (int)objIndex;
                    else int.TryParse(objIndex?.ToString() ?? "-1", out chosenIndex);

                    found = true;
                    break;
                }

                // also accept prefab name if set
                if (PhotonNetwork.LocalPlayer.CustomProperties != null &&
                    PhotonNetwork.LocalPlayer.CustomProperties.TryGetValue(SessionPlayerSpawnerCharacterKeys.PROP_CHARACTER_PREFAB, out object objPrefab))
                {
                    prefabNameToUse = objPrefab?.ToString();
                    // don't break here — prefer index if available
                }

                yield return null; // wait a frame and try again
            }
            // after waiting, proceed (either found or timed out)
        }
        else if (havePhoton)
        {
            // If not waiting, try immediately (no blocking)
            if (PhotonNetwork.LocalPlayer.CustomProperties != null &&
                PhotonNetwork.LocalPlayer.CustomProperties.TryGetValue(SessionPlayerSpawnerCharacterKeys.PROP_CHARACTER_INDEX, out object objIndex))
            {
                if (objIndex is int) chosenIndex = (int)objIndex;
                else int.TryParse(objIndex?.ToString() ?? "-1", out chosenIndex);
            }
            if (PhotonNetwork.LocalPlayer.CustomProperties != null &&
                PhotonNetwork.LocalPlayer.CustomProperties.TryGetValue(SessionPlayerSpawnerCharacterKeys.PROP_CHARACTER_PREFAB, out object objPrefab))
            {
                prefabNameToUse = objPrefab?.ToString();
            }
        }

        // If we still don't have a chosenIndex, fall back to PlayerPrefs (local machine fallback)
        if (chosenIndex < 0)
        {
            if (PlayerPrefs.HasKey(SessionPlayerSpawnerCharacterKeys.PROP_CHARACTER_INDEX))
                chosenIndex = PlayerPrefs.GetInt(SessionPlayerSpawnerCharacterKeys.PROP_CHARACTER_INDEX, -1);
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

        // Instantiate via Photon and pass the chosenIndex as instantiationData
        GameObject player = null;
        try
        {
            object[] instantiationData = new object[] { chosenIndex };
            player = PhotonNetwork.Instantiate(prefabNameToUse, spawnPos, spawnRot, 0, instantiationData);
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"SessionPlayerSpawner: PhotonNetwork.Instantiate failed for '{prefabNameToUse}'. Exception: {ex.Message}\nEnsure the prefab is available to Photon (Resources or PrefabPool).");
        }

        if (player != null)
        {
            hasSpawned = true;
            Debug.Log($"SessionPlayerSpawner: Spawned local player '{prefabNameToUse}' with chosenIndex={chosenIndex}.");
        }
        else
        {
            Debug.LogError($"SessionPlayerSpawner: Failed to instantiate '{prefabNameToUse}'. Ensure the prefab is available to Photon (Resources or PrefabPool).");
        }
    }

#if UNITY_EDITOR
    // Editor-time checks to warn you if assigned prefabs are not in a Resources folder
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
}
