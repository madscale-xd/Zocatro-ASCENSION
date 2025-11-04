using System;
using UnityEngine;
using Photon.Pun;

/// <summary>
/// Stores the shooter identity for a projectile in network-friendly form.
/// Also implements IPunInstantiateMagicCallback so it can parse Photon instantiation data
/// and initialize its metadata on all clients when instantiated via PhotonNetwork.Instantiate.
/// </summary>
public class BulletOwner : MonoBehaviour, IPunInstantiateMagicCallback
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

    /// <summary>
    /// Called by Photon when this object is instantiated via PhotonNetwork.Instantiate.
    /// We read the instantiation data (if present) and initialize fields.
    /// Expected instantiationData format: [0]=ownerActor (int), [1]=headshotMultiplier (float), [2]=outgoingMultiplier (float), [3]=ignoreBody (bool)
    /// </summary>
    public void OnPhotonInstantiate(PhotonMessageInfo info)
    {
        try
        {
            PhotonView pv = GetComponent<PhotonView>();
            object[] data = pv != null ? pv.InstantiationData : null;
            if (data != null && data.Length >= 4)
            {
                // Defensive conversions
                ownerActorNumber = Convert.ToInt32(data[0]);
                headshotMultiplier = Convert.ToSingle(data[1]);
                outgoingDamageMultiplier = Convert.ToSingle(data[2]);
                // If the value was passed as an int (0/1), Convert.ToBoolean still works.
                ignoreBodyHits = Convert.ToBoolean(data[3]);
            }

            if (pv != null)
                ownerViewId = pv.ViewID;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[BulletOwner] OnPhotonInstantiate parse failed: {ex}");
        }
    }
}
