using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using Photon.Pun;
using TMPro;

/// <summary>
/// CharacterSkills - charge handling + character-specific abilities
/// Uses your existing charge system and maps Q/E to abilities by characterName.
/// NOTE: Prefabs spawned via PhotonNetwork.Instantiate must be placed in Resources/ for Photon.
/// Damage/status application uses SendMessage so you can integrate with your Health/System APIs.
/// All abilities now REQUIRE the charge pool to be FULL (currentCharge >= maxCharge) and will consume the entire pool on successful cast.
/// If an aiming/placement error occurs, charge is NOT consumed; instead a short cooldown is applied.
/// </summary>
[DisallowMultipleComponent]
public class CharacterSkills : MonoBehaviourPunCallbacks, IPunObservable
{
    [Header("Placement filters")]
    [Tooltip("Tags to ignore when raycasting for ability placement (e.g. Hitbox, Bullet, Untagged).")]
    public string[] placementIgnoredTags = new string[] { "Hitbox", "Bullet", "Untagged" };

    [Header("UI Feedback")]
    [Tooltip("Optional TMP text used for temporary error messages (alpha should start at 0).")]
    public TextMeshProUGUI errorMessageText;

    [Header("Cooldown on error")]
    [Tooltip("Cooldown (seconds) applied when an ability fails to cast due to aiming/placement error.")]
    public float errorCooldownOnAimingError = 1f;

    // runtime: time until next skill input is accepted (used when an aiming error occurs)
    private float nextSkillAvailableTime = 0f;

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

    public float darkPropulsionSpeed = 50f;
    public float darkPropulsionDuration = 0.5f;

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

        bool __isOwner = (photonView == null) || !PhotonNetwork.InRoom || photonView.IsMine;
        if (chargeBarImage != null)
        {
            chargeBarImage.gameObject.SetActive(__isOwner);
        }

        UpdateChargeBar();

