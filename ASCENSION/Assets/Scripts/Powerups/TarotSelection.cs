// TarotSelection.cs
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using Photon.Pun;

/// <summary>
/// TarotSelection - reads triad from PhotonView.InstantiationData (preferred), then falls back
/// to LocalPlayer custom props, then PlayerPrefs. Owner-only UI flash. Triad-first acquisition, then shuffled deck.
/// Attach to a component under your player prefab so that the panel is a child and owner-only logic works.
///
/// NEW: public GameObject[] TarotCards (length 10) can be assigned in inspector. These GameObjects should be children
/// of tarotPanel and will be initially deactivated. When a tarot is acquired, the corresponding TarotCards[(int)card]
/// will be activated (cumulative: previously acquired visuals remain active). This activation is owner-specific.
/// </summary>
[DisallowMultipleComponent]
public class TarotSelection : MonoBehaviour
{
    public enum TarotCard
    {
        Justice,
        Fool,
        Temperance,
        Lovers,
        Devil,
        Sun,
        Moon,
        Star,
        Magician,
        Strength
    }

    [Header("UI (owner-only)")]
    [Tooltip("Panel that shows the current triad. MUST have a CanvasGroup. Prefer as a child of the player prefab.")]
    public GameObject tarotPanel;

    [Tooltip("Three Image components (left->center->right) used to display the triad.")]
    public Image[] triadImages = new Image[3];

    [Header("Visuals")]
    [Tooltip("Sprites for each TarotCard (index must match TarotCard enum).")]
    public Sprite[] tarotSprites = new Sprite[10];

    [Tooltip("How long the panel fades from alpha 1 -> 0 (seconds).")]
    public float flashDuration = 2f;

    [Tooltip("If true the panel flash coroutine will interrupt / restart when O is pressed repeatedly.")]
    public bool allowInterruptingFlash = true;

    [Header("Tarot visual objects (child GameObjects under tarotPanel)")]
    [Tooltip("Assign one GameObject (UI/Image/GameObject) per TarotCard index. They will be initially deactivated and toggled when a card is acquired.")]
    public GameObject[] TarotCards = new GameObject[10];

    [Header("Events")]
    public UnityEngine.Events.UnityEvent<TarotCard> OnCardAcquired;

    // Runtime state (visible in inspector for debugging)
    [SerializeField] private List<TarotCard> triad = new List<TarotCard>();
    [SerializeField] private int triadIndex = 0;
    [SerializeField] private int[] triadIndices = new int[3] { -1, -1, -1 };
    [SerializeField] private List<TarotCard> deckQueue = new List<TarotCard>();

    // internals
    private CanvasGroup panelCanvasGroup;
    private bool isOwnerInstance = false;
    private TarotCard[] allCards;
    private Coroutine fadeCoroutine = null;

    private const float INST_DATA_WAIT_SECONDS = 0.25f;

    [Header("Owner-local component references (optional)")]
    [Tooltip("Optional: reference to the PlayerMovement3D component on this player. If null the script will attempt to auto-find.")]
    public PlayerMovement3D playerMovement;

    [Tooltip("Optional: reference to the PlayerHealth component on this player. If null the script will attempt to auto-find.")]
    public PlayerHealth playerHealth;

    [Tooltip("Optional: reference to the SimpleShooter_PhotonSafe (local shooter) on this player. If null the script will attempt to auto-find.")]
    public SimpleShooter_PhotonSafe localShooter;
    // add to the "Owner-local component references (optional)" area
    [Tooltip("Optional: reference to the CharacterSkills component on this player. If null the script will attempt to auto-find.")]
    public CharacterSkills playerSkills;

    // runtime flag for Star
    [NonSerialized] public bool starAcquired = false;
    private bool magicianAcquired = false;


