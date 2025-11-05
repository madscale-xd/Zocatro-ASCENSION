// TriadCard.cs
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

[DisallowMultipleComponent]
public class TriadCard : MonoBehaviour, IPointerClickHandler
{
    [Header("Shared sprite pool")]
    [Tooltip("Assign the sprite pool here. You may assign the same array to all three TriadCard instances " +
             "or assign it to only one — the script will take the first non-empty pool it finds at runtime.")]
    public Sprite[] sharedSprites;

    [Header("UI target")]
    [Tooltip("The Image component that will display the chosen sprite. If null, will try to auto-find an Image on this GameObject.")]
    public Image targetImage;

    [Header("Options")]
    [Tooltip("If true, the card randomizes a unique sprite on Start.")]
    public bool randomizeOnStart = true;

    // Public read-only state
    [Tooltip("Index in the sharedSprites array that this card currently uses (-1 = none).")]
    [SerializeField] public int currentIndex = -1;
    public Sprite CurrentSprite { get; private set; }

    // --- STATIC shared state across all TriadCard instances ---
    private static Sprite[] s_sharedSprites = null;
    private static bool[] s_taken = null;
    private static readonly object s_lock = new object();
    private static List<TriadCard> s_instances = new List<TriadCard>();

    // --- lifecycle ---
    void Awake()
    {
        // cache UI target if not set
        if (targetImage == null)
            targetImage = GetComponent<Image>();

        // register instance
        lock (s_lock)
        {
            if (!s_instances.Contains(this)) s_instances.Add(this);

            // If our inspector has a non-empty sprites array and no shared pool exists yet, adopt it.
            if ((s_sharedSprites == null || s_sharedSprites.Length == 0) && sharedSprites != null && sharedSprites.Length > 0)
            {
                AdoptSharedPool(sharedSprites);
            }
            else
            {
                // If shared pool exists but this instance also assigned a different pool, warn to avoid confusion.
                if (sharedSprites != null && sharedSprites.Length > 0 && s_sharedSprites != null)
                {
                    if (!ReferenceEquals(sharedSprites, s_sharedSprites) && sharedSprites.Length == s_sharedSprites.Length)
                    {
                        Debug.LogWarning($"TriadCard ({name}): you assigned a sprite pool locally but a shared pool was already set. The shared pool will be used.");
                    }
                    else
                    {
                        Debug.LogWarning($"TriadCard ({name}): local sprite pool ignored because a shared pool already exists.");
                    }
                }
            }
        }
    }

    void Start()
    {
        // If no shared pool yet and we have one locally, ensure adoption happened (cover Start ordering differences)
        lock (s_lock)
        {
            if ((s_sharedSprites == null || s_sharedSprites.Length == 0) && sharedSprites != null && sharedSprites.Length > 0)
            {
                AdoptSharedPool(sharedSprites);
            }
        }

        if (randomizeOnStart)
            AssignRandomUnique();
    }

    void OnDestroy()
    {
        // free taken slot if any
        lock (s_lock)
        {
            FreeSlot();
            s_instances.Remove(this);
        }
    }

    // --- Public API ---

    /// <summary>
    /// Force-assign a specific index from the shared pool. Returns true on success.
    /// </summary>
    public bool SetIndex(int index)
    {
        lock (s_lock)
        {
            if (!EnsureSharedPool()) return false;
            if (index < 0 || index >= s_sharedSprites.Length)
            {
                Debug.LogWarning($"TriadCard ({name}): SetIndex out of range: {index}");
                return false;
            }

            if (s_taken[index])
            {
                // if it's already our own index, that's fine; otherwise fail
                if (index == currentIndex) return true;
                Debug.LogWarning($"TriadCard ({name}): requested index {index} is already taken by another card.");
                return false;
            }

            FreeSlot(); // free existing before taking new
            TakeSlot(index);
            return true;
        }
    }

    /// <summary>
    /// Randomly picks an available index from the shared pool that is not taken by other TriadCard instances.
    /// Returns true if a new unique index was assigned; false if no available slots (or on failure).
    /// </summary>
    public bool AssignRandomUnique()
    {
        lock (s_lock)
        {
            if (!EnsureSharedPool()) return false;

            // build list of available indices
            List<int> available = new List<int>();
            for (int i = 0; i < s_sharedSprites.Length; i++)
            {
                if (!s_taken[i]) available.Add(i);
            }

            if (available.Count == 0)
            {
                Debug.LogWarning($"TriadCard ({name}): No available unique sprites left in shared pool. Assignment skipped.");
                return false;
            }

            int pick = available[UnityEngine.Random.Range(0, available.Count)];
            FreeSlot();
            TakeSlot(pick);
            return true;
        }
    }

