using UnityEngine;

/// <summary>
/// Stores the shooter identity for a projectile in network-friendly form.
/// We store the shooter's ActorNumber (Photon) and optionally the owner's PhotonView ID.
/// </summary>
public class BulletOwner : MonoBehaviour
{
    // ActorNumber of the player who fired this bullet (PhotonNetwork.LocalPlayer.ActorNumber)
    public int ownerActorNumber = -1;

    // Optional: owner's PhotonView ID (for debugging/extra checks)
    public int ownerViewId = -1;
}
