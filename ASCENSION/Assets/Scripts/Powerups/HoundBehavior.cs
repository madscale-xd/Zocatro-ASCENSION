using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Photon.Pun;

/// <summary>
/// HoundBehaviour - does NOT explode on first hit by default.
/// - Moves forward
/// - Spawns smoke puffs along its path
/// - Applies authoritative damage to hit players (via RPC to victim owner)
/// - Does NOT explode on hit unless explodeOnHit == true
/// </summary>
[RequireComponent(typeof(Collider))]
public class HoundBehaviour : MonoBehaviourPun
{
    [Header("Movement / lifetime")]
    public float speed = 12f;
    public float lifetime = 3f;

    [Header("Damage / owner")]
    public int damage = 50;
    /// <summary>ActorNumber of the player that spawned this hound</summary>
    public int ownerActorNumber = -1;

    [Header("Smoke trail")]
    public GameObject smokePrefab;
    public float smokeInterval = 0.25f;
    public float smokeDuration = 5f;

    [Tooltip("How long to ignore collisions with the owner right after spawn (seconds).")]
    public float ownerIgnoreSeconds = 0.18f;

    [Tooltip("If true, the hound will explode after hitting targets.")]
    public bool explodeOnHit = false;

    [Tooltip("If explodeOnHit==true, maximum number of hits before explode. -1 = unlimited.")]
    public int maxPierce = -1;

    [Tooltip("If true smoke puffs are network-instantiated (requires prefab in Resources). Default false to save bandwidth.")]
    public bool networkSmoke = false;

    // runtime
    Rigidbody rb;
    Vector3 moveDir;
    bool exploded = false;
    int hitCount = 0;

    Collider[] myColliders;
    Collider[] ownerColliders;

    // avoid damaging same target repeatedly (by actor number or instance id)
    HashSet<int> damagedActorNumbers = new HashSet<int>();
    HashSet<int> damagedInstanceIds = new HashSet<int>();

    void Awake()
    {
        rb = GetComponent<Rigidbody>();
        if (rb == null)
        {
            rb = gameObject.AddComponent<Rigidbody>();
            rb.useGravity = false;
            rb.isKinematic = false;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
        }

        // If an OwnedEntity component exists, prefer its owner info
        var owned = GetComponent<OwnedEntity>();
        if (owned != null && owned.ownerActor >= 0)
        {
            ownerActorNumber = owned.ownerActor;
        }

        // read instantiation data (if any) as a fallback / compatibility
        var pv = GetComponent<PhotonView>();
        if (pv != null && pv.InstantiationData != null)
        {
            try
            {
                object[] data = pv.InstantiationData;
                if (data.Length >= 1)
                {
                    // instantiation might use int or other numeric types
                    try { ownerActorNumber = Convert.ToInt32(data[0]); } catch { }
                }
                if (data.Length >= 2) damage = Convert.ToInt32(data[1]);
                if (data.Length >= 3) speed = Convert.ToSingle(data[2]);
                if (data.Length >= 4) lifetime = Convert.ToSingle(data[3]);
                if (data.Length >= 5 && data[4] is string && !string.IsNullOrEmpty((string)data[4]))
                {
                    string smokeResName = (string)data[4];
                    var res = Resources.Load<GameObject>(smokeResName);
                    if (res != null) smokePrefab = res;
                }
                if (data.Length >= 6) smokeDuration = Convert.ToSingle(data[5]);
                if (data.Length >= 7) smokeInterval = Convert.ToSingle(data[6]);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("HoundBehaviour: error reading instantiation data: " + ex);
            }
        }

        myColliders = GetComponentsInChildren<Collider>(true);

        // find owner colliders so we can ignore collisions briefly (if ownerActorNumber available)
        if (ownerActorNumber >= 0)
        {
            var allPVs = FindObjectsOfType<PhotonView>();
            foreach (var p in allPVs)
            {
                if (p.Owner != null && p.Owner.ActorNumber == ownerActorNumber)
                {
                    ownerColliders = p.GetComponentsInChildren<Collider>(true);
                    break;
                }
            }
        }

        // If OwnedEntity contained a direct ownerGameObject, prefer that for ownerColliders
        if (ownerColliders == null || ownerColliders.Length == 0)
        {
            var oe = GetComponent<OwnedEntity>();
            if (oe != null && oe.ownerGameObject != null)
            {
                ownerColliders = oe.ownerGameObject.GetComponentsInChildren<Collider>(true);
            }
        }

        if (ownerColliders != null && ownerColliders.Length > 0 && myColliders != null)
        {
            foreach (var a in myColliders)
                foreach (var b in ownerColliders)
                    if (a != null && b != null) Physics.IgnoreCollision(a, b, true);

            StartCoroutine(ReenableOwnerCollisionsAfter(ownerIgnoreSeconds));
        }
    }

