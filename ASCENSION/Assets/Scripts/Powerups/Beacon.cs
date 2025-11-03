using System.Collections;
using UnityEngine;
using Photon.Pun;

/// <summary>
/// Beacon: waits for delay then spawns a cannonball (networked when in-room).
/// InstantiationData expected (optional): [0] ownerActor (int), [1] cannonballResourceName (string), [2] damage (float), [3] radius (float), [4] delay (float)
/// Only the owner (photonView.IsMine) will actually perform the spawn when in a Photon room.
/// </summary>
public class Beacon : MonoBehaviourPun
{
    public int ownerActor = -1;
    public string cannonballResourceName = "";
    public float damage = 75f;
    public float radius = 2.5f;
    public float delay = 2f;

    private bool started = false;
    private bool hasSpawned = false;

    // Local fallback initializer
    public void InitializeFromSpawner(int ownerActor_, string cannonballName, float damage_, float radius_, float delay_)
    {
        ownerActor = ownerActor_;
        cannonballResourceName = cannonballName;
        damage = damage_;
        radius = radius_;
        delay = delay_;
        // only start if not already started
        if (!started)
        {
            started = true;
            StartCoroutine(SpawnAfterDelay());
        }
    }

    void Awake()
    {
        // read instantiation data if present
        if (photonView != null && photonView.InstantiationData != null)
        {
            var d = photonView.InstantiationData;
            if (d.Length >= 1 && d[0] is int) ownerActor = (int)d[0];
            if (d.Length >= 2 && d[1] is string) cannonballResourceName = (string)d[1];
            if (d.Length >= 3 && (d[2] is float || d[2] is double)) damage = (float)(double)d[2];
            if (d.Length >= 4 && (d[3] is float || d[3] is double)) radius = (float)(double)d[3];
            if (d.Length >= 5 && (d[4] is float || d[4] is double)) delay = (float)(double)d[4];
        }

        // Start the spawn timer only if:
        //  - We're offline (PhotonNetwork.InRoom == false) => local preview mode OR
        //  - We're online AND this networked beacon is owned by this client (photonView.IsMine)
        // This prevents all clients from running the spawn coroutine.
        if (!started)
        {
            if (!PhotonNetwork.InRoom || (photonView != null && photonView.IsMine))
            {
                started = true;
                StartCoroutine(SpawnAfterDelay());
            }
        }
    }

    private IEnumerator SpawnAfterDelay()
    {
        yield return new WaitForSeconds(delay);

        if (hasSpawned) yield break; // guard

        if (string.IsNullOrEmpty(cannonballResourceName))
        {
            Debug.LogWarning("Beacon: cannonball resource name empty; abort spawn.");
            hasSpawned = true;
            yield break;
        }

        Vector3 spawnPos = transform.position + Vector3.up * 12f; // spawn high above the beacon

        object[] cbData = new object[] { ownerActor, damage, radius };

        GameObject cb = null;

        // If in a Photon room, only the owner should call PhotonNetwork.Instantiate.
        if (PhotonNetwork.InRoom)
        {
            if (photonView != null && photonView.IsMine)
            {
                try
                {
                    cb = PhotonNetwork.Instantiate(cannonballResourceName, spawnPos, Quaternion.identity, 0, cbData);
                }
                catch (System.Exception ex)
                {
                    Debug.LogWarning($"Beacon: PhotonNetwork.Instantiate failed for '{cannonballResourceName}': {ex.Message}. Falling back to local Instantiate.");
                    var prefab = Resources.Load<GameObject>(cannonballResourceName);
                    if (prefab != null) cb = Instantiate(prefab, spawnPos, Quaternion.identity);
                }
            }
            else
            {
                // Not owner: do NOT instantiate. The owner will spawn the networked cannonball which will appear on this client automatically.
            }
        }
        else
        {
            // Offline/local mode: instantiate locally
            var prefab = Resources.Load<GameObject>(cannonballResourceName);
            if (prefab != null) cb = Instantiate(prefab, spawnPos, Quaternion.identity);
            else Debug.LogWarning($"Beacon (local): could not find cannonball prefab '{cannonballResourceName}' in Resources.");
        }

        hasSpawned = true;

        // If cannonball prefab doesn't exist or spawn failed, fallback to immediate AoE
        if (cb == null)
        {
            var cols = Physics.OverlapSphere(transform.position, radius);
            foreach (var c in cols)
            {
                if (c.gameObject == null) continue;
                c.gameObject.SendMessage("TakeDamage", damage, SendMessageOptions.DontRequireReceiver);
            }
        }

        // destroy the beacon after spawn (owner should destroy the networked beacon)
        if (PhotonNetwork.InRoom)
        {
            if (photonView != null && photonView.IsMine)
            {
                try { PhotonNetwork.Destroy(gameObject); } catch { Destroy(gameObject); }
            }
            else
            {
                // non-owner: let network destroy propagate; as safety, destroy local object after short delay if it's still around
                Destroy(gameObject, 5f);
            }
        }
        else
        {
            Destroy(gameObject);
        }
    }
}
