using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using Photon.Pun;

/// <summary>
/// CharacterSkills - charge handling + character-specific abilities
/// Uses your existing charge system and maps Q/E to abilities by characterName.
/// NOTE: Prefabs spawned via PhotonNetwork.Instantiate must be placed in Resources/ for Photon.
/// Damage/status application uses SendMessage so you can integrate with your Health/System APIs.
/// All abilities now REQUIRE the charge pool to be FULL (currentCharge >= maxCharge) and will consume the entire pool on cast.
/// </summary>
[DisallowMultipleComponent]
public class CharacterSkills : MonoBehaviourPunCallbacks, IPunObservable
{
    [Header("Character")]
    [Tooltip("Friendly display name for this character. Use: MAYHEM, IVY, REGALIA, SIGIL (case-insensitive).")]
    public string characterName = "NewCharacter";

    [Header("Skills (placeholders)")]
    public string offensiveSkillName = "OffensiveSkill (Q)";
    public string defensiveSkillName = "DefensiveSkill (E)";

    [Header("Charge (shared between Q and E)")]
    [Tooltip("Maximum charge value.")]
    public float maxCharge = 100f;
    [Tooltip("Starting charge value (will be 0 per your request).")]
    public float startCharge = 0f;

    [Tooltip("Image (center bottom bar) used to display charge. Assign your center-bottom UI Image here.")]
    public Image chargeBarImage;

    [Tooltip("If true, sync charge value over Photon.")]
    public bool syncChargeOverNetwork = true;

    // regen settings: +5 every 1 second
    private const float REGEN_INTERVAL = 1.0f;
    private const float REGEN_AMOUNT = 50f;

    // internal state
    [SerializeField] private float currentCharge = 0f;
    private float regenAccumulator = 0f;

    // cached rect width for static-image fallback
    private float originalBarWidth = -1f;
    private RectTransform chargeBarRect;

    // input keys
    private KeyCode offensiveKey = KeyCode.Q;
    private KeyCode defensiveKey = KeyCode.E;

    private const float EPS = 0.0001f;

    #region Ability Prefabs & tuning
    [Header("Ability Prefabs (assign in inspector)")]
    public GameObject houndPrefab;            // MAYHEM Q
    public GameObject puffPrefab;             // MAYHEM E (puff of smoke)
    public GameObject orbEffectPrefab;        // MAYHEM visual orb (optional)

    public GameObject turretPlantPrefab;      // IVY Q (place on wall)
    public GameObject plantWallPrefab;        // IVY E (wall)

    public GameObject beaconPrefab;           // REGALIA Q (marker / beacon)
    public GameObject cannonballPrefab;       // REGALIA: the cannonball that will be dropped
    public GameObject guardPrefab;            // REGALIA E (royal brigand)

    public GameObject electricFieldPrefab;    // SIGIL Q (AOE)
    public GameObject homeostasisEffectPrefab;// SIGIL E (visual effect)

    [Header("Ability tuning (defaults)")]
    public float gnawingDamage = 50f;
    public float gnawingHoundSpeed = 12f;
    public float gnawingLifetime = 4f;

    public float darkPropulsionSpeed = 25f;
    public float darkPropulsionDuration = 0.25f;

    public float sporewardDuration = 20f;   // turret lifetime (if you want)
    public float thornveilDuration = 10f;

    public float downwardDelay = 2f;
    public float downwardDamage = 75f;

    public float tetherRadius = 4f;
    public float tetherRootDuration = 1.5f;

    public float homeostasisShieldAmount = 200f;
    public float homeostasisHealAmount = 50f;
    public float homeostasisDuration = 1f;

