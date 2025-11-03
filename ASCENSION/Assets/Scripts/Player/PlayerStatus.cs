using System.Collections;
using UnityEngine;
using Photon.Pun;
using UnityEngine.UI;

/// <summary>
/// Central place to implement status effects on the player: root, stop-healing, homeostasis, invulnerability, etc.
/// Designed to be owner-local: RPCs are targeted at the player's owner so these methods run on the owning client.
/// Attach to your player prefab (same root as PlayerHealth / PhotonView).
/// 
/// NOTE: This version does NOT immobilize the player during Homeostasis (per request).
/// Homeostasis still CANCELS if the player attempts to move or actually moves.
/// </summary>
[RequireComponent(typeof(PhotonView))]
public class PlayerStatus : MonoBehaviourPun
{
    [Header("Movement cancellation")]
    [Tooltip("Speed threshold that counts as 'movement' and cancels homeostasis (units/sec).")]
    public float movementCancelThreshold = 0.15f;

    [Header("Homeostasis Visual (optional)")]
    [Tooltip("CanvasGroup that will pulse while homeostasis is active. Assign the CanvasGroup on the Image GameObject in the inspector.")]
    public CanvasGroup homeostasisCanvasGroup;

    [Tooltip("Maximum alpha value during pulse (0 -> pulseAlpha -> 0).")]
    public float homeostasisPulseAlpha = 0.3f;

    [Tooltip("Time (seconds) to go from 0 -> pulseAlpha (and also for pulseAlpha -> 0).")]
    public float homeostasisPulseStepDuration = 0.25f;

    // internal
    private Coroutine homeostasisPulseCoroutine;

    // State flags
    bool isRooted = false;
    bool stopHealingActive = false;
    bool invulnerable = false;

    Coroutine rootCoroutine;
    Coroutine stopHealCoroutine;
    Coroutine homeostasisCoroutine;

    Vector3 lastPosition;

    void Start()
    {
        lastPosition = transform.position;

        // ensure the pulsing UI is invisible by default
        if (homeostasisCanvasGroup != null)
            homeostasisCanvasGroup.alpha = 0f;
    }

    #region RPC / Local wrappers for root & stop-heal
    // Called via Photon RPC targeted at the owner's client
    [PunRPC]
    public void RPC_ApplyRoot(float duration, int attackerActorNumber)
    {
        ApplyRootLocal(duration);
    }

    // Called via Photon RPC targeted at the owner's client
    [PunRPC]
    public void RPC_StopHealing(float duration, int attackerActorNumber)
    {
        StopHealingLocal(duration);
    }

    /// <summary>
    /// Apply a root locally (disable movement). Use SendMessage to communicate with character controllers.
    /// </summary>
    public void ApplyRootLocal(float duration)
    {
        if (rootCoroutine != null) StopCoroutine(rootCoroutine);
        rootCoroutine = StartCoroutine(RootRoutine(duration));
    }

    IEnumerator RootRoutine(float dur)
    {
        isRooted = true;
        // Ask movement systems to disable movement - use SendMessage to keep decoupled
        gameObject.SendMessage("SetImmobilized", true, SendMessageOptions.DontRequireReceiver);

        yield return new WaitForSeconds(dur);

        gameObject.SendMessage("SetImmobilized", false, SendMessageOptions.DontRequireReceiver);
        isRooted = false;
        rootCoroutine = null;
    }

    public void StopHealingLocal(float duration)
    {
        if (stopHealCoroutine != null) StopCoroutine(stopHealCoroutine);
        stopHealCoroutine = StartCoroutine(StopHealingRoutine(duration));
    }

    IEnumerator StopHealingRoutine(float dur)
    {
        stopHealingActive = true;
        // If you have a healing system, it should check for this flag - we also send a message for compatibility
        gameObject.SendMessage("StopHealing", dur, SendMessageOptions.DontRequireReceiver);

        yield return new WaitForSeconds(dur);

        gameObject.SendMessage("ResumeHealing", SendMessageOptions.DontRequireReceiver);
        stopHealingActive = false;
        stopHealCoroutine = null;
    }
    #endregion

