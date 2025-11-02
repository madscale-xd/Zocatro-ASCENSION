// SimpleShooter_PhotonSafe.cs
using UnityEngine;
using Photon.Pun;
using System.Collections;

[DisallowMultipleComponent]
public class SimpleShooter_PhotonSafe : MonoBehaviour
{
    [Header("References")]
    public Camera sourceCamera;
    public GameObject bulletPrefab;      // must have collider + Rigidbody (or will be added)
    public Transform spawnPoint;

    [Header("Bullet Settings")]
    public float bulletSpeed = 40f;
    public float fireRate = 0.2f;        // 0 = single-shot
    public float bulletLifetime = 5f;
    public bool ignoreOwnerCollision = true;
    public float ignoreCollisionDuration = 0.12f;

    [Header("Pool (simple, per-client)")]
    public bool usePooling = true;
    public int poolSize = 20;

    float nextFireTime = 0f;
    private GameObject[] pool;
    private Transform poolParent;

    // Name for the shared root that stores per-player pools but is NOT parented to player transforms
    const string GLOBAL_POOLS_ROOT_NAME = "___BulletPoolsRoot";

    void Awake()
    {
        // Ensure there's a global root in the scene to hold all pools so they're not nested under player transforms
        Transform globalRoot = GetOrCreateGlobalPoolsRoot();

        // Initialize pool local to this client/player, but parent it under the global root (not under the player)
        if (usePooling && bulletPrefab != null)
        {
            poolParent = new GameObject($"{name}_BulletPool").transform;
            poolParent.SetParent(globalRoot, true); // <-- no longer parented to the player
            pool = new GameObject[poolSize];
            for (int i = 0; i < poolSize; i++)
            {
                var b = Instantiate(bulletPrefab, poolParent);
                b.SetActive(false);
                if (b.GetComponent<Bullet>() == null) b.AddComponent<Bullet>();
                pool[i] = b;
            }
        }

        // Do not assume Camera.main is correct on networked clones.
        // We'll try to assign the camera for the local owner in Start.
    }

    Transform GetOrCreateGlobalPoolsRoot()
    {
        var existing = GameObject.Find(GLOBAL_POOLS_ROOT_NAME);
        if (existing != null) return existing.transform;

        var go = new GameObject(GLOBAL_POOLS_ROOT_NAME);
        // Optionally keep across scenes; remove if you don't want this:
        // DontDestroyOnLoad(go);
        return go.transform;
    }

    void Start()
    {
        // If this belongs to a player prefab, use local player camera if this is mine.
        PhotonView pv = GetComponentInParent<PhotonView>();
        bool isOwner = (pv == null) || pv.IsMine || !PhotonNetwork.InRoom;

        if (isOwner)
        {
            // Prefer a camera on this prefab first, else Camera.main fallback.
            if (sourceCamera == null)
            {
                sourceCamera = GetComponentInChildren<Camera>(true);
                if (sourceCamera == null)
                    sourceCamera = Camera.main;
            }

            if (spawnPoint == null)
            {
                var muzzle = transform.Find("Muzzle");
                spawnPoint = muzzle != null ? muzzle : transform;
            }
        }
        else
        {
            // Remote instances - do not use Camera.main for firing origin
            // (we won't do firing on remote)
        }
    }

    void Update()
    {
        // Ownership check: allow firing only on the owner (or offline if there's no Photon)
        PhotonView pv = GetComponentInParent<PhotonView>();
        if (pv != null && PhotonNetwork.InRoom && !pv.IsMine)
            return;

        // Input
        if (fireRate <= 0f)
        {
            if (Input.GetButtonDown("Fire1"))
                Fire();
        }
        else
        {
            if (Input.GetButton("Fire1") && Time.time >= nextFireTime)
            {
                nextFireTime = Time.time + fireRate;
                Fire();
            }
        }
    }

    GameObject GetBulletFromPoolOrNew(out bool pooled)
    {
        pooled = false;
        if (!usePooling || pool == null)
        {
            var inst = Instantiate(bulletPrefab);
            if (inst.GetComponent<Bullet>() == null) inst.AddComponent<Bullet>();
            return inst;
        }

        for (int i = 0; i < pool.Length; i++)
        {
            // Defensive: if the pool slot contains a destroyed object, recreate replacement
            if (pool[i] == null)
            {
                var repl = Instantiate(bulletPrefab, poolParent);
                repl.SetActive(false);
                if (repl.GetComponent<Bullet>() == null) repl.AddComponent<Bullet>();
                pool[i] = repl;
            }

            if (!pool[i].activeInHierarchy)
            {
                pooled = true;
                return pool[i];
            }
        }

        // Pool exhausted -> create temporary non-pooled bullet (do NOT add it to pool)
        var temp = Instantiate(bulletPrefab);
        if (temp.GetComponent<Bullet>() == null) temp.AddComponent<Bullet>();
        temp.SetActive(false);
        // Put temporary bullets under the global root too so they don't hang under a player
        if (poolParent != null) temp.transform.SetParent(poolParent.parent, true);
        return temp;
    }


