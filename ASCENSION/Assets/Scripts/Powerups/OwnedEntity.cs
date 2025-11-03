using System;
using Photon.Pun;
using UnityEngine;

/// <summary>
/// Attaches to any summoned/projectile prefab. Stores owner info (actor id + optional owner GameObject).
/// Works both for Photon instantiation (reads photonView.InstantiationData) and local InitializeFromSpawner calls.
/// </summary>
public class OwnedEntity : MonoBehaviourPun, IPunInstantiateMagicCallback
{
    [Tooltip("Photon actor number of owner (or -1 if unknown/local).")]
    public int ownerActor = -1;

    [Tooltip("Optional direct reference to owner GameObject (helpful for offline/local instantiates).")]
    public GameObject ownerGameObject;

    // Photon instantiation callback
    public void OnPhotonInstantiate(PhotonMessageInfo info)
    {
        if (photonView != null && photonView.InstantiationData != null && photonView.InstantiationData.Length > 0)
        {
            try
            {
                ownerActor = Convert.ToInt32(photonView.InstantiationData[0]);
            }
            catch
            {
                ownerActor = -1;
            }
        }
    }

    /// <summary>
    /// Call this in your local fallback initialization routines.
    /// Example: st.InitializeFromSpawner(ownerActor, this.gameObject);
    /// </summary>
    public void InitializeFromSpawner(int actor, GameObject owner = null)
    {
        ownerActor = actor;
        ownerGameObject = owner;
    }
}