    void Awake()
    {
        if (playerSkills == null)
            playerSkills = GetComponentInParent<CharacterSkills>();
        allCards = (TarotCard[])Enum.GetValues(typeof(TarotCard));
        PhotonView pv = GetComponentInParent<PhotonView>();
        isOwnerInstance = (pv == null) || !PhotonNetwork.InRoom || pv.IsMine;

        if (tarotPanel == null)
        {
            Debug.LogError("[TarotSelection] tarotPanel not assigned. Please assign a panel (child of player prefab preferred). Disabling component.");
            enabled = false;
            return;
        }

        // If panel is not a child of this component (i.e., a shared prefab root),
        // only owner instances create a local clone to avoid visual conflicts.
        bool panelIsChild = tarotPanel.transform.IsChildOf(this.transform);
        if (!panelIsChild)
        {
            if (isOwnerInstance)
            {
                var clone = Instantiate(tarotPanel, this.transform);
                clone.name = tarotPanel.name + "_Local";
                tarotPanel = clone;
            }
            else
            {
                // Non-owner instances do not need owner UI; exit early (keeps behavior unchanged)
                return;
            }
        }

        panelCanvasGroup = tarotPanel.GetComponent<CanvasGroup>();
        if (panelCanvasGroup == null)
            panelCanvasGroup = tarotPanel.AddComponent<CanvasGroup>();

        panelCanvasGroup.alpha = 0f;
        panelCanvasGroup.interactable = false;
        panelCanvasGroup.blocksRaycasts = false;

        AutoFindTriadImagesIfNeeded();
        AutoFindTarotCardsIfNeeded();

        // ensure tarot visuals are initially deactivated for owner instance
        if (isOwnerInstance && TarotCards != null)
        {
            for (int i = 0; i < TarotCards.Length; i++)
            {
                if (TarotCards[i] != null)
                    TarotCards[i].SetActive(false);
            }
        }

        // -----------------------
        // Auto-find owner-local components (convenience)
        // -----------------------
        // If you assigned these in the inspector this block will be skipped.
        if (isOwnerInstance)
        {
            if (playerHealth == null)
                playerHealth = GetComponentInParent<PlayerHealth>();

            if (playerMovement == null)
                playerMovement = GetComponentInParent<PlayerMovement3D>();

            if (localShooter == null)
            {
                // prefer same-root shooter if present
                localShooter = GetComponentInParent<SimpleShooter_PhotonSafe>();
                // fallback to the utility method (keeps compatibility with previous logic)
                if (localShooter == null)
                    localShooter = FindLocalShooter();
            }
        }

        // inside the isOwnerInstance block in Awake() add:
        if (playerSkills == null)
            playerSkills = GetComponentInParent<CharacterSkills>();


        // Defensive: wait a small amount for instantiation data then fallback to other sources
        StartCoroutine(ApplyInstantiationDataOrFallbackRoutine());
    }

    private IEnumerator ApplyInstantiationDataOrFallbackRoutine()
    {
        bool applied = false;
        PhotonView myPv = GetComponentInParent<PhotonView>();

        // 1) Try instantiation data immediately (works for owner + remote when Photon provides it)
        if (TryApplyInstData(myPv))
            applied = true;

        // 2) Wait a short window for instantiation data to arrive (defensive)
        if (!applied)
        {
            float deadline = Time.realtimeSinceStartup + INST_DATA_WAIT_SECONDS;
            while (Time.realtimeSinceStartup <= deadline)
            {
                if (TryApplyInstData(myPv))
                {
                    applied = true;
                    break;
                }
                yield return null;
            }
        }

        // 3) If still not applied, attempt to read the OWNER's custom props (important for remote instances)
        //    We also allow a short wait so owner/client props have a chance to be set by lobby logic.
        if (!applied && myPv != null && myPv.Owner != null)
        {
            float ownerPropDeadline = Time.realtimeSinceStartup + INST_DATA_WAIT_SECONDS;
            while (Time.realtimeSinceStartup <= ownerPropDeadline)
            {
                var ownerProps = myPv.Owner.CustomProperties;
                if (ownerProps != null && ownerProps.TryGetValue(PhotonKeys.PROP_TRIAD, out object objTriadOwner))
                {
                    ParseTriadObject(objTriadOwner, out int a, out int b, out int c);
                    ApplyTriadFromIndices(new int[] { a, b, c });
                    applied = true;
                    Debug.Log($"[TarotSelection] Applied triad from owner ({myPv.Owner.ActorNumber}) custom props: ({a},{b},{c})");
                    break;
                }
                yield return null;
            }
        }

        // 4) Fallback for owner instance only: LocalPlayer custom props then PlayerPrefs (existing behavior)
        if (!applied && isOwnerInstance)
        {
            if (PhotonNetwork.IsConnected && PhotonNetwork.LocalPlayer != null &&
                PhotonNetwork.LocalPlayer.CustomProperties != null &&
                PhotonNetwork.LocalPlayer.CustomProperties.TryGetValue(PhotonKeys.PROP_TRIAD, out object objTriad))
            {
                ParseTriadObject(objTriad, out int a, out int b, out int c);
                ApplyTriadFromIndices(new int[] { a, b, c });
                applied = true;
                Debug.Log("[TarotSelection] Applied triad from LocalPlayer custom props as fallback.");
            }
            else if (PlayerPrefs.HasKey(PhotonKeys.PREF_KEY_TRIAD))
            {
                string triCsv = PlayerPrefs.GetString(PhotonKeys.PREF_KEY_TRIAD, null);
                if (!string.IsNullOrEmpty(triCsv))
                {
                    var parts = triCsv.Split(',');
                    int.TryParse(parts[0], out int a);
                    int.TryParse(parts[1], out int b);
                    int.TryParse(parts[2], out int c);
                    ApplyTriadFromIndices(new int[] { a, b, c });
                    applied = true;
                    Debug.Log("[TarotSelection] Applied triad from PlayerPrefs as fallback.");
                }
            }
        }

        // 5) Still nothing? Generate a private triad (existing behavior)
        if (!applied)
        {
            GenerateTriad();
            Debug.Log("[TarotSelection] No instantiation triad present; generated private triad.");
        }

        UpdateTriadImages();
        yield break;
    }


