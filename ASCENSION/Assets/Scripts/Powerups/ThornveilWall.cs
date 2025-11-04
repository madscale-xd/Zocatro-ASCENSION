using System.Linq;
using UnityEngine;
using Photon.Pun;

/// <summary>
/// Thornveil wall:
/// - HP (default 300)
/// - Blocks projectiles (use non-trigger collider)
/// - Accepts damage via TakeDamage(float)
/// - Supports Photon instantiation data (ownerActor, hp) OR local InitializeFromSpawner
/// - Handles both OnCollisionEnter and OnTriggerEnter
/// - Configurable projectile tags (supports 'bullet')
/// - Network/owner-aware: damage application and destruction are routed to the owner for authority.
/// </summary>
public class ThornveilWall : MonoBehaviourPunCallbacks
{
    [Header("Wall")]
    public int hp = 300;
    public int ownerActor = -1;
    public GameObject destroyEffect;

    [Header("Projectile detection")]
    [Tooltip("Tags considered as projectiles. Add 'bullet' if your bullets are tagged that way.")]
    public string[] projectileTags = new string[] { "Projectile", "bullet", "Bullet" };
    [Tooltip("Default damage to apply if projectile does not expose damage field/property.")]
    public float defaultProjectileDamage = 10f;

    void Awake()
    {
        // If an OwnedEntity component exists, prefer its owner info (useful for local Instantiate fallback)
        var oe = GetComponent<OwnedEntity>();
        if (oe != null && oe.ownerActor >= 0)
            ownerActor = oe.ownerActor;

        // read Photon instantiation data if present
        if (photonView != null && photonView.InstantiationData != null && photonView.InstantiationData.Length >= 1)
        {
            var d = photonView.InstantiationData;
            // [0] ownerActor, [1] hp (optional)
            if (d.Length >= 1)
            {
                // best-effort parsing (instantiation data can be boxed as different numeric types)
                int parsedOwner = -1;
                int.TryParse(d[0].ToString(), out parsedOwner);
                if (parsedOwner >= 0) ownerActor = parsedOwner;
            }

            if (d.Length >= 2)
            {
                // parse hp
                int parsedHp = hp;
                if (d[1] is int) parsedHp = (int)d[1];
                else
                {
                    float tmp;
                    if (float.TryParse(d[1].ToString(), out tmp)) parsedHp = Mathf.RoundToInt(tmp);
                }
                hp = parsedHp;
            }
        }
    }

    /// <summary>
    /// Local initializer used by CharacterSkills when not using PhotonNetwork.Instantiate.
    /// </summary>
    public void InitializeFromSpawner(int ownerActor_, GameObject ownerObj_, int hpValue)
    {
        ownerActor = ownerActor_;
        hp = hpValue;
    }

    // Called when projectile uses physics collision
    private void OnCollisionEnter(Collision collision)
    {
        HandlePotentialProjectileCollision(collision.gameObject, collision);
    }

    // Called when projectile uses triggers (common for fast bullets)
    private void OnTriggerEnter(Collider other)
    {
        HandlePotentialProjectileCollision(other.gameObject, null);
    }

    private void HandlePotentialProjectileCollision(GameObject other, Collision collision)
    {
        if (other == null) return;

        // Only consider objects whose tag is listed in projectileTags
        bool tagMatch = projectileTags != null && projectileTags.Any(t => !string.IsNullOrEmpty(t) && other.CompareTag(t));
        if (!tagMatch) return;

        // If the projectile has an OwnedEntity, skip friendly projectiles
        var projOwned = other.GetComponentInChildren<OwnedEntity>();
        if (projOwned != null && projOwned.ownerActor >= 0 && ownerActor >= 0 && projOwned.ownerActor == ownerActor)
        {
            // projectile belongs to same owner as wall -> ignore friendly hit
            return;
        }

        // Also skip projectiles whose PhotonView owner's actor matches us (extra safety)
        var projPv = other.GetComponentInParent<PhotonView>();
        if (projPv != null && projPv.Owner != null && ownerActor >= 0 && projPv.Owner.ActorNumber == ownerActor)
        {
            return;
        }

        // attempt to read damage value from projectile
        float dmg = GetDamageFromObject(other);
        if (Mathf.Approximately(dmg, 0f)) dmg = defaultProjectileDamage;

        // apply damage to the wall (authoritative if networked)
        TakeDamage(dmg);

        // destroy or deactivate the projectile
        // prefer networked destroy when appropriate (only attempt PhotonNetwork.Destroy if this client owns the projectile)
        if (PhotonNetwork.InRoom)
        {
            var pv = other.GetComponent<PhotonView>();
            if (pv != null && pv.IsMine)
            {
                try { PhotonNetwork.Destroy(other); return; } catch { /* fallback to local destroy */ }
            }
        }

        Destroy(other);
    }

