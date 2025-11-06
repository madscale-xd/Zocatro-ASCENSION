// SimpleShooter_PhotonSafe.cs
using UnityEngine;
using Photon.Pun;
using System.Collections;
using System;
using TMPro;

[DisallowMultipleComponent]
public class SimpleShooter_PhotonSafe : MonoBehaviour
{
    [Header("References")]
    public Camera sourceCamera;
    public GameObject bulletPrefab;      // must be in Resources/ when using networkBullets
    public Transform spawnPoint;
    public TextMeshProUGUI ammoText;

    [Header("Bullet Settings")]
    public float bulletSpeed = 40f;
    public float fireRate = 0.2f;        // 0 = single-shot
    public float bulletLifetime = 5f;

    [Header("Ammo / Reload")]
    public int maxAmmo = 25;
    public bool autoReloadOnEmpty = true;
    public float reloadTime = 1.5f;
    public KeyCode reloadKey = KeyCode.R;

    [Header("Tarot-modifiable defaults")]
    public float defaultHeadshotMultiplier = 3f;
    public float defaultOutgoingDamageMultiplier = 1f;
    public bool defaultIgnoreBodyHits = false;

    [Header("Devil / special")]
    public float defaultLifestealPercent = 0f;
    public int defaultSelfDamagePerShot = 0;

    [Header("Network")]
    [Tooltip("If true and in a Photon room, bullets are created via PhotonNetwork.Instantiate (prefab must be in Resources/ and have a PhotonView).")]
    public bool networkBullets = true;

    // runtime
    float nextFireTime = 0f;
    private int currentAmmo;
    private bool isReloading = false;

    // store original fireRate for multipliers
    private float originalFireRate = -1f;

    // spawn forward offset to avoid overlapping the shooter
    const float spawnForwardOffset = 0.45f;

    void Awake()
    {
        originalFireRate = fireRate;
        currentAmmo = maxAmmo;
    }

