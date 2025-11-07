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
/// to owner -> room mapping ("triad_<actor>"), then owner CustomProperties, then PlayerPrefs.
/// Uses a direct PlayerIdentity component (same gameobject or parent) to determine actor number
/// in cases where PhotonView.Owner is not yet available.
///
/// Changes:
/// - Defers the inst-data coroutine if the GameObject is inactive (avoids coroutine warning).
/// - Once triad is applied from instantiation data (HasInstTriad==true), it's locked and will NOT be overwritten.
/// - Extra debug logging to make source of triad obvious.
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
    public PlayerMovement3D playerMovement;
    public PlayerHealth playerHealth;
    public SimpleShooter_PhotonSafe localShooter;
    public CharacterSkills playerSkills;

    // direct reference to PlayerIdentity (same gameobject or parent)
    public PlayerIdentity playerIdentity;

    [NonSerialized] public bool starAcquired = false;
    private bool magicianAcquired = false;

    // NEW: indicates triad was accepted from instantiation data (authoritative)
    public bool HasInstTriad { get; private set; } = false;

    void Awake()
    {
        allCards = (TarotCard[])Enum.GetValues(typeof(TarotCard));
        PhotonView pv = GetComponentInParent<PhotonView>();
        isOwnerInstance = (pv == null) || !PhotonNetwork.InRoom || pv.IsMine;

        // try to get PlayerIdentity directly on this GameObject first, then parent
        playerIdentity = GetComponent<PlayerIdentity>() ?? GetComponentInParent<PlayerIdentity>();

        if (tarotPanel == null)
        {
            Debug.LogError("[TarotSelection] tarotPanel not assigned. Please assign a panel (child of player prefab preferred). Disabling component.");
            enabled = false;
            return;
        }

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
                // Non-owner instances do not need owner UI; still we must avoid starting owner-only flows.
                // but still continue - no UI for remote instances.
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

        if (isOwnerInstance && TarotCards != null)
        {
            for (int i = 0; i < TarotCards.Length; i++)
            {
                if (TarotCards[i] != null)
                    TarotCards[i].SetActive(false);
            }
        }

        // owner-local convenience autowire
        if (isOwnerInstance)
        {
            if (playerHealth == null)
                playerHealth = GetComponentInParent<PlayerHealth>();

            if (playerMovement == null)
                playerMovement = GetComponentInParent<PlayerMovement3D>();

            if (localShooter == null)
            {
                localShooter = GetComponentInParent<SimpleShooter_PhotonSafe>();
                if (localShooter == null)
                    localShooter = FindLocalShooter();
            }
        }

        if (playerSkills == null)
            playerSkills = GetComponentInParent<CharacterSkills>();

        // If this GameObject is inactive we must defer the inst-data coroutine (avoid Unity warning).
        if (!gameObject.activeInHierarchy)
        {
            // start a short coroutine that waits until active then runs the install routine
            StartCoroutine(DeferredStartWhenActive());
        }
        else
        {
            StartCoroutine(ApplyInstantiationDataOrFallbackRoutine());
        }
    }

    private IEnumerator DeferredStartWhenActive()
    {
        // Wait until this object is active to start the initialization coroutine.
        yield return new WaitUntil(() => gameObject != null && gameObject.activeInHierarchy);
        // give one frame to allow Photon to finish setting up pv/inst data
        yield return null;
        StartCoroutine(ApplyInstantiationDataOrFallbackRoutine());
    }

    private IEnumerator ApplyInstantiationDataOrFallbackRoutine()
    {
        bool applied = false;
        PhotonView myPv = GetComponentInParent<PhotonView>();

        // 1) Try instantiation data immediately (works for owner + remote when Photon provides it)
        if (TryApplyInstData(myPv))
        {
            applied = true;
            HasInstTriad = true;
            Debug.Log("[TarotSelection] Triad applied from instantiation data (immediate). Locking triad.");
        }

        // 2) Wait a short window for instantiation data to arrive (defensive)
        if (!applied)
        {
            float deadline = Time.realtimeSinceStartup + INST_DATA_WAIT_SECONDS;
            while (Time.realtimeSinceStartup <= deadline)
            {
                if (TryApplyInstData(myPv))
                {
                    applied = true;
                    HasInstTriad = true;
                    Debug.Log("[TarotSelection] Triad applied from instantiation data (delayed). Locking triad.");
                    break;
                }
                yield return null;
            }
        }

        // 3) If we already accepted inst data, DO NOT try to read room mapping or owner props.
        if (!applied && myPv != null && myPv.Owner != null)
        {
            int ownerActor = DetermineOwnerActor(myPv, -1);
            string roomKey = "triad_" + ownerActor;

            float ownerPropDeadline = Time.realtimeSinceStartup + INST_DATA_WAIT_SECONDS;
            while (Time.realtimeSinceStartup <= ownerPropDeadline)
            {
                // first, authoritative room mapping
                var roomProps = PhotonNetwork.CurrentRoom?.CustomProperties;
                if (roomProps != null && roomProps.TryGetValue(roomKey, out object objTriadOwner))
                {
                    ParseTriadObject(objTriadOwner, out int a, out int b, out int c);
                    ApplyTriadFromIndices(new int[] { a, b, c });
                    applied = true;
                    Debug.Log($"[TarotSelection] Applied triad from room mapping for owner {ownerActor}: ({a},{b},{c})");
                    break;
                }

                // then, owner's custom props
                var ownerProps = myPv.Owner.CustomProperties;
                if (ownerProps != null && ownerProps.TryGetValue(PhotonKeys.PROP_TRIAD, out object ownerTriad))
                {
                    ParseTriadObject(ownerTriad, out int a2, out int b2, out int c2);
                    ApplyTriadFromIndices(new int[] { a2, b2, c2 });
                    applied = true;
                    Debug.Log($"[TarotSelection] Applied triad from owner CustomProperties for owner {ownerActor}: ({a2},{b2},{c2})");
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

        // 5) Still nothing? Generate a private triad
        if (!applied)
        {
            GenerateTriad();
            Debug.Log("[TarotSelection] No instantiation or mapping triad present; generated private triad.");
        }

        UpdateTriadImages();
        yield break;
    }

    // try to read PV.InstantiationData and apply; returns true if data was valid/applied
    public bool TryApplyInstData(PhotonView myPv)
    {
        if (myPv == null) return false;
        if (myPv.InstantiationData == null || myPv.InstantiationData.Length < 4) return false;

        object[] d = myPv.InstantiationData;
        int tri0 = -1, tri1 = -1, tri2 = -1;
        int ownerActorFromInst = -1;

        if (d.Length >= 2) int.TryParse(d[1]?.ToString() ?? "-1", out tri0);
        if (d.Length >= 3) int.TryParse(d[2]?.ToString() ?? "-1", out tri1);
        if (d.Length >= 4) int.TryParse(d[3]?.ToString() ?? "-1", out tri2);
        if (d.Length >= 5) int.TryParse(d[4]?.ToString() ?? "-1", out ownerActorFromInst);

        if (tri0 < 0 && tri1 < 0 && tri2 < 0)
            return false; // nothing meaningful

        // Determine the authoritative owner actor for this PhotonView (use PlayerIdentity if needed)
        int detectedOwner = DetermineOwnerActor(myPv, ownerActorFromInst);

        // If instantiation data included an actor, ensure it's intended for this owner (when possible)
        if (ownerActorFromInst >= 0 && detectedOwner >= 0 && ownerActorFromInst != detectedOwner)
        {
            Debug.Log($"[TarotSelection] Instantiation triad belonged to actor {ownerActorFromInst} but this object owner is {detectedOwner}. Ignoring inst data.");
            return false;
        }

        // If detectedOwner is -1 but inst data has owner, accept inst data and proceed (best-effort)
        if (detectedOwner < 0 && ownerActorFromInst >= 0)
        {
            detectedOwner = ownerActorFromInst;
            Debug.Log($"[TarotSelection] No authoritative owner available; using instantiation owner {ownerActorFromInst} as detectedOwner.");
        }

        ApplyTriadFromIndices(new int[] { tri0, tri1, tri2 });
        Debug.Log($"[TarotSelection] Applied triad from instantiation data: ({tri0},{tri1},{tri2}) ownerFromInst={ownerActorFromInst} detectedOwner={detectedOwner} (PlayerIdentity.actorNumber={(playerIdentity != null ? playerIdentity.actorNumber : -1)})");
        return true;
    }
    
    /// <summary>
    /// Convenience wrapper: call TryApplyInstData using this object's PhotonView.
    /// External callers (PlayerIdentity.Initialize / Awake) should call this when actorNumber becomes known.
    /// </summary>
    public bool TryApplyInstDataNow()
    {
        PhotonView myPv = GetComponentInParent<PhotonView>();
        return TryApplyInstData(myPv);
    }

    // Determine owner actor number for this PhotonView / object:
    // Priority:
    //  1) PhotonView.Owner.ActorNumber (if available)
    //  2) PlayerIdentity.actorNumber (if PlayerIdentity present)
    //  3) fallbackOwnerIfProvided (usually from instantiation data)
    private int DetermineOwnerActor(PhotonView pv, int fallbackOwnerIfProvided)
    {
        if (pv != null && pv.Owner != null)
            return pv.Owner.ActorNumber;

        if (playerIdentity != null && playerIdentity.actorNumber > 0)
        {
            // note: PlayerIdentity.actorNumber may be set by your spawn/setup code
            return playerIdentity.actorNumber;
        }

        // final fallback
        return fallbackOwnerIfProvided >= 0 ? fallbackOwnerIfProvided : -1;
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
        // if we've previously accepted instantiation triad, DO NOT overwrite it
        if (HasInstTriad)
        {
            Debug.Log("[TarotSelection] Attempt to overwrite instantiation triad ignored.");
            return;
        }

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
                if (acquired == TarotCard.Justice) ActivateJusticeOnLocalShooter();
                else if (acquired == TarotCard.Devil) ActivateDevilOnLocalShooter();
                else if (acquired == TarotCard.Magician) ActivateMagicianOnLocalShooter();
                else if (acquired == TarotCard.Strength) ActivateStrengthOnLocalShooter();
                else if (acquired == TarotCard.Star) ActivateStarOnLocalShooter();

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

                ActivateTarotVisualCumulative(acquired);
            }
            return true;
        }

        return false;
    }

    // -------------------------
    // Tarot visual activation (cumulative)
    // -------------------------
    private void ActivateTarotVisualCumulative(TarotCard card)
    {
        if (!isOwnerInstance) return;
        if (TarotCards == null || TarotCards.Length == 0) return;

        int idx = (int)card;
        if (idx < 0 || idx >= TarotCards.Length) return;

        var go = TarotCards[idx];
        if (go == null) return;

        if (!go.activeSelf)
            go.SetActive(true);
    }

    public void ResetTarotVisuals()
    {
        if (!isOwnerInstance) return;
        if (TarotCards == null) return;
        for (int i = 0; i < TarotCards.Length; i++)
            if (TarotCards[i] != null)
                TarotCards[i].SetActive(false);
    }

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

    private void AutoFindTarotCardsIfNeeded()
    {
        if (TarotCards != null && TarotCards.Length == Enum.GetNames(typeof(TarotCard)).Length)
        {
            bool anyNull = false;
            for (int i = 0; i < TarotCards.Length; i++) if (TarotCards[i] == null) anyNull = true;
            if (!anyNull) return; // already fully assigned
        }

        if (tarotPanel == null) return;

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

        bool allFound = true;
        for (int i = 0; i < enumCount; i++) if (foundArr[i] == null) { allFound = false; break; }

        if (allFound)
        {
            TarotCards = foundArr;
            return;
        }

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

    // Utility: parse triad object from Photon custom properties (supports int[], long[], object[] or comma string)
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

        if (obj is long[])
        {
            var arr = (long[])obj;
            if (arr.Length > 0) a = (int)arr[0];
            if (arr.Length > 1) b = (int)arr[1];
            if (arr.Length > 2) c = (int)arr[2];
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

    // Activation helpers (unchanged)...
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
        if (!isOwnerInstance)
        {
            Debug.LogWarning("[TarotSelection] ActivateStrength called on non-owner instance.");
            return;
        }

        if (playerHealth == null)
        {
            playerHealth = GetComponentInParent<PlayerHealth>();
        }
        if (playerHealth != null)
        {
            playerHealth.ApplyStrengthBoost(3f);
        }
        else
        {
            Debug.LogWarning("[TarotSelection] ActivateStrength: PlayerHealth not found on local player.");
        }

        if (playerMovement == null)
        {
            playerMovement = GetComponentInParent<PlayerMovement3D>();
        }
        if (playerMovement != null)
        {
            playerMovement.speedMultiplier = 1f / 3f;
        }
        else
        {
            Debug.LogWarning("[TarotSelection] ActivateStrength: PlayerMovement3D not found on local player.");
        }

        if (localShooter == null)
        {
            localShooter = FindLocalShooter();
        }
        if (localShooter != null)
        {
            localShooter.SetFireRateMultiplier(3f);
        }
        else
        {
            Debug.LogWarning("[TarotSelection] ActivateStrength: SimpleShooter_PhotonSafe not found on local player.");
        }

        ActivateTarotVisualCumulative(TarotCard.Strength);
        Debug.Log("[TarotSelection] Strength activated: maxHP x3, healed to full, movement=1/3, fireRate multiplier=3");
    }

    void ActivateStarOnLocalShooter()
    {
        if (!isOwnerInstance)
        {
            Debug.LogWarning("[TarotSelection] ActivateStar called on non-owner instance.");
            return;
        }

        starAcquired = true;
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

        ActivateTarotVisualCumulative(TarotCard.Star);
    }

    void ActivateMagicianOnLocalShooter()
    {
        if (!isOwnerInstance)
        {
            Debug.LogWarning("[TarotSelection] ActivateMagician called on non-owner instance.");
            return;
        }

        magicianAcquired = true;

        if (playerSkills == null) playerSkills = GetComponentInParent<CharacterSkills>();
        if (localShooter == null) localShooter = FindLocalShooter();

        if (playerSkills != null)
        {
            if (playerSkills.OnSkillUsed == null)
                playerSkills.OnSkillUsed = new UnityEvent();
            playerSkills.OnSkillUsed.AddListener(OnMagicianSkillUsed);
        }
        else
        {
            Debug.LogWarning("[TarotSelection] ActivateMagician: CharacterSkills not found on local player.");
        }

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

        float grant = playerSkills.maxCharge * 0.25f;
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