    [Header("Ability charge costs (kept for reference but NOT USED — all abilities require full charge now)")]
    public float cost_MAYHEM_Q = 20f; // Gnawing Dread (not used)
    public float cost_MAYHEM_E = 15f; // Dark Propulsion (not used)
    public float cost_IVY_Q = 25f;    // Sporeward (not used)
    public float cost_IVY_E = 20f;    // Thornveil (not used)
    public float cost_REGALIA_Q = 30f;// Downward Decree (not used)
    public float cost_REGALIA_E = 18f;// Royal Brigand (not used)
    public float cost_SIGIL_Q = 20f;  // Tethering Pulse (not used)
    public float cost_SIGIL_E = 25f;  // Homeostasis (not used)
    #endregion

    void Awake()
    {
        // Enforce start state (you requested start at 0)
        currentCharge = Mathf.Clamp(startCharge, 0f, maxCharge);
        regenAccumulator = 0f;

        if (chargeBarImage != null)
        {
            chargeBarRect = chargeBarImage.rectTransform;
            // capture original width so we can re-scale if image isn't a Filled type
            originalBarWidth = chargeBarRect.sizeDelta.x;
        }
        UpdateChargeBar();
    }

    void Start()
    {
        // If the assigned image isn't Filled, we will resize its width. Warn the user.
        if (chargeBarImage != null && chargeBarImage.type != Image.Type.Filled)
        {
            Debug.LogWarning($"CharacterSkills ({characterName}): chargeBarImage is not of type 'Filled'. The script will resize the image width as a fallback. If possible use Image.Type = Filled for simpler behavior.");
        }
    }

    void Update()
    {
        // Only owner should run regeneration and input
        if (photonView != null && PhotonNetwork.InRoom && !photonView.IsMine)
            return;

        // regenerate discretely: accumulate time and add 5 per full second tick
        RegenOverTime();

        // Input to use skills - now character-specific
        if (Input.GetKeyDown(offensiveKey))
        {
            TryUseOffensiveByCharacter();
        }
        if (Input.GetKeyDown(defensiveKey))
        {
            TryUseDefensiveByCharacter();
        }
    }

    private void RegenOverTime()
    {
        if (currentCharge >= maxCharge - EPS) return;

        regenAccumulator += Time.deltaTime;
        if (regenAccumulator >= REGEN_INTERVAL)
        {
            int ticks = Mathf.FloorToInt(regenAccumulator / REGEN_INTERVAL);
            float amountToAdd = REGEN_AMOUNT * ticks;
            AddCharge(amountToAdd);
            regenAccumulator -= ticks * REGEN_INTERVAL;
        }
    }

    #region Charge API
    public void AddCharge(float amount)
    {
        if (amount <= 0f) return;
        float prev = currentCharge;
        currentCharge = Mathf.Clamp(currentCharge + amount, 0f, maxCharge);
        if (Mathf.Abs(currentCharge - prev) > EPS)
            UpdateChargeBar();
    }

    public bool ConsumeCharge(float amount)
    {
        if (amount <= 0f) return true;
        if (currentCharge + EPS >= amount)
        {
            currentCharge = Mathf.Clamp(currentCharge - amount, 0f, maxCharge);
            UpdateChargeBar();
            return true;
        }
        return false;
    }

    public bool CanUseSkill()
    {
        // Keep this generic if you want quick checks; final full-charge validation happens at cast time.
        return currentCharge > EPS;
    }

    private void UpdateChargeBar()
    {
        if (chargeBarImage == null) return;

        float t = (maxCharge <= 0f) ? 0f : Mathf.Clamp01(currentCharge / maxCharge);

        if (chargeBarImage.type == Image.Type.Filled)
        {
            chargeBarImage.fillAmount = t;
        }
        else
        {
            // resize width proportionally (preserve height)
            if (chargeBarRect != null && originalBarWidth > 0f)
            {
                Vector2 s = chargeBarRect.sizeDelta;
                s.x = originalBarWidth * t;
                chargeBarRect.sizeDelta = s;
            }
            else
            {
                Vector3 sc = chargeBarImage.transform.localScale;
                sc.x = t;
                chargeBarImage.transform.localScale = sc;
            }
        }
    }
    #endregion

