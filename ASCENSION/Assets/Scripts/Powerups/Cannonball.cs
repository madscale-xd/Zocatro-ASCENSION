using System;
using System.Collections;
using UnityEngine;
using Photon.Pun;

[RequireComponent(typeof(Rigidbody))]
public class Cannonball : MonoBehaviourPun
{
    public int ownerActor = -1;
    public float damage = 75f;
    public float radius = 2.5f;
    public float lifetime = 8f;

    [Header("Safety / arming")]
    public float armDelay = 0.06f;
    public float overlapNudge = 0.5f;
    public int maxNudgeAttempts = 6;

    private bool armed = false;
    private bool exploded = false;
    private Collider myCollider;
    private Rigidbody rb;

    void Awake()
    {
        myCollider = GetComponent<Collider>();
        rb = GetComponent<Rigidbody>();

        // OwnedEntity preference (if you use it)
        var oe = GetComponent<OwnedEntity>();
        if (oe != null && oe.ownerActor >= 0) ownerActor = oe.ownerActor;

        // Read InstantiationData robustly if present
        if (photonView != null && photonView.InstantiationData != null)
        {
            var d = photonView.InstantiationData;
            if (d.Length >= 1)
            {
                try { ownerActor = Convert.ToInt32(d[0]); } catch { }
            }
            if (d.Length >= 2)
            {
                try { damage = Convert.ToSingle(d[1]); } catch { }
            }
            if (d.Length >= 3)
            {
                try { radius = Convert.ToSingle(d[2]); } catch { }
            }
            if (d.Length >= 4)
            {
                try { lifetime = Convert.ToSingle(d[3]); } catch { }
            }
        }

        Destroy(gameObject, lifetime);
    }

    void Start()
    {
        StartCoroutine(ArmAndPrepareCoroutine());
    }

    private IEnumerator ArmAndPrepareCoroutine()
    {
        // try to nudge out of overlaps
        if (myCollider != null)
        {
            float probeRadius = Mathf.Max(myCollider.bounds.extents.x, myCollider.bounds.extents.y, myCollider.bounds.extents.z);
            int attempts = 0;
            while (attempts < maxNudgeAttempts && Physics.CheckSphere(transform.position, probeRadius, ~0, QueryTriggerInteraction.Ignore))
            {
                transform.position += Vector3.up * overlapNudge;
                attempts++;
                yield return null;
            }
        }

        yield return new WaitForSeconds(armDelay);
        armed = true;

        if (rb != null)
        {
            if (rb.velocity.sqrMagnitude < 0.01f) rb.AddForce(Vector3.down * 1f, ForceMode.VelocityChange);
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        }
    }

    private void OnCollisionEnter(Collision collision)
    {
        if (!armed) return;
        if (exploded) return;

        // only consider valid collision targets (tagged Player or Ground in hierarchy)
        if (!IsValidExplodeCollider(collision.collider)) return;

        // Owner-authoritative explosion: only the network owner of this cannonball should explode it
        if (PhotonNetwork.InRoom && photonView != null && !photonView.IsMine)
            return;

        Explode();
    }

    private void OnTriggerEnter(Collider other)
    {
        if (!armed) return;
        if (exploded) return;

        if (!IsValidExplodeCollider(other)) return;

        if (PhotonNetwork.InRoom && photonView != null && !photonView.IsMine)
            return;

        Explode();
    }

    /// <summary>
    /// Walks up the transform chain looking for a "Player" or "Ground" tag.
    /// Returns true if any parent (including the collider's GameObject) has one of those tags.
    /// </summary>
    private bool IsValidExplodeCollider(Collider col)
    {
        if (col == null) return false;

        Transform t = col.transform;
        while (t != null)
        {
            if (t.CompareTag("Player") || t.CompareTag("Ground"))
                return true;
            t = t.parent;
        }

        return false;
    }

    private void Explode()
    {
        if (exploded) return;
        exploded = true;

        // AoE damage - owner-authoritative (skip owner's own actor/objects)
        Collider[] cols = Physics.OverlapSphere(transform.position, radius);
        foreach (var c in cols)
        {
            if (c == null || c.gameObject == null) continue;
            GameObject target = c.gameObject;

            // Skip self
            if (target == gameObject) continue;

            // Try find PlayerHealth and PhotonView on parent
            var targetPv = target.GetComponentInParent<PhotonView>();
            var ph = target.GetComponentInParent<PlayerHealth>();

            // If PhotonView + owner present and PlayerHealth exists -> use RPC to victim owner
            if (targetPv != null && targetPv.Owner != null && ph != null)
            {
                int actorNum = targetPv.Owner.ActorNumber;

                // skip if target is the same actor as the cannonball owner
                if (ownerActor >= 0 && actorNum == ownerActor) continue;

                try
                {
                    // PlayerHealth.RPC_TakeDamage signature: (int amount, bool isHead, int attackerActorNumber)
                    int amountInt = Mathf.RoundToInt(damage);
                    targetPv.RPC("RPC_TakeDamage", targetPv.Owner, amountInt, false, ownerActor);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[Cannonball] Explosion RPC failed, applying locally: " + ex);
                    ph.TakeDamage(Mathf.RoundToInt(damage), false);
                }
            }
            else if (ph != null)
            {
                // Local-only player (no PhotonView owner) - apply damage directly
                ph.TakeDamage(Mathf.RoundToInt(damage), false);
            }
            else
            {
                // Not a player - forward to other systems if they support TakeDamage
                try
                {
                    target.SendMessage("TakeDamage", Mathf.RoundToInt(damage), SendMessageOptions.DontRequireReceiver);
                }
                catch { }
            }
        }

        // TODO: spawn VFX / SFX here

        // network-aware destruction
        if (PhotonNetwork.InRoom && photonView != null)
        {
            try { PhotonNetwork.Destroy(gameObject); } catch { Destroy(gameObject); }
        }
        else
        {
            Destroy(gameObject);
        }
    }

    /// <summary>
    /// For local instantiation fallback: allow the spawner to initialize this cannonball.
    /// </summary>
    public void InitializeFromSpawner(int ownerActor_, GameObject ownerGO_, float damage_, float radius_, float lifetime_ = 8f)
    {
        ownerActor = ownerActor_;
        damage = damage_;
        radius = radius_;
        lifetime = lifetime_;

        var oe = GetComponent<OwnedEntity>();
        if (oe != null) oe.InitializeFromSpawner(ownerActor_, ownerGO_);

        CancelInvoke();
        Destroy(gameObject, lifetime);
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.red;
        Gizmos.DrawWireSphere(transform.position, radius);
    }
}