    // try to read PV.InstantiationData and apply; returns true if data was valid/applied
    private bool TryApplyInstData(PhotonView myPv)
    {
        if (myPv == null) return false;
        if (myPv.InstantiationData == null || myPv.InstantiationData.Length < 4) return false;

        object[] d = myPv.InstantiationData;
        int tri0 = -1, tri1 = -1, tri2 = -1;
        if (d.Length >= 2) int.TryParse(d[1]?.ToString() ?? "-1", out tri0);
        if (d.Length >= 3) int.TryParse(d[2]?.ToString() ?? "-1", out tri1);
        if (d.Length >= 4) int.TryParse(d[3]?.ToString() ?? "-1", out tri2);

        if (tri0 < 0 && tri1 < 0 && tri2 < 0)
            return false; // nothing meaningful

        ApplyTriadFromIndices(new int[] { tri0, tri1, tri2 });
        Debug.Log($"[TarotSelection] Applied triad from instantiation data: ({tri0},{tri1},{tri2})");
        return true;
    }

    void Update()
    {
        if (!isOwnerInstance) return;
        if (Input.GetKeyDown(KeyCode.O))
            FlashPanel();
    }

    // -------------------------
    // Triad generation & application
    // -------------------------
    void GenerateTriad()
    {
        triad.Clear();
        triadIndices = new int[3] { -1, -1, -1 };

        List<TarotCard> pool = new List<TarotCard>(allCards);
        for (int i = 0; i < 3 && pool.Count > 0; i++)
        {
            int r = UnityEngine.Random.Range(0, pool.Count);
            triad.Add(pool[r]);
            triadIndices[i] = (int)pool[r];
            pool.RemoveAt(r);
        }

        triadIndex = 0;
        BuildDeckQueue();
    }

    public void ApplyTriadFromIndices(int[] indices)
    {
        triad.Clear();
        triadIndices = new int[3] { -1, -1, -1 };

        if (indices == null) indices = new int[0];

        HashSet<int> used = new HashSet<int>();
        int outIdx = 0;
        foreach (var i in indices)
        {
            if (i < 0 || i >= allCards.Length) continue;
            if (used.Contains(i)) continue;
            used.Add(i);
            triad.Add((TarotCard)i);
            if (outIdx < triadIndices.Length) triadIndices[outIdx] = i;
            outIdx++;
            if (triad.Count >= 3) break;
        }

        if (triad.Count < 3)
        {
            List<TarotCard> pool = new List<TarotCard>(allCards);
            pool.RemoveAll(c => used.Contains((int)c));
            while (triad.Count < 3 && pool.Count > 0)
            {
                int r = UnityEngine.Random.Range(0, pool.Count);
                triad.Add(pool[r]);
                if (outIdx < triadIndices.Length) triadIndices[outIdx] = (int)pool[r];
                pool.RemoveAt(r);
                outIdx++;
            }
        }

        triadIndex = 0;
        BuildDeckQueue();
    }

