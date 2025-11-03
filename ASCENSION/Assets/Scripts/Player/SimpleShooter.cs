// SimpleShooter_PhotonSafe.cs (modified)
using UnityEngine;
using Photon.Pun;
using System.Collections;
using TMPro;

[DisallowMultipleComponent]
public class SimpleShooter_PhotonSafe : MonoBehaviour
{
    [Header("References")]
    public Camera sourceCamera;
    public GameObject bulletPrefab;      // must have collider + Rigidbody (or will be added)
    public Transform spawnPoint;
    public TextMeshProUGUI ammoText;     // assign your UI Text (optional)

    [Header("Bullet Settings")]
    public float bulletSpeed = 40f;
    public float fireRate = 0.2f;        // 0 = single-shot
    public float bulletLifetime = 5f;
    public bool ignoreOwnerCollision = true;
    public float ignoreCollisionDuration = 0.12f;

    [Header("Ammo / Reload")]
    public int maxAmmo = 25;             // magazine size
    [Tooltip("If true, will auto reload when magazine empties")]
    public bool autoReloadOnEmpty = true;
    public float reloadTime = 1.5f;      // seconds to reload
    public KeyCode reloadKey = KeyCode.R;

    [Header("Pool (simple, per-client)")]
    public bool usePooling = true;
    public int poolSize = 20;

    // --- NEW: Defaults that TarotSelection will modify ---
    [Header("Bullet metadata defaults (can be changed by TarotSelection)")]
    [Tooltip("Default headshot multiplier applied to bullets (3 by default).")]
    public float defaultHeadshotMultiplier = 3f;

    [Tooltip("Default outgoing damage multiplier (1 = unchanged).")]
    public float defaultOutgoingDamageMultiplier = 1f;

    [Tooltip("If true, body hits are ignored for bullets (no body damage).")]
    public bool defaultIgnoreBodyHits = false;

    // --------------------------

    // runtime
    float nextFireTime = 0f;
    private GameObject[] pool;
    private Transform poolParent;

    private int currentAmmo;
    private bool isReloading = false;

    // Name for the shared root that stores per-player pools but is NOT parented to player transforms
    const string GLOBAL_POOLS_ROOT_NAME = "___BulletPoolsRoot";

    // store original fireRate so we can apply multipliers safely (prevent stacking)
    private float originalFireRate = -1f;

