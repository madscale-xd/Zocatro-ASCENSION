using UnityEngine;
using System.Collections;

/// <summary>
/// Drop this on your Canvas (or any GameObject). Assign the target Panel GameObject
/// (usually a child of the Canvas). Pressing Escape (KeyCode.Escape) will toggle the
/// panel open/closed. Optional fade using a CanvasGroup is supported.
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

    Coroutine fadeCoroutine;

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
                panel.SetActive(true);
                canvasGroup.interactable = true;
                canvasGroup.blocksRaycasts = true;
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
            panel.SetActive(open);
        }
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
    }
}
