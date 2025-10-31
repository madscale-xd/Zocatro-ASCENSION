// CharacterSelectorUIButtonPhoton.cs
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Events;
using Photon.Pun;
using ExitGames.Client.Photon; // for Hashtable
using Photon.Realtime;
using TMPro;

public class CharacterSelectorUIButtonPhoton : MonoBehaviour
{
    public enum Mode { GameObjectMode, SpriteMode }

    [Header("General")]
    public Mode mode = Mode.GameObjectMode;
    [Tooltip("Start index (0-based)")]
    public int startIndex = 0;
    public bool loopAround = true;

    [Header("GameObject Mode")]
    public List<GameObject> characterObjects = new List<GameObject>();

    [Header("Sprite Mode")]
    public Image targetImage;
    public List<Sprite> characterSprites = new List<Sprite>();

    [Header("Name UI (TextMeshPro)")]
    [Tooltip("Optional: TextMeshProUGUI that displays the current character's name.")]
    [SerializeField] public TextMeshProUGUI characterNameText;

    [Tooltip("Optional: explicit display names for your characters (index -> name). If empty, falls back to prefabResourceNames or characterObjects' names.")]
    [SerializeField] public List<string> characterNames = new List<string>();

    [Header("Prefab mapping (Resource names)")]
    [Tooltip("List of prefab names (strings) that exist in a Resources folder. Index must correspond to characters.")]
    public List<string> prefabResourceNames = new List<string>();

    [Header("UI Buttons (clickable)")]
    public Button leftUIBtn;
    public Button rightUIBtn;

    [Header("Events")]
    public UnityEvent<int> onSelectionChanged;

    // Photon custom property keys
    public const string PROP_CHARACTER_INDEX = "characterIndex";
    public const string PROP_CHARACTER_PREFAB = "characterPrefab";

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

        // <-- ensure default selection is persisted so spawner can use it
        SaveSelectionToPhoton();
    }

    public void Next()
    {
        int count = GetCount();
        if (count == 0) return;

        currentIndex++;
        if (currentIndex >= count)
            currentIndex = loopAround ? 0 : count - 1;

        RefreshSelection();
        SaveSelectionToPhoton();
    }

    public void Previous()
    {
        int count = GetCount();
        if (count == 0) return;

        currentIndex--;
        if (currentIndex < 0)
            currentIndex = loopAround ? count - 1 : 0;

        RefreshSelection();
        SaveSelectionToPhoton();
    }

    int GetCount()
    {
        return mode == Mode.GameObjectMode ? characterObjects.Count : characterSprites.Count;
    }

    void RefreshSelection()
    {
        if (mode == Mode.GameObjectMode)
        {
            for (int i = 0; i < characterObjects.Count; i++)
            {
                var go = characterObjects[i];
                if (go != null)
                    go.SetActive(i == currentIndex);
            }
        }
        else // SpriteMode
        {
            if (targetImage != null && characterSprites != null && characterSprites.Count > 0)
            {
                int safeIndex = Mathf.Clamp(currentIndex, 0, characterSprites.Count - 1);
                targetImage.sprite = characterSprites[safeIndex];
                UpdateNameUI();
            }
        }

        onSelectionChanged?.Invoke(currentIndex);
    }

    // Writes the selection into Photon local player custom properties (and PlayerPrefs fallback)
    void SaveSelectionToPhoton()
    {
        // Prefab name fallback: check mapping bounds
        string prefabName = null;
        if (prefabResourceNames != null && currentIndex >= 0 && currentIndex < prefabResourceNames.Count)
        {
            prefabName = prefabResourceNames[currentIndex];
        }

        // Always save a local fallback in case Photon isn't connected yet or properties fail
        if (!string.IsNullOrEmpty(prefabName))
            PlayerPrefs.SetString(PROP_CHARACTER_PREFAB, prefabName);

        PlayerPrefs.SetInt(PROP_CHARACTER_INDEX, currentIndex);
        PlayerPrefs.Save();

        // If Photon is connected, set custom properties on the local player
        if (PhotonNetwork.IsConnected && PhotonNetwork.LocalPlayer != null)
        {
            Hashtable props = new Hashtable
            {
                { PROP_CHARACTER_INDEX, currentIndex }
            };

            if (!string.IsNullOrEmpty(prefabName))
                props[PROP_CHARACTER_PREFAB] = prefabName;

            PhotonNetwork.LocalPlayer.SetCustomProperties(props);
        }
    }

    // Optional public helpers
    public void OnNextButton() => Next();
    public void OnPrevButton() => Previous();

    public void SetIndex(int index)
    {
        int count = GetCount();
        if (count == 0) return;
        currentIndex = Mathf.Clamp(index, 0, count - 1);
        RefreshSelection();
        SaveSelectionToPhoton();
    }

    public int GetCurrentIndex() => currentIndex;

    private void UpdateNameUI()
    {
        if (characterNameText == null) return;

        int count = GetCount();
        if (count == 0)
        {
            characterNameText.text = "";
            return;
        }

        // Ensure safe index in range (should already be valid, but be defensive)
        int safeIndex = Mathf.Clamp(currentIndex, 0, Mathf.Max(0, count - 1));
        string displayName = null;

        if (characterNames != null && safeIndex < characterNames.Count && !string.IsNullOrEmpty(characterNames[safeIndex]))
            displayName = characterNames[safeIndex];
        else if (prefabResourceNames != null && safeIndex < prefabResourceNames.Count && !string.IsNullOrEmpty(prefabResourceNames[safeIndex]))
            displayName = prefabResourceNames[safeIndex];
        else if (mode == Mode.GameObjectMode && characterObjects != null && safeIndex < characterObjects.Count && characterObjects[safeIndex] != null)
            displayName = characterObjects[safeIndex].name;
        else if (mode == Mode.SpriteMode && characterSprites != null && safeIndex < characterSprites.Count && characterSprites[safeIndex] != null)
            displayName = characterSprites[safeIndex].name;

        if (string.IsNullOrEmpty(displayName))
            displayName = $"Character {safeIndex}";

        characterNameText.text = displayName;
    }
}
