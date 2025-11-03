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
    [Tooltip("Multiplier applied to base damage for headshots (e.g. 6 for Justice headshots).")]
    public float headshotMultiplier = 3f;

    [Tooltip("Multiplier applied to base (body) damage for outgoing damage (1 = unchanged).")]
    public float outgoingDamageMultiplier = 1f;

    [Tooltip("If true, body hits should be ignored (no damage).")]
    public bool ignoreBodyHits = false;
}
