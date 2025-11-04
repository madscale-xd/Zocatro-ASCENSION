using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Photon.Pun;
using System;

/// <summary>
/// Sporeward turret (detection collider + projectile-ignore support)
/// - Uses OverlapSphere scanning for detection (no trigger required on main collider).
/// - Prefers child tagged "Hitbox" for aim points.
/// - When spawning a projectile, ignores collisions between that projectile and this turret's physical colliders.
/// - Handles incoming projectile collisions/triggers and applies damage (authoritatively via RPC to owner).
/// </summary>
[RequireComponent(typeof(Collider))]
public class SporewardTurret : MonoBehaviourPunCallbacks
{
    [Header("Turret stats")]
    public int hp = 50;
    [Tooltip("Used only if no detectionCollider is assigned.")]
    public float detectionRadius = 4f;
    public float fireRate = 1f; // shots per second
    public float damage = 12f;
    public GameObject projectilePrefab; // optional visual projectile (must be in Resources/ to Photon instantiate)
    public float projectileSpeed = 18f;
    public bool applyHitscanIfNoProjectile = true;

    [Header("Aim tuning")]
    [Tooltip("Fallback vertical offset (meters) from target.position when no collider or bone is found.")]
    public float aimHeightOffset = 1.1f;
    [Tooltip("Optional muzzle offset from turret position when spawning a projectile.")]
    public Vector3 muzzleLocalOffset = new Vector3(0f, 0.2f, 0.2f);

    [Header("Detection settings")]
    [Tooltip("Optional: assign a child SphereCollider here to be used as the detection zone. If null, detectionRadius (centered on this.transform) is used.")]
    public SphereCollider detectionCollider;
    [Tooltip("How often (seconds) to refresh the detection list.")]
    public float detectionScanInterval = 0.12f;
    [Tooltip("Optional layer mask to speed up OverlapSphere (set to players layer if possible).")]
    public LayerMask detectionLayerMask = ~0;

    [Header("Projectile detection")]
    [Tooltip("Tags considered as projectiles. Add 'bullet' if your bullets are tagged that way.")]
    public string[] projectileTags = new string[] { "Projectile", "bullet", "Bullet" };
    [Tooltip("Default damage to apply if projectile does not expose damage field/property.")]
    public float defaultProjectileDamage = 10f;

    // owner filtering
    private int ownerActor = -1;
    private GameObject ownerObject;

    // target bookkeeping (store player ROOT transforms)
    private readonly List<Transform> entrants = new List<Transform>();
    private Transform currentTarget;

    private Coroutine shootingCoroutine;
    private Coroutine detectionCoroutine;

    // cached turret colliders (physical colliders that projectiles should ignore)
    private Collider[] turretPhysicalColliders;

    void Awake()
    {
        // cache turret colliders (exclude the detectionCollider if assigned)
        var allCols = GetComponentsInChildren<Collider>(true);
        if (detectionCollider != null)
        {
            var tmp = new List<Collider>();
            foreach (var c in allCols)
            {
                if (c == detectionCollider) continue;
                tmp.Add(c);
            }
            turretPhysicalColliders = tmp.ToArray();
        }
        else
        {
            turretPhysicalColliders = allCols;
        }

        // read Photon instantiation data if present (ownerActor, hp)
        if (photonView != null && photonView.InstantiationData != null && photonView.InstantiationData.Length >= 1)
        {
            object[] d = photonView.InstantiationData;
            if (d.Length >= 1)
            {
                try { ownerActor = Convert.ToInt32(d[0]); } catch { }
            }
            if (d.Length >= 2)
            {
                if (d[1] is int) hp = (int)d[1];
                else if (d[1] is float) hp = Mathf.RoundToInt((float)d[1]);
            }
        }

        // OwnedEntity fallback
        var oe = GetComponent<OwnedEntity>();
        if (oe != null && oe.ownerActor >= 0) ownerActor = oe.ownerActor;
        if (oe != null && oe.ownerGameObject != null) ownerObject = oe.ownerGameObject;
    }

    void Start()
    {
        // ensure we only run detection/shooting on the client that owns this network object (if in room)
        if (PhotonNetwork.InRoom && photonView != null && !photonView.IsMine)
            return;

        // start detection and shooting loops (owner-only)
        detectionCoroutine = StartCoroutine(DetectionLoop());
        shootingCoroutine = StartCoroutine(ShootingLoop());
    }

