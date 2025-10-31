using UnityEngine;
using Photon.Pun;
using System.Collections;

public class PowerUpSpawner : MonoBehaviourPunCallbacks
{
    public Transform[] spawnPoints;
    public string[] powerUpPrefabs = { "Powerups/SpeedBoost", "Powerups/JumpBoost" }; // updated to match JumpBoost
    public float spawnInterval = 15f;

    void Start()
    {
        if (PhotonNetwork.IsMasterClient)
        {
            Debug.Log("[Spawner] MasterClient starting spawn routine...");
            StartCoroutine(SpawnRoutine());
        }
    }

    IEnumerator SpawnRoutine()
    {
        while (true)
        {
            yield return new WaitForSeconds(spawnInterval);
            SpawnRandomPowerUp();
        }
    }

    void SpawnRandomPowerUp()
    {
        if (!PhotonNetwork.IsMasterClient) return;

        if (spawnPoints.Length == 0 || powerUpPrefabs.Length == 0)
        {
            Debug.LogWarning("[Spawner] No spawn points or prefabs assigned!");
            return;
        }

        Transform spawnPoint = spawnPoints[Random.Range(0, spawnPoints.Length)];
        string prefabName = powerUpPrefabs[Random.Range(0, powerUpPrefabs.Length)];

        PhotonNetwork.Instantiate(prefabName, spawnPoint.position, Quaternion.identity);
        Debug.Log($"[Spawner] Spawned {prefabName} at {spawnPoint.position}");
    }

    public override void OnMasterClientSwitched(Photon.Realtime.Player newMasterClient)
    {
        if (PhotonNetwork.IsMasterClient)
        {
            Debug.Log("[Spawner] New MasterClient detected, starting spawn routine...");
            StartCoroutine(SpawnRoutine());
        }
    }
}
