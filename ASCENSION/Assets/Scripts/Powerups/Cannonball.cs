using System.Collections;
using UnityEngine;
using Photon.Pun;
using System;

/// <summary>
/// Cannonball (robust spawn):
/// - Reads instantiation data: [0] ownerActor (int), [1] damage (float), [2] radius (float)
/// - Has a short arming delay to avoid exploding immediately if spawned overlapping geometry
/// - Attempts to nudge itself upward if overlapping on spawn
/// - Owner-authoritative explosion (only the owner actually performs the AoE and destroys the network object)
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class Cannonball : MonoBehaviourPun
{
    public int ownerActor = -1;
    public float damage = 75f;
    public float radius = 2.5f;
    public float lifetime = 8f;

    [Header("Safety / arming")]
    [Tooltip("Seconds to wait before cannonball can explode from collisions (prevents instant death when spawned overlapping).")]
    public float armDelay = 0.06f;
    [Tooltip("If the cannonball is overlapping geometry on spawn, it will be nudged upward this many meters per attempt.")]
    public float overlapNudge = 0.5f;
    [Tooltip("Maximum number of nudge attempts to free the cannonball from overlaps.")]
    public int maxNudgeAttempts = 6;

    private bool armed = false;
    private bool exploded = false;
    private Collider myCollider;
    private Rigidbody rb;

    void Awake()
    {
        myCollider = GetComponent<Collider>();
        rb = GetComponent<Rigidbody>();

        // If OwnedEntity is present, prefer its owner
        var oe = GetComponent<OwnedEntity>();
        if (oe != null && oe.ownerActor >= 0)
            ownerActor = oe.ownerActor;

        if (photonView != null && photonView.InstantiationData != null)
        {
            var d = photonView.InstantiationData;
            if (d.Length >= 1)
            {
                try { ownerActor = Convert.ToInt32(d[0]); } catch { }
            }
            if (d.Length >= 2 && (d[1] is float || d[1] is double || d[1] is int))
            {
                damage = Convert.ToSingle(d[1]);
            }
            if (d.Length >= 3 && (d[2] is float || d[2] is double || d[2] is int))
            {
                radius = Convert.ToSingle(d[2]);
            }
        }

        // schedule destruction in case it never collides
        Destroy(gameObject, lifetime);
    }

    void Start()
    {
        StartCoroutine(ArmAndPrepareCoroutine());
    }

    private IEnumerator ArmAndPrepareCoroutine()
    {
        // small attempt to free overlapping spawn
        if (myCollider != null)
        {
            // compute approximate probe radius
            float probeRadius = Mathf.Max(myCollider.bounds.extents.x, myCollider.bounds.extents.y, myCollider.bounds.extents.z);
            int attempts = 0;
            // Use QueryTriggerInteraction.Ignore so triggers don't count
            while (attempts < maxNudgeAttempts && Physics.CheckSphere(transform.position, probeRadius, ~0, QueryTriggerInteraction.Ignore))
            {
                transform.position += Vector3.up * overlapNudge;
                attempts++;
                // tiny wait to allow physics to catch up if something else is moving
                yield return null;
            }
        }

        // short arming delay to ensure any residual immediate collisions are ignored
        yield return new WaitForSeconds(armDelay);
        armed = true;

        // ensure it is falling if a rigidbody exists
        if (rb != null)
        {
            // small downward impulse if it's stationary to start motion
            if (rb.velocity.sqrMagnitude < 0.01f)
                rb.AddForce(Vector3.down * 1f, ForceMode.VelocityChange);

            // enable continuous collision detection for accuracy
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        }
    }

    private void OnCollisionEnter(Collision collision)
    {
        if (!armed) return;
        if (exploded) return;
        // Owner-authoritative: only the owner should perform the explosion when networked
        if (PhotonNetwork.InRoom && photonView != null)
        {
            if (!photonView.IsMine) return;
        }
        Explode();
    }

    private void OnTriggerEnter(Collider other)
    {
        if (!armed) return;
        if (exploded) return;
        if (PhotonNetwork.InRoom && photonView != null)
        {
            if (!photonView.IsMine) return;
        }
        Explode();
    }

    private void Explode()
    {
        if (exploded) return;
        exploded = true;

        // AoE damage - skip owner's actor/objects
        Collider[] cols = Physics.OverlapSphere(transform.position, radius);
        foreach (var c in cols)
        {
            if (c == null || c.gameObject == null) continue;
            GameObject target = c.gameObject;

            // Skip self
            if (target == gameObject) continue;

            // skip owner by Photon actor
            var pv = target.GetComponentInParent<PhotonView>();
            if (pv != null && pv.Owner != null && ownerActor >= 0 && pv.Owner.ActorNumber == ownerActor)
                continue;

            // skip if target equals OwnedEntity.ownerGameObject (if we have OwnedEntity on this cannonball)
            var oe = GetComponent<OwnedEntity>();
            if (oe != null && oe.ownerGameObject != null && target == oe.ownerGameObject)
                continue;

            // Skip PlayerIdentity match (local/offline)
            var pid = target.GetComponentInParent<PlayerIdentity>();
            if (pid != null && ownerActor >= 0 && pid.actorNumber == ownerActor)
                continue;

            // apply damage (best-effort)
            target.SendMessage("TakeDamage", damage, SendMessageOptions.DontRequireReceiver);
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

        // schedule destruction (override)
        CancelInvoke();
        Destroy(gameObject, lifetime);
    }

    // Optional: editor gizmo to see explosion radius
    void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.red;
        Gizmos.DrawWireSphere(transform.position, radius);
    }
}
