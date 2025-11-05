// AscensionParticipant.cs
using UnityEngine;
using Photon.Pun;

/// <summary>
/// Attach to the player prefab. Handles being locked into a zone (movement disabling / keep-inside),
/// reporting death to the zone master, and answering CanBeDamagedBy queries for local owner.
/// </summary>
[DisallowMultipleComponent]
public class AscensionParticipant : MonoBehaviourPun
{
    private bool isLocked = false;
    private int lockedZoneViewID = -1;
    private Collider lockedZoneCollider = null;

    [Tooltip("Optional movement components to disable while locked (e.g. player controller).")]
    public MonoBehaviour[] componentsToDisableWhileLocked;

    [Tooltip("If we nudge the player slightly inside the zone when locked, use this Y offset.")]
    public float insideOffsetY = 0.15f;

    void LateUpdate()
    {
        // Keep the locked player inside the zone collider if the zone exists.
        if (!isLocked || lockedZoneCollider == null) return;

        Vector3 pos = transform.position;
        Vector3 closest = lockedZoneCollider.ClosestPoint(pos);
        if ((closest - pos).sqrMagnitude > 0.0001f)
        {
            Vector3 newPos = closest + Vector3.up * insideOffsetY;
            transform.position = newPos;
            var rb = GetComponent<Rigidbody>();
            if (rb != null) { rb.velocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
        }
    }

    public void LockToZone(int zoneViewID)
    {
        lockedZoneViewID = zoneViewID;
        isLocked = true;

        var zonePv = PhotonView.Find(zoneViewID);
        if (zonePv != null)
        {
            var zoneCollider = zonePv.GetComponent<Collider>();
            if (zoneCollider != null) lockedZoneCollider = zoneCollider;
        }

        if (componentsToDisableWhileLocked != null)
            foreach (var c in componentsToDisableWhileLocked) if (c != null) c.enabled = false;

        Debug.Log($"[AscensionParticipant] Locked to zone {zoneViewID}");
    }

    public void UnlockFromZone()
    {
        isLocked = false;
        lockedZoneViewID = -1;
        lockedZoneCollider = null;
        if (componentsToDisableWhileLocked != null)
            foreach (var c in componentsToDisableWhileLocked) if (c != null) c.enabled = true;
        Debug.Log("[AscensionParticipant] Unlocked from zone");
    }

    /// <summary>
    /// If owner: report local death to the master for the zone we were locked to.
    /// </summary>
    public void ReportDeathToZoneMaster()
    {
        if (!isLocked || lockedZoneViewID <= 0) return;
        var zonePv = PhotonView.Find(lockedZoneViewID);
        if (zonePv == null) return;
        zonePv.RPC("RPC_ReportParticipantDeath", RpcTarget.MasterClient, photonView.OwnerActorNr);
        Debug.Log($"[AscensionParticipant] Reported death of {photonView.OwnerActorNr} to master for zone {lockedZoneViewID}");
    }

    /// <summary>
    /// Local-owner check: returns true if this (owner) can be damaged by attackerActorNumber.
    /// Rules: must be locked to a zone and attacker must be in the same zone (AscensionState mirror).
    /// </summary>
    public bool CanBeDamagedBy(int attackerActorNumber)
    {
        if (!isLocked || lockedZoneViewID <= 0) return false;
        return AscensionState.AreActorsInSameZone(lockedZoneViewID, photonView.OwnerActorNr, attackerActorNumber);
    }

    /// <summary>
    /// Called locally when this player dies on their owner client.
    /// Performs reporting + local cleanup.
    /// </summary>
    public void OnLocalDeath()
    {
        if (isLocked)
        {
            ReportDeathToZoneMaster();
            UnlockFromZone();
            AscensionState.RemoveActorFromAllZones(photonView.OwnerActorNr);
        }
    }
}