    private void BuildDeckQueue()
    {
        deckQueue.Clear();

        HashSet<TarotCard> triadSet = new HashSet<TarotCard>(triad);
        List<TarotCard> pool = new List<TarotCard>(allCards);
        pool.RemoveAll(c => triadSet.Contains(c));

        Shuffle(pool);
        deckQueue.AddRange(pool);
    }

    // -------------------------
    // Acquisition: triad-first, then deckQueue
    // -------------------------
    public bool AcquireNextTarot(out TarotCard acquired)
    {
        acquired = default;

        if (triadIndex < triad.Count)
        {
            acquired = triad[triadIndex];
            triadIndex++;
            OnCardAcquired?.Invoke(acquired);
            if (isOwnerInstance)
            {
                // Activate owner-local gameplay effects
                if (acquired == TarotCard.Justice) ActivateJusticeOnLocalShooter();
                else if (acquired == TarotCard.Devil) ActivateDevilOnLocalShooter();
                else if (acquired == TarotCard.Magician) ActivateMagicianOnLocalShooter();
                else if (acquired == TarotCard.Strength) ActivateStrengthOnLocalShooter();
                else if (acquired == TarotCard.Star) ActivateStarOnLocalShooter();

                // Activate the visual for this acquired card (cumulative)
                ActivateTarotVisualCumulative(acquired);
            }
            return true;
        }

        if (deckQueue.Count > 0)
        {
            acquired = deckQueue[0];
            deckQueue.RemoveAt(0);
            OnCardAcquired?.Invoke(acquired);
            if (isOwnerInstance)
            {
                if (acquired == TarotCard.Justice) ActivateJusticeOnLocalShooter();
                else if (acquired == TarotCard.Devil) ActivateDevilOnLocalShooter();
                else if (acquired == TarotCard.Magician) ActivateMagicianOnLocalShooter();
                else if (acquired == TarotCard.Strength) ActivateStrengthOnLocalShooter();
                else if (acquired == TarotCard.Star) ActivateStarOnLocalShooter();

                // Activate the visual for this acquired card (cumulative)
                ActivateTarotVisualCumulative(acquired);
            }
            return true;
        }

        return false;
    }

    // -------------------------
    // Tarot visual activation (cumulative)
    // -------------------------
    /// <summary>
    /// Activates the visual GameObject for 'card' and leaves previously activated visuals intact.
    /// Does nothing for non-owner instances or if TarotCards not configured.
    /// </summary>
    private void ActivateTarotVisualCumulative(TarotCard card)
    {
        if (!isOwnerInstance) return;
        if (TarotCards == null || TarotCards.Length == 0) return;

        int idx = (int)card;
        if (idx < 0 || idx >= TarotCards.Length) return;

        var go = TarotCards[idx];
        if (go == null) return;

        // activate this acquired card visual; DO NOT deactivate previously active visuals
        if (!go.activeSelf)
            go.SetActive(true);
    }

    /// <summary>
    /// Optional helper to reset (deactivate) all tarot visuals. Owner-only.
    /// </summary>
    public void ResetTarotVisuals()
    {
        if (!isOwnerInstance) return;
        if (TarotCards == null) return;
        for (int i = 0; i < TarotCards.Length; i++)
            if (TarotCards[i] != null)
                TarotCards[i].SetActive(false);
    }

    // -------------------------
    // UI: flash panel
    // -------------------------
    public void FlashPanel()
    {
        if (!isOwnerInstance) return;
        if (panelCanvasGroup == null) return;

        UpdateTriadImages();

        if (fadeCoroutine != null)
        {
            if (allowInterruptingFlash)
            {
                StopCoroutine(fadeCoroutine);
                fadeCoroutine = StartCoroutine(FlashRoutine());
            }
        }
        else
        {
            fadeCoroutine = StartCoroutine(FlashRoutine());
        }
    }

    IEnumerator FlashRoutine()
    {
        panelCanvasGroup.alpha = 1f;
        panelCanvasGroup.interactable = false;
        panelCanvasGroup.blocksRaycasts = false;

        float elapsed = 0f;
        while (elapsed < flashDuration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / flashDuration);
            panelCanvasGroup.alpha = Mathf.Lerp(1f, 0f, t);
            yield return null;
        }

