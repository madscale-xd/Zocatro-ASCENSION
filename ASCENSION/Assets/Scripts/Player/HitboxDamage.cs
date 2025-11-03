// HitboxDamage.cs
using UnityEngine;
using Photon.Pun;

/// <summary>
/// Robust hitbox handler. Put this on trigger colliders (head/body).
/// Requires the bullet prefab to have BulletOwner (ownerActorNumber + optional metadata) and Bullet components.
/// This file contains a tiny DamageInfo + DamagePipeline to compute final outgoing damage
/// using metadata attached to the bullet by the shooter (headshot multipliers, ignore-body flag, etc).
/// </summary>
public class HitboxDamage : MonoBehaviour
{
    [Tooltip("Damage to apply when this hitbox is hit (used for body; used as fallback on head).")]
    public int damage = 15;

    [Tooltip("Check if this is the head hitbox. Headshots traditionally multiply the body damage.")]
    public bool isHead = false;

    [Tooltip("If true, bullets fired by the same player will not damage that player.")]
    public bool ignoreFriendlyFire = true;

    // Optional: restrict which layers the hitbox responds to (set in inspector). If left empty, defaults to all.
    public LayerMask optionalLayerMask = Physics.AllLayers;

    void OnTriggerEnter(Collider other)
    {
        // Quick layer check (skip if user set a mask and the other isn't in it)
        if (optionalLayerMask != Physics.AllLayers && ((1 << other.gameObject.layer) & optionalLayerMask) == 0)
            return;

        // Only react to bullets — rely on tag so child colliders can pass the check
        if (!other.CompareTag("Bullet"))
            return;

        // Find BulletOwner (search parent chain). Be defensive.
        BulletOwner bo = other.GetComponentInParent<BulletOwner>();
        if (bo == null)
        {
            Debug.LogWarning($"[HitboxDamage] Hit by 'Bullet' but no BulletOwner on parents of '{other.gameObject.name}'.");
            CleanupBullet(other);
            return;
        }

        // Find Bullet component (to check pooled status)
        Bullet bulletComp = other.GetComponentInParent<Bullet>();

        // Find PlayerHealth for this hitbox (the victim)
        PlayerHealth ph = GetComponentInParent<PlayerHealth>();
        if (ph == null)
        {
            Debug.LogWarning($"[HitboxDamage] No PlayerHealth in parents of '{name}'.");
            CleanupBullet(other);
            return;
        }

        // Determine the base damage to use (head should fallback to body damage value if present)
        int baseDamage = damage;
        if (isHead && ph.bodyCollider != null)
        {
            var bodyHb = ph.bodyCollider.GetComponent<HitboxDamage>();
            if (bodyHb != null) baseDamage = bodyHb.damage;
        }

        // Build DamageInfo
        DamageInfo dinfo = new DamageInfo()
        {
            baseDamage = baseDamage,
            isHead = isHead,
            attackerActor = bo != null ? bo.ownerActorNumber : -1
        };

        // Let DamagePipeline compute the final outgoing damage using the bullet metadata (if present).
        int finalDamage = DamagePipeline.ProcessOutgoingDamage(dinfo, bo);

        // Determine target PhotonView owner actor
        PhotonView targetPv = ph.GetComponent<PhotonView>();
        int targetActor = -1;
        if (targetPv != null && targetPv.Owner != null) targetActor = targetPv.Owner.ActorNumber;

        Debug.Log($"[HitboxDamage] Bullet by actor={dinfo.attackerActor} hit playerActor={targetActor} ({ph.name}). baseDamage={dinfo.baseDamage} finalDamage={finalDamage} isHead={isHead}");

        // Friendly-fire check
        if (ignoreFriendlyFire && dinfo.attackerActor >= 0 && targetActor >= 0 && dinfo.attackerActor == targetActor)
        {
            Debug.Log("[HitboxDamage] Ignored friendly fire (attacker == target).");
            CleanupBullet(other, bulletComp);
            return;
        }

        // Send the numeric finalDamage to the target owner (authoritative on owner client)
        if (targetPv != null && PhotonNetwork.InRoom && targetPv.Owner != null)
        {
            targetPv.RPC("RPC_TakeDamage", targetPv.Owner, finalDamage, isHead, dinfo.attackerActor);
            Debug.Log($"[HitboxDamage] Sent RPC_TakeDamage to actor {targetPv.Owner.ActorNumber} (attacker {dinfo.attackerActor}).");
        }
        else
        {
            // Offline / no PhotonView on target: apply locally
            ph.TakeDamage(finalDamage, isHead);
            Debug.Log("[HitboxDamage] Applied damage locally (no PhotonView/owner).");
        }

        // Cleanup bullet — use Bullet component to decide whether to deactivate or destroy
        CleanupBullet(other, bulletComp);
    }

    void CleanupBullet(Collider bulletCollider, Bullet bulletComp = null)
    {
        if (bulletCollider == null) return;

        if (bulletComp == null)
            bulletComp = bulletCollider.GetComponentInParent<Bullet>();

        if (bulletComp != null && bulletComp.IsPooled)
        {
            // returned to pool
            bulletComp.Deactivate();
            return;
        }

        // Non-pooled bullet or unknown: destroy the root bullet GameObject
        GameObject root = bulletCollider.transform.root.gameObject;
        Destroy(root);
    }

    #region DamageInfo & pipeline (lightweight)
    /// <summary>
    /// Minimal damage transport structure used locally before the final numeric damage is calculated.
    /// </summary>
    public struct DamageInfo
    {
        public int baseDamage;
        public bool isHead;
        public int attackerActor;
    }

    /// <summary>
    /// Lightweight damage pipeline that inspects BulletOwner metadata and returns a final integer damage value.
    /// This is intentionally simple — it reads headshotMultiplier, outgoingDamageMultiplier, ignoreBodyHits from the bullet metadata.
    /// If the metadata isn't present, it falls back to legacy behavior (headMultiplier = 3, outgoing multiplier = 1).
    /// </summary>
    public static class DamagePipeline
    {
        public static int ProcessOutgoingDamage(DamageInfo info, BulletOwner bulletOwner)
        {
            // Defaults (legacy)
            float headMultiplier = 3f;
            float outgoingMult = 1f;
            bool ignoreBody = false;

            if (bulletOwner != null)
            {
                // Use provided metadata if valid
                if (bulletOwner.headshotMultiplier > 0f) headMultiplier = bulletOwner.headshotMultiplier;
                if (bulletOwner.outgoingDamageMultiplier > 0f) outgoingMult = bulletOwner.outgoingDamageMultiplier;
                ignoreBody = bulletOwner.ignoreBodyHits;
            }

            // If body hits are ignored (e.g. Justice), then a non-head hit does no damage.
            if (!info.isHead && ignoreBody)
                return 0;

            // Compute base result
            float raw = info.baseDamage * outgoingMult;

            if (info.isHead)
                raw *= headMultiplier;

            // Ensure at least 0 and convert to int (ceil to favor gameplay)
            int finalDamage = Mathf.CeilToInt(Mathf.Max(0f, raw));
            return finalDamage;
        }
    }
    #endregion
}