    void OnDisable()
    {
        if (detectionCoroutine != null) StopCoroutine(detectionCoroutine);
        if (shootingCoroutine != null) StopCoroutine(shootingCoroutine);
    }

    // Called when spawned locally without Photon; lets CharacterSkills initialize fields
    public void InitializeFromSpawner(int ownerActor_, GameObject ownerObj_, int hp_)
    {
        ownerActor = ownerActor_;
        ownerObject = ownerObj_;
        hp = hp_;
    }

    /// <summary>
    /// Aligns the turret to a RaycastHit surface (position + normal). Optionally parents to the hit collider's transform.
    /// Call this on locally-instantiated turrets (SpawnPrefab fallback) or for preview placement.
    /// </summary>
    public void PlaceOnSurface(RaycastHit hit, bool parentToSurface = false)
    {
        transform.position = hit.point + hit.normal * 0.01f;
        transform.rotation = Quaternion.LookRotation(-hit.normal, Vector3.up);
        if (parentToSurface && hit.collider != null)
        {
            transform.SetParent(hit.collider.transform, true);
        }
    }

    // ----- Detection via OverlapSphere -----
    private IEnumerator DetectionLoop()
    {
        while (true)
        {
            entrants.Clear();

            // Determine center & radius
            Vector3 center;
            float radius;
            if (detectionCollider != null)
            {
                center = detectionCollider.bounds.center;
                radius = detectionCollider.radius * Mathf.Max(
                    detectionCollider.transform.lossyScale.x,
                    detectionCollider.transform.lossyScale.y,
                    detectionCollider.transform.lossyScale.z);
            }
            else
            {
                center = transform.position;
                radius = detectionRadius;
            }

            // OverlapSphere with layer mask
            var colliders = Physics.OverlapSphere(center, radius, detectionLayerMask);
            foreach (var c in colliders)
            {
                if (c == null || c.gameObject == null) continue;

                Transform playerRoot = GetPlayerRootFromCollider(c);
                if (playerRoot == null) continue;
                if (!IsValidPlayerRoot(playerRoot)) continue;

                if (!entrants.Contains(playerRoot))
                    entrants.Add(playerRoot);
            }

            // choose most-recent entrant (last in list)
            currentTarget = (entrants.Count > 0) ? entrants[entrants.Count - 1] : null;

            yield return new WaitForSeconds(detectionScanInterval);
        }
    }

    private IEnumerator ShootingLoop()
    {
        while (true)
        {
            if (currentTarget != null)
            {
                Vector3 aimPoint = GetTargetAimPoint(currentTarget);
                if (projectilePrefab != null)
                {
                    // spawn projectile from a muzzle point and aim toward aimPoint
                    Vector3 spawnPos = transform.TransformPoint(muzzleLocalOffset);
                    Quaternion rot = Quaternion.LookRotation((aimPoint - spawnPos).normalized);
                    Vector3 dir = rot * Vector3.forward;

                    GameObject p = null;

                    // prepare instantiationData for networked projectile (ownerActor, damage, speed)
                    object[] instData = new object[] { ownerActor, (float)damage, projectileSpeed };

                    if (PhotonNetwork.InRoom)
                    {
                        // try Photon instantiate (prefab must be in Resources/ and have a PhotonView)
                        try
                        {
                            p = PhotonNetwork.Instantiate(projectilePrefab.name, spawnPos, rot, 0, instData);
                        }
                        catch (Exception ex)
                        {
                            Debug.LogWarning($"SporewardTurret: Photon.Instantiate failed for '{projectilePrefab.name}': {ex.Message}. Falling back to local Instantiate.");
                            p = Instantiate(projectilePrefab, spawnPos, rot);
                        }
                    }
                    else
                    {
                        // offline/local fallback
                        try { p = Instantiate(projectilePrefab, spawnPos, rot); } catch { p = null; }
                    }

                    if (p != null)
                    {
                        // set velocity if Rigidbody present
                        var rb = p.GetComponent<Rigidbody>();
                        if (rb != null)
                            rb.velocity = dir.normalized * projectileSpeed;

                        // ensure turret's own colliders don't block the projectile
                        IgnoreProjectileWithTurret(p);

                        // If projectile has OwnedEntity or expects owner data, initialize it
                        var oe = p.GetComponent<OwnedEntity>();
                        if (oe != null)
                        {
                            try { oe.InitializeFromSpawner(ownerActor, ownerObject); } catch { }
                        }

                        // If projectile has a simple script expecting InitializeFromSpawner, try to call it (reflection-safe)
                        var mono = p.GetComponent<MonoBehaviour>();
                        if (mono != null)
                        {
                            var m = mono.GetType().GetMethod("InitializeFromSpawner");
                            if (m != null)
                            {
                                try { m.Invoke(mono, new object[] { ownerActor, ownerObject, Mathf.RoundToInt(damage) }); } catch { }
                            }
                        }
                    }
                }
                else if (applyHitscanIfNoProjectile)
                {
                    // instant damage application (send to targeted gameobject)
                    try
                    {
                        currentTarget.gameObject.SendMessage("TakeDamage", damage, SendMessageOptions.DontRequireReceiver);
                    }
                    catch { }
                }
            }

            yield return new WaitForSeconds(1f / Mathf.Max(0.0001f, fireRate));
        }
    }

