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
        // read instantiation data if present
        if (photonView != null && photonView.InstantiationData != null && photonView.InstantiationData.Length >= 1)
        {
            var d = photonView.InstantiationData;
            if (d.Length >= 1 && d[0] is int) ownerActor = (int)d[0];
            if (d.Length >= 2)
            {
                if (d[1] is int) hp = (int)d[1];
                else if (d[1] is float) hp = Mathf.RoundToInt((float)d[1]);
            }
        }
    }

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

        // attempt to read damage value from projectile
        float dmg = GetDamageFromObject(other);
        if (Mathf.Approximately(dmg, 0f)) dmg = defaultProjectileDamage;

        // apply damage to the wall
        TakeDamage(dmg);

        // destroy or deactivate the projectile
        // prefer networked destroy when appropriate
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

        // If nothing found, attempt SendMessage "GetDamage" which some projectiles implement.
        // Note: SendMessage can't return a value; it's just an opportunity for the projectile to react.
        // As fallback, return 0 so caller substitutes defaultProjectileDamage.
        try
        {
            object response = null;
            // Not reliable — kept for compatibility with some setups that implement GetDamage through another pattern.
            // Most robust path is the field/property extraction above.
        }
        catch { }

        return 0f;
    }

    // Generic public damage entrypoint (recommended: projectiles or other scripts call SendMessage("TakeDamage", amount))
    public void TakeDamage(float amount)
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

        if (PhotonNetwork.InRoom)
        {
            try { PhotonNetwork.Destroy(gameObject); } catch { Destroy(gameObject); }
        }
        else
        {
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