    #region Character -> Ability dispatch
    private void TryUseOffensiveByCharacter()
    {
        string key = (characterName ?? "").Trim().ToUpperInvariant();
        switch (key)
        {
            case "MAYHEM":
                TryConsumeFullChargeAndPerform(() => Ability_Mayhem_GnawingDread());
                break;
            case "IVY":
                TryConsumeFullChargeAndPerform(() => Ability_Ivy_Sporeward());
                break;
            case "REGALIA":
                TryConsumeFullChargeAndPerform(() => Ability_Regalia_DownwardDecree());
                break;
            case "SIGIL":
                TryConsumeFullChargeAndPerform(() => Ability_Sigil_TetheringPulse());
                break;
            default:
                TryConsumeFullChargeAndPerform(() => Debug.Log($"{characterName}: used generic offensive action"));
                break;
        }
    }

    private void TryUseDefensiveByCharacter()
    {
        string key = (characterName ?? "").Trim().ToUpperInvariant();
        switch (key)
        {
            case "MAYHEM":
                TryConsumeFullChargeAndPerform(() => Ability_Mayhem_DarkPropulsion());
                break;
            case "IVY":
                TryConsumeFullChargeAndPerform(() => Ability_Ivy_Thornveil());
                break;
            case "REGALIA":
                TryConsumeFullChargeAndPerform(() => Ability_Regalia_RoyalBrigand());
                break;
            case "SIGIL":
                TryConsumeFullChargeAndPerform(() => Ability_Sigil_Homeostasis());
                break;
            default:
                TryConsumeFullChargeAndPerform(() => Debug.Log($"{characterName}: used generic defensive action"));
                break;
        }
    }

    /// <summary>
    /// New: Abilities require full charge (currentCharge >= maxCharge). If satisfied, consumes entire pool (sets to 0) and invokes perform().
    /// </summary>
    private void TryConsumeFullChargeAndPerform(Action perform)
    {
        if (currentCharge + EPS >= maxCharge)
        {
            // consume entire pool
            currentCharge = 0f;
            UpdateChargeBar();
            perform?.Invoke();
            Debug.Log($"{characterName}: used full-charge skill. Charge now {currentCharge}/{maxCharge}");
        }
        else
        {
            Debug.Log($"{characterName}: insufficient charge for full-charge skill (need {maxCharge}, have {currentCharge}).");
        }
    }
    #endregion

    #region Abilities Implementation (owner-only runtime behaviour)
    // NOTE: All spawning is done via SpawnPrefab() which uses Photon when in-room.
    // Damage/status application uses SendMessage so adapt to your project's API (TakeDamage, Heal, ApplyRoot, etc).

    // -------- MAYHEM --------
    private void Ability_Mayhem_GnawingDread()
    {
        Vector3 spawnPos = GetAbilitySpawnOrigin();
        Quaternion rot = Quaternion.LookRotation(GetAimDirection(spawnPos));

        if (houndPrefab != null)
        {
            int ownerActor = (PhotonNetwork.InRoom && PhotonNetwork.LocalPlayer != null)
                ? PhotonNetwork.LocalPlayer.ActorNumber
                : -1;

            string smokeName = puffPrefab != null ? puffPrefab.name : "";

            object[] instData = new object[]
            {
                ownerActor,                         // [0] owner actor number
                Mathf.RoundToInt(gnawingDamage),    // [1] damage (int)
                gnawingHoundSpeed,                  // [2] speed (float)
                3f,                                  // [3] lifetime (float)
                smokeName,                          // [4] smoke prefab resource name (string) or ""
                5f,                                  // [5] smokeDuration (float)
                0.2f                                 // [6] smokeInterval (float)
            };

            GameObject go = SpawnPrefab(houndPrefab, spawnPos, rot, instData);
            if (go == null) return;

            // Local-fallback: make sure hound is initialized immediately if Photon wasn't used
            var hb = go.GetComponent<HoundBehaviour>();
            if (hb != null && !PhotonNetwork.InRoom)
            {
                hb.InitializeFromSpawner(ownerActor, this.gameObject,
                                        Mathf.RoundToInt(gnawingDamage),
                                        gnawingHoundSpeed,
                                        3f,
                                        puffPrefab,
                                        5f,
                                        0.2f);
            }
        }
    }