    void Start()
    {
        moveDir = transform.forward.normalized;
        if (rb != null) rb.velocity = moveDir * speed;

        StartCoroutine(SmokeTrailCoroutine());
        Invoke(nameof(OnLifetimeExpired), lifetime);
    }

    IEnumerator ReenableOwnerCollisionsAfter(float delay)
    {
        yield return new WaitForSeconds(delay);
        if (ownerColliders != null && myColliders != null)
        {
            foreach (var a in myColliders)
                foreach (var b in ownerColliders)
                    if (a != null && b != null) Physics.IgnoreCollision(a, b, false);
        }
    }

    void Update()
    {
        if (rb == null)
            transform.position += moveDir * speed * Time.deltaTime;
        else
        {
            if (rb.velocity.sqrMagnitude < (speed * 0.5f) * (speed * 0.5f))
                rb.velocity = moveDir * speed;
        }
    }

    void OnCollisionEnter(Collision collision) => HandleHit(collision.collider);
    void OnTriggerEnter(Collider other) => HandleHit(other);

    private void HandleHit(Collider col)
    {
        if (exploded) return;
        if (col == null) return;

        GameObject otherGo = col.gameObject;
        if (otherGo == null) return;

        // ignore owner's objects (extra safety)
        if (ownerActorNumber >= 0)
        {
            var otherPvCheck = otherGo.GetComponentInParent<PhotonView>();
            if (otherPvCheck != null && otherPvCheck.Owner != null &&
                otherPvCheck.Owner.ActorNumber == ownerActorNumber) return;
        }

        bool appliedDamage = false;

        // networked target? prefer PlayerHealth + PhotonView owner RPC
        var targetPv = otherGo.GetComponentInParent<PhotonView>();
        var ph = otherGo.GetComponentInParent<PlayerHealth>();

        if (targetPv != null && targetPv.Owner != null && ph != null)
        {
            int actorNum = targetPv.Owner.ActorNumber;

            // skip if same owner (double-check)
            if (ownerActorNumber >= 0 && actorNum == ownerActorNumber) return;

            if (!damagedActorNumbers.Contains(actorNum))
            {
                // call their owner RPC to apply damage (authoritative)
                try
                {
                    targetPv.RPC("RPC_TakeDamage", targetPv.Owner, damage, false, ownerActorNumber);
                    Debug.Log($"[HoundBehaviour] Sent RPC_TakeDamage to actor {actorNum} (attacker {ownerActorNumber}).");
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[HoundBehaviour] RPC failed, applying locally: " + ex);
                    ph.TakeDamage(damage, false);
                }

                damagedActorNumbers.Add(actorNum);
                appliedDamage = true;
            }
        }
        else if (ph != null)
        {
            // local/offline PlayerHealth (no PhotonView owner) — apply damage directly once per instance
            int id = ph.gameObject.GetInstanceID();

            // skip if ownerGameObject equals this target (if OwnedEntity provided ownerGameObject)
            var oe = GetComponent<OwnedEntity>();
            if (oe != null && oe.ownerGameObject != null && oe.ownerGameObject == ph.gameObject)
                return;

            if (!damagedInstanceIds.Contains(id))
            {
                ph.TakeDamage(damage, false);
                damagedInstanceIds.Add(id);
                appliedDamage = true;
            }
        }
        else
        {
            // not a player — ignore for damage purposes
            return;
        }

        // increment hit count only when damage was applied
        if (appliedDamage)
        {
            hitCount++;
            // Explode only if explicitly configured to do so
            if (explodeOnHit && (maxPierce < 0 || hitCount >= maxPierce))
            {
                ExplodeToSmoke();
                return;
            }
        }

        // otherwise, keep traveling — do not explode on hit
    }

