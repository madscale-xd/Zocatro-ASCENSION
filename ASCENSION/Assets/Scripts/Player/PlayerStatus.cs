using System.Collections;
using UnityEngine;
using Photon.Pun;
using UnityEngine.UI;

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
    [PunRPC]
    public void RPC_ApplyRoot(float duration, int attackerActorNumber)
    {
        ApplyRootLocal(duration);
    }

    [PunRPC]
    public void RPC_StopHealing(float duration, int attackerActorNumber)
    {
        StopHealingLocal(duration);
    }

    public void ApplyRootLocal(float duration)
    {
        if (rootCoroutine != null) StopCoroutine(rootCoroutine);
        rootCoroutine = StartCoroutine(RootRoutine(duration));
    }

    IEnumerator RootRoutine(float dur)
    {
        isRooted = true;
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
        gameObject.SendMessage("StopHealing", dur, SendMessageOptions.DontRequireReceiver);

        yield return new WaitForSeconds(dur);

        gameObject.SendMessage("ResumeHealing", SendMessageOptions.DontRequireReceiver);
        stopHealingActive = false;
        stopHealCoroutine = null;
    }
    #endregion

    #region Homeostasis (no immobilize)
    public void StartHomeostasis(float shieldAmount, float healAmount, float duration)
    {
        // Start only on the owner — this is expected; but be defensive.
        if (photonView != null && PhotonNetwork.InRoom && !photonView.IsMine)
        {
            Debug.LogWarning("[PlayerStatus] StartHomeostasis called on non-owner instance. Ignoring.");
            return;
        }

        if (homeostasisCoroutine != null) StopCoroutine(homeostasisCoroutine);
        homeostasisCoroutine = StartCoroutine(HomeostasisRoutine(shieldAmount, healAmount, duration));
    }

    private bool IsAttemptedMovement()
    {
        float h = 0f, v = 0f;
        try
        {
            h = Input.GetAxisRaw("Horizontal");
            v = Input.GetAxisRaw("Vertical");
        }
        catch { /* ignore if axes aren't defined */ }

        if (Mathf.Abs(h) > 0.001f || Mathf.Abs(v) > 0.001f) return true;

        if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.D)) return true;
        if (Input.GetKey(KeyCode.UpArrow) || Input.GetKey(KeyCode.DownArrow) || Input.GetKey(KeyCode.LeftArrow) || Input.GetKey(KeyCode.RightArrow)) return true;
        if (Input.GetKey(KeyCode.Space) || Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) return true;

        return false;
    }

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
        // Defensive: ensure this runs on owner only
        if (photonView != null && PhotonNetwork.InRoom && !photonView.IsMine)
        {
            Debug.LogWarning("[PlayerStatus] HomeostasisRoutine invoked on non-owner, aborting.");
            yield break;
        }

        // Initialization
        float appliedShield = 0f;
        float appliedHeal = 0f;
        float healAccumulator = 0f; // accumulate fractional heals, send ints to PlayerHealth

        // Apply invulnerability immediately for the full duration (owner-local).
        gameObject.SendMessage("ApplyInvulnerability", duration, SendMessageOptions.DontRequireReceiver);
        invulnerable = true;

        // Start the pulsing UI
        StartHomeostasisPulse();

        float elapsed = 0f;
        lastPosition = transform.position;
        bool canceledByMovement = false;

        // Cache PlayerHealth (owner-local) if present
        PlayerHealth phComp = GetComponent<PlayerHealth>() ?? GetComponentInChildren<PlayerHealth>() ?? GetComponentInParent<PlayerHealth>();

        // owner's actor to include as healer if we need to RPC (defensive)
        int myActor = (PhotonNetwork.InRoom && PhotonNetwork.LocalPlayer != null) ? PhotonNetwork.LocalPlayer.ActorNumber : -1;

        while (elapsed < duration)
        {
            float dt = Time.deltaTime;

            // movement checks
            bool attempted = false;
            try { attempted = IsAttemptedMovement(); } catch { attempted = false; }

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
                    float d = (transform.position - lastPosition).sqrMagnitude;
                    if (d > (movementCancelThreshold * movementCancelThreshold) * dt * dt) moved = true;
                }
            }

            if (attempted || moved)
            {
                canceledByMovement = true;
                break;
            }

            // Skip healing if stop-heal is active
            if (!stopHealingActive)
            {
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
                    // accumulate fraction
                    healAccumulator += addHeal;
                    appliedHeal += addHeal;

                    // whenever we have at least 1.0 accumulated, send integer heal to PlayerHealth
                    if (healAccumulator >= 1f)
                    {
                        int toApply = Mathf.FloorToInt(healAccumulator);
                        if (toApply > 0)
                        {
                            // Apply heal on owner via PlayerHealth.ApplyHeal (preferred)
                            if (phComp != null)
                            {
                                var targetPv = phComp.GetComponent<PhotonView>();
                                if (targetPv != null && PhotonNetwork.InRoom && !targetPv.IsMine)
                                {
                                    // defensive: call owner's RPC to heal themselves
                                    try
                                    {
                                        targetPv.RPC("RPC_Heal", targetPv.Owner, toApply, myActor);
                                    }
                                    catch
                                    {
                                        // fallback: try to call local ApplyHeal (best-effort)
                                        try { phComp.ApplyHeal(toApply); } catch { }
                                    }
                                }
                                else
                                {
                                    // owner-local: apply directly (this will broadcast)
                                    try { phComp.ApplyHeal(toApply); } catch { }
                                }
                            }
                            else
                            {
                                // No PlayerHealth: fallback to SendMessage (maintain backwards compatibility)
                                try { gameObject.SendMessage("Heal", (float)toApply, SendMessageOptions.DontRequireReceiver); } catch { }
                            }

                            // subtract applied integer from accumulator
                            healAccumulator -= toApply;
                        }
                    }
                }
            }

            elapsed += dt;
            lastPosition = transform.position;
            yield return null;
        }

        // If not canceled, apply any remainder (round remaining fractional heal)
        if (!canceledByMovement)
        {
            float remainingShield = Mathf.Max(0f, shieldAmount - appliedShield);
            float remainingHeal = Mathf.Max(0f, healAmount - appliedHeal);

            if (remainingShield > 0f)
                gameObject.SendMessage("ApplyShield", remainingShield, SendMessageOptions.DontRequireReceiver);

            if (remainingHeal > 0f)
            {
                // Prefer PlayerHealth
                int finalHeal = Mathf.RoundToInt(remainingHeal);

                if (phComp != null)
                {
                    var targetPv = phComp.GetComponent<PhotonView>();
                    if (targetPv != null && PhotonNetwork.InRoom && !targetPv.IsMine)
                    {
                        try { targetPv.RPC("RPC_Heal", targetPv.Owner, finalHeal, myActor); }
                        catch { try { phComp.ApplyHeal(finalHeal); } catch { } }
                    }
                    else
                    {
                        try { phComp.ApplyHeal(finalHeal); } catch { }
                    }
                }
                else
                {
                    try { gameObject.SendMessage("Heal", remainingHeal, SendMessageOptions.DontRequireReceiver); } catch { }
                }
            }
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