    private float GetDamageFromObject(GameObject obj)
    {
        // Try to find a component with a field/property named damage/Damage/dmg/Dmg.
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

        return 0f;
    }

    // Public entry point for taking damage. Routes to owner if networked.
    public void TakeDamage(float amount)
    {
        // If this object is networked and has an owner, route to owner for authoritative application
        if (PhotonNetwork.InRoom && photonView != null && photonView.Owner != null)
        {
            try
            {
                // Request owner to apply damage via RPC (owner will run RPC_ApplyDamage and apply locally)
                photonView.RPC(nameof(RPC_ApplyDamage), photonView.Owner, amount);
                return;
            }
            catch
            {
                // If RPC fails, fall back to local apply
            }
        }

        // local mode or fallback
        ApplyDamageLocally(amount);
    }

    [PunRPC]
    private void RPC_ApplyDamage(float amount, PhotonMessageInfo info)
    {
        // Only the owner should apply damage locally when networked
        if (PhotonNetwork.InRoom)
        {
            if (photonView != null && photonView.IsMine)
            {
                ApplyDamageLocally(amount);
            }
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
        if (destroyEffect != null) Instantiate(destroyEffect, transform.position, Quaternion.identity);

        if (PhotonNetwork.InRoom && photonView != null)
        {
            // Ask the owner to destroy the networked object (owner-authoritative)
            if (photonView.Owner != null)
            {
                try
                {
                    photonView.RPC(nameof(RPC_RequestDestroy), photonView.Owner);
                    return;
                }
                catch
                {
                    // fallback below
                }
            }

            // If owner is missing (left), allow MasterClient to destroy it locally
            if (PhotonNetwork.IsMasterClient)
            {
                try { PhotonNetwork.Destroy(gameObject); return; } catch { /* fallback */ }
            }

            // last-resort: local destroy
            Destroy(gameObject);
        }
        else
        {
            Destroy(gameObject);
        }
    }

    // Called on the owner via RPC to perform the actual network destroy
    [PunRPC]
    private void RPC_RequestDestroy(PhotonMessageInfo info)
    {
        // Only the owner should execute the network destroy
        if (photonView != null && photonView.IsMine)
        {
            try { PhotonNetwork.Destroy(gameObject); }
            catch { Destroy(gameObject); }
        }
        else
        {
            // If this client is not the owner, fallback to local destroy to avoid orphan objects
            Destroy(gameObject);
        }
    }

    /// <summary>
    /// Aligns this ThornveilWall so it sits on the given hit surface.
    /// If parentToSurface is true, the wall will be parented to the hit collider's transform (keeps it attached to moving geometry).
    /// </summary>
    public void PlaceOnGround(RaycastHit hit, bool parentToSurface = false)
    {
        if (hit.collider == null) return;

        // Slightly lift above surface to avoid z-fighting
        transform.position = hit.point + hit.normal * 0.01f;

        // Choose a forward direction projected onto the ground plane (so the wall stands upright)
        Vector3 forward = Vector3.ProjectOnPlane(Camera.main != null ? Camera.main.transform.forward : transform.forward, hit.normal).normalized;
        if (forward.sqrMagnitude < 0.0001f) forward = Vector3.forward;

        transform.rotation = Quaternion.LookRotation(forward, hit.normal);

        if (parentToSurface)
        {
            transform.SetParent(hit.collider.transform, true);
        }
    }
}