    #region Homeostasis (no immobilize)
    /// <summary>
    /// Starts the homeostasis effect on the owner client:
    /// - Gradually applies shield and heal over 'duration' using SendMessage("ApplyShield", amount) and SendMessage("Heal", amount).
    /// - Applies invulnerability for the duration (SendMessage "ApplyInvulnerability"/"RemoveInvulnerability").
    /// - DOES NOT immobilize the player (per request), but the effect cancels immediately if the player attempts to move or actually moves.
    /// </summary>
    public void StartHomeostasis(float shieldAmount, float healAmount, float duration)
    {
        // cancel any existing homeostasis
        if (homeostasisCoroutine != null) StopCoroutine(homeostasisCoroutine);
        homeostasisCoroutine = StartCoroutine(HomeostasisRoutine(shieldAmount, healAmount, duration));
    }

    /// <summary>
    /// Helper that detects attempted movement (input). Works with Unity's legacy input manager.
    /// If you're using the new Input System, call CancelHomeostasis() from your input callbacks instead.
    /// </summary>
    private bool IsAttemptedMovement()
    {
        // Legacy Input axes (works with Input Manager)
        float h = 0f, v = 0f;
        try
        {
            h = Input.GetAxisRaw("Horizontal");
            v = Input.GetAxisRaw("Vertical");
        }
        catch { /* ignore if axes aren't defined */ }

        if (Mathf.Abs(h) > 0.001f || Mathf.Abs(v) > 0.001f) return true;

        // Common keys: WASD, arrows, jump, sprint
        if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.D)) return true;
        if (Input.GetKey(KeyCode.UpArrow) || Input.GetKey(KeyCode.DownArrow) || Input.GetKey(KeyCode.LeftArrow) || Input.GetKey(KeyCode.RightArrow)) return true;
        if (Input.GetKey(KeyCode.Space) || Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) return true;

        return false;
    }

    /// <summary>
    /// Public cancel API (useful for new Input System callbacks).
    /// </summary>
    public void CancelHomeostasis()
    {
        if (homeostasisCoroutine != null)
        {
            StopCoroutine(homeostasisCoroutine);
            homeostasisCoroutine = null;
        }

        StopHomeostasisPulse();

        // Remove invulnerability immediately
        gameObject.SendMessage("RemoveInvulnerability", SendMessageOptions.DontRequireReceiver);
    }

    IEnumerator HomeostasisRoutine(float shieldAmount, float healAmount, float duration)
    {
        // Initialization
        float appliedShield = 0f;
        float appliedHeal = 0f;

        // Apply invulnerability immediately for the full duration (owner-local).
        gameObject.SendMessage("ApplyInvulnerability", duration, SendMessageOptions.DontRequireReceiver);
        invulnerable = true;

        // Start the pulsing UI
        StartHomeostasisPulse();

        float elapsed = 0f;
        lastPosition = transform.position;
        bool canceledByMovement = false;

        // Loop until full duration or until movement/attempt cancels
        while (elapsed < duration)
        {
            float dt = Time.deltaTime;

            // 1) detect attempted movement (input)
            bool attempted = false;
            try { attempted = IsAttemptedMovement(); } catch { attempted = false; }

            // 2) detect actual movement (rigidbody/charactercontroller/position-delta)
            bool moved = false;
            var rb = GetComponent<Rigidbody>();
            if (rb != null)
            {
                if (rb.velocity.sqrMagnitude > movementCancelThreshold * movementCancelThreshold) moved = true;
            }
            else
            {
                var cc = GetComponent<CharacterController>();
                if (cc != null)
                {
                    if (cc.velocity.sqrMagnitude > movementCancelThreshold * movementCancelThreshold) moved = true;
                }
                else
                {
                    // fallback: position delta scaled by dt
                    float d = (transform.position - lastPosition).sqrMagnitude;
                    if (d > (movementCancelThreshold * movementCancelThreshold) * dt * dt) moved = true;
                }
            }

            if (attempted || moved)
            {
                canceledByMovement = true;
                break; // cancel immediately
            }

            // apply fractional shield/heal this frame
            float frac = (duration > 0f) ? (dt / duration) : 1f;
            float addShield = shieldAmount * frac;
            float addHeal = healAmount * frac;

            // clamp to remaining
            if (appliedShield + addShield > shieldAmount) addShield = shieldAmount - appliedShield;
            if (appliedHeal + addHeal > healAmount) addHeal = healAmount - appliedHeal;

            if (addShield > 0f)
            {
                try { gameObject.SendMessage("ApplyShield", addShield, SendMessageOptions.DontRequireReceiver); } catch { }
                appliedShield += addShield;
            }
            if (addHeal > 0f)
            {
                try { gameObject.SendMessage("Heal", addHeal, SendMessageOptions.DontRequireReceiver); } catch { }
                appliedHeal += addHeal;
            }

            elapsed += dt;
            lastPosition = transform.position;
            yield return null;
        }

        // If not canceled, apply any remainder
        if (!canceledByMovement)
        {
            float remainingShield = Mathf.Max(0f, shieldAmount - appliedShield);
            float remainingHeal = Mathf.Max(0f, healAmount - appliedHeal);
            if (remainingShield > 0f)
                gameObject.SendMessage("ApplyShield", remainingShield, SendMessageOptions.DontRequireReceiver);
            if (remainingHeal > 0f)
                gameObject.SendMessage("Heal", remainingHeal, SendMessageOptions.DontRequireReceiver);
        }

        // Stop visuals and remove invulnerability (immediate)
        StopHomeostasisPulse();
        gameObject.SendMessage("RemoveInvulnerability", SendMessageOptions.DontRequireReceiver);
        invulnerable = false;

        homeostasisCoroutine = null;
    }

    #endregion

    #region Pulse UI
    private void StartHomeostasisPulse()
    {
        if (homeostasisCanvasGroup == null) return;
        if (homeostasisPulseCoroutine != null) return;
        homeostasisPulseCoroutine = StartCoroutine(HomeostasisPulseCoroutine());
    }

    private void StopHomeostasisPulse()
    {
        if (homeostasisPulseCoroutine != null)
        {
            StopCoroutine(homeostasisPulseCoroutine);
            homeostasisPulseCoroutine = null;
        }
        if (homeostasisCanvasGroup != null)
        {
            homeostasisCanvasGroup.alpha = 0f; // ensure invisible when not active
        }
    }

    private IEnumerator HomeostasisPulseCoroutine()
    {
        if (homeostasisCanvasGroup == null)
            yield break;

        homeostasisCanvasGroup.alpha = 0f;

        while (true)
        {
            // up
            float t = 0f;
            while (t < homeostasisPulseStepDuration)
            {
                t += Time.deltaTime;
                float f = Mathf.Clamp01(t / Mathf.Max(0.0001f, homeostasisPulseStepDuration));
                homeostasisCanvasGroup.alpha = Mathf.Lerp(0f, homeostasisPulseAlpha, f);
                yield return null;
            }
            homeostasisCanvasGroup.alpha = homeostasisPulseAlpha;

            // down
            t = 0f;
            while (t < homeostasisPulseStepDuration)
            {
                t += Time.deltaTime;
                float f = Mathf.Clamp01(t / Mathf.Max(0.0001f, homeostasisPulseStepDuration));
                homeostasisCanvasGroup.alpha = Mathf.Lerp(homeostasisPulseAlpha, 0f, f);
                yield return null;
            }
            homeostasisCanvasGroup.alpha = 0f;
        }
    }
    #endregion
}
