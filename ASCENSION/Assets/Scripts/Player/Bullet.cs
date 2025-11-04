using UnityEngine;

/// <summary>
/// Simple bullet helper: handles lifetime and deactivation on hit (works with pooling).
/// Exposes IsPooled so other scripts can decide whether to destroy or deactivate bullets.
/// </summary>
public class Bullet : MonoBehaviour
{
    public bool IsPooled { get; private set; } = false;

    private float lifetime = 5f;
    private float spawnTime;

    /// <summary>
    /// Call this to start the bullet. isPooled = true means this bullet belongs to a pool and should be deactivated, not destroyed.
    /// </summary>
    public void Launch(float life, bool isPooled)
    {
        lifetime = life;
        IsPooled = isPooled;
        spawnTime = Time.time;
        CancelInvoke();

        // If not pooled, schedule a destroy. If pooled, Update() will handle lifetime and call Deactivate().
        if (!IsPooled)
            Invoke(nameof(DestroySelf), lifetime);
        else
        {
            // Ensure the object is active (pool may reuse)
            // spawnTime used in Update()
        }
    }

    void OnEnable()
    {
        spawnTime = Time.time;
    }

    void Update()
    {
        if (IsPooled && Time.time - spawnTime >= lifetime)
            Deactivate();
    }

    void OnCollisionEnter(Collision other)
    {
        // On hit, deactivate / destroy immediately. You can add damage logic here.
        Deactivate();
    }

    /// <summary>
    /// Deactivate the bullet (return to pool) or destroy when not pooled.
    /// </summary>
    public void Deactivate()
    {
        Rigidbody rb = GetComponent<Rigidbody>();
        if (rb != null)
            rb.velocity = Vector3.zero;

        // If this bullet is a PhotonNetworked object:
        var pv = GetComponent<Photon.Pun.PhotonView>();
        if (pv != null && Photon.Pun.PhotonNetwork.InRoom)
        {
            if (pv.IsMine)
            {
                try { Photon.Pun.PhotonNetwork.Destroy(pv.gameObject); return; }
                catch { /* fallback below */ }
            }
        }

        if (IsPooled)
            gameObject.SetActive(false);
        else
            Destroy(gameObject);
    }

    void DestroySelf()
    {
        Destroy(gameObject);
    }
}