    /// <summary>
    /// Cycle to the next available sprite in array order, skipping already-taken indices.
    /// Returns true if the card changed to a new sprite; false if no change (no free slots).
    /// Call this from a UI Button.onClick, or it will be invoked automatically when user clicks if using a Graphic + EventSystem.
    /// </summary>
    public bool OnClickCycle()
    {
        lock (s_lock)
        {
            if (!EnsureSharedPool()) return false;

            int n = s_sharedSprites.Length;
            if (n == 0) return false;

            int start;
            if (currentIndex >= 0 && currentIndex < n)
                start = (currentIndex + 1) % n;
            else
                start = 0;

            for (int offset = 0; offset < n; offset++)
            {
                int idx = (start + offset) % n;
                // candidate must be free (not taken by other) to pick it
                if (!s_taken[idx])
                {
                    // found next free slot
                    FreeSlot();
                    TakeSlot(idx);
                    return true;
                }
            }

            // no free indices found -> keep current (no change)
            Debug.Log($"TriadCard ({name}): No free sprite to cycle to (all taken).");
            return false;
        }
    }

    /// <summary>
    /// Returns true if this TriadCard currently has an assigned sprite.
    /// </summary>
    public bool HasAssignedSprite() => currentIndex >= 0 && CurrentSprite != null;

    // --- IPointerClickHandler (so clicks work without wiring Button.onClick) ---
    public void OnPointerClick(PointerEventData eventData)
    {
        // Only respond to left-click/tap by default
        if (eventData.button == PointerEventData.InputButton.Left)
            OnClickCycle();
    }

    // --- Internal helpers ---

    // Adopt a given sprite array as the global shared pool and initialize taken slots array.
    private void AdoptSharedPool(Sprite[] pool)
    {
        s_sharedSprites = pool;
        s_taken = new bool[s_sharedSprites.Length];

        // If there are more instances than slots, that's allowed but some instances won't be able to get unique sprites.
        Debug.Log($"TriadCard: Adopted shared sprite pool with {s_sharedSprites.Length} entries.");
    }

    // Ensure pool exists; return false and warn if not.
    private bool EnsureSharedPool()
    {
        // If static pool hasn't been set, but this instance has a local pool, adopt it now.
        if ((s_sharedSprites == null || s_sharedSprites.Length == 0) && sharedSprites != null && sharedSprites.Length > 0)
        {
            AdoptSharedPool(sharedSprites);
        }

        if (s_sharedSprites == null || s_sharedSprites.Length == 0)
        {
            Debug.LogWarning($"TriadCard ({name}): No shared sprite pool has been provided. Assign a non-empty sharedSprites array on one TriadCard in the scene.");
            return false;
        }
        if (s_taken == null || s_taken.Length != s_sharedSprites.Length)
        {
            s_taken = new bool[s_sharedSprites.Length];
        }
        return true;
    }

    // Mark a slot as taken and update visual.
    private void TakeSlot(int index)
    {
        if (index < 0 || s_sharedSprites == null || index >= s_sharedSprites.Length) return;

        s_taken[index] = true;
        currentIndex = index;
        CurrentSprite = s_sharedSprites[index];
        ApplySpriteToImage(CurrentSprite);
    }

    // Free our currently-taken slot (if any)
    private void FreeSlot()
    {
        if (currentIndex >= 0 && s_taken != null && currentIndex < s_taken.Length)
        {
            // only free if this instance actually "owns" it (defensive)
            if (s_taken[currentIndex])
                s_taken[currentIndex] = false;
        }
        currentIndex = -1;
        CurrentSprite = null;
    }

    // Apply sprite to target image (safe)
    private void ApplySpriteToImage(Sprite s)
    {
        if (targetImage == null)
        {
            // try to auto-find one now (late binding)
            targetImage = GetComponent<Image>();
            if (targetImage == null)
            {
                Debug.LogWarning($"TriadCard ({name}): No Image component assigned and none found on GameObject. Cannot display sprite.");
                return;
            }
        }

        targetImage.sprite = s;
        targetImage.enabled = (s != null);
    }

#if UNITY_EDITOR
    // Editor helper to reset static pool when stopping/entering edit mode (useful during development).
    // NOTE: This code only compiles in the Editor.
    [UnityEditor.MenuItem("TriadCard/Reset Shared Pool (Editor only)")]
    private static void EditorResetSharedPool()
    {
        lock (s_lock)
        {
            s_sharedSprites = null;
            s_taken = null;
            Debug.Log("TriadCard: Shared pool reset (editor menu).");
        }
    }
#endif
}
