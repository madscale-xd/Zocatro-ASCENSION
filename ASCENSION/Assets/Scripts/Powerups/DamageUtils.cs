using Photon.Pun;
using UnityEngine;

public static class DamageUtils
{
    /// <summary>
    /// Returns true if the target is owned by ownerActor (so damage should be skipped).
    /// If ownerActor < 0 this will also try to compare ownerGameObject (if provided by an OwnedEntity).
    /// </summary>
    public static bool IsSameOwner(GameObject target, int ownerActor, GameObject ownerGameObject = null)
    {
        if (target == null) return false;

        // 1) If target has a PhotonView, compare actor numbers:
        var pv = target.GetComponent<PhotonView>();
        if (pv != null && pv.Owner != null)
        {
            if (ownerActor >= 0 && pv.Owner.ActorNumber == ownerActor) return true;
        }

        // 2) Look for PlayerIdentity (useful offline/local)
        var pid = target.GetComponent<PlayerIdentity>();
        if (pid != null && ownerActor >= 0 && pid.actorNumber == ownerActor) return true;

        // 3) If caller has direct ownerGameObject reference, compare GameObjects
        if (ownerGameObject != null && target == ownerGameObject) return true;

        // not the same owner
        return false;
    }
}