    private void Ability_Mayhem_DarkPropulsion()
    {
        // short invulnerable dash/charge forward (owner only). We'll move the transform forward for a brief moment.
        StartCoroutine(DashForwardCoroutine(darkPropulsionDuration, darkPropulsionSpeed, spawnPuff: true));
    }

    // -------- IVY --------
    private void Ability_Ivy_Sporeward()
    {
        // place a turret plant on any wall/surface under your reticle.
        Ray center = (GetCameraOrDefault())?.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f)) ?? new Ray(transform.position, transform.forward);
        if (Physics.Raycast(center, out RaycastHit hit, 100f))
        {
            Vector3 pos = hit.point + hit.normal * 0.01f;
            Quaternion rot = Quaternion.LookRotation(-hit.normal, Vector3.up); // face away from surface
            GameObject go = SpawnPrefab(turretPlantPrefab, pos, rot);
            // parent to hit object to attach to wall
            if (hit.collider != null && go != null) go.transform.SetParent(hit.collider.transform, true);
            DestroyIfLocal(go, sporewardDuration);
        }
        else
        {
            Debug.Log($"{characterName}: No valid surface under reticle to place turret.");
        }
    }

    private void Ability_Ivy_Thornveil()
    {
        // place a plant wall at the reticle location (or in front if no hit)
        Ray center = (GetCameraOrDefault())?.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f)) ?? new Ray(transform.position, transform.forward);
        Vector3 pos;
        Quaternion rot;
        if (Physics.Raycast(center, out RaycastHit hit, 100f))
        {
            pos = hit.point + hit.normal * 0.01f;
            rot = Quaternion.LookRotation(hit.normal, Vector3.up);
        }
        else
        {
            pos = center.origin + center.direction * 10f;
            rot = Quaternion.LookRotation(-center.direction, Vector3.up);
        }
        GameObject go = SpawnPrefab(plantWallPrefab, pos, rot);
        DestroyIfLocal(go, thornveilDuration);
    }

    // -------- REGALIA --------
    private void Ability_Regalia_DownwardDecree()
    {
        // Throw a beacon/marker that after downwardDelay spawns a cannonball which deals damage in AoE.
        Ray center = (GetCameraOrDefault())?.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f)) ?? new Ray(transform.position, transform.forward);
        Vector3 pos;
        if (Physics.Raycast(center, out RaycastHit hit, 100f))
            pos = hit.point;
        else
            pos = center.origin + center.direction * 20f;

        GameObject beacon = SpawnPrefab(beaconPrefab != null ? beaconPrefab : new GameObject("Beacon"), pos, Quaternion.identity);
        StartCoroutine(DelayedCannonball(pos, downwardDelay));
        DestroyIfLocal(beacon, downwardDelay + 0.5f);
    }

    private void Ability_Regalia_RoyalBrigand()
    {
        // Summon a guard in front of the player that blocks enemy fire (you'll need guard script to implement blocking)
        Vector3 spawn = transform.position + transform.forward * 1.5f + Vector3.up * 0.25f;
        Quaternion rot = Quaternion.LookRotation(transform.forward);
        GameObject guard = SpawnPrefab(guardPrefab, spawn, rot);
        DestroyIfLocal(guard, 12f); // default lifetime
    }

    // -------- SIGIL --------
    private void Ability_Sigil_TetheringPulse()
    {
        // Spawn electric field at player that roots and stops healing
        Vector3 spawn = transform.position;
        GameObject field = SpawnPrefab(electricFieldPrefab, spawn, Quaternion.identity);
        // Notify local objects: use OverlapSphere to inform targets (owner-side)
        ApplyAOEAction(spawn, tetherRadius, (Collider c) =>
        {
            // Suggested API on targets: "ApplyRoot" and "StopHealing"
            c.gameObject.SendMessage("ApplyRoot", tetherRootDuration, SendMessageOptions.DontRequireReceiver);
            c.gameObject.SendMessage("StopHealing", tetherRootDuration, SendMessageOptions.DontRequireReceiver);
        });
        DestroyIfLocal(field, 2f);
    }

    private void Ability_Sigil_Homeostasis()
    {
        // Instantly grant shield and heal while immobilizing and granting invulnerability for a short time.
        // We use SendMessage to call into your player's health/shield system: "ApplyShield", "Heal", "ApplyInvulnerability", "SetImmobilized"
        gameObject.SendMessage("ApplyShield", homeostasisShieldAmount, SendMessageOptions.DontRequireReceiver);
        gameObject.SendMessage("Heal", homeostasisHealAmount, SendMessageOptions.DontRequireReceiver);
        gameObject.SendMessage("ApplyInvulnerability", homeostasisDuration, SendMessageOptions.DontRequireReceiver);
        // Immobilize (you'll want to un-immobilize after duration)
        gameObject.SendMessage("SetImmobilized", true, SendMessageOptions.DontRequireReceiver);
        StartCoroutine(HomeostasisEndCoroutine(homeostasisDuration));
        // spawn effect
        if (homeostasisEffectPrefab != null)
        {
            GameObject fx = SpawnPrefab(homeostasisEffectPrefab, transform.position, Quaternion.identity);
            DestroyIfLocal(fx, homeostasisDuration + 0.25f);
        }
    }
    #endregion

    #region Helper coroutines & utilities
    IEnumerator DashForwardCoroutine(float dur, float speed, bool spawnPuff = false)
    {
        float t = 0f;
        Vector3 dir = transform.forward;
        // optional: set character controller state / disable collisions
        while (t < dur)
        {
            float dt = Time.deltaTime;
            transform.position += dir * speed * dt;
            t += dt;
            yield return null;
        }
        if (spawnPuff && puffPrefab != null)
        {
            GameObject p = SpawnPrefab(puffPrefab, transform.position, Quaternion.identity);
            DestroyIfLocal(p, 1.5f);
        }
    }

    IEnumerator DelayedCannonball(Vector3 landPos, float delay)
    {
        yield return new WaitForSeconds(delay);
        if (cannonballPrefab != null)
        {
            // spawn slightly above and let physics drop, or spawn effect and apply AoE damage immediately
            Vector3 spawn = landPos + Vector3.up * 12f;
            GameObject cb = SpawnPrefab(cannonballPrefab, spawn, Quaternion.identity);
            // if prefab has Rigidbody, it will fall. If not, we can simulate explosion immediately:
            // Wait a short time to allow it to reach ground if physics is used
            yield return new WaitForSeconds(0.6f);
            // Apply AoE damage at landPos (use OverlapSphere)
            float radius = 2.5f;
            ApplyAOEAction(landPos, radius, (Collider c) =>
            {
                c.gameObject.SendMessage("TakeDamage", downwardDamage, SendMessageOptions.DontRequireReceiver);
            });
            DestroyIfLocal(cb, 2f);
        }
        else
        {
            // fallback: immediate AoE
            float radius = 2.5f;
            ApplyAOEAction(landPos, radius, (Collider c) =>
            {
                c.gameObject.SendMessage("TakeDamage", downwardDamage, SendMessageOptions.DontRequireReceiver);
            });
        }
    }

    IEnumerator HomeostasisEndCoroutine(float duration)
    {
        yield return new WaitForSeconds(duration);
        // un-immobilize player
        gameObject.SendMessage("SetImmobilized", false, SendMessageOptions.DontRequireReceiver);
        // Remove invulnerability if your system expects a call
        gameObject.SendMessage("RemoveInvulnerability", SendMessageOptions.DontRequireReceiver);
    }

    private void ApplyAOEAction(Vector3 center, float radius, Action<Collider> action)
    {
        var cols = Physics.OverlapSphere(center, radius);
        foreach (var c in cols)
        {
            // skip self
            if (c.gameObject == gameObject) continue;
            try { action?.Invoke(c); } catch { }
        }
    }

    private Vector3 GetAbilitySpawnOrigin()
    {
        // prefer a spawn point at camera or character position
        Camera cam = GetCameraOrDefault();
        if (cam != null) return cam.transform.position + cam.transform.forward * 0.5f;
        return transform.position + Vector3.up * 1.2f + transform.forward * 0.6f;
    }

    private Camera GetCameraOrDefault()
    {
        // prefer a local camera (owner)
        Camera c = GetComponentInChildren<Camera>(true);
        if (c != null) return c;
        return Camera.main;
    }

    private Vector3 GetAimDirection(Vector3 origin)
    {
        Camera cam = GetCameraOrDefault();
        if (cam != null)
        {
            Ray r = cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
            if (Physics.Raycast(r, out RaycastHit h, 1000f))
                return (h.point - origin).normalized;
            return r.direction.normalized;
        }
        return transform.forward;
    }

    /// <summary>
    /// Spawns a prefab using Photon if in a room. Prefab must be in Resources/ for PhotonNetwork.Instantiate.
    /// If prefab is null, returns null.
    /// </summary>
    // replace old SpawnPrefab with this
    // in CharacterSkills (or wherever SpawnPrefab lives)
    private GameObject SpawnPrefab(GameObject prefab, Vector3 pos, Quaternion rot, object[] instantiationData = null)
    {
        if (prefab == null) return null;
        if (PhotonNetwork.InRoom)
        {
            string resName = prefab.name;
            try
            {
                if (instantiationData != null)
                    return PhotonNetwork.Instantiate(resName, pos, rot, 0, instantiationData);
                else
                    return PhotonNetwork.Instantiate(resName, pos, rot, 0);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"SpawnPrefab: PhotonNetwork.Instantiate failed for '{resName}'. Falling back to local Instantiate. Exception: {ex.Message}");
                return Instantiate(prefab, pos, rot);
            }
        }
        else
        {
            return Instantiate(prefab, pos, rot);
        }
    }

    private void DestroyIfLocal(GameObject go, float after)
    {
        if (go == null) return;
        if (PhotonNetwork.InRoom)
        {
            // If networked spawn, do not call Destroy (it should be managed by a network script) — but we'll schedule a local destroy only if it's a local-only instantiate
            // Safe fallback: destroy local instance after time (for preview). Remove/comment if you have networked lifetime management.
            Destroy(go, after);
        }
        else
        {
            Destroy(go, after);
        }
    }
    #endregion

    #region Photon Serialization
    public void OnPhotonSerializeView(PhotonStream stream, PhotonMessageInfo info)
    {
        if (!syncChargeOverNetwork) return;

        if (stream.IsWriting)
        {
            // Owner sends current charge
            stream.SendNext(currentCharge);
        }
        else
        {
            // Remote receives
            float incoming = (float)stream.ReceiveNext();
            currentCharge = Mathf.Clamp(incoming, 0f, maxCharge);
            UpdateChargeBar();
        }
    }
    #endregion

    // Public API
    public float GetCurrentCharge() => currentCharge;
    public float GetChargeNormalized() => (maxCharge <= 0f) ? 0f : currentCharge / maxCharge;
    public void SetCharge(float value) { currentCharge = Mathf.Clamp(value, 0f, maxCharge); UpdateChargeBar(); }
}
