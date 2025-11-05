// CharacterSelectorUIButtonPhoton.cs
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Photon.Pun;
using ExitGames.Client.Photon;
using TMPro;

/// <summary>
/// Character selector UI. IMPORTANT: this no longer auto-writes selection/triad.
/// Assign triadSlots[0..2] (TriadCard) in the inspector. The triad is read when TriadTransferManager locks.
/// </summary>
public class CharacterSelectorUIButtonPhoton : MonoBehaviour
{
    [Header("General")]
    [Tooltip("Start index (0-based)")]
    public int startIndex = 0;
    public bool loopAround = true;

    [Header("GameObject Mode")]
    public List<GameObject> characterObjects = new List<GameObject>();

    [Header("Sprite Mode")]
    public Image targetImage;
    public List<Sprite> characterSprites = new List<Sprite>();

    [Header("Name UI (TextMeshPro)")]
    public TextMeshProUGUI characterNameText;
    public List<string> characterNames = new List<string>();

    [Header("Prefab mapping (Resource names)")]
    public List<string> prefabResourceNames = new List<string>();

    [Header("UI Buttons (clickable)")]
    public Button leftUIBtn;
    public Button rightUIBtn;

    [Header("Triad slots (assign 3 TriadCard components here)")]
    [Tooltip("Exactly 3 TriadCard references — these are read directly to get the triad indices.")]
    public TriadCard[] triadSlots = new TriadCard[3];

    [Header("Options")]
    [Tooltip("If true, this component will attempt to write selection to Photon on Start (use with caution).")]
    public bool writeOnStart = false; // IMPORTANT: default false so we don't auto-save when entering Room

    int currentIndex = 0;

    void Start()
    {
        int maxIndex = Mathf.Max(0, GetCount() - 1);
        currentIndex = Mathf.Clamp(startIndex, 0, maxIndex);

        if (leftUIBtn != null)
        {
            leftUIBtn.onClick.RemoveAllListeners();
            leftUIBtn.onClick.AddListener(Previous);
        }

        if (rightUIBtn != null)
        {
            rightUIBtn.onClick.RemoveAllListeners();
            rightUIBtn.onClick.AddListener(Next);
        }

        RefreshSelection();

        if (writeOnStart)
            SaveSelectionToPhoton(); // left as an option (default false)
    }

    public void Next()
    {
        int count = GetCount();
        if (count == 0) return;

        currentIndex++;
        if (currentIndex >= count)
            currentIndex = loopAround ? 0 : count - 1;

        RefreshSelection();
        // NO auto-save
    }

    public void Previous()
    {
        int count = GetCount();
        if (count == 0) return;

        currentIndex--;
        if (currentIndex < 0)
            currentIndex = loopAround ? count - 1 : 0;

        RefreshSelection();
        // NO auto-save
    }

    int GetCount()
    {
        // detect mode by presence of characterObjects vs sprites
        if (characterObjects != null && characterObjects.Count > 0) return characterObjects.Count;
        if (characterSprites != null && characterSprites.Count > 0) return characterSprites.Count;
        return 0;
    }

    void RefreshSelection()
    {
        if (characterObjects != null && characterObjects.Count > 0)
        {
            for (int i = 0; i < characterObjects.Count; i++)
            {
                var go = characterObjects[i];
                if (go != null)
                    go.SetActive(i == currentIndex);
            }
        }
        else if (targetImage != null && characterSprites != null && characterSprites.Count > 0)
        {
            int safeIndex = Mathf.Clamp(currentIndex, 0, characterSprites.Count - 1);
            targetImage.sprite = characterSprites[safeIndex];
        }

        UpdateNameUI();
    }