    // Ignore collisions between all colliders on projectile and all physical colliders on this turret
    private void IgnoreProjectileWithTurret(GameObject projectile)
    {
        if (projectile == null || turretPhysicalColliders == null || turretPhysicalColliders.Length == 0) return;

        var projCols = projectile.GetComponentsInChildren<Collider>(true);
        if (projCols == null || projCols.Length == 0) return;

        foreach (var pc in projCols)
        {
            if (pc == null) continue;
            foreach (var tc in turretPhysicalColliders)
            {
                if (tc == null) continue;
                Physics.IgnoreCollision(pc, tc, true);
            }
        }
    }

    // ----- Incoming projectile collision handling -----
    private void OnCollisionEnter(Collision collision)
    {
        HandlePotentialProjectileCollision(collision.gameObject, collision);
    }

    private void HandlePotentialProjectileCollision(GameObject other, Collision collision)
    {
        if (other == null) return;

        // Consider other objects matching projectileTags.
        bool tagMatch = projectileTags != null && projectileTags.Any(t => !string.IsNullOrEmpty(t) && other.CompareTag(t));
        if (!tagMatch) return;

        // If the projectile has ownership info, skip if it belongs to the turret owner (no friendly fire)
        var projOwned = other.GetComponentInChildren<OwnedEntity>();
        if (projOwned != null && projOwned.ownerActor >= 0 && ownerActor >= 0 && projOwned.ownerActor == ownerActor)
        {
            // projectile belongs to same owner as turret -> ignore
            return;
        }

        // attempt to read damage value from projectile
        float dmg = GetDamageFromObject(other);
        if (Mathf.Approximately(dmg, 0f)) dmg = defaultProjectileDamage;

        // apply damage (authoritative via RPC to owner of this turret)
        TakeDamage(dmg);

        // destroy the projectile (prefer networked destroy if applicable)
        if (PhotonNetwork.InRoom)
        {
            var pv = other.GetComponent<PhotonView>();
            if (pv != null && pv.IsMine)
            {
                try { PhotonNetwork.Destroy(other); return; } catch { /* fallback */ }
            }
        }

        Destroy(other);
    }

    private float GetDamageFromObject(GameObject obj)
    {
        var monos = obj.GetComponents<MonoBehaviour>();
        foreach (var m in monos)
        {
            if (m == null) continue;
            var t = m.GetType();

            // fields
            var f = t.GetField("damage");
            if (f != null)
            {
                var val = f.GetValue(m);
                if (val is float) return (float)val;
                if (val is int) return (int)val;
                if (val is double) return (float)(double)val;
            }
            f = t.GetField("Damage");
            if (f != null)
            {
                var val = f.GetValue(m);
                if (val is float) return (float)val;
                if (val is int) return (int)val;
            }
            f = t.GetField("dmg");
            if (f != null)
            {
                var val = f.GetValue(m);
                if (val is float) return (float)val;
                if (val is int) return (int)val;
            }

            // properties
            var p = t.GetProperty("damage");
            if (p != null)
            {
                var val = p.GetValue(m, null);
                if (val is float) return (float)val;
                if (val is int) return (int)val;
            }
            p = t.GetProperty("Damage");
            if (p != null)
            {
                var val = p.GetValue(m, null);
                if (val is float) return (float)val;
                if (val is int) return (int)val;
            }
            p = t.GetProperty("dmg");
            if (p != null)
            {
                var val = p.GetValue(m, null);
                if (val is float) return (float)val;
                if (val is int) return (int)val;
            }
        }

        // nothing found
        return 0f;
    }