    void Awake()
    {
        // Remember original fire rate once
        originalFireRate = fireRate;

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
                if (b.GetComponent<BulletOwner>() == null) b.AddComponent<BulletOwner>();
                pool[i] = b;
            }
        }

        // initialize ammo
        currentAmmo = maxAmmo;
    }

    Transform GetOrCreateGlobalPoolsRoot()
    {
        var existing = GameObject.Find(GLOBAL_POOLS_ROOT_NAME);
        if (existing != null) return existing.transform;

        var go = new GameObject(GLOBAL_POOLS_ROOT_NAME);
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

        UpdateAmmoUI();
    }

    void Update()
    {
        // Ownership check: allow firing only on the owner (or offline if there's no Photon)
        PhotonView pv = GetComponentInParent<PhotonView>();
        if (pv != null && PhotonNetwork.InRoom && !pv.IsMine)
            return;

        // Reload input (manual)
        if (!isReloading && Input.GetKeyDown(reloadKey) && currentAmmo < maxAmmo)
        {
            StartCoroutine(ReloadCoroutine());
        }

        // Input
        if (fireRate <= 0f)
        {
            if (Input.GetButtonDown("Fire1"))
                TryFire();
        }
        else
        {
            if (Input.GetButton("Fire1") && Time.time >= nextFireTime)
            {
                nextFireTime = Time.time + fireRate;
                TryFire();
            }
        }
    }

    private void RegenOverTime() { /* not used here - omitted */ }

    #region Ammo/UI helpers (unchanged)
    void TryFire()
    {
        if (isReloading) return;
        if (currentAmmo <= 0)
        {
            if (autoReloadOnEmpty)
            {
                StartCoroutine(ReloadCoroutine());
            }
            else
            {
                // optionally play empty click sound here
            }
            return;
        }

        Fire();
        currentAmmo--;
        UpdateAmmoUI();

        // Auto-reload when magazine reaches zero and autoReloadOnEmpty is true
        if (currentAmmo <= 0 && autoReloadOnEmpty && !isReloading)
            StartCoroutine(ReloadCoroutine());
    }
    #endregion

    GameObject GetBulletFromPoolOrNew(out bool pooled)
    {
        pooled = false;
        if (!usePooling || pool == null)
        {
            var inst = Instantiate(bulletPrefab);
            if (inst.GetComponent<Bullet>() == null) inst.AddComponent<Bullet>();
            if (inst.GetComponent<BulletOwner>() == null) inst.AddComponent<BulletOwner>();
            return inst;
        }

        for (int i = 0; i < pool.Length; i++)
        {
            if (pool[i] == null)
            {
                var repl = Instantiate(bulletPrefab, poolParent);
                repl.SetActive(false);
                if (repl.GetComponent<Bullet>() == null) repl.AddComponent<Bullet>();
                if (repl.GetComponent<BulletOwner>() == null) repl.AddComponent<BulletOwner>();
                pool[i] = repl;
            }

            if (!pool[i].activeInHierarchy)
            {
                pooled = true;
                return pool[i];
            }
        }

        var temp = Instantiate(bulletPrefab);
        if (temp.GetComponent<Bullet>() == null) temp.AddComponent<Bullet>();
        if (temp.GetComponent<BulletOwner>() == null) temp.AddComponent<BulletOwner>();
        temp.SetActive(false);
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

        int myActor = (PhotonNetwork.InRoom && PhotonNetwork.LocalPlayer != null) ? PhotonNetwork.LocalPlayer.ActorNumber : -1;

        if (sourceCamera == null)
            sourceCamera = Camera.main;

        Vector3 originPos;
        Quaternion originRot;
        const float cameraSpawnOffset = 0.25f;

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

        bool isPooled;
        GameObject bullet = GetBulletFromPoolOrNew(out isPooled);
        if (bullet == null)
        {
            Debug.LogWarning("[SimpleShooter] Failed to obtain bullet instance.");
            return;
        }

        if (poolParent != null)
            bullet.transform.SetParent(poolParent, true);

        bullet.transform.position = originPos;
        bullet.transform.rotation = Quaternion.LookRotation(aimDirection);

        Bullet bulletComp = bullet.GetComponent<Bullet>();
        if (bulletComp == null)
            bulletComp = bullet.AddComponent<Bullet>();

        // Assign bullet owner and metadata BEFORE activating the bullet so friendly-fire checks are valid immediately
        BulletOwner bo = bullet.GetComponent<BulletOwner>();
        if (bo == null) bo = bullet.AddComponent<BulletOwner>();
        bo.ownerActorNumber = myActor;

        // --- APPLY CURRENT DEFAULT METADATA to the bullet ---
        bo.headshotMultiplier = defaultHeadshotMultiplier;
        bo.outgoingDamageMultiplier = defaultOutgoingDamageMultiplier;
        bo.ignoreBodyHits = defaultIgnoreBodyHits;
        // ----------------------------------------------------

        Rigidbody rb = bullet.GetComponent<Rigidbody>();
        if (rb == null) rb = bullet.AddComponent<Rigidbody>();
        rb.velocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
        rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        rb.interpolation = RigidbodyInterpolation.Interpolate;

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
                StartCoroutine(ReenableCollisionsAfter(bullet, bulletCol, ownerCols, ignoreCollisionDuration));
            }
        }

        bullet.SetActive(true);
        rb.velocity = aimDirection * bulletSpeed;

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

    IEnumerator ReloadCoroutine()
    {
        if (isReloading) yield break;
        isReloading = true;
        UpdateAmmoUI(true);
        yield return new WaitForSeconds(reloadTime);
        currentAmmo = maxAmmo;
        isReloading = false;
        UpdateAmmoUI();
    }

    void UpdateAmmoUI(bool showReloading = false)
    {
        if (ammoText == null) return;
        if (showReloading)
            ammoText.text = $"RELOADING...";
        else
            ammoText.text = $"{currentAmmo} / {maxAmmo}";
    }

    // API helpers
    public void AddAmmo(int amount)
    {
        currentAmmo = Mathf.Clamp(currentAmmo + amount, 0, maxAmmo);
        UpdateAmmoUI();
    }

    public void SetAmmo(int amount)
    {
        currentAmmo = Mathf.Clamp(amount, 0, maxAmmo);
        UpdateAmmoUI();
    }

    public int GetCurrentAmmo()
    {
        return currentAmmo;
    }

    // ---------------- NEW PUBLIC API ----------------
    /// <summary>
    /// Applies a fire-rate multiplier based on the original fireRate captured in Awake().
    /// multiplier > 1 makes firing slower (e.g. 2 doubles the delay between shots).
    /// multiplier < 1 makes firing faster.
    /// </summary>
    /// <param name="multiplier"></param>
    public void SetFireRateMultiplier(float multiplier)
    {
        if (originalFireRate <= 0f)
            originalFireRate = fireRate;

        fireRate = originalFireRate * multiplier;
        Debug.Log($"[SimpleShooter] fireRate adjusted to {fireRate} (multiplier {multiplier}).");
    }

    /// <summary>
    /// Restore fireRate to original (undo any multipliers).
    /// </summary>
    public void RestoreFireRate()
    {
        if (originalFireRate > 0f)
        {
            fireRate = originalFireRate;
            Debug.Log($"[SimpleShooter] fireRate restored to {fireRate}.");
        }
    }
}
