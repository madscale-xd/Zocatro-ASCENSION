using UnityEngine;
using System.Collections;
using UnityEngine.EventSystems;

/// <summary>
/// Drop this on your Canvas (or any GameObject). Assign the target Panel GameObject
/// (usually a child of the Canvas). Pressing Escape (KeyCode.Escape) will toggle the
/// panel open/closed. Optional fade using a CanvasGroup is supported.
///
/// Extra: optional cursor handling — when the panel opens the cursor can be unlocked
/// and made visible; original cursor state is restored when the panel closes.
///
/// Aggressive override: when overrideCursorWhileOpen is true the script will actively
/// force the cursor unlocked & visible every frame while the panel is open to prevent
/// other code re-locking it. This makes UI buttons reliably clickable.
/// </summary>
[DisallowMultipleComponent]
public class EscapeTogglePanel : MonoBehaviour
{
    [Header("Target Panel")]
    [Tooltip("Assign the panel GameObject (e.g. a child Panel under your Canvas).")]
    public GameObject panel;

    [Header("Optional CanvasGroup Fade")]
    [Tooltip("If true and a CanvasGroup is present, the panel will fade in/out.")]
    public bool useFade = false;
    public CanvasGroup canvasGroup;
    public float fadeDuration = 0.15f;

    [Header("Behavior")]
    [Tooltip("If true the panel will start closed.")]
    public bool startClosed = false;
    [Tooltip("If true the panel GameObject will be deactivated when closed.")]
    public bool deactivateGameObjectWhenClosed = true;

    [Header("Cursor Handling (optional)")]
    [Tooltip("If true, opening the panel will unlock & show the cursor and closing will restore the previous cursor state.")]
    public bool unlockCursorOnOpen = true;
    [Tooltip("If true, the script will attempt to restore the cursor state when the panel closes (only if unlocked by this script).")]
    public bool restoreCursorOnClose = true;

    [Header("Aggressive override")]
    [Tooltip("If true, continuously enforce the unlocked & visible cursor while the panel is open (prevents other scripts from re-locking it).")]
    public bool overrideCursorWhileOpen = true;

    Coroutine fadeCoroutine;
    Coroutine enforceCursorCoroutine;

    // saved cursor state so we can restore on close (if requested)
    private CursorLockMode savedLockState = CursorLockMode.None;
    private bool savedCursorVisible = true;
    private bool savedCursorStateStored = false;

