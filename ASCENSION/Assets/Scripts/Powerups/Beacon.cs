using System.Collections;
using UnityEngine;
using Photon.Pun;
using System.Globalization;
using System;

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
        if (!started)
        {
            started = true;
            StartCoroutine(SpawnAfterDelay());
        }
    }

    void Awake()
    {
        // read instantiation data if present (robust conversion)
        if (photonView != null && photonView.InstantiationData != null)
        {
            var d = photonView.InstantiationData;
            if (d.Length >= 1)
            {
                try { ownerActor = Convert.ToInt32(d[0]); } catch { }
            }
            if (d.Length >= 2 && d[1] is string) cannonballResourceName = (string)d[1];

            if (d.Length >= 3) damage = ParseFloatFromInst(d[2], damage);
            if (d.Length >= 4) radius = ParseFloatFromInst(d[3], radius);
            if (d.Length >= 5) delay = ParseFloatFromInst(d[4], delay);
        }

        // Start the spawn timer only if offline OR owner of this networked beacon
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
            Debug.LogWarning($"[Beacon] cannonballResourceName empty on Beacon '{name}' (ownerActor={ownerActor}). Aborting spawn.");
            hasSpawned = true;
            yield break;
        }

        Vector3 spawnPos = transform.position + Vector3.up * 12f; // spawn high above the beacon
        object[] cbData = new object[] { ownerActor, damage, radius };

        GameObject cb = null;

        Debug.Log($"[Beacon] SpawnAfterDelay: InRoom={PhotonNetwork.InRoom}, PhotonView.IsMine={(photonView != null ? photonView.IsMine.ToString() : "null")}, LocalActor={PhotonNetwork.LocalPlayer?.ActorNumber ?? -1}, ownerActor={ownerActor}, cannonballResourceName='{cannonballResourceName}'");

        // If in a Photon room, only the owner should call PhotonNetwork.Instantiate.
        if (PhotonNetwork.InRoom)
        {
            if (photonView != null && photonView.IsMine)
            {
                try
                {
                    Debug.Log($"[Beacon] Attempting PhotonNetwork.Instantiate('{cannonballResourceName}') by owner.");
                    cb = PhotonNetwork.Instantiate(cannonballResourceName, spawnPos, Quaternion.identity, 0, cbData);
                    Debug.Log($"[Beacon] PhotonNetwork.Instantiate returned {(cb != null ? cb.name : "null")}.");
                }
                catch (System.Exception ex)
                {
                    Debug.LogWarning($"[Beacon] PhotonNetwork.Instantiate failed for '{cannonballResourceName}': {ex.Message}. Will try local Resources.Load fallback.");
                    cb = null;
                }

                // If PhotonNetwork.Instantiate failed, try Resources.Load and local instantiate as fallback (owner-only)
                if (cb == null)
                {
                    var prefab = Resources.Load<GameObject>(cannonballResourceName);
                    if (prefab != null)
                    {
                        cb = Instantiate(prefab, spawnPos, Quaternion.identity);
                        // If the cannonball has an initialization hook, pass instantiation data
                        var cbComp = cb.GetComponent<Cannonball>();
                        if (cbComp != null) cbComp.InitializeFromSpawner(ownerActor, this.gameObject, damage, radius, cbComp.lifetime);
                        Debug.LogWarning($"[Beacon] PhotonNetwork.Instantiate failed but Resources.Load succeeded — spawned local cannonball '{prefab.name}' (local-only).");
                    }
                    else
                    {
                        Debug.LogError($"[Beacon] Resources.Load<GameObject>('{cannonballResourceName}') returned null. Make sure the cannonball prefab is in a Resources/ folder and name matches exactly.");
                    }
                }
            }
            else
            {
                Debug.Log($"[Beacon] Not owner of beacon on this client; owner will spawn the cannonball.");
            }
        }
        else
        {
            // Offline/local mode: instantiate locally
            var prefab = Resources.Load<GameObject>(cannonballResourceName);
            if (prefab != null)
            {
                cb = Instantiate(prefab, spawnPos, Quaternion.identity);
                var cbComp = cb.GetComponent<Cannonball>();
                if (cbComp != null) cbComp.InitializeFromSpawner(ownerActor, this.gameObject, damage, radius, cbComp.lifetime);
                Debug.Log($"[Beacon] Offline: spawned local cannonball '{prefab.name}'.");
            }
            else
            {
                Debug.LogWarning($"[Beacon] Offline: could not find cannonball prefab '{cannonballResourceName}' in Resources.");
            }
        }

        hasSpawned = true;

        // If cannonball prefab doesn't exist or spawn failed, fallback to immediate AoE
        if (cb == null)
        {
            Debug.LogWarning("[Beacon] Cannonball spawn failed or returned null — executing fallback AoE damage now.");
            var cols = Physics.OverlapSphere(transform.position, radius);
            foreach (var c in cols)
            {
                if (c.gameObject == null) continue;
                c.gameObject.SendMessage("TakeDamage", Mathf.RoundToInt(damage), SendMessageOptions.DontRequireReceiver);
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

    private float ParseFloatFromInst(object o, float fallback)
    {
        if (o == null) return fallback;

        // direct pattern matches for common boxed types
        if (o is float f) return f;
        if (o is double d) return (float)d;
        if (o is int i) return (float)i;
        if (o is long l) return (float)l;
        if (o is short s) return (float)s;
        if (o is byte b) return (float)b;
        if (o is decimal dec) return (float)dec;

        // try parsing string
        if (o is string str)
        {
            if (float.TryParse(str, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out float parsed))
                return parsed;
            // try with local culture as a last resort
            if (float.TryParse(str, out parsed)) return parsed;
            return fallback;
        }

        // final fallback via Convert (handles a wider range of IConvertible objects)
        try
        {
            return Convert.ToSingle(o, CultureInfo.InvariantCulture);
        }
        catch
        {
            return fallback;
        }
    }
}
