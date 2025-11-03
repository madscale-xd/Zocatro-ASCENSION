using Photon.Pun;
using UnityEngine;

/// <summary>
/// Small helper on player prefab to provide an actor number for non-Photon checks (and offline).
/// </summary>
public class PlayerIdentity : MonoBehaviourPun
{
    public int actorNumber = -1;

    void Awake()
    {
        if (photonView != null && photonView.Owner != null)
            actorNumber = photonView.Owner.ActorNumber;
    }

    // optional: call this from your player spawn/setup code for offline/local players
    public void Initialize(int actor)
    {
        actorNumber = actor;
    }
}
