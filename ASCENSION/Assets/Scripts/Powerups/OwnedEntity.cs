using System;
using Photon.Pun;
using UnityEngine;

/// <summary>
/// OwnedEntity: knows who spawned this object. Works with Photon instantiation data and provides
/// a fallback RPC so ownerActor can be set even if instantiation data didn't arrive for some reason.
/// Ensure this component lives on the same root GameObject that has the PhotonView.
/// </summary>
public class OwnedEntity : MonoBehaviourPun, IPunInstantiateMagicCallback
{
    [Tooltip("Photon actor number of owner (or -1 if unknown/local).")]
    public int ownerActor = -1;

    [Tooltip("Optional direct reference to owner GameObject (helpful for offline/local instantiates).")]
    public GameObject ownerGameObject;

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

        Debug.Log($"[OwnedEntity] OnPhotonInstantiate: viewId={photonView?.ViewID ?? -1}, ownerActor={ownerActor}");
    }

    /// <summary>
    /// Call this in your local fallback initialization routines (offline fallback).
    /// Example: st.InitializeFromSpawner(ownerActor, this.gameObject);
    /// </summary>
    public void InitializeFromSpawner(int actor, GameObject owner = null)
    {
        ownerActor = actor;
        ownerGameObject = owner;
        Debug.Log($"[OwnedEntity] InitializeFromSpawner: ownerActor={ownerActor}, ownerGameObject={(ownerGameObject!=null?ownerGameObject.name:"null")}");
    }

    /// <summary>
    /// RPC fallback: master/creator can call this immediately after instantiate to ensure all clients receive ownerActor.
    /// Using AllBuffered is recommended when sending right after instantiate so late joiners maintain info — adjust as needed.
    /// </summary>
    [PunRPC]
    public void RPC_SetOwnerActor(int actor)
    {
        ownerActor = actor;
        Debug.Log($"[OwnedEntity] RPC_SetOwnerActor called. ownerActor={ownerActor}");
    }
}
