using System.Collections;
using UnityEngine;
using Photon.Pun;

/// <summary>
/// GuardBehavior:
/// - Basic summoned guard that blocks/destroys incoming projectiles and can take damage via TakeDamage(float).
/// - InstantiationData expected: [0] ownerActor (int), [1] hp (int)
/// </summary>
[RequireComponent(typeof(Collider))]
public class GuardBehavior : MonoBehaviourPun
{
    [Header("Guard")]
    public int hp = 150;
    public int ownerActor = -1;
    public float lifetime = 12f;
    public string[] projectileTags = new string[] { "Projectile", "bullet", "Bullet" };

    void Awake()
    {
        if (photonView != null && photonView.InstantiationData != null)
        {
            var d = photonView.InstantiationData;
            if (d.Length >= 1 && d[0] is int) ownerActor = (int)d[0];
            if (d.Length >= 2 && d[1] is int) hp = (int)d[1];
            else if (d.Length >= 2 && (d[1] is float || d[1] is double)) hp = Mathf.RoundToInt((float)(double)d[1]);
        }

        // auto-despawn after lifetime for safety (owner will destroy networked object)
        StartCoroutine(LifetimeCoroutine());
    }

    public void InitializeFromSpawner(int ownerActor_, GameObject ownerObj_, int hp_)
    {
        ownerActor = ownerActor_;
        hp = hp_;
    }

    private IEnumerator LifetimeCoroutine()
    {
        yield return new WaitForSeconds(lifetime);
        if (PhotonNetwork.InRoom && photonView != null)
        {
            try { PhotonNetwork.Destroy(gameObject); } catch { Destroy(gameObject); }
        }
        else
        {
            Destroy(gameObject);
        }
    }

    private void OnCollisionEnter(Collision collision)
    {
        var other = collision.gameObject;
        if (other == null) return;

        // If it's a projectile, block/destroy the projectile and optionally take small damage
        foreach (var tag in projectileTags)
        {
            if (other.CompareTag(tag))
            {
                // destroy projectile (network-aware)
                if (PhotonNetwork.InRoom)
                {
                    var pv = other.GetComponent<PhotonView>();
                    if (pv != null && pv.IsMine)
                    {
                        try { PhotonNetwork.Destroy(other); }
                        catch { Destroy(other); }
                    }
                    else
                    {
                        Destroy(other);
                    }
                }
                else
                {
                    Destroy(other);
                }

                // Optionally take minor impact damage
                TakeDamage(5f);
                return;
            }
        }
    }

    // Generic public damage entrypoint
    public void TakeDamage(float amount)
    {
        // route to owner if networked
        if (PhotonNetwork.InRoom && photonView != null && photonView.Owner != null)
        {
            photonView.RPC(nameof(RPC_ApplyDamage), photonView.Owner, amount);
            return;
        }

        ApplyDamageLocally(amount);
    }

    [PunRPC]
    private void RPC_ApplyDamage(float amount, PhotonMessageInfo info)
    {
        if (PhotonNetwork.InRoom)
        {
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
            Die();
    }

    private void Die()
    {
        // VFX / sfx here

        if (PhotonNetwork.InRoom && photonView != null)
        {
            try { PhotonNetwork.Destroy(gameObject); } catch { Destroy(gameObject); }
        }
        else
        {
            Destroy(gameObject);
        }
    }
}
