// Bullet.cs
using System;
using UnityEngine;
using Photon.Pun;

[RequireComponent(typeof(Collider))]
public class Bullet : MonoBehaviourPun, IPunInstantiateMagicCallback
{
    private float lifetime = 5f;
    private float spawnTime;
    private Rigidbody rb;

    // metadata (filled from instantiationData or by owner before spawn)
    [HideInInspector] public int ownerActorNumber = -1;
    [HideInInspector] public float headshotMultiplier = 3f;
    [HideInInspector] public float outgoingDamageMultiplier = 1f;
    [HideInInspector] public bool ignoreBodyHits = false;
    [HideInInspector] public float bulletSpeed = 40f;

    const float initialColliderDisableSeconds = 0.06f;

    void Awake()
    {
        spawnTime = Time.time;
        rb = GetComponent<Rigidbody>();
        if (rb == null)
        {
            rb = gameObject.AddComponent<Rigidbody>();
            rb.useGravity = false;
            rb.isKinematic = false;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
        }

        TryParseInstantiationDataFromPhotonView();
    }

    void OnEnable()
    {
        spawnTime = Time.time;
    }

    void Update()
    {
        if (lifetime > 0f && Time.time - spawnTime >= lifetime)
            DestroySelf();
    }

    public void Launch(float life, bool _unusedForNow)
    {
        lifetime = life;
        spawnTime = Time.time;

        if (rb != null && rb.velocity.sqrMagnitude <= 0.01f && bulletSpeed > 0f)
            rb.velocity = transform.forward * bulletSpeed;

        if (lifetime > 0f)
            Invoke(nameof(DestroySelf), lifetime);
    }

    void OnCollisionEnter(Collision other)
    {
        DestroySelf();
    }

    void OnTriggerEnter(Collider other)
    {
        // HitboxDamage should handle actual damage; do not apply damage here.
    }

    void DestroySelf()
    {
        var pv = GetComponent<PhotonView>();
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
        }
        else
        {
            Destroy(gameObject);
        }
    }

    // Photon integration
    public void OnPhotonInstantiate(PhotonMessageInfo info)
    {
        TryParseInstantiationDataFromPhotonView();

        spawnTime = Time.time;
        if (lifetime > 0f)
        {
            CancelInvoke(nameof(DestroySelf));
            Invoke(nameof(DestroySelf), lifetime);
        }

        if (rb != null && bulletSpeed > 0f && rb.velocity.sqrMagnitude <= 0.01f)
            rb.velocity = transform.forward * bulletSpeed;

        StartCoroutine(ApplyIgnoreWithOwnerCollidersAndEnableCollider());
    }

    private System.Collections.IEnumerator ApplyIgnoreWithOwnerCollidersAndEnableCollider()
    {
        var col = GetComponent<Collider>();
        if (col != null) col.enabled = false;

        yield return null;
        yield return null;

        if (ownerActorNumber >= 0)
        {
            var allPVs = FindObjectsOfType<PhotonView>();
            foreach (var p in allPVs)
            {
                if (p.Owner != null && p.Owner.ActorNumber == ownerActorNumber)
                {
                    var ownerCols = p.GetComponentsInChildren<Collider>(true);
                    if (ownerCols != null && ownerCols.Length > 0 && col != null)
                    {
                        foreach (var oc in ownerCols)
                        {
                            if (oc != null)
                                Physics.IgnoreCollision(col, oc, true);
                        }
                    }
                    break;
                }
            }
        }

        var oe = GetComponent<OwnedEntity>();
        if (oe != null && oe.ownerGameObject != null)
        {
            var ownerCols2 = oe.ownerGameObject.GetComponentsInChildren<Collider>(true);
            var c = GetComponent<Collider>();
            if (ownerCols2 != null && ownerCols2.Length > 0 && c != null)
            {
                foreach (var oc in ownerCols2)
                    if (oc != null) Physics.IgnoreCollision(c, oc, true);
            }
        }

        yield return new WaitForSeconds(initialColliderDisableSeconds);

        if (col != null) col.enabled = true;
    }

    private void TryParseInstantiationDataFromPhotonView()
    {
        if (!PhotonNetwork.InRoom) return;
        var pv = GetComponent<PhotonView>();
        if (pv == null) return;

        object[] data = pv.InstantiationData;
        if (data == null || data.Length == 0) return;

        try
        {
            if (data.Length >= 1 && data[0] != null) ownerActorNumber = Convert.ToInt32(data[0]);
            if (data.Length >= 2 && data[1] != null) headshotMultiplier = Convert.ToSingle(data[1]);
            if (data.Length >= 3 && data[2] != null) outgoingDamageMultiplier = Convert.ToSingle(data[2]);
            if (data.Length >= 4 && data[3] != null) ignoreBodyHits = Convert.ToBoolean(data[3]);
            if (data.Length >= 5 && data[4] != null) bulletSpeed = Convert.ToSingle(data[4]);
            if (data.Length >= 6 && data[5] != null) lifetime = Convert.ToSingle(data[5]);
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[Bullet] Failed parsing instantiationData: " + ex);
        }

        Debug.Log($"[Bullet] OnPhotonInstantiate parsed owner={ownerActorNumber}, headMult={headshotMultiplier}, outMult={outgoingDamageMultiplier}, speed={bulletSpeed}, life={lifetime}");
    }
}