    void OnLifetimeExpired()
    {
        if (exploded) return;
        ExplodeToSmoke();
    }

    void ExplodeToSmoke()
    {
        if (exploded) return;
        exploded = true;

        if (smokePrefab != null)
        {
            SpawnSmokeAt(transform.position);
        }

        var pv = GetComponent<PhotonView>();
        if (PhotonNetwork.InRoom && pv != null && pv.IsMine)
        {
            try { PhotonNetwork.Destroy(gameObject); } catch { Destroy(gameObject); }
        }
        else Destroy(gameObject);
    }

    IEnumerator SmokeTrailCoroutine()
    {
        if (smokePrefab == null) yield break;
        var wait = new WaitForSeconds(smokeInterval);
        while (!exploded)
        {
            SpawnSmokeAt(transform.position);
            yield return wait;
        }
    }

    private void SpawnSmokeAt(Vector3 pos)
    {
        if (smokePrefab == null) return;

        if (networkSmoke && PhotonNetwork.InRoom)
        {
            try
            {
                var s = PhotonNetwork.Instantiate(smokePrefab.name, pos, Quaternion.identity, 0);
                var sa = s.GetComponent<SmokeArea>();
                if (sa != null) sa.duration = smokeDuration;
            }
            catch
            {
                var s = Instantiate(smokePrefab, pos, Quaternion.identity);
                var sa = s.GetComponent<SmokeArea>();
                if (sa != null) sa.duration = smokeDuration;
            }
        }
        else
        {
            var s = Instantiate(smokePrefab, pos, Quaternion.identity);
            var sa = s.GetComponent<SmokeArea>();
            if (sa != null) { sa.duration = smokeDuration; sa.Initialize(); }
            else Destroy(s, smokeDuration);
        }
    }

    /// <summary>
    /// Call this when you spawned the hound via local Instantiate or want to ensure owner is set immediately.
    /// Safe to call after Awake() (which runs automatically on spawn).
    /// </summary>
    public void InitializeFromSpawner(int ownerActor, GameObject ownerGO,
                                    int dmg, float spd, float life,
                                    GameObject smokePrefabOverride, float smokeDur, float smokeInt)
    {
        ownerActorNumber = ownerActor;
        damage = dmg;
        speed = spd;
        lifetime = life;

        if (smokePrefabOverride != null) smokePrefab = smokePrefabOverride;
        smokeDuration = smokeDur;
        smokeInterval = smokeInt;

        // also forward to OwnedEntity if present
        var oe = GetComponent<OwnedEntity>();
        if (oe != null) oe.InitializeFromSpawner(ownerActor, ownerGO);

        // set velocity now that speed may have changed
        moveDir = transform.forward.normalized;
        if (rb == null) rb = GetComponent<Rigidbody>();
        if (rb != null) rb.velocity = moveDir * speed;

        // cache colliders if not already
        if (myColliders == null) myColliders = GetComponentsInChildren<Collider>(true);

        // set owner colliders from the provided owner GameObject and ignore collisions briefly
        if (ownerGO != null)
        {
            ownerColliders = ownerGO.GetComponentsInChildren<Collider>(true);
            if (ownerColliders != null && ownerColliders.Length > 0 && myColliders != null)
            {
                foreach (var a in myColliders)
                    foreach (var b in ownerColliders)
                        if (a != null && b != null) Physics.IgnoreCollision(a, b, true);

                // ensure re-enable after the usual grace period
                StartCoroutine(ReenableOwnerCollisionsAfter(ownerIgnoreSeconds));
            }
        }

        Debug.Log($"[HoundBehaviour] InitializeFromSpawner owner={ownerActor} dmg={dmg} speed={spd}");
    }
}
