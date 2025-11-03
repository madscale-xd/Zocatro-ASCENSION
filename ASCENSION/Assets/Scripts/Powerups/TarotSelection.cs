using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Events;
using Photon.Pun; // used to find the local player/shooter

/// <summary>
/// TarotSelection
/// (same as before) — but JusticeSelected activates Justice bonuses immediately on the local shooter.
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

    [Header("UI (assign in inspector)")]
    [Tooltip("Root panel (enable/disable to show/hide the tarot screen).")]
    public GameObject tarotPanel;

    [Tooltip("Exactly three UI Buttons that will be used for the randomized choices.")]
    public Button[] optionButtons = new Button[3];

    [Header("Optional visuals")]
    [Tooltip("Optional sprites for each TarotCard (order must match the TarotCard enum).")]
    public Sprite[] tarotSprites = new Sprite[10];

    [Header("Events")]
    [Tooltip("Invoked when a card is chosen. Provides the chosen TarotCard.")]
    public UnityEventTarotCard OnCardChosen;

    // Internal state: which cards have already been chosen during this session (local only)
    private HashSet<TarotCard> chosenCards = new HashSet<TarotCard>();

    // Cards currently assigned to each button (length = 3)
    private TarotCard[] assignedForButton = new TarotCard[3];

    // Convenience: list of all tarot values (for randomization)
    private TarotCard[] allCards;

    void Awake()
    {
        if (optionButtons == null || optionButtons.Length != 3)
            Debug.LogWarning("TarotSelection: optionButtons should be an array of exactly 3 Buttons.");

        allCards = (TarotCard[])Enum.GetValues(typeof(TarotCard));

        if (tarotPanel != null)
            tarotPanel.SetActive(false);

        ApplyDefaultButtonVisuals();
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.O))
            ShowPanel();
    }

    public void ShowPanel()
    {
        if (tarotPanel == null)
        {
            Debug.LogWarning("TarotSelection: tarotPanel not assigned.");
            return;
        }

        RandomizeChoices();
        tarotPanel.SetActive(true);
    }

    public void HidePanel()
    {
        if (tarotPanel != null)
            tarotPanel.SetActive(false);
    }

    public void ResetSelections()
    {
        chosenCards.Clear();
    }

    int GetAvailableCount()
    {
        return allCards.Length - chosenCards.Count;
    }

    void RandomizeChoices()
    {
        List<TarotCard> available = new List<TarotCard>();
        foreach (var c in allCards)
            if (!chosenCards.Contains(c))
                available.Add(c);

        if (available.Count == 0)
        {
            for (int i = 0; i < optionButtons.Length; i++)
            {
                DisableButton(optionButtons[i]);
                assignedForButton[i] = default;
            }
            return;
        }

        for (int i = 0; i < available.Count; i++)
        {
            int j = UnityEngine.Random.Range(i, available.Count);
            var tmp = available[i]; available[i] = available[j]; available[j] = tmp;
        }

        for (int i = 0; i < optionButtons.Length; i++)
        {
            if (i < available.Count)
            {
                TarotCard assigned = available[i];
                assignedForButton[i] = assigned;
                AssignButtonToTarot(optionButtons[i], assigned);
                EnableButton(optionButtons[i], true);
            }
            else
            {
                DisableButton(optionButtons[i]);
            }
        }
    }

    void AssignButtonToTarot(Button btn, TarotCard card)
    {
        if (btn == null) return;
        btn.onClick.RemoveAllListeners();

        var img = btn.GetComponent<Image>();
        if (img != null && tarotSprites != null && (int)card < tarotSprites.Length && tarotSprites[(int)card] != null)
        {
            img.sprite = tarotSprites[(int)card];
            img.enabled = true;
        }

        switch (card)
        {
            case TarotCard.Justice: btn.onClick.AddListener(JusticeSelected); break;
            case TarotCard.Fool: btn.onClick.AddListener(FoolSelected); break;
            case TarotCard.Temperance: btn.onClick.AddListener(TemperanceSelected); break;
            case TarotCard.Lovers: btn.onClick.AddListener(LoversSelected); break;
            case TarotCard.Devil: btn.onClick.AddListener(DevilSelected); break;
            case TarotCard.Sun: btn.onClick.AddListener(SunSelected); break;
            case TarotCard.Moon: btn.onClick.AddListener(MoonSelected); break;
            case TarotCard.Star: btn.onClick.AddListener(StarSelected); break;
            case TarotCard.Magician: btn.onClick.AddListener(MagicianSelected); break;
            case TarotCard.Strength: btn.onClick.AddListener(StrengthSelected); break;
            default:
                Debug.LogWarning("TarotSelection: unhandled card when assigning button.");
                break;
        }
    }

    void EnableButton(Button btn, bool enabled)
    {
        if (btn == null) return;
        btn.interactable = enabled;
        btn.gameObject.SetActive(enabled);
    }

    void DisableButton(Button btn)
    {
        if (btn == null) return;
        btn.onClick.RemoveAllListeners();
        btn.gameObject.SetActive(false);
    }

    void ApplyDefaultButtonVisuals()
    {
        if (tarotSprites == null) return;
        for (int i = 0; i < optionButtons.Length && i < tarotSprites.Length; i++)
        {
            if (optionButtons[i] == null) continue;
            var img = optionButtons[i].GetComponent<Image>();
            if (img != null && img.sprite == null)
                img.sprite = tarotSprites[0];
        }
    }

    #region Selection methods (Justice activation added)

    // Justice: We apply the bonuses immediately to the local player's SimpleShooter:
    // - Half attack speed -> double the fireRate (so fewer shots/sec)
    // - No body shot damage -> bullets set ignoreBodyHits = true
    // - 6x headshot multiplier -> bullets set headshotMultiplier = 6
    public void JusticeSelected()
    {
        ActivateJusticeOnLocalShooter();
        SelectAndClose(TarotCard.Justice);
    }

    public void FoolSelected()       { SelectAndClose(TarotCard.Fool); }
    public void TemperanceSelected(){ SelectAndClose(TarotCard.Temperance); }
    public void LoversSelected()     { SelectAndClose(TarotCard.Lovers); }
    public void DevilSelected()      { SelectAndClose(TarotCard.Devil); }
    public void SunSelected()        { SelectAndClose(TarotCard.Sun); }
    public void MoonSelected()       { SelectAndClose(TarotCard.Moon); }
    public void StarSelected()       { SelectAndClose(TarotCard.Star); }
    public void MagicianSelected()   { SelectAndClose(TarotCard.Magician); }
    public void StrengthSelected()   { SelectAndClose(TarotCard.Strength); }

    void ActivateJusticeOnLocalShooter()
    {
        // Try to find the local SimpleShooter_PhotonSafe instance.
        SimpleShooter_PhotonSafe shooter = FindLocalShooter();
        if (shooter == null)
        {
            Debug.LogWarning("[TarotSelection] JusticeSelected: no local SimpleShooter_PhotonSafe instance found to apply bonuses.");
            return;
        }

        // Apply Justice modifications:
        // Half attack speed -> double the fireRate delay (multiplier=2)
        shooter.SetFireRateMultiplier(2f);

        // Bullets: ignore body hits, 6x headshot multiplier
        shooter.defaultIgnoreBodyHits = true;
        shooter.defaultHeadshotMultiplier = 6f;
        shooter.defaultOutgoingDamageMultiplier = 1f; // no change to base outgoing damage

        Debug.Log("[TarotSelection] Justice applied: fireRate x2 (attack speed halved), body hits ignored, headshot x6.");
    }

    /// <summary>
    /// Finds the SimpleShooter_PhotonSafe instance belonging to the local player (owner).
    /// Works in Photon rooms (finds the instance with PhotonView.IsMine) and falls back to the first found when offline.
    /// </summary>
    SimpleShooter_PhotonSafe FindLocalShooter()
    {
        var shooters = GameObject.FindObjectsOfType<SimpleShooter_PhotonSafe>(true);
        if (shooters == null || shooters.Length == 0) return null;

        // If in Photon room, pick the one whose PhotonView.IsMine or that has no PhotonView (local)
        if (PhotonNetwork.InRoom)
        {
            foreach (var s in shooters)
            {
                var pv = s.GetComponentInParent<PhotonView>();
                if (pv == null) return s; // local non-networked shooter
                if (pv.IsMine) return s;
            }
        }
        else
        {
            // Offline - return the first one
            return shooters[0];
        }

        // fallback
        return shooters[0];
    }

    void SelectAndClose(TarotCard chosen)
    {
        chosenCards.Add(chosen);
        Debug.Log($"TarotSelection: {chosen} chosen and removed from future pools. Remaining available: {GetAvailableCount()}");
        HidePanel();
        OnCardChosen?.Invoke(chosen);
    }
    #endregion

    #region UnityEvent wrapper
    [Serializable]
    public class UnityEventTarotCard : UnityEvent<TarotCard> { }
    #endregion
}
