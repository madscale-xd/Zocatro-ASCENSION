using System.Collections;
using UnityEngine;

/// <summary>
/// SmokeArea: a single smoke puff that sits in the world and dissipates after `duration`.
/// Optionally applies periodic damage to objects inside.
/// Attach to your smoke prefab.
/// </summary>
[RequireComponent(typeof(Collider))]
public class SmokeArea : MonoBehaviour
{
    [Tooltip("How long the smoke stays in the world (seconds).")]
    public float duration = 5f;

    [Tooltip("If > 0, damage applied per tick to objects inside the smoke.")]
    public int damagePerTick = 0;

    [Tooltip("Interval between damage ticks (seconds).")]
    public float tickInterval = 0.5f;

    bool started = false;

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
        // If Initialize wasn't called explicitly, start automatically
        Initialize();
    }

    IEnumerator SelfDestroy()
    {
        yield return new WaitForSeconds(duration);
        Destroy(gameObject);
    }

    IEnumerator DamageTick()
    {
        var wait = new WaitForSeconds(tickInterval);
        Collider col = GetComponent<Collider>();
        if (col == null) yield break;

        // derive center+radius from collider bounds as approximation
        Vector3 center = col.bounds.center;
        float radius = Mathf.Max(col.bounds.extents.x, col.bounds.extents.z);

        while (true)
        {
            var hits = Physics.OverlapSphere(center, radius);
            foreach (var h in hits)
            {
                if (h.gameObject == gameObject) continue;

                // If target has PhotonView -> call RPC on its owner to apply damage (authoritative)
                var targetPv = h.GetComponentInParent<Photon.Pun.PhotonView>();
                if (targetPv != null && targetPv.Owner != null)
                {
                    try
                    {
                        targetPv.RPC("RPC_TakeDamage", targetPv.Owner, damagePerTick, false, -1); // -1 = environmental/unknown attacker
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