        // ensure error text is initially invisible
        if (errorMessageText != null)
        {
            Color col = errorMessageText.color;
            col.a = 0f;
            errorMessageText.color = col;
            errorMessageText.text = string.Empty;
        }
    }

    void Start()
    {
        // If the assigned image isn't Filled, we will resize its width. Warn the user.
        if (chargeBarImage != null && chargeBarImage.type != Image.Type.Filled)
        {
            Debug.LogWarning($"CharacterSkills ({characterName}): chargeBarImage is not of type 'Filled'. The script will resize the image width as a fallback. If possible use Image.Type = Filled for simpler behavior.");
        }
                // Defensive: ensure charge UI remains visible only for owner
        bool __isOwnerStart = (photonView == null) || !PhotonNetwork.InRoom || photonView.IsMine;
        if (chargeBarImage != null)
            chargeBarImage.gameObject.SetActive(__isOwnerStart);

    }

    void Update()
    {
        // Only owner should run regeneration and input
        if (photonView != null && PhotonNetwork.InRoom && !photonView.IsMine)
            return;

        // regenerate discretely: accumulate time and add 5 per full second tick
        RegenOverTime();

        // Input to use skills - now character-specific
        // If we're currently inside an error-imposed cooldown, don't accept skill input
        if (Time.time < nextSkillAvailableTime)
            return;

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
        // if the UI has been disabled for remote instances, avoid modifying it
        if (!chargeBarImage.gameObject.activeInHierarchy) return;

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
                TryConsumeFullChargeAndPerform(() => { Ability_Mayhem_GnawingDread(); return true; });
                break;
            case "IVY":
                TryConsumeFullChargeAndPerform(() => Ability_Ivy_Sporeward());
                break;
            case "REGALIA":
                TryConsumeFullChargeAndPerform(() => Ability_Regalia_DownwardDecree());
                break;
            case "SIGIL":
                TryConsumeFullChargeAndPerform(() => { Ability_Sigil_TetheringPulse(); return true; });
                break;
            default:
                TryConsumeFullChargeAndPerform(() => { Debug.Log($"{characterName}: used generic offensive action"); return true; });
                break;
        }
    }

    private void TryUseDefensiveByCharacter()
    {
        string key = (characterName ?? "").Trim().ToUpperInvariant();
        switch (key)
        {
            case "MAYHEM":
                TryConsumeFullChargeAndPerform(() => { Ability_Mayhem_DarkPropulsion(); return true; });
                break;
            case "IVY":
                TryConsumeFullChargeAndPerform(() => Ability_Ivy_Thornveil());
                break;
            case "REGALIA":
                TryConsumeFullChargeAndPerform(() => { Ability_Regalia_RoyalBrigand(); return true; });
                break;
            case "SIGIL":
                TryConsumeFullChargeAndPerform(() => { Ability_Sigil_Homeostasis(); return true; });
                break;
            default:
                TryConsumeFullChargeAndPerform(() => { Debug.Log($"{characterName}: used generic defensive action"); return true; });
                break;
        }
    }

    /// <summary>
    /// Attempt to perform a full-charge skill. 'tryPerform' should return true if the ability succeeded
    /// (so charge should be consumed). Return false to indicate the ability failed (aim/placement error).
    /// On failure, charge is preserved and an error cooldown is applied.
    /// </summary>
    private void TryConsumeFullChargeAndPerform(Func<bool> tryPerform)
    {
        if (currentCharge + EPS < maxCharge)
        {
            Debug.Log($"{characterName}: insufficient charge for full-charge skill (need {maxCharge}, have {currentCharge}).");
            return;
        }

        bool success = false;
        try
        {
            success = tryPerform?.Invoke() ?? false;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"Exception while performing skill: {ex.Message}");
            success = false;
        }

        if (success)
        {
            // consume entire pool only on success
            currentCharge = 0f;
            UpdateChargeBar();
            Debug.Log($"{characterName}: used full-charge skill. Charge now {currentCharge}/{maxCharge}");
        }
        else
        {
            // apply short cooldown and do not consume charge
            nextSkillAvailableTime = Time.time + errorCooldownOnAimingError;
            Debug.Log($"{characterName}: skill cast failed/invalid; applying error cooldown {errorCooldownOnAimingError}s. Charge preserved ({currentCharge}/{maxCharge})");
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
    // Returns true on successful placement & spawn, false if validation/aim failed.
    private bool Ability_Ivy_Sporeward()
    {
        // prefer a camera ray; fallback to forward from player
        Ray center = (GetCameraOrDefault())?.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f)) ?? new Ray(transform.position, transform.forward);

        // Use RaycastAll and pick the first hit that is NOT ignored by tag
        RaycastHit[] hits = Physics.RaycastAll(center, 100f);
        if (hits == null || hits.Length == 0)
        {
            ShowErrorMessage("Must aim at a wall!");
            return false;
        }

        // Sort hits by distance to ensure we pick closest valid surface
        Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

        RaycastHit? chosen = null;
        foreach (var h in hits)
        {
            if (IsIgnoredPlacementTag(h.collider)) continue;
            chosen = h;
            break;
        }

        if (!chosen.HasValue)
        {
            // No non-ignored hit found
            ShowErrorMessage("Must aim at a wall!");
            return false;
        }

        var hit = chosen.Value;

        // IMPORTANT: require the hit collider to be tagged "Wall"
        if (!hit.collider.CompareTag("Wall"))
        {
            ShowErrorMessage("Must aim at a wall!");
            return false;
        }

        // valid wall hit -> compute placement and orientation
        Vector3 pos = hit.point + hit.normal * 0.01f;
        Quaternion rot = Quaternion.LookRotation(-hit.normal, Vector3.up); // face away from surface

        int ownerActor = (PhotonNetwork.InRoom && PhotonNetwork.LocalPlayer != null)
            ? PhotonNetwork.LocalPlayer.ActorNumber
            : -1;

        object[] instData = new object[] { ownerActor, 50 }; // HP = 50 for Sporeward

        GameObject go = SpawnPrefab(turretPlantPrefab, pos, rot, instData);

        // If locally instantiated (Photon fallback or offline preview), initialize and align it explicitly
        if (go != null && !PhotonNetwork.InRoom)
        {
            var st = go.GetComponent<SporewardTurret>();
            if (st != null)
            {
                st.InitializeFromSpawner(ownerActor, this.gameObject, 50); // hp=50
                // align to the surface precisely (PlaceOnSurface added to SporewardTurret)
                st.PlaceOnSurface(hit, parentToSurface: false);
            }
        }

        return true; // success
    }

    // Returns true on successful placement & spawn, false if validation/aim failed.
    private bool Ability_Ivy_Thornveil()
    {
        // center ray from camera; fallback to player forward
        Ray center = (GetCameraOrDefault())?.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f)) ?? new Ray(transform.position, transform.forward);

        // RaycastAll and choose first non-ignored hit
        RaycastHit[] hits = Physics.RaycastAll(center, 100f);
        if (hits == null || hits.Length == 0)
        {
            ShowErrorMessage("Must aim at ground!");
            return false;
        }

        Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

        RaycastHit? chosen = null;
        foreach (var h in hits)
        {
            if (IsIgnoredPlacementTag(h.collider)) continue;
            chosen = h;
            break;
        }

        if (!chosen.HasValue)
        {
            ShowErrorMessage("Must aim at ground!");
            return false;
        }

        var hit = chosen.Value;

        // Require ground tag
        if (!hit.collider.CompareTag("Ground"))
        {
            ShowErrorMessage("Must aim at ground!");
            return false;
        }

        // Place wall slightly above the hit point
        Vector3 pos = hit.point + hit.normal * 0.01f;

        // Compute forward projected onto the surface plane so the wall stands upright and faces roughly the camera/player
        Camera cam = GetCameraOrDefault();
        Vector3 forward = (cam != null) ? cam.transform.forward : transform.forward;
        Vector3 forwardOnPlane = Vector3.ProjectOnPlane(forward, hit.normal).normalized;
        if (forwardOnPlane.sqrMagnitude < 0.001f) forwardOnPlane = transform.forward; // fallback

        Quaternion rot = Quaternion.LookRotation(forwardOnPlane, hit.normal);

        int ownerActor = (PhotonNetwork.InRoom && PhotonNetwork.LocalPlayer != null)
            ? PhotonNetwork.LocalPlayer.ActorNumber
            : -1;

        object[] instData = new object[] { ownerActor, 300 }; // HP = 300 for Thornveil

        GameObject go = SpawnPrefab(plantWallPrefab, pos, rot, instData);

        // initialize locally if Photon isn't being used
        if (go != null && !PhotonNetwork.InRoom)
        {
            var tw = go.GetComponent<ThornveilWall>();
            if (tw != null)
            {
                tw.InitializeFromSpawner(ownerActor, this.gameObject, 300); // hp=300
                // Align to actual hit surface
                tw.PlaceOnGround(hit, parentToSurface: false);
            }
        }

        DestroyIfLocal(go, thornveilDuration);

        return true; // success
    }

    // -------- REGALIA --------
    // DownwardDecree now returns bool: true if beacon was placed (valid ground), false if invalid aim
    private bool Ability_Regalia_DownwardDecree()
    {
        // center ray from camera; fallback to player forward
        Ray center = (GetCameraOrDefault())?.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f)) ?? new Ray(transform.position, transform.forward);

        // RaycastAll and choose first non-ignored hit
        RaycastHit[] hits = Physics.RaycastAll(center, 100f);
        if (hits == null || hits.Length == 0)
        {
            ShowErrorMessage("Must aim at ground!");
            return false;
        }

        Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

        RaycastHit? chosen = null;
        foreach (var h in hits)
        {
            if (IsIgnoredPlacementTag(h.collider)) continue;
            chosen = h;
            break;
        }

        if (!chosen.HasValue)
        {
            ShowErrorMessage("Must aim at ground!");
            return false;
        }

        var hit = chosen.Value;

        // hard-coded requirement: Ground tag
        if (!hit.collider.CompareTag("Ground"))
        {
            ShowErrorMessage("Must aim at ground!");
            return false;
        }

        Vector3 pos = hit.point;

        int ownerActor = (PhotonNetwork.InRoom && PhotonNetwork.LocalPlayer != null)
            ? PhotonNetwork.LocalPlayer.ActorNumber
            : -1;

        // We pass: [0] ownerActor (int), [1] cannonballPrefabName (string), [2] damage (float), [3] radius (float), [4] delay (float)
        string cbName = cannonballPrefab != null ? cannonballPrefab.name : "";
        object[] instData = new object[] { ownerActor, cbName, downwardDamage, 2.5f, downwardDelay };

        GameObject beacon = SpawnPrefab(beaconPrefab != null ? beaconPrefab : new GameObject("Beacon"), pos, Quaternion.identity, instData);

        // locally initialize if Photon wasn't used
        if (beacon != null && !PhotonNetwork.InRoom)
        {
            var b = beacon.GetComponent<Beacon>();
            if (b != null)
                b.InitializeFromSpawner(ownerActor, cbName, downwardDamage, 2.5f, downwardDelay);
        }

        // keep a small life so stray beacons don't persist in local preview mode
        DestroyIfLocal(beacon, downwardDelay + 5f);

        return true;
    }

    private void Ability_Regalia_RoyalBrigand()
    {
        // Summon a guard in front of the player that blocks enemy fire.
        Vector3 spawn = transform.position + transform.forward * 3f + Vector3.up * 0.25f;
        Quaternion rot = Quaternion.LookRotation(transform.forward);

        int ownerActor = (PhotonNetwork.InRoom && PhotonNetwork.LocalPlayer != null)
            ? PhotonNetwork.LocalPlayer.ActorNumber
            : -1;

        // pass owner and hp to the guard via instantiationData: [0] ownerActor (int), [1] hp (int)
        object[] instData = new object[] { ownerActor, 150 }; // guard HP default 150 (adjust as needed)

        GameObject guard = SpawnPrefab(guardPrefab, spawn, rot, instData);

        // initialize locally if Photon isn't being used
        if (guard != null && !PhotonNetwork.InRoom)
        {
            var gb = guard.GetComponent<GuardBehavior>();
            if (gb != null)
                gb.InitializeFromSpawner(ownerActor, this.gameObject, 150);
        }

        DestroyIfLocal(guard, 12f); // default lifetime
    }

    // -------- SIGIL --------
    // --- TETHERING PULSE (Q) ---