    void Start()
    {
        // try to auto-assign the first child as a convenience
        if (panel == null && transform.childCount > 0)
            panel = transform.GetChild(0).gameObject;

        if (panel == null)
            Debug.LogWarning("EscapeTogglePanel: No panel assigned and no child found.");

        if (useFade && canvasGroup == null && panel != null)
            canvasGroup = panel.GetComponent<CanvasGroup>();

        if (startClosed)
            SetOpen(false, instant: true);
        else
            SetOpen(true, instant: true);
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.Escape))
            Toggle();
    }

    void OnDisable()
    {
        // safety: ensure enforcement coroutine stopped if object disabled
        StopEnforceCursor();
        // and try restore
        if (restoreCursorOnClose) RestoreCursorIfSaved();
    }

    /// <summary>
    /// Toggle the panel.
    /// </summary>
    public void Toggle()
    {
        SetOpen(!IsOpen());
    }

    /// <summary>
    /// Returns whether the panel is currently open (visible).
    /// </summary>
    public bool IsOpen()
    {
        if (panel == null) return false;
        if (useFade && canvasGroup != null)
            return canvasGroup.alpha > 0.5f;
        return panel.activeSelf;
    }

    /// <summary>
    /// Open or close the panel. If using fade, will animate CanvasGroup.alpha.
    /// </summary>
    public void SetOpen(bool open, bool instant = false)
    {
        if (panel == null) return;

        if (useFade && canvasGroup != null)
        {
            // if opening ensure GameObject is active so CanvasGroup is visible
            if (open)
            {
                // Activate before fading in
                panel.SetActive(true);
                canvasGroup.interactable = true;
                canvasGroup.blocksRaycasts = true;

                // Unlock cursor immediately so user can interact while panel fades in
                if (unlockCursorOnOpen)
                    UnlockCursorForUI();

                // Start aggressive enforcement if requested
                if (overrideCursorWhileOpen)
                    StartEnforceCursor();

                if (fadeCoroutine != null) StopCoroutine(fadeCoroutine);
                fadeCoroutine = StartCoroutine(FadeRoutine(canvasGroup, true, instant));
            }
            else
            {
                // closing: animate then optionally deactivate
                if (fadeCoroutine != null) StopCoroutine(fadeCoroutine);
                fadeCoroutine = StartCoroutine(FadeRoutine(canvasGroup, false, instant, deactivateGameObjectWhenClosed));
            }
        }
        else
        {
            // no fade: just activate/deactivate and handle cursor state
            panel.SetActive(open);

            if (open)
            {
                if (canvasGroup != null)
                {
                    canvasGroup.alpha = 1f;
                    canvasGroup.interactable = true;
                    canvasGroup.blocksRaycasts = true;
                }

                if (unlockCursorOnOpen)
                    UnlockCursorForUI();

                if (overrideCursorWhileOpen)
                    StartEnforceCursor();
            }
            else
            {
                if (canvasGroup != null)
                {
                    canvasGroup.alpha = 0f;
                    canvasGroup.interactable = false;
                    canvasGroup.blocksRaycasts = false;
                }

                if (deactivateGameObjectWhenClosed)
                    panel.SetActive(false);

                // Stop enforcement and restore cursor
                StopEnforceCursor();
                if (restoreCursorOnClose)
                    RestoreCursorIfSaved();
            }
        }

        // Clear any selected UI element so clicks go through and InputFields commit
        if (open && EventSystem.current != null)
            EventSystem.current.SetSelectedGameObject(null);
    }

    IEnumerator FadeRoutine(CanvasGroup cg, bool fadeIn, bool instant = false, bool deactivateAfter = false)
    {
        float start = cg.alpha;
        float end = fadeIn ? 1f : 0f;

        if (instant)
        {
            cg.alpha = end;
            cg.interactable = fadeIn;
            cg.blocksRaycasts = fadeIn;
            if (!fadeIn && deactivateAfter) panel.SetActive(false);

            // If we just closed, stop enforcement and restore cursor now
            if (!fadeIn)
            {
                StopEnforceCursor();
                if (restoreCursorOnClose) RestoreCursorIfSaved();
            }

            yield break;
        }

        float t = 0f;
        float duration = Mathf.Max(0.0001f, fadeDuration);
        while (t < duration)
        {
            t += Time.unscaledDeltaTime; // unscaled so UI still fades even if timeScale changes
            cg.alpha = Mathf.Lerp(start, end, Mathf.Clamp01(t / duration));
            yield return null;
        }

        cg.alpha = end;
        cg.interactable = fadeIn;
        cg.blocksRaycasts = fadeIn;

        if (!fadeIn && deactivateAfter)
            panel.SetActive(false);

        // If we just closed, stop enforcement and restore cursor now
        if (!fadeIn)
        {
            StopEnforceCursor();
            if (restoreCursorOnClose) RestoreCursorIfSaved();
        }
    }

    // Save the current cursor state (called once when unlocking the first time)
    private void SaveCursorState()
    {
        if (savedCursorStateStored) return;
        savedLockState = Cursor.lockState;
        savedCursorVisible = Cursor.visible;
        savedCursorStateStored = true;
    }

    // Unlock and show the cursor for UI interaction. Also clear any selected UI element.
    private void UnlockCursorForUI()
    {
        SaveCursorState();

        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;

        // Clear selection so InputFields commit edits and nothing stays focused.
        if (EventSystem.current != null)
            EventSystem.current.SetSelectedGameObject(null);
    }

    // Restore cursor state if this script saved it earlier.
    private void RestoreCursorIfSaved()
    {
        if (!savedCursorStateStored) return;

        Cursor.lockState = savedLockState;
        Cursor.visible = savedCursorVisible;

        savedCursorStateStored = false;
    }

    // Start aggressive per-frame enforcement coroutine
    private void StartEnforceCursor()
    {
        if (enforceCursorCoroutine != null) return;
        enforceCursorCoroutine = StartCoroutine(EnforceCursorRoutine());
    }

    // Stop enforcement coroutine
    private void StopEnforceCursor()
    {
        if (enforceCursorCoroutine != null)
        {
            StopCoroutine(enforceCursorCoroutine);
            enforceCursorCoroutine = null;
        }
    }

    // Keeps forcing unlocked & visible cursor while panel is open.
    // This is intentionally aggressive to override other scripts which may attempt to re-lock the cursor each frame.
    IEnumerator EnforceCursorRoutine()
    {
        // Ensure we saved the prior state
        SaveCursorState();

        while (IsOpen())
        {
            // Force unlocked & visible every frame
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            // Also ensure CanvasGroup is interactable & blocks raycasts so buttons stay clickable
            if (canvasGroup != null)
            {
                canvasGroup.interactable = true;
                canvasGroup.blocksRaycasts = true;
            }
            else if (panel != null)
            {
                // If no CanvasGroup, make sure panel active (buttons rely on GameObject active)
                panel.SetActive(true);
            }

            // Clear any stuck selection so clicks are delivered normally
            if (EventSystem.current != null && EventSystem.current.currentSelectedGameObject != null)
            {
                // don't constantly set selected null if it's already null (but this is cheap)
                EventSystem.current.SetSelectedGameObject(null);
            }

            yield return null; // next frame
        }

        // When we exit loop (panel closed) restore if requested
        if (restoreCursorOnClose)
            RestoreCursorIfSaved();

        enforceCursorCoroutine = null;
    }

    void OnDestroy()
    {
        // if object destroyed while panel open, attempt to restore saved cursor state
        StopEnforceCursor();
        if (restoreCursorOnClose)
            RestoreCursorIfSaved();
    }
}