    // ----- Damage application + RPC authority -----
    public void TakeDamage(float amount)
    {
        if (PhotonNetwork.InRoom && photonView != null && photonView.Owner != null)
        {
            // route to owner for authoritative application
            photonView.RPC(nameof(RPC_ApplyDamage), photonView.Owner, amount);
            return;
        }

        // local mode
        ApplyDamageLocally(amount);
    }

    [PunRPC]
    private void RPC_ApplyDamage(float amount, PhotonMessageInfo info)
    {
        if (PhotonNetwork.InRoom)
        {
            // only owner should apply locally
            if (photonView != null && photonView.IsMine)
                ApplyDamageLocally(amount);
        }
        else
        {
            ApplyDamageLocally(amount);
        }
    }

    private void ApplyDamageLocally(float amount)
    {
        hp -= Mathf.RoundToInt(amount);
        if (hp <= 0)
        {
            Die();
        }
    }

    private void Die()
    {
        // play VFX / sfx here if desired

        if (PhotonNetwork.InRoom)
        {
            // networked destroy if possible
            try { PhotonNetwork.Destroy(gameObject); } catch { Destroy(gameObject); }
        }
        else
        {
            Destroy(gameObject);
        }
    }

    // ----- Player root detection helpers (same as before) -----
    private Transform GetPlayerRootFromCollider(Collider c)
    {
        if (c == null) return null;
        if (c.gameObject.CompareTag("Player"))
            return c.transform;

        Transform root = c.transform;
        while (root.parent != null)
        {
            root = root.parent;
        }
        if (root != null && root.gameObject.CompareTag("Player"))
            return root;

        return null;
    }

    private bool IsValidPlayerRoot(Transform playerRoot)
    {
        if (playerRoot == null || playerRoot.gameObject == null) return false;
        if (!playerRoot.gameObject.CompareTag("Player")) return false;
        if (ownerObject != null && playerRoot.gameObject == ownerObject) return false;
        var pv = playerRoot.GetComponent<PhotonView>();
        if (pv != null && ownerActor >= 0)
        {
            if (pv.Owner != null && pv.Owner.ActorNumber == ownerActor) return false;
        }
        return true;
    }

    private Vector3 GetTargetAimPoint(Transform playerRoot)
    {
        if (playerRoot == null) return transform.position;

        Transform bestHitbox = FindClosestTaggedChild(playerRoot, "Hitbox");
        if (bestHitbox != null)
        {
            var c = bestHitbox.GetComponent<Collider>();
            if (c != null) return c.bounds.center;
            return bestHitbox.position;
        }

        var rootCollider = playerRoot.GetComponent<Collider>();
        if (rootCollider != null) return rootCollider.bounds.center;

        string[] names = { "Chest", "Torso", "Spine", "UpperChest", "Head", "Hips", "Pelvis" };
        foreach (var n in names)
        {
            var t = playerRoot.Find(n) ?? playerRoot.root?.Find(n);
            if (t != null) return t.position;
        }

        var ap = playerRoot.Find("AimPoint") ?? playerRoot.root?.Find("AimPoint");
        if (ap != null) return ap.position;

        return playerRoot.position + Vector3.up * aimHeightOffset;
    }

    private Transform FindClosestTaggedChild(Transform parent, string tag)
    {
        if (parent == null || string.IsNullOrEmpty(tag)) return null;

        Transform best = null;
        float bestDist = float.MaxValue;
        var children = parent.GetComponentsInChildren<Transform>(true);
        foreach (var t in children)
        {
            if (t == null || t.gameObject == null) continue;
            if (!t.gameObject.CompareTag(tag)) continue;
            float d = Vector3.Distance(transform.position, t.position);
            if (d < bestDist)
            {
                bestDist = d;
                best = t;
            }
        }
        return best;
    }

    // safety: if destroyed, stop coroutines
    void OnDestroy()
    {
        if (detectionCoroutine != null) StopCoroutine(detectionCoroutine);
        if (shootingCoroutine != null) StopCoroutine(shootingCoroutine);
    }
}
