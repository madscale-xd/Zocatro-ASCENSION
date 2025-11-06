// HitboxDamage.cs
using UnityEngine;
using Photon.Pun;

/// <summary>
/// Handles applying damage when a projectile/bullet hits a player's hitbox.
/// IMPORTANT: only the projectile's owner (pv.IsMine) will report the hit to avoid duplicate RPCs.
/// Supports networked bullets (PhotonView + instantiationData) and local bullets.
/// </summary>
public class HitboxDamage : MonoBehaviour
{
    public int damage = 15;
    public bool isHead = false;
    public bool ignoreFriendlyFire = true;
    public LayerMask optionalLayerMask = Physics.AllLayers;

    void OnTriggerEnter(Collider other)
    {
        // Layer mask guard
        if (optionalLayerMask != Physics.AllLayers && ((1 << other.gameObject.layer) & optionalLayerMask) == 0)
            return;

        // only care about objects that actually are bullets in your setup
        if (!other.CompareTag("Bullet"))
            return;

        // Try to get the "owner" metadata from the bullet.
        // I try to support two common setups:
        //  - BulletOwner/Bullet component on the projectile's parent,
        //  - OwnedEntity on the projectile (alternative).
        var bo = other.GetComponentInParent<BulletOwner>();
        var bulletComp = other.GetComponentInParent<Bullet>();
        var ownedEntity = other.GetComponentInParent<OwnedEntity>();
        var bulletPv = other.GetComponentInParent<PhotonView>();

        // If we have a networked bullet, ONLY the bullet's owner (pv.IsMine) should process the collision.
        // This avoids multiple clients sending the same RPC to the victim owner.
        if (bulletPv != null && PhotonNetwork.InRoom)
        {
            if (!bulletPv.IsMine)
            {
                // Not the projectile's owner: ignore this collision entirely (do not call RPC or destroy).
                // Let the projectile owner handle the hit and cleanup.
                // Helpful debug to track duplicate reporting issues:
                Debug.Log($"[HitboxDamage] Ignoring collision on non-owner client for projectile PV id={bulletPv.ViewID}. (local actor={PhotonNetwork.LocalPlayer?.ActorNumber})");
                return;
            }
        }

        if (bo == null && ownedEntity != null)
        {
            // Some projects use OwnedEntity -> expose ownerActor
            bo = other.GetComponentInParent<BulletOwner>(); // keep bo null if none
        }

        if (bo == null && ownedEntity == null && bulletComp == null)
        {
            Debug.LogWarning($"[HitboxDamage] Bullet tagged object but missing BulletOwner/OwnedEntity/Bullet on parents of '{other.gameObject.name}'. Ignoring and cleaning up if local projectile.");
            // If it's a local projectile with no PV, we can still destroy it to avoid lingering objects.
            // But if it's networked, we should not attempt to destroy here (owner would handle).
            if (bulletPv == null && bulletComp != null)
            {
                CleanupBullet(other, bulletComp);
            }
            return;
        }

        // find player health on my player prefab parent chain (the hitbox is a child of the player)
        PlayerHealth ph = GetComponentInParent<PlayerHealth>();
        if (ph == null)
        {
            Debug.LogWarning($"[HitboxDamage] No PlayerHealth in parents of '{name}'.");
            // No player to damage; still allow owner to cleanup bullet if it is local-only
            if (bulletPv == null && bulletComp != null)
                CleanupBullet(other, bulletComp);
            return;
        }

        // body-ignore check (bullet configured to ignore body hits)
        if (!isHead && bo != null && bo.ignoreBodyHits)
        {
            Debug.Log("[HitboxDamage] Bullet configured to ignore body hits -> ignoring this body hit.");
            CleanupBullet(other, bulletComp);
            return;
        }

        // base damage/resolution (respect headshot multipliers if provided by bullet owner component)
        int baseBodyDamage = damage;
        if (isHead && ph.bodyCollider != null)
        {
            var bodyHb = ph.bodyCollider.GetComponent<HitboxDamage>();
            if (bodyHb != null) baseBodyDamage = bodyHb.damage;
        }

        float outgoingMult = 1f;
        float headMult = 3f;
        int attackerActorNumber = -1;

        if (bo != null)
        {
            outgoingMult = bo.outgoingDamageMultiplier;
            headMult = bo.headshotMultiplier;
            attackerActorNumber = bo.ownerActorNumber;
        }
        else if (ownedEntity != null)
        {
            // fallback: OwnedEntity only has ownerActor
            outgoingMult = 1f;
            attackerActorNumber = ownedEntity.ownerActor;
        }
        else if (bulletComp != null)
        {
            // fallback: Bullet component might expose multipliers
            outgoingMult = bulletComp.outgoingDamageMultiplier;
            headMult = bulletComp.headshotMultiplier;
            attackerActorNumber = bulletComp.ownerActorNumber;
        }

        int appliedDamage;
        if (isHead)
        {
            float raw = baseBodyDamage * headMult * outgoingMult;
            appliedDamage = Mathf.Max(0, Mathf.RoundToInt(raw));
        }
        else
        {
            float raw = damage * outgoingMult;
            appliedDamage = Mathf.Max(0, Mathf.RoundToInt(raw));
        }

        PhotonView targetPv = ph.GetComponent<PhotonView>();
        int targetActor = -1;
        if (targetPv != null && targetPv.Owner != null) targetActor = targetPv.Owner.ActorNumber;

        Debug.Log($"[HitboxDamage] Bullet by actor={attackerActorNumber} hit playerActor={targetActor} ({ph.name}). appliedDamage={appliedDamage} isHead={isHead}");

        // Friendly fire ignore
        if (ignoreFriendlyFire && attackerActorNumber >= 0 && targetActor >= 0 && attackerActorNumber == targetActor)
        {
            Debug.Log("[HitboxDamage] Ignored friendly fire (attacker == target).");
            CleanupBullet(other, bulletComp);
            return;
        }

        // Report damage: for networked players call RPC to owner; for local targets apply directly.
        if (targetPv != null && PhotonNetwork.InRoom && targetPv.Owner != null)
        {
            try
            {
                // This call will be executed on the target's owner client (the player who should apply authoritative damage).
                // Because we enforced "only projectile owner reports", the target will only get a single RPC per hit.
                targetPv.RPC("RPC_TakeDamage", targetPv.Owner, appliedDamage, isHead, attackerActorNumber);
                Debug.Log($"[HitboxDamage] Sent RPC_TakeDamage to actor {targetPv.Owner.ActorNumber} (attacker {attackerActorNumber}).");
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning("[HitboxDamage] RPC failed, applying damage locally as fallback: " + ex);
                ph.TakeDamage(appliedDamage, isHead);
            }
        }
        else
        {
            // offline/local fallback
            ph.TakeDamage(appliedDamage, isHead);
            Debug.Log("[HitboxDamage] Applied damage locally (no PhotonView/owner).");
        }

        // Cleanup: only the projectile owner should actually destroy networked bullets.
        // If this client was the bullet owner (pv==null OR pv.IsMine), CleanupBullet will either call PhotonNetwork.Destroy or Destroy local instance.
        CleanupBullet(other, bulletComp);
    }

    void CleanupBullet(Collider bulletCollider, Bullet bulletComp = null)
    {
        if (bulletCollider == null) return;

        if (bulletComp == null)
            bulletComp = bulletCollider.GetComponentInParent<Bullet>();

        PhotonView pv = bulletCollider.GetComponentInParent<PhotonView>();
        if (pv != null && PhotonNetwork.InRoom)
        {
            // only the owning client should call PhotonNetwork.Destroy — other clients should not attempt to remove it
            if (pv.IsMine)
            {
                try { PhotonNetwork.Destroy(pv.gameObject); }
                catch { if (pv.gameObject != null) Destroy(pv.gameObject); }
            }
            else
            {
                // non-owner: do not try to destroy networked object — just return
            }
            return;
        }

        // Non-networked fallback: destroy local instance
        if (bulletComp != null)
        {
            Destroy(bulletComp.gameObject);
            return;
        }

        GameObject root = bulletCollider.transform.root.gameObject;
        Destroy(root);
    }
}
