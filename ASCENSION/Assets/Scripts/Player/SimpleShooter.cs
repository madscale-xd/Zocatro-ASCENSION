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

    [Header("Devil / Bullet special metadata")]
    [Tooltip("Percent of damage dealt healed to the attacker (0.25 = 25%).")]
    public float defaultLifestealPercent = 0f;

    [Tooltip("If >0, the shooter takes this much self-damage each time they fire a bullet (Devil).")]
    public int defaultSelfDamagePerShot = 0;

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
    [Header("Networked bullets")]
    [Tooltip("When true and in a Photon room, bullets are created via PhotonNetwork.Instantiate (prefab must be in Resources/ and have a PhotonView).")]
    public bool networkBullets = true;


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

        // --- NETWORKED BULLET PATH (preferred when in Photon room) ---
        if (PhotonNetwork.InRoom /* && networkBullets toggle if you have one */)
        {
            // Use PhotonNetwork.Instantiate. Prefab must be inside Resources/ and have a PhotonView.
            string resName = bulletPrefab.name;
            object[] instData = new object[]
            {
                myActor,
                defaultHeadshotMultiplier,
                defaultOutgoingDamageMultiplier,
                defaultIgnoreBodyHits
            };

            GameObject netBullet = null;
            try
            {
                netBullet = PhotonNetwork.Instantiate(resName, originPos + aimDirection * 0.35f, Quaternion.LookRotation(aimDirection), 0, instData);
                // NOTE: small forward offset (0.35) to reduce spawn overlap with player's colliders
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[SimpleShooter] PhotonNetwork.Instantiate failed for '{resName}': {ex.Message}. Falling back to local spawn.");
                netBullet = null;
            }

            if (netBullet != null)
            {
                // Ensure Bullet component exists and launch lifetime locally (non-pooled networked bullet).
                Bullet bcomp = netBullet.GetComponent<Bullet>();
                if (bcomp == null) bcomp = netBullet.AddComponent<Bullet>();

                // Ensure BulletOwner exists - OnPhotonInstantiate will also parse instantiationData
                BulletOwner bo = netBullet.GetComponent<BulletOwner>();
                if (bo == null) bo = netBullet.AddComponent<BulletOwner>();
                // Set ownerActorNumber locally in case OnPhotonInstantiate hasn't run yet (defensive)
                bo.ownerActorNumber = myActor;

                // IMPORTANT: prevent immediate collision with the shooter's colliders (same logic as pooled path)
                Collider bulletCol = netBullet.GetComponent<Collider>();
                if (bulletCol != null)
                {
                    Collider[] ownerCols = GetComponentsInChildren<Collider>(true);
                    foreach (var c in ownerCols)
                    {
                        if (c != null)
                        {
                            Physics.IgnoreCollision(bulletCol, c, true);
                        }
                    }

                    // schedule re-enable after ignoreCollisionDuration on the owner client
                    StartCoroutine(ReenableCollisionsAfter(netBullet, bulletCol, ownerCols, ignoreCollisionDuration));
                }

                // Rigidbody and velocity (owner sets initial velocity)
                Rigidbody rb = netBullet.GetComponent<Rigidbody>();
                if (rb == null) rb = netBullet.AddComponent<Rigidbody>();
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
                rb.interpolation = RigidbodyInterpolation.Interpolate;

                rb.velocity = aimDirection * bulletSpeed;

                // Launch lifetime (networked bullets: not pooled)
                bcomp.Launch(bulletLifetime, false);

                // --- Devil self-damage on shot (owner-local) ---
                if (defaultSelfDamagePerShot > 0)
                {
                    PlayerHealth ph = GetComponentInParent<PlayerHealth>();
                    if (ph != null)
                    {
                        ph.TakeDamage(defaultSelfDamagePerShot, false);
                        Debug.Log($"[SimpleShooter] Devil self-damage applied: {defaultSelfDamagePerShot}");
                    }
                }

                return; // done — networked bullet spawned and configured
            }

            // If network instantiate failed, fall through to pooled/local path below
        }

        // --- FALLBACK: local pooled / non-networked bullet path (your existing code) ---
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
        BulletOwner boLocal = bullet.GetComponent<BulletOwner>();
        if (boLocal == null) boLocal = bullet.AddComponent<BulletOwner>();
        boLocal.ownerActorNumber = myActor;

        // --- APPLY CURRENT DEFAULT METADATA to the bullet ---
        boLocal.headshotMultiplier = defaultHeadshotMultiplier;
        boLocal.outgoingDamageMultiplier = defaultOutgoingDamageMultiplier;
        boLocal.ignoreBodyHits = defaultIgnoreBodyHits;
        // ----------------------------------------------------

        Rigidbody rbLocal = bullet.GetComponent<Rigidbody>();
        if (rbLocal == null) rbLocal = bullet.AddComponent<Rigidbody>();
        rbLocal.velocity = Vector3.zero;
        rbLocal.angularVelocity = Vector3.zero;
        rbLocal.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        rbLocal.interpolation = RigidbodyInterpolation.Interpolate;

        if (ignoreOwnerCollision)
        {
            Collider bulletColLocal = bullet.GetComponent<Collider>();
            if (bulletColLocal != null)
            {
                Collider[] ownerColsLocal = GetComponentsInChildren<Collider>(true);
                foreach (var c in ownerColsLocal)
                {
                    if (c != null) Physics.IgnoreCollision(bulletColLocal, c, true);
                }
                StartCoroutine(ReenableCollisionsAfter(bullet, bulletColLocal, ownerColsLocal, ignoreCollisionDuration));
            }
        }

        bullet.SetActive(true);
        rbLocal.velocity = aimDirection * bulletSpeed;

        if (bulletComp != null) bulletComp.Launch(bulletLifetime, isPooled);
        else Destroy(bullet, bulletLifetime);

        // --- NEW: Devil self-damage on shot (owner-local) ---
        if (defaultSelfDamagePerShot > 0)
        {
            // Apply self-damage on owner client (this shooter owns their player object)
            PlayerHealth ph = GetComponentInParent<PlayerHealth>();
            if (ph != null)
            {
                ph.TakeDamage(defaultSelfDamagePerShot, false);
                Debug.Log($"[SimpleShooter] Devil self-damage applied: {defaultSelfDamagePerShot}");
            }
        }
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
