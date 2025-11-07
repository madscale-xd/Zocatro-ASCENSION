using Photon.Pun;
using UnityEngine;

/// <summary>
/// Small helper on player prefab to provide an actor number for non-Photon checks (and offline).
/// Also optionally holds a reference to the TarotSelection instance on the same player so it can
/// notify TarotSelection when the actorNumber becomes known (fixes timing races).
/// </summary>
public class PlayerIdentity : MonoBehaviourPun
{
    [Tooltip("Set automatically from PhotonView.Owner when available. Can be initialized manually via Initialize().")]
    public int actorNumber = -1;

    [Tooltip("Optional: assign the TarotSelection on this player prefab so PlayerIdentity can notify it when actorNumber is known.")]
    public TarotSelection tarotSelection;

    void Awake()
    {
        // If PhotonView.Owner is already available, set actorNumber now
        if (photonView != null && photonView.Owner != null)
            actorNumber = photonView.Owner.ActorNumber;

        // If we have a TarotSelection reference (owner instance), ask it to re-check instantiation data now that actorNumber may be known.
        if (tarotSelection != null)
        {
            try
            {
                // Attempt immediate re-check (safe to call even if TarotSelection already applied data).
                tarotSelection.TryApplyInstDataNow();
            }
            catch { /* swallow; this is best-effort */ }
        }
    }

    /// <summary>
    /// Optional: call this from your spawn/initialization code for offline/local players.
    /// This will set the actor number and notify TarotSelection to re-check instantiation data.
    /// </summary>
    public void Initialize(int actor)
    {
        actorNumber = actor;

        if (tarotSelection != null)
        {
            try
            {
                tarotSelection.TryApplyInstDataNow();
            }
            catch { /* ignore */ }
        }
    }
}