    void Fire()
    {
        if (bulletPrefab == null)
        {
            Debug.LogWarning("[SimpleShooter] bulletPrefab missing.");
            return;
        }

        // --- Determine local actor (network-safe) ---
        int myActor = (PhotonNetwork.InRoom && PhotonNetwork.LocalPlayer != null) ? PhotonNetwork.LocalPlayer.ActorNumber : -1;

        // --- Compute spawn origin: prefer explicit spawnPoint (muzzle), else camera eye, else player transform ---
        if (sourceCamera == null)
            sourceCamera = Camera.main;

        Vector3 originPos;
        Quaternion originRot;
        const float cameraSpawnOffset = 0.25f; // spawn slightly in front of camera to avoid self-collisions

        if (spawnPoint != null)
        {
            originPos = spawnPoint.position;
            originRot = spawnPoint.rotation;
        }
        else if (sourceCamera != null)
        {
            originPos = sourceCamera.transform.position + sourceCamera.transform.forward * cameraSpawnOffset;
            originRot = sourceCamera.transform.rotation;
        }
        else
        {
            originPos = transform.position + Vector3.up * 1.6f;
            originRot = transform.rotation;
        }

        // --- Compute aim direction using a center-screen ray (crosshair) ---
        Vector3 aimDirection = originRot * Vector3.forward;
        float maxAimDistance = 1000f;
        Vector3 targetPoint = originPos + aimDirection * maxAimDistance;

        if (sourceCamera != null)
        {
            Ray centerRay = sourceCamera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
            RaycastHit hit;
            if (Physics.Raycast(centerRay, out hit, maxAimDistance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            {
                targetPoint = hit.point;
            }
            else
            {
                targetPoint = centerRay.origin + centerRay.direction * maxAimDistance;
            }

            aimDirection = (targetPoint - originPos).normalized;
        }

        Debug.Log($"[SimpleShooter] Fire() actor={myActor} origin={originPos} aimDir={aimDirection}");

        // --- Acquire a bullet (pooled or new) ---
        bool isPooled;
        GameObject bullet = GetBulletFromPoolOrNew(out isPooled);
        if (bullet == null)
        {
            Debug.LogWarning("[SimpleShooter] Failed to obtain bullet instance.");
            return;
        }

        // Ensure pooled bullets are parented under the global pool root (poolParent) so they don't follow the player transform
        if (poolParent != null)
            bullet.transform.SetParent(poolParent, true);

        // Place but don't activate yet — set up everything before enabling
        bullet.transform.position = originPos;
        bullet.transform.rotation = Quaternion.LookRotation(aimDirection);

        // Ensure Bullet component exists
        Bullet bulletComp = bullet.GetComponent<Bullet>();
        if (bulletComp == null)
            bulletComp = bullet.AddComponent<Bullet>();

        // Assign bullet owner (network-safe) BEFORE activating the bullet so friendly-fire checks are valid immediately
        BulletOwner bo = bullet.GetComponent<BulletOwner>();
        if (bo == null) bo = bullet.AddComponent<BulletOwner>();
        bo.ownerActorNumber = myActor;

        // Make sure a Rigidbody exists and reset its state
        Rigidbody rb = bullet.GetComponent<Rigidbody>();
        if (rb == null) rb = bullet.AddComponent<Rigidbody>();
        rb.velocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        rb.interpolation = RigidbodyInterpolation.Interpolate;

        // Prevent immediate self-hit: ignore collisions with owner's colliders briefly
        if (ignoreOwnerCollision)
        {
            Collider bulletCol = bullet.GetComponent<Collider>();
            if (bulletCol != null)
            {
                Collider[] ownerCols = GetComponentsInChildren<Collider>(true);
                foreach (var c in ownerCols)
                {
                    if (c != null) Physics.IgnoreCollision(bulletCol, c, true);
                }
                // Re-enable after short delay
                StartCoroutine(ReenableCollisionsAfter(bullet, bulletCol, ownerCols, ignoreCollisionDuration));
            }
        }

        // Finally enable the bullet and give it velocity (aimDirection ensures it goes toward crosshair)
        bullet.SetActive(true);
        rb.velocity = aimDirection * bulletSpeed;

        // Launch bullet lifetime cleanup (Bullet component handles pooling/deactivation; fallback to destroy)
        if (bulletComp != null) bulletComp.Launch(bulletLifetime, isPooled);
        else Destroy(bullet, bulletLifetime);
    }

    IEnumerator ReenableCollisionsAfter(GameObject bullet, Collider bulletCol, Collider[] ownerCols, float delay)
    {
        yield return new WaitForSeconds(delay);
        if (bullet == null) yield break;
        foreach (var c in ownerCols)
            if (c != null && bulletCol != null) Physics.IgnoreCollision(bulletCol, c, false);
    }
}