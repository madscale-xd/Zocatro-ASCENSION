using UnityEngine;
using Photon.Pun;

/// <summary>
/// Robust hitbox handler. Put this on trigger colliders (head/body).
/// Requires bullet prefab to have BulletOwner (with ownerActorNumber) and Bullet (IsPooled) components.
/// </summary>
public class HitboxDamage : MonoBehaviour
{
    [Tooltip("Damage to apply when this hitbox is hit (used for body; used as fallback on head).")]
    public int damage = 15;

    [Tooltip("Check if this is the head hitbox. Headshots will multiply the body damage by 3.")]
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
            Debug.LogWarning($"[HitboxDamage] Hit by object tagged 'Bullet' but no BulletOwner found on parents of '{other.gameObject.name}'.");
            CleanupBullet(other);
            return;
        }

        // Find Bullet component (to check pooled status)
        Bullet bulletComp = other.GetComponentInParent<Bullet>();

        // Find PlayerHealth for this hitbox
        PlayerHealth ph = GetComponentInParent<PlayerHealth>();
        if (ph == null)
        {
            Debug.LogWarning($"[HitboxDamage] No PlayerHealth in parents of '{name}'.");
            CleanupBullet(other);
            return;
        }

        // Compute damage (headshot rules)
        int appliedDamage;
        if (isHead)
        {
            int bodyDamage = damage;
            if (ph.bodyCollider != null)
            {
                HitboxDamage bodyHb = ph.bodyCollider.GetComponent<HitboxDamage>();
                if (bodyHb != null) bodyDamage = bodyHb.damage;
            }
            appliedDamage = bodyDamage * 3;
        }
        else appliedDamage = damage;

        // Find target PhotonView and owner actor
        PhotonView targetPv = ph.GetComponent<PhotonView>();
        int targetActor = -1;
        if (targetPv != null && targetPv.Owner != null) targetActor = targetPv.Owner.ActorNumber;

        // Debug log: who hit who
        Debug.Log($"[HitboxDamage] Bullet by actor={bo.ownerActorNumber} hit playerActor={targetActor} ({ph.name}). appliedDamage={appliedDamage} isHead={isHead}");

        // Friendly-fire check
        if (ignoreFriendlyFire && bo.ownerActorNumber >= 0 && targetActor >= 0 && bo.ownerActorNumber == targetActor)
        {
            Debug.Log("[HitboxDamage] Ignored friendly fire (attacker == target).");
            CleanupBullet(other, bulletComp);
            return;
        }

        // If we have a Photon target owner, RPC the authoritative TakeDamage on the owner client.
        if (targetPv != null && PhotonNetwork.InRoom && targetPv.Owner != null)
        {
            // NOTE: this RPC is targeted at the owner of the target player, so damage is applied authoritatively.
            // It will call the owner's RPC_TakeDamage(amount, isHead, attackerActorNumber)
            targetPv.RPC("RPC_TakeDamage", targetPv.Owner, appliedDamage, isHead, bo.ownerActorNumber);

            Debug.Log($"[HitboxDamage] Sent RPC_TakeDamage to actor {targetPv.Owner.ActorNumber} (attacker {bo.ownerActorNumber}).");
        }
        else
        {
            // Offline / no PhotonView on target: apply locally
            ph.TakeDamage(appliedDamage, isHead);
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
}
