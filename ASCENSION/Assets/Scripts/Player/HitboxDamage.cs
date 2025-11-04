// HitboxDamage.cs
using UnityEngine;
using Photon.Pun;

public class HitboxDamage : MonoBehaviour
{
    public int damage = 15;
    public bool isHead = false;
    public bool ignoreFriendlyFire = true;
    public LayerMask optionalLayerMask = Physics.AllLayers;

    void OnTriggerEnter(Collider other)
    {
        if (optionalLayerMask != Physics.AllLayers && ((1 << other.gameObject.layer) & optionalLayerMask) == 0)
            return;

        if (!other.CompareTag("Bullet"))
            return;

        BulletOwner bo = other.GetComponentInParent<BulletOwner>();
        if (bo == null)
        {
            Debug.LogWarning($"[HitboxDamage] Hit by 'Bullet' but no BulletOwner on parents of '{other.gameObject.name}'. Cleaning up bullet.");
            CleanupBullet(other);
            return;
        }

        Bullet bulletComp = other.GetComponentInParent<Bullet>();

        PlayerHealth ph = GetComponentInParent<PlayerHealth>();
        if (ph == null)
        {
            Debug.LogWarning($"[HitboxDamage] No PlayerHealth in parents of '{name}'.");
            CleanupBullet(other, bulletComp);
            return;
        }

        if (!isHead && bo.ignoreBodyHits)
        {
            Debug.Log("[HitboxDamage] Bullet configured to ignore body hits -> ignoring this body hit.");
            CleanupBullet(other, bulletComp);
            return;
        }

        int baseBodyDamage = damage;
        if (isHead && ph.bodyCollider != null)
        {
            var bodyHb = ph.bodyCollider.GetComponent<HitboxDamage>();
            if (bodyHb != null) baseBodyDamage = bodyHb.damage;
        }

        float outgoingMult = (bo != null) ? bo.outgoingDamageMultiplier : 1f;
        float headMult = (bo != null) ? bo.headshotMultiplier : 3f;

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

        Debug.Log($"[HitboxDamage] Bullet by actor={bo.ownerActorNumber} hit playerActor={targetActor} ({ph.name}). appliedDamage={appliedDamage} isHead={isHead}");

        if (ignoreFriendlyFire && bo.ownerActorNumber >= 0 && targetActor >= 0 && bo.ownerActorNumber == targetActor)
        {
            Debug.Log("[HitboxDamage] Ignored friendly fire (attacker == target).");
            CleanupBullet(other, bulletComp);
            return;
        }

        if (targetPv != null && PhotonNetwork.InRoom && targetPv.Owner != null)
        {
            try
            {
                targetPv.RPC("RPC_TakeDamage", targetPv.Owner, appliedDamage, isHead, bo.ownerActorNumber);
                Debug.Log($"[HitboxDamage] Sent RPC_TakeDamage to actor {targetPv.Owner.ActorNumber} (attacker {bo.ownerActorNumber}).");
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning("[HitboxDamage] RPC failed, applying damage locally as fallback: " + ex);
                ph.TakeDamage(appliedDamage, isHead);
            }
        }
        else
        {
            ph.TakeDamage(appliedDamage, isHead);
            Debug.Log("[HitboxDamage] Applied damage locally (no PhotonView/owner).");
        }

        CleanupBullet(other, bulletComp);
    }

    void CleanupBullet(Collider bulletCollider, Bullet bulletComp = null)
    {
        if (bulletCollider == null) return;

        if (bulletComp == null)
            bulletComp = bulletCollider.GetComponentInParent<Bullet>();

        // Networked bullet (PhotonView) -> owner destroys properly
        PhotonView pv = bulletCollider.GetComponentInParent<PhotonView>();
        if (pv != null && PhotonNetwork.InRoom)
        {
            if (pv.IsMine)
            {
                try { PhotonNetwork.Destroy(pv.gameObject); }
                catch { if (pv.gameObject != null) Destroy(pv.gameObject); }
            }
            else
            {
                try { Destroy(pv.gameObject); } catch { }
            }
            return;
        }

        // Non-networked fallback
        if (bulletComp != null)
        {
            Destroy(bulletComp.gameObject);
            return;
        }

        GameObject root = bulletCollider.transform.root.gameObject;
        Destroy(root);
    }
}