        panelCanvasGroup.alpha = 0f;
        fadeCoroutine = null;
    }

    // -------------------------
    // Utilities & debug helpers
    // -------------------------
    void UpdateTriadImages()
    {
        if (triadImages == null) return;

        for (int i = 0; i < triadImages.Length; i++)
        {
            if (triadImages[i] == null) continue;
            if (i < triad.Count)
            {
                int idx = (int)triad[i];
                if (tarotSprites != null && idx >= 0 && idx < tarotSprites.Length && tarotSprites[idx] != null)
                {
                    triadImages[i].sprite = tarotSprites[idx];
                    triadImages[i].enabled = true;
                }
                else
                {
                    triadImages[i].sprite = null;
                    triadImages[i].enabled = false;
                }
            }
            else
            {
                triadImages[i].sprite = null;
                triadImages[i].enabled = false;
            }
        }
    }

    private void AutoFindTriadImagesIfNeeded()
    {
        if (triadImages != null && triadImages.Length == 3)
        {
            bool anyNull = false;
            for (int i = 0; i < 3; i++) if (triadImages[i] == null) anyNull = true;
            if (!anyNull) return;
        }

        if (tarotPanel == null) return;
        var found = tarotPanel.GetComponentsInChildren<Image>(true);
        triadImages = new Image[3];
        for (int i = 0; i < 3 && i < found.Length; i++)
            triadImages[i] = found[i];
    }

    /// <summary>
    /// Auto-populate TarotCards[] if not assigned: looks for children under tarotPanel that match enum names,
    /// otherwise takes first 10 child GameObjects found (order not guaranteed).
    /// </summary>
    private void AutoFindTarotCardsIfNeeded()
    {
        if (TarotCards != null && TarotCards.Length == Enum.GetNames(typeof(TarotCard)).Length)
        {
            bool anyNull = false;
            for (int i = 0; i < TarotCards.Length; i++) if (TarotCards[i] == null) anyNull = true;
            if (!anyNull) return; // already fully assigned
        }

        if (tarotPanel == null) return;

        // Try matching by name
        var children = tarotPanel.GetComponentsInChildren<Transform>(true);
        int enumCount = Enum.GetNames(typeof(TarotCard)).Length;
        GameObject[] foundArr = new GameObject[enumCount];

        for (int i = 0; i < children.Length; i++)
        {
            var t = children[i];
            if (t == tarotPanel.transform) continue;
            for (int ei = 0; ei < enumCount; ei++)
            {
                if (string.Equals(t.name, ((TarotCard)ei).ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    foundArr[ei] = t.gameObject;
                }
            }
        }

        // If all found by name, use them. Otherwise, fallback to first N child gameObjects (excluding panel root)
        bool allFound = true;
        for (int i = 0; i < enumCount; i++) if (foundArr[i] == null) { allFound = false; break; }

        if (allFound)
        {
            TarotCards = foundArr;
            return;
        }

        // fallback: collect first N child GameObjects
        List<GameObject> list = new List<GameObject>();
        foreach (var t in children)
        {
            if (t == tarotPanel.transform) continue;
            list.Add(t.gameObject);
            if (list.Count >= enumCount) break;
        }

        TarotCards = new GameObject[enumCount];
        for (int i = 0; i < enumCount && i < list.Count; i++)
            TarotCards[i] = list[i];
    }

    private void Shuffle<T>(List<T> list)
    {
        for (int i = 0; i < list.Count; i++)
        {
            int r = UnityEngine.Random.Range(i, list.Count);
            T tmp = list[i];
            list[i] = list[r];
            list[r] = tmp;
        }
    }

    // Utility: parse triad object from Photon custom properties (supports int[], object[] or comma string)
    private void ParseTriadObject(object obj, out int a, out int b, out int c)
    {
        a = b = c = -1;
        if (obj == null) return;

        if (obj is int[])
        {
            var arr = (int[])obj;
            if (arr.Length > 0) a = arr[0];
            if (arr.Length > 1) b = arr[1];
            if (arr.Length > 2) c = arr[2];
            return;
        }

        if (obj is object[])
        {
            var oarr = (object[])obj;
            if (oarr.Length > 0) int.TryParse(oarr[0]?.ToString(), out a);
            if (oarr.Length > 1) int.TryParse(oarr[1]?.ToString(), out b);
            if (oarr.Length > 2) int.TryParse(oarr[2]?.ToString(), out c);
            return;
        }

        var s = obj.ToString();
        if (!string.IsNullOrEmpty(s))
        {
            var parts = s.Split(',');
            if (parts.Length > 0) int.TryParse(parts[0], out a);
            if (parts.Length > 1) int.TryParse(parts[1], out b);
            if (parts.Length > 2) int.TryParse(parts[2], out c);
        }
    }

    // Activation helpers (unchanged)
    void ActivateJusticeOnLocalShooter()
    {
        var shooter = FindLocalShooter();
        if (shooter == null)
        {
            Debug.LogWarning("[TarotSelection] ActivateJustice: no local SimpleShooter_PhotonSafe found.");
            return;
        }

        shooter.SetFireRateMultiplier(2f);
        shooter.defaultIgnoreBodyHits = true;
        shooter.defaultHeadshotMultiplier = 6f;
        shooter.defaultOutgoingDamageMultiplier = 1f;
        Debug.Log("[TarotSelection] Justice activated on local shooter.");
    }

    void ActivateDevilOnLocalShooter()
    {
        var shooter = FindLocalShooter();
        if (shooter == null)
        {
            Debug.LogWarning("[TarotSelection] ActivateDevil: no local SimpleShooter_PhotonSafe found.");
            return;
        }

        shooter.defaultOutgoingDamageMultiplier = 2f;
        shooter.defaultLifestealPercent = 0.25f;
        shooter.defaultSelfDamagePerShot = 15;
        Debug.Log("[TarotSelection] Devil activated on local shooter.");
    }

    void ActivateStrengthOnLocalShooter()
    {
        // Only run on owner instance
        if (!isOwnerInstance)
        {
            Debug.LogWarning("[TarotSelection] ActivateStrength called on non-owner instance.");
            return;
        }

        // 1) PlayerHealth: triple max HP & heal to full (owner-local + RPC broadcast)
        if (playerHealth == null)
        {
            playerHealth = GetComponentInParent<PlayerHealth>();
        }
        if (playerHealth != null)
        {
            playerHealth.ApplyStrengthBoost(3f); // healthMultiplier = 3 => triple maxHealth & heal to full
        }
        else
        {
            Debug.LogWarning("[TarotSelection] ActivateStrength: PlayerHealth not found on local player.");
        }

        // 2) Movement speed: reduce to 1/3 of original (owner-local).
        // Prefer to set the provided multiplier (PlayerMovement3D already multiplies base speeds by speedMultiplier).
        if (playerMovement == null)
        {
            playerMovement = GetComponentInParent<PlayerMovement3D>();
        }
        if (playerMovement != null)
        {
            // set the runtime multiplier to 1/3
            playerMovement.speedMultiplier = 1f / 3f;

            // Optional: also scale base walk/run so other systems reading them see the change.
            // Uncomment the following lines if you want the base speeds permanently adjusted:
            // playerMovement.walkSpeed = playerMovement.walkSpeed * (1f / 3f);
            // playerMovement.runSpeed = playerMovement.runSpeed * (1f / 3f);
        }
        else
        {
            Debug.LogWarning("[TarotSelection] ActivateStrength: PlayerMovement3D not found on local player.");
        }

        // 3) Shooter fire rate: interpret as slowing attack speed to 1/3 (increase delay by 3x)
        if (localShooter == null)
        {
            localShooter = FindLocalShooter();
        }
        if (localShooter != null)
        {
            localShooter.SetFireRateMultiplier(3f); // originalFireRate * 3 => 1/3 firing frequency
        }
        else
        {
            Debug.LogWarning("[TarotSelection] ActivateStrength: SimpleShooter_PhotonSafe not found on local player.");
        }

        // Activate visual (existing behavior)
        ActivateTarotVisualCumulative(TarotCard.Strength);

        Debug.Log("[TarotSelection] Strength activated: maxHP x3, healed to full, movement=1/3, fireRate multiplier=3");
    }

    /// <summary>
    /// Activate Star behaviour on the local owner: mark flag and (optional) visual feedback.
    /// </summary>
    void ActivateStarOnLocalShooter()
    {
        if (!isOwnerInstance)
        {
            Debug.LogWarning("[TarotSelection] ActivateStar called on non-owner instance.");
            return;
        }

        // mark acquired
        starAcquired = true;

        // cache CharacterSkills reference if not already set
        if (playerSkills == null)
            playerSkills = GetComponentInParent<CharacterSkills>();

        if (playerSkills == null)
        {
            Debug.LogWarning("[TarotSelection] ActivateStar: CharacterSkills not found on local player. The Star effect requires CharacterSkills.AddCharge to exist.");
        }
        else
        {
            Debug.Log("[TarotSelection] Star activated: reloading on empty will grant 25% of max charge.");
        }

        // Activate visual (if assigned)
        ActivateTarotVisualCumulative(TarotCard.Star);
    }

    void ActivateMagicianOnLocalShooter()
    {
        if (!isOwnerInstance)
        {
            Debug.LogWarning("[TarotSelection] ActivateMagician called on non-owner instance.");
            return;
        }

        // mark acquired
        magicianAcquired = true;

        // ensure local references
        if (playerSkills == null) playerSkills = GetComponentInParent<CharacterSkills>();
        if (localShooter == null) localShooter = FindLocalShooter();

        // subscribe to the skill-used event if possible
        if (playerSkills != null)
        {
            if (playerSkills.OnSkillUsed == null)
                playerSkills.OnSkillUsed = new UnityEvent(); // defensive if inspector didn't serialize it
            playerSkills.OnSkillUsed.AddListener(OnMagicianSkillUsed);
        }
        else
        {
            Debug.LogWarning("[TarotSelection] ActivateMagician: CharacterSkills not found on local player.");
        }

        // Visual + log (keeps same visual behavior)
        ActivateTarotVisualCumulative(TarotCard.Magician);
        Debug.Log("[TarotSelection] Magician activated: using any skill will reload to full.");
    }

    private void OnMagicianSkillUsed()
    {
        if (!isOwnerInstance) return;
        if (localShooter == null) localShooter = FindLocalShooter();
        if (localShooter != null)
        {
            localShooter.SetAmmo(localShooter.maxAmmo);
            Debug.Log("[TarotSelection] Magician: reloaded to full on skill use.");
        }
        else
        {
            Debug.LogWarning("[TarotSelection] OnMagicianSkillUsed: local shooter not found to reload.");
        }
    }


    /// <summary>
    /// Called by the local shooter after a reload completes that was triggered while ammo was empty.
    /// Grants 25% of max skill charge (calls CharacterSkills.AddCharge).
    /// This function is owner-only; safe to call from SimpleShooter after reload finishes.
    /// </summary>
    public void OnReloadedEmpty()
    {
        if (!isOwnerInstance) return;
        if (!starAcquired) return;

        if (playerSkills == null)
            playerSkills = GetComponentInParent<CharacterSkills>();

        if (playerSkills == null)
        {
            Debug.LogWarning("[TarotSelection] OnReloadedEmpty: no CharacterSkills found to grant charge.");
            return;
        }

        float grant = playerSkills.maxCharge * 0.25f; // 1/4 of max
        playerSkills.AddCharge(grant);

        Debug.Log($"[TarotSelection] OnReloadedEmpty: granted {grant} charge ({playerSkills.maxCharge} max).");
    }


    SimpleShooter_PhotonSafe FindLocalShooter()
    {
        var shooters = GameObject.FindObjectsOfType<SimpleShooter_PhotonSafe>(true);
        if (shooters == null || shooters.Length == 0) return null;

        if (PhotonNetwork.InRoom)
        {
            foreach (var s in shooters)
            {
                var pv = s.GetComponentInParent<PhotonView>();
                if (pv == null) return s;
                if (pv.IsMine) return s;
            }
        }
        else
        {
            return shooters[0];
        }

        return shooters[0];
    }

    void OnDestroy()
    {
        if (fadeCoroutine != null) StopCoroutine(fadeCoroutine);

        // unsubscribe magician listener if present
        if (playerSkills != null && playerSkills.OnSkillUsed != null)
        {
            try { playerSkills.OnSkillUsed.RemoveListener(OnMagicianSkillUsed); } catch { }
        }
    }

    // Public helpers to inspect runtime triad programmatically
    public List<TarotCard> GetTriad() => new List<TarotCard>(triad);
    public int[] GetTriadIndices() => (int[])triadIndices.Clone();
    public List<TarotCard> GetDeckQueue() => new List<TarotCard>(deckQueue);
}