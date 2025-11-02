using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Events;

/// <summary>
/// TarotSelection
/// - Shows/hides a panel containing three tarot-card buttons.
/// - On ShowPanel the three choices are randomized from the remaining (not-yet-selected) deck.
/// - Ten public selection methods (JusticeSelected, FoolSelected, ... StrengthSelected) that:
///     * mark the card as selected (so it won't reappear),
///     * close the tarot panel,
///     * invoke OnCardChosen (so you can hook functionality later).
/// - Keeps track of already-selected cards locally; does NOT auto-reset.
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
        // Validate inspector assignments
        if (optionButtons == null || optionButtons.Length != 3)
            Debug.LogWarning("TarotSelection: optionButtons should be an array of exactly 3 Buttons.");

        // Prepare the list of all cards
        allCards = (TarotCard[])Enum.GetValues(typeof(TarotCard));

        // Ensure panel starts hidden (you can change this)
        if (tarotPanel != null)
            tarotPanel.SetActive(false);

        // Optional safety: hook default visuals if sprites provided
        ApplyDefaultButtonVisuals();
    }

    void Update()
    {
        // TEMP: Press O to open the tarot selection panel (per request)
        if (Input.GetKeyDown(KeyCode.O))
            ShowPanel();
    }

    #region Public API: show/hide/reset

    /// <summary>
    /// Show the tarot selection panel and randomize choices.
    /// Note: does NOT reset chosen cards. If no available cards remain,
    /// buttons will be disabled and the panel will show empty.
    /// </summary>
    public void ShowPanel()
    {
        if (tarotPanel == null)
        {
            Debug.LogWarning("TarotSelection: tarotPanel not assigned.");
            return;
        }

        // Randomize assignments now (RandomizeChoices disables buttons if none available)
        RandomizeChoices();

        // Show UI
        tarotPanel.SetActive(true);
    }

    /// <summary>
    /// Hide the tarot selection panel.
    /// </summary>
    public void HidePanel()
    {
        if (tarotPanel != null)
            tarotPanel.SetActive(false);
    }

    /// <summary>
    /// Clear the remembered chosen cards so they can appear again in subsequent randomizations.
    /// Manual only — no automatic resets.
    /// </summary>
    public void ResetSelections()
    {
        chosenCards.Clear();
    }

    #endregion

    #region Randomization & assignment

    int GetAvailableCount()
    {
        return allCards.Length - chosenCards.Count;
    }

    /// <summary>
    /// Build a pool of available (not-yet-chosen) cards, pick up to 3 unique random ones, and assign them to the buttons.
    /// If fewer than 3 remain, only that many buttons will be enabled (others disabled).
    /// If zero remain, all buttons are disabled (panel shows nothing).
    /// </summary>
    void RandomizeChoices()
    {
        // Build available list
        List<TarotCard> available = new List<TarotCard>();
        foreach (var c in allCards)
            if (!chosenCards.Contains(c))
                available.Add(c);

        // If no available, disable all buttons and clear assignments
        if (available.Count == 0)
        {
            for (int i = 0; i < optionButtons.Length; i++)
            {
                DisableButton(optionButtons[i]);
                assignedForButton[i] = default;
            }
            return;
        }

        // Shuffle available list (Fisher-Yates-ish)
        for (int i = 0; i < available.Count; i++)
        {
            int j = UnityEngine.Random.Range(i, available.Count);
            var tmp = available[i]; available[i] = available[j]; available[j] = tmp;
        }

        // Assign up to optionButtons.Length entries
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
                // No card to assign here — disable the button
                DisableButton(optionButtons[i]);
            }
        }
    }

    void AssignButtonToTarot(Button btn, TarotCard card)
    {
        if (btn == null) return;

        // Clear previous listeners
        btn.onClick.RemoveAllListeners();

        // Set button image if sprite provided
        var img = btn.GetComponent<Image>();
        if (img != null && tarotSprites != null && (int)card < tarotSprites.Length && tarotSprites[(int)card] != null)
        {
            img.sprite = tarotSprites[(int)card];
            img.enabled = true;
        }

        // Map the tarot to its selection method by adding the appropriate listener
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
        // If sprites assigned but buttons empty, try to set them to a default sprite
        if (tarotSprites == null) return;
        for (int i = 0; i < optionButtons.Length && i < tarotSprites.Length; i++)
        {
            if (optionButtons[i] == null) continue;
            var img = optionButtons[i].GetComponent<Image>();
            if (img != null && img.sprite == null)
                img.sprite = tarotSprites[0]; // fallback
        }
    }

    #endregion

    #region Selection methods (user requested exact names)
    // Each method: mark chosen card so it won't appear again, hide the panel, invoke event.
    // They intentionally do not implement card behaviour other than marking & closing.

    public void JusticeSelected()    { SelectAndClose(TarotCard.Justice); }
    public void FoolSelected()       { SelectAndClose(TarotCard.Fool); }
    public void TemperanceSelected(){ SelectAndClose(TarotCard.Temperance); }
    public void LoversSelected()     { SelectAndClose(TarotCard.Lovers); }
    public void DevilSelected()      { SelectAndClose(TarotCard.Devil); }
    public void SunSelected()        { SelectAndClose(TarotCard.Sun); }
    public void MoonSelected()       { SelectAndClose(TarotCard.Moon); }
    public void StarSelected()       { SelectAndClose(TarotCard.Star); }
    public void MagicianSelected()   { SelectAndClose(TarotCard.Magician); }
    public void StrengthSelected()   { SelectAndClose(TarotCard.Strength); }

    void SelectAndClose(TarotCard chosen)
    {
        // Mark chosen (idempotent)
        chosenCards.Add(chosen);

        // Debug log so you can confirm it was marked
        Debug.Log($"TarotSelection: {chosen} chosen and removed from future pools. Remaining available: {GetAvailableCount()}");

        // Close UI first so any listeners won't immediately re-open it
        HidePanel();

        // Invoke event so other systems can react (panel is already closed)
        OnCardChosen?.Invoke(chosen);
    }
    #endregion

    #region Inspector-friendly UnityEvent wrapper for TarotCard
    [Serializable]
    public class UnityEventTarotCard : UnityEvent<TarotCard> { }
    #endregion
}
