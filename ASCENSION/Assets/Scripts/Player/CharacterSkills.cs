using UnityEngine;
using UnityEngine.UI;
using Photon.Pun;

/// <summary>
/// CharacterSkills - charge handling version
/// - inspector-assignable characterName
/// - two skill placeholders (Q/E)
/// - shared charge gauge (assign Image in inspector)
/// - discrete regen: +5 charge every 1 second, starting at 0, max 100
/// - supports both Filled Image (fillAmount) and static Image (resizes width)
/// - Photon-aware: owner writes charge, remotes receive it via OnPhotonSerializeView
/// </summary>
[DisallowMultipleComponent]
public class CharacterSkills : MonoBehaviourPunCallbacks, IPunObservable
{
    [Header("Character")]
    [Tooltip("Friendly display name for this character.")]
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
    private const float REGEN_AMOUNT = 5f;

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

        // Input to use skills - placeholders only
        if (Input.GetKeyDown(offensiveKey))
        {
            if (CanUseSkill())
                UseOffensiveSkill();
            else
                OnSkillFailed(offensiveSkillName);
        }
        if (Input.GetKeyDown(defensiveKey))
        {
            if (CanUseSkill())
                UseDefensiveSkill();
            else
                OnSkillFailed(defensiveSkillName);
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

    /// <summary>
    /// Add charge (owner only should call when sync enabled).
    /// </summary>
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
        // simple rule: require at least 1 charge (customize)
        return currentCharge > EPS;
    }

    private void UseOffensiveSkill()
    {
        // Placeholder: consume a sample cost; actual behavior to be implemented later
        float cost = 15f;
        if (ConsumeCharge(cost))
            Debug.Log($"{characterName}: Used Offensive skill '{offensiveSkillName}' (-{cost}). Charge now {currentCharge}/{maxCharge}");
    }

    private void UseDefensiveSkill()
    {
        float cost = 10f;
        if (ConsumeCharge(cost))
            Debug.Log($"{characterName}: Used Defensive skill '{defensiveSkillName}' (-{cost}). Charge now {currentCharge}/{maxCharge}");
    }

    private void OnSkillFailed(string skillName)
    {
        Debug.Log($"{characterName}: Failed to use {skillName} — insufficient charge ({currentCharge}/{maxCharge}).");
    }

    /// <summary>
    /// Updates the visual charge bar. Uses fillAmount if Image.Type == Filled; otherwise resizes width.
    /// </summary>
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
                // fallback: set localScale.x (less desirable)
                Vector3 sc = chargeBarImage.transform.localScale;
                sc.x = t;
                chargeBarImage.transform.localScale = sc;
            }
        }
    }

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