// CharacterSkills: SIGIL Q - simplified spawn: only pass owner actor (no radius/expand overrides)
private bool Ability_Sigil_TetheringPulse()
{
    // Only the owner runs this input (CharacterSkills owner check exists), so safe to spawn
    int ownerActor = (PhotonNetwork.InRoom && PhotonNetwork.LocalPlayer != null)
        ? PhotonNetwork.LocalPlayer.ActorNumber
        : -1;

    // We only pass the owner actor in instantiationData so the pulse knows who spawned it.
    // DO NOT pass tetherRadius/tetherRootDuration/expandDuration here — let the pulse prefab's inspector control those.
    object[] instData = new object[] { ownerActor };

    GameObject pulse = SpawnPrefab(electricFieldPrefab, transform.position, Quaternion.identity, instData);

    // Local fallback initialization: set ownerGameObject so the pulse can ignore owner's colliders etc.
    if (pulse != null && !PhotonNetwork.InRoom)
    {
        var pb = pulse.GetComponent<ExpandingPulseBehavior>();
        if (pb != null)
        {
            // InitializeFromSpawner expects radius/rootDur/expandDur but only applies values > 0.
            // We pass -1 for those floats so the pulse keeps whatever inspector values are set on the prefab.
            pb.InitializeFromSpawner(ownerActor, this.gameObject, -1f, -1f, true, -1f);
        }
    }

    // Do NOT DestroyIfLocal(pulse, ... ) here — ExpandingPulseBehavior will self-destruct after expansion by default.
    // If you want a fallback local-only destroy (for preview), you can keep a long buffer, but it's usually redundant.

    return true; // success — consumes full charge
}

    // --- HOMEOSTASIS (E) ---
    private bool Ability_Sigil_Homeostasis()
    {
        // Homeostasis is owner-only effect (it affects only the caster's shield/HP/invuln).
        // CharacterSkills only runs for the owner, so we call the player's PlayerStatus to apply this.
        var status = GetComponentInChildren<PlayerStatus>(true) ?? GetComponent<PlayerStatus>();
        if (status == null)
        {
            // If there's no PlayerStatus on the player prefab, try to find one on the root player object
            status = GetComponentInParent<PlayerStatus>();
        }

        if (status == null)
        {
            // Minimal fallback: apply one-shot shield + heal (no immobilize / no gradual application).
            Debug.LogWarning("[CharacterSkills] Homeostasis: no PlayerStatus found. Applying immediate shield/heal fallback (no immobilize/invuln). " +
                            "Recommend adding PlayerStatus to player prefab to enable full behavior.");

            gameObject.SendMessage("ApplyShield", homeostasisShieldAmount, SendMessageOptions.DontRequireReceiver);
            gameObject.SendMessage("Heal", homeostasisHealAmount, SendMessageOptions.DontRequireReceiver);

            // We won't set immobilize or invulnerability here to avoid duplicate/conflicting logic.
        }
        else
        {
            // Delegate full homeostasis behaviour to PlayerStatus (owner-local).
            status.StartHomeostasis(homeostasisShieldAmount, homeostasisHealAmount, homeostasisDuration);
        }

        // spawn visual effect for others/yourself (optional). Use networked visual or local-only as desired.
        if (homeostasisEffectPrefab != null)
        {
            // Pass instantiation data: [0]=ownerActor so visual can be owner-aware if needed
            int ownerActor = (PhotonNetwork.InRoom && PhotonNetwork.LocalPlayer != null) ? PhotonNetwork.LocalPlayer.ActorNumber : -1;
            object[] inst = new object[] { ownerActor };
            GameObject fx = SpawnPrefab(homeostasisEffectPrefab, transform.position, Quaternion.identity, inst);
            DestroyIfLocal(fx, homeostasisDuration + 0.5f);
        }

        return true; // success - consumes full charge
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

    private void ApplyAOEAction(Vector3 center, float radius, Action<Collider> action, int ownerActor = -1, GameObject ownerGameObject = null)
    {
        var cols = Physics.OverlapSphere(center, radius);
        foreach (var c in cols)
        {
            // skip self (the player object owning this CharacterSkills)
            if (c.gameObject == gameObject) continue;

            // skip owner's own objects
            if (ownerActor >= 0 || ownerGameObject != null)
            {
                if (DamageUtils.IsSameOwner(c.gameObject, ownerActor, ownerGameObject))
                    continue;
            }

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
    /// <summary>
    /// Returns true if this collider's tag is in the placementIgnoredTags list.
    /// Treats null collider as ignored.
    /// </summary>
    private bool IsIgnoredPlacementTag(Collider col)
    {
        if (col == null) return true;
        if (placementIgnoredTags == null || placementIgnoredTags.Length == 0) return false;

        // compare tags (fast). skip empty entries in the list.
        foreach (var t in placementIgnoredTags)
        {
            if (string.IsNullOrEmpty(t)) continue;
            if (col.CompareTag(t)) return true;
        }
        return false;
    }


    private Coroutine errorCoroutine;

    /// <summary>
    /// Show a short error message using the assigned TMP. Fades in, waits, fades out.
    /// </summary>
    public void ShowErrorMessage(string message, float visibleDuration = 1.2f, float fadeIn = 0.12f, float fadeOut = 0.25f)
    {
        if (errorMessageText == null) return;
        if (errorCoroutine != null) StopCoroutine(errorCoroutine);
        errorCoroutine = StartCoroutine(ShowErrorCoroutine(message, visibleDuration, fadeIn, fadeOut));
    }

    private IEnumerator ShowErrorCoroutine(string message, float visibleDuration, float fadeIn, float fadeOut)
    {
        var tmp = errorMessageText;
        Color c = tmp.color;
        tmp.text = message;

        // fade in
        float t = 0f;
        while (t < fadeIn)
        {
            t += Time.deltaTime;
            c.a = Mathf.Lerp(0f, 1f, t / Mathf.Max(0.0001f, fadeIn));
            tmp.color = c;
            yield return null;
        }
        c.a = 1f; tmp.color = c;

        // hold
        yield return new WaitForSeconds(visibleDuration);

        // fade out
        t = 0f;
        while (t < fadeOut)
        {
            t += Time.deltaTime;
            c.a = Mathf.Lerp(1f, 0f, t / Mathf.Max(0.0001f, fadeOut));
            tmp.color = c;
            yield return null;
        }
        c.a = 0f; tmp.color = c;
        tmp.text = string.Empty;
        errorCoroutine = null;
    }

    #endregion

    // Public API
    public float GetCurrentCharge() => currentCharge;
    public float GetChargeNormalized() => (maxCharge <= 0f) ? 0f : currentCharge / maxCharge;
    public void SetCharge(float value) { currentCharge = Mathf.Clamp(value, 0f, maxCharge); UpdateChargeBar(); }
}
