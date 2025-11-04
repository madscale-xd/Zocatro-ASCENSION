using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Events;
using Photon.Pun;

/// <summary>
/// TarotSelection (reworked)
/// - On Awake: randomly picks a private "Tarot Triad" of 3 unique cards for this player instance.
/// - Pressing O (owner only) flashes a small top-right panel (CanvasGroup) that shows the 3 triad cards,
///   alpha goes from 1 -> 0 over flashDuration seconds. There are NO buttons.
/// - Provides helper APIs to "acquire next tarot" and to activate Devil/Justice on the local shooter.
/// - Designed to be owner-local (only the owning player's inputs will trigger the flash).
/// 
/// Usage:
/// - Add this to your player prefab.
/// - Assign tarotPanel (a GameObject that has a CanvasGroup) as a child of the player prefab
///   or assign a prefab/scene object; the script will clone a local copy for the owner if needed.
/// - Assign triadImages (3 Image components) that will display the card art (order left->right).
/// - Provide tarotSprites where index matches TarotCard enum order.
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
    [Tooltip("Panel that shows the current triad. MUST have a CanvasGroup. Prefer as a child of the player prefab. If not a child, owner will get a cloned local copy.")]
    public GameObject tarotPanel;

    [Tooltip("Three Image components (left->center->right) used to display the triad. These must be children of tarotPanel / or assigned to the cloned copy.")]
    public Image[] triadImages = new Image[3];

    [Header("Visuals")]
    [Tooltip("Sprites for each TarotCard (index must match TarotCard enum).")]
    public Sprite[] tarotSprites = new Sprite[10];

    [Tooltip("How long the panel fades from alpha 1 -> 0 (seconds).")]
    public float flashDuration = 2f;

    [Tooltip("If true the panel flash coroutine will interrupt / restart when O is pressed repeatedly.")]
    public bool allowInterruptingFlash = true;

    [Header("Events")]
    [Tooltip("Invoked when a tarot is 'acquired' via AcquireNextTarot(). Provides the acquired TarotCard.")]
    public UnityEventTarotCard OnCardAcquired;

    // runtime
    private CanvasGroup panelCanvasGroup;
    private bool isOwnerInstance = false;
    private List<TarotCard> triad = new List<TarotCard>();        // the three cards chosen for this player
    private int triadIndex = 0;                                  // how many triad cards already acquired (0..3)
    private TarotCard[] allCards;

    // coroutine handle
    private Coroutine fadeCoroutine = null;

    void Awake()
    {
        allCards = (TarotCard[])Enum.GetValues(typeof(TarotCard));

        // Determine owner (owner listens for O)
        PhotonView pv = GetComponentInParent<PhotonView>();
        isOwnerInstance = (pv == null) || !PhotonNetwork.InRoom || pv.IsMine;

        if (tarotPanel == null)
        {
            Debug.LogError("[TarotSelection] tarotPanel not assigned. Please assign a panel (child of player prefab preferred). Disabling component.");
            enabled = false;
            return;
        }

        // If assigned panel isn't a child of this player, we will clone it for the owner to avoid shared UI problems.
        bool panelIsChild = tarotPanel.transform.IsChildOf(this.transform);
        if (!panelIsChild)
        {
            if (isOwnerInstance)
            {
                var clone = Instantiate(tarotPanel, this.transform);
                clone.name = tarotPanel.name + "_Local";
                tarotPanel = clone;
                // triadImages likely still reference inspector objects; user should reassign after cloning OR we try auto-find below
            }
            else
            {
                // Non-owner: do not touch the shared panel. We will leave the component enabled so future RPCs can run,
                // but input listening and UI manip is disabled for non-owners.
                return;
            }
        }

        // Now tarotPanel is guaranteed to be our local owner's panel (if owner) OR is a child (if not owner we returned earlier).
        panelCanvasGroup = tarotPanel.GetComponent<CanvasGroup>();
        if (panelCanvasGroup == null)
            panelCanvasGroup = tarotPanel.AddComponent<CanvasGroup>();

        // Ensure panel starts invisible & non-interactable
        panelCanvasGroup.alpha = 0f;
        panelCanvasGroup.interactable = false;
        panelCanvasGroup.blocksRaycasts = false;

        // If triadImages were not assigned, try to auto-find up to 3 Images under tarotPanel
        AutoFindTriadImagesIfNeeded();

        // Generate the triad for this player (3 unique random cards)
        GenerateTriad();

        // Populate the triad images immediately (makes the panel ready to flash)
        UpdateTriadImages();
    }

    void Update()
    {
        if (!isOwnerInstance) return;

        if (Input.GetKeyDown(KeyCode.O))
        {
            FlashPanel();
        }
    }

    // ----------------- Triad generation & access -----------------

    void GenerateTriad()
    {
        triad.Clear();

        // Build a temporary list of all cards and pick 3 unique
        List<TarotCard> pool = new List<TarotCard>(allCards);
        for (int i = 0; i < 3 && pool.Count > 0; i++)
        {
            int r = UnityEngine.Random.Range(0, pool.Count);
            triad.Add(pool[r]);
            pool.RemoveAt(r);
        }

        triadIndex = 0; // none acquired yet
    }

    /// <summary>
    /// Returns a copy of the triad list for inspection.
    /// </summary>
    public List<TarotCard> GetTriad()
    {
        return new List<TarotCard>(triad);
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

    // ----------------- UI flash -----------------

    /// <summary>
    /// Owner-only: flash the triad panel (alpha = 1 -> 0 over flashDuration).
    /// </summary>
    public void FlashPanel()
    {
        if (!isOwnerInstance) return;
        if (panelCanvasGroup == null) return;

        // Reset images to current triad (in case triad changed)
        UpdateTriadImages();

        // If previous coroutine exists and we allow interrupt, stop it then start new; otherwise ignore new requests while running.
        if (fadeCoroutine != null)
        {
            if (allowInterruptingFlash)
            {
                StopCoroutine(fadeCoroutine);
                fadeCoroutine = StartCoroutine(FlashRoutine());
            }
            // else ignore new press
        }
        else
        {
            fadeCoroutine = StartCoroutine(FlashRoutine());
        }
    }

    IEnumerator FlashRoutine()
    {
        panelCanvasGroup.alpha = 1f;
        panelCanvasGroup.interactable = false;    // panel is informational only
        panelCanvasGroup.blocksRaycasts = false;

        float elapsed = 0f;
        while (elapsed < flashDuration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / flashDuration);
            // fade from 1 -> 0
            panelCanvasGroup.alpha = Mathf.Lerp(1f, 0f, t);
            yield return null;
        }

        panelCanvasGroup.alpha = 0f;
        fadeCoroutine = null;
    }

    // ----------------- Acquisition API (to be called when player "gets" a card) -----------------

    /// <summary>
    /// Acquire the next tarot from the triad (in triad order). If triad exhausted, returns null (false).
    /// When a tarot is acquired:
    /// - triadIndex increments
    /// - OnCardAcquired invoked
    /// - If card is Justice or Devil, the corresponding local activation helper is invoked immediately (owner only).
    /// </summary>
    public bool AcquireNextTarot(out TarotCard acquired)
    {
        acquired = default;

        if (triadIndex >= triad.Count) return false;

        acquired = triad[triadIndex];
        triadIndex++;

        // Notify listeners
        OnCardAcquired?.Invoke(acquired);

        // If this is the owner and the card has an immediate effect, apply it locally
        if (isOwnerInstance)
        {
            if (acquired == TarotCard.Justice) ActivateJusticeOnLocalShooter();
            else if (acquired == TarotCard.Devil) ActivateDevilOnLocalShooter();
        }

        return true;
    }

    // ----------------- Activation helpers (kept from previous design) -----------------

    void ActivateJusticeOnLocalShooter()
    {
        var shooter = FindLocalShooter();
        if (shooter == null)
        {
            Debug.LogWarning("[TarotSelection] ActivateJustice: no local SimpleShooter_PhotonSafe found.");
            return;
        }

        shooter.SetFireRateMultiplier(2f);             // fire delay x2 -> attack speed halved
        shooter.defaultIgnoreBodyHits = true;          // no body damage
        shooter.defaultHeadshotMultiplier = 6f;        // 6x headshot
        shooter.defaultOutgoingDamageMultiplier = 1f;  // no change to outgoing
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

    // Reuse your previous helper for locating the local shooter instance.
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

    // ----------------- Utilities -----------------

    private void AutoFindTriadImagesIfNeeded()
    {
        if (triadImages != null && triadImages.Length == 3)
        {
            bool anyNull = false;
            for (int i = 0; i < 3; i++) if (triadImages[i] == null) anyNull = true;
            if (!anyNull) return; // assigned and valid
        }

        // try to find images under tarotPanel
        if (tarotPanel == null) return;
        var found = tarotPanel.GetComponentsInChildren<Image>(true);
        triadImages = new Image[3];
        for (int i = 0; i < 3 && i < found.Length; i++)
            triadImages[i] = found[i];
    }

    void OnDestroy()
    {
        // stop coroutine (safety)
        if (fadeCoroutine != null) StopCoroutine(fadeCoroutine);
    }

    [Serializable]
    public class UnityEventTarotCard : UnityEvent<TarotCard> { }
}