    void Start()
    {
        PhotonView pv = GetComponentInParent<PhotonView>();
        bool isOwner = (pv == null) || pv.IsMine || !PhotonNetwork.InRoom;

        if (isOwner)
        {
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

        // hide remote ammo UI so only the owning client sees their ammo
        if (pv != null && PhotonNetwork.InRoom && !pv.IsMine && ammoText != null)
        {
            ammoText.gameObject.SetActive(false);
        }
        UpdateAmmoUI();
    }

    void Update()
    {
        PhotonView pv = GetComponentInParent<PhotonView>();
        if (pv != null && PhotonNetwork.InRoom && !pv.IsMine)
            return;

        if (!isReloading && Input.GetKeyDown(reloadKey) && currentAmmo < maxAmmo)
            StartCoroutine(ReloadCoroutine());

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

    void TryFire()
    {
        if (isReloading) return;
        if (currentAmmo <= 0)
        {
            if (autoReloadOnEmpty) StartCoroutine(ReloadCoroutine());
            return;
        }

        Fire();
        currentAmmo--;
        UpdateAmmoUI();

        if (currentAmmo <= 0 && autoReloadOnEmpty && !isReloading)
            StartCoroutine(ReloadCoroutine());
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

        if (spawnPoint != null)
        {
            originPos = spawnPoint.position;
            originRot = spawnPoint.rotation;
        }
        else if (sourceCamera != null)
        {
            originPos = sourceCamera.transform.position + sourceCamera.transform.forward * 0.25f;
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
            if (Physics.Raycast(centerRay, out RaycastHit hit, maxAimDistance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                targetPoint = hit.point;
            else
                targetPoint = centerRay.origin + centerRay.direction * maxAimDistance;

            aimDirection = (targetPoint - originPos).normalized;
        }

        // prefer network instantiate in Photon room
        if (PhotonNetwork.InRoom && networkBullets)
        {
            // instantiation data: [0] ownerActor, [1] headMult, [2] outgoingMult, [3] ignoreBodyHits, [4] bulletSpeed, [5] bulletLifetime
            object[] instData = new object[] {
                myActor,
                defaultHeadshotMultiplier,
                defaultOutgoingDamageMultiplier,
                defaultIgnoreBodyHits,
                bulletSpeed,
                bulletLifetime
            };

            string resName = bulletPrefab.name;
            Vector3 spawnPos = originPos + aimDirection * spawnForwardOffset;
            Quaternion spawnRot = Quaternion.LookRotation(aimDirection);

            GameObject netBullet = null;
            try
            {
                netBullet = PhotonNetwork.Instantiate(resName, spawnPos, spawnRot, 0, instData);
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[SimpleShooter] PhotonNetwork.Instantiate failed for '{resName}': {ex.Message}. Falling back to local instantiate.");
                netBullet = null;
            }

            if (netBullet != null)
            {
                // Owner sets initial velocity (so owner sees the projectile moving immediately)
                var rb = netBullet.GetComponent<Rigidbody>();
                if (rb == null) rb = netBullet.AddComponent<Rigidbody>();
                rb.velocity = aimDirection * bulletSpeed;
                rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
                rb.interpolation = RigidbodyInterpolation.Interpolate;
                rb.useGravity = false;

                // If the prefab has a BulletOwner we set ownerActorNumber defensively here
                var bo = netBullet.GetComponent<BulletOwner>();
                if (bo == null) bo = netBullet.AddComponent<BulletOwner>();
                bo.ownerActorNumber = myActor;
                bo.headshotMultiplier = defaultHeadshotMultiplier;
                bo.outgoingDamageMultiplier = defaultOutgoingDamageMultiplier;
                bo.ignoreBodyHits = defaultIgnoreBodyHits;

                // self-damage (Devil)
                if (defaultSelfDamagePerShot > 0)
                {
                    var ph = GetComponentInParent<PlayerHealth>();
                    if (ph != null)
                        ph.RequestTakeDamageFrom(PhotonNetwork.LocalPlayer.ActorNumber, defaultSelfDamagePerShot, false);
                }

                return;
            }
            // else fall back to local instantiate below
        }

        // Offline / fallback local instantiate
        Vector3 localSpawn = originPos + aimDirection * spawnForwardOffset;
        GameObject localBullet = Instantiate(bulletPrefab, localSpawn, Quaternion.LookRotation(aimDirection));
        var localRb = localBullet.GetComponent<Rigidbody>();
        if (localRb == null) localRb = localBullet.AddComponent<Rigidbody>();
        localRb.velocity = aimDirection * bulletSpeed;
        localRb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        localRb.interpolation = RigidbodyInterpolation.Interpolate;
        localRb.useGravity = false;

        var boLocal = localBullet.GetComponent<BulletOwner>();
        if (boLocal == null) boLocal = localBullet.AddComponent<BulletOwner>();
        boLocal.ownerActorNumber = myActor;
        boLocal.headshotMultiplier = defaultHeadshotMultiplier;
        boLocal.outgoingDamageMultiplier = defaultOutgoingDamageMultiplier;
        boLocal.ignoreBodyHits = defaultIgnoreBodyHits;

        var bcomp = localBullet.GetComponent<Bullet>();
        if (bcomp == null) bcomp = localBullet.AddComponent<Bullet>();
        bcomp.Launch(bulletLifetime, false);

        if (defaultSelfDamagePerShot > 0)
        {
            var ph = GetComponentInParent<PlayerHealth>();
            if (ph != null) ph.TakeDamage(defaultSelfDamagePerShot, false);
        }
    }

    IEnumerator ReloadCoroutine()
    {
        if (isReloading) yield break;
        // remember whether we started the reload from empty
        bool wasEmpty = currentAmmo <= 0;

        isReloading = true;
        UpdateAmmoUI(true);
        yield return new WaitForSeconds(reloadTime);
        currentAmmo = maxAmmo;
        isReloading = false;
        UpdateAmmoUI();

        // If we started reload from empty and we are the owner, notify local TarotSelection (Star card)
        PhotonView pv = GetComponentInParent<PhotonView>();
        bool isOwner = (pv == null) || !PhotonNetwork.InRoom || pv.IsMine;
        if (wasEmpty && isOwner)
        {
            var tarot = GetComponentInParent<TarotSelection>();
            if (tarot != null)
            {
                try { tarot.OnReloadedEmpty(); } catch (Exception ex) { Debug.LogWarning("[SimpleShooter] Tarot.OnReloadedEmpty threw: " + ex); }
            }
        }
    }

    void UpdateAmmoUI(bool showReloading = false)
    {
        if (ammoText == null) return;

        // Prevent remote clients from touching the local player's HUD.
        PhotonView pv = GetComponentInParent<PhotonView>();
        if (PhotonNetwork.InRoom && pv != null && !pv.IsMine)
            return;

        ammoText.text = showReloading ? "RELOADING..." : $"{currentAmmo} / {maxAmmo}";
    }

    // API
    public void AddAmmo(int amount) { currentAmmo = Mathf.Clamp(currentAmmo + amount, 0, maxAmmo); UpdateAmmoUI(); }
    public void SetAmmo(int amount) { currentAmmo = Mathf.Clamp(amount, 0, maxAmmo); UpdateAmmoUI(); }
    public int GetCurrentAmmo() => currentAmmo;

    public void SetFireRateMultiplier(float multiplier)
    {
        if (originalFireRate <= 0f) originalFireRate = fireRate;
        fireRate = originalFireRate * multiplier;
        Debug.Log($"[SimpleShooter] fireRate adjusted to {fireRate} (mult {multiplier})");
    }
    public void RestoreFireRate()
    {
        if (originalFireRate > 0f) { fireRate = originalFireRate; Debug.Log($"[SimpleShooter] fireRate restored to {fireRate}"); }
    }
}