    void UpdateNameUI()
    {
        if (characterNameText == null) return;

        int count = GetCount();
        if (count == 0)
        {
            characterNameText.text = "";
            return;
        }

        int safeIndex = Mathf.Clamp(currentIndex, 0, Mathf.Max(0, count - 1));
        string displayName = null;

        if (characterNames != null && safeIndex < characterNames.Count && !string.IsNullOrEmpty(characterNames[safeIndex]))
            displayName = characterNames[safeIndex];
        else if (prefabResourceNames != null && safeIndex < prefabResourceNames.Count && !string.IsNullOrEmpty(prefabResourceNames[safeIndex]))
            displayName = prefabResourceNames[safeIndex];
        else if (characterObjects != null && safeIndex < characterObjects.Count && characterObjects[safeIndex] != null)
            displayName = characterObjects[safeIndex].name;
        else if (characterSprites != null && safeIndex < characterSprites.Count && characterSprites[safeIndex] != null)
            displayName = characterSprites[safeIndex].name;

        if (string.IsNullOrEmpty(displayName))
            displayName = $"Character {safeIndex}";

        characterNameText.text = displayName;
    }

    // Collect triad indices from the assigned triadSlots array ONLY
    // returns int[3] with -1 entries if any were not set
    private int[] CollectTriadIndicesFromSlots()
    {
        int[] outTri = new int[3] { -1, -1, -1 };
        if (triadSlots == null || triadSlots.Length < 3) return outTri;

        for (int i = 0; i < 3; i++)
        {
            var t = triadSlots[i];
            if (t == null) continue;
            int cur = t.currentIndex;
            if (cur >= 0)
                outTri[i] = cur;
        }

        return outTri;
    }

    // Public accessor so TriadTransferManager (or other code) can read the triad before scene change
    public int[] GetTriadIndices()
    {
        return CollectTriadIndicesFromSlots();
    }

    // Public accessor to get currently selected character index
    public int GetCurrentIndex() => currentIndex;

    // Writes the selection into Photon local player custom properties (and PlayerPrefs fallback)
    // This is public so TriadTransferManager can call it at transfer time.
    public void SaveSelectionToPhoton()
    {
        string prefabName = null;
        if (prefabResourceNames != null && currentIndex >= 0 && currentIndex < prefabResourceNames.Count)
            prefabName = prefabResourceNames[currentIndex];

        // local fallback
        if (!string.IsNullOrEmpty(prefabName))
            PlayerPrefs.SetString(PhotonKeys.PREF_CHARACTER_PREFAB, prefabName);

        PlayerPrefs.SetInt(PhotonKeys.PREF_CHARACTER_INDEX, currentIndex);

        // collect triad from explicitly assigned slots
        int[] tri = CollectTriadIndicesFromSlots();
        if (tri != null)
        {
            string csv = $"{tri[0]},{tri[1]},{tri[2]}";
            PlayerPrefs.SetString(PhotonKeys.PREF_KEY_TRIAD, csv);
        }

        PlayerPrefs.Save();

        // set Photon local player custom props (if connected)
        if (PhotonNetwork.IsConnected && PhotonNetwork.LocalPlayer != null)
        {
            Hashtable props = new Hashtable { { PhotonKeys.PROP_CHARACTER_INDEX, currentIndex } };
            if (!string.IsNullOrEmpty(prefabName)) props[PhotonKeys.PROP_CHARACTER_PREFAB] = prefabName;

            if (tri != null)
            {
                object[] triObj = new object[] { tri[0], tri[1], tri[2] };
                props[PhotonKeys.PROP_TRIAD] = triObj;
            }

            PhotonNetwork.LocalPlayer.SetCustomProperties(props);
            Debug.Log($"CharacterSelector: Saved idx={currentIndex} prefab='{prefabName}' triad={(tri!=null? $"{tri[0]},{tri[1]},{tri[2]}" : "null")}");
        }
    }

    // Force-write triad+selection to Photon (useful just before a scene load)
    public void ForceSyncToPhoton()
    {
        SaveSelectionToPhoton();
    }

    // Optional inspector helper
    public void SetIndex(int index)
    {
        int count = GetCount();
        if (count == 0) return;
        currentIndex = Mathf.Clamp(index, 0, count - 1);
        RefreshSelection();
        // NO auto-save here either
    }
}
