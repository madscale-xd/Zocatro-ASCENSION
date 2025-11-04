using System.Collections;
using UnityEngine;
using Photon.Pun;

[RequireComponent(typeof(Collider))]
public class SmokeArea : MonoBehaviourPun
{
    [Tooltip("How long the smoke stays in the world (seconds).")]
    public float duration = 5f;

    [Tooltip("If > 0, damage applied per tick to objects inside the smoke.")]
    public int damagePerTick = 0;

    [Tooltip("Interval between damage ticks (seconds).")]
    public float tickInterval = 0.5f;

    bool started = false;

    void Awake()
    {
        // Read Photon instantiation data if available: [0] ownerActor (int), [1] duration (float), [2] damagePerTick (int/float), [3] tickInterval (float)
        if (photonView != null && photonView.InstantiationData != null)
        {
            object[] d = photonView.InstantiationData;
            try
            {
                if (d.Length >= 1) { int owner = -1; if (int.TryParse(d[0].ToString(), out owner)) { /* we don't store owner here, but could */ } }
                if (d.Length >= 2)
                {
                    float dur;
                    if (float.TryParse(d[1].ToString(), out dur)) duration = dur;
                }
                if (d.Length >= 3)
                {
                    int dmg;
                    if (int.TryParse(d[2].ToString(), out dmg)) damagePerTick = dmg;
                    else
                    {
                        float df; if (float.TryParse(d[2].ToString(), out df)) damagePerTick = Mathf.RoundToInt(df);
                    }
                }
                if (d.Length >= 4)
                {
                    float ti;
                    if (float.TryParse(d[3].ToString(), out ti)) tickInterval = ti;
                }
            }
            catch
            {
                // best-effort parsing; fall back to defaults on parse failure
            }
        }
    }

    public void Initialize()
    {
        if (!started)
        {
            started = true;
            StartCoroutine(SelfDestroy());
            if (damagePerTick > 0)
                StartCoroutine(DamageTick());
        }
    }

    void Start()
    {
        // Start automatically if not initialized manually
        Initialize();
    }

    IEnumerator SelfDestroy()
    {
        yield return new WaitForSeconds(duration);
        // network destroy if spawned via Photon owner
        if (PhotonNetwork.InRoom && photonView != null && photonView.IsMine)
        {
            try { PhotonNetwork.Destroy(gameObject); } catch { Destroy(gameObject); }
        }
        else
        {
            Destroy(gameObject);
        }
    }

    IEnumerator DamageTick()
    {
        var wait = new WaitForSeconds(tickInterval);
        Collider col = GetComponent<Collider>();
        if (col == null) yield break;

        Vector3 center = col.bounds.center;
        float radius = Mathf.Max(col.bounds.extents.x, col.bounds.extents.z);

        while (true)
        {
            var hits = Physics.OverlapSphere(center, radius);
            foreach (var h in hits)
            {
                if (h.gameObject == gameObject) continue;

                // If target has PhotonView -> call RPC on its owner to apply damage (authoritative)
                var targetPv = h.GetComponentInParent<PhotonView>();
                if (targetPv != null && targetPv.Owner != null)
                {
                    try
                    {
                        // match your PlayerHealth RPC signature; you used "RPC_TakeDamage" earlier
                        targetPv.RPC("RPC_TakeDamage", targetPv.Owner, damagePerTick, false, -1);
                    }
                    catch
                    {
                        h.gameObject.SendMessage("TakeDamage", damagePerTick, SendMessageOptions.DontRequireReceiver);
                    }
                }
                else
                {
                    h.gameObject.SendMessage("TakeDamage", damagePerTick, SendMessageOptions.DontRequireReceiver);
                }
            }

            yield return wait;
        }
    }
}
