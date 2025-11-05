// TarotSelection.cs
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Photon.Pun;

/// <summary>
/// TarotSelection - reads triad from PhotonView.InstantiationData (preferred), then falls back
/// to LocalPlayer custom props, then PlayerPrefs. Owner-only UI flash. Triad-first acquisition, then shuffled deck.
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

    void Awake()
    {
        allCards = (TarotCard[])Enum.GetValues(typeof(TarotCard));
        PhotonView pv = GetComponentInParent<PhotonView>();
        isOwnerInstance = (pv == null) || !PhotonNetwork.InRoom || pv.IsMine;

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
                // Non-owner: do nothing for owner UI
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

        // Defensive: wait a small amount for instantiation data then fallback to other sources
        StartCoroutine(ApplyInstantiationDataOrFallbackRoutine());
    }

    private IEnumerator ApplyInstantiationDataOrFallbackRoutine()
    {
        bool applied = false;
        PhotonView myPv = GetComponentInParent<PhotonView>();

        // immediate check first
        if (TryApplyInstData(myPv))
            applied = true;

        // wait loop if not applied
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

        // fallback to LocalPlayer custom props then PlayerPrefs (owner only)
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
                if (acquired == TarotCard.Justice) ActivateJusticeOnLocalShooter();
                else if (acquired == TarotCard.Devil) ActivateDevilOnLocalShooter();
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
            }
            return true;
        }

        return false;
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
    }

    // Public helpers to inspect runtime triad programmatically
    public List<TarotCard> GetTriad() => new List<TarotCard>(triad);
    public int[] GetTriadIndices() => (int[])triadIndices.Clone();
    public List<TarotCard> GetDeckQueue() => new List<TarotCard>(deckQueue);
}
