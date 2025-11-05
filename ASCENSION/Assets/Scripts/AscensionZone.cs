// AscensionZone.cs
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Photon.Pun;
using Photon.Realtime;
using TMPro;

/// <summary>
/// Place on the ascension zone GameObject (with a trigger collider).
/// Clients report their local player's enter/exit to the master. The master decides when to start the rite and notifies all clients.
/// This version only counts entries/exits for objects tagged "Player" (root object).
/// </summary>
[RequireComponent(typeof(Collider), typeof(PhotonView))]
public class AscensionZone : MonoBehaviourPunCallbacks
{
    [Header("Zone")]
    [Tooltip("Number of players required to start the rite (N).")]
    public int requiredPlayersToStart = 2;

    [Tooltip("If true the master will auto-start the rite when enough players are inside.")]
    public bool autoStartWhenFilled = true;

    [Tooltip("Delay after filled before starting (seconds).")]
    public float fillDelayBeforeStart = 0.5f;

    [Header("Visuals/UI (optional)")]
    [Tooltip("Renderer used as the visible perimeter (enable/disable and color).")]
    public Renderer perimeterRenderer;

    [Tooltip("Text label (like 0/N) shown above the zone. Assign a TextMeshPro component.")]
    public TextMeshPro countLabel;

    [Tooltip("Color used for waiting (enough players inside but not active).")]
    public Color waitingColor = new Color(1f, 0.2f, 0.2f, 0.3f);

    [Tooltip("Color used for active rite.")]
    public Color activeColor = new Color(1f, 0f, 0f, 0.45f);

    [Header("Master polling")]
    [Tooltip("How often the master checks for end conditions.")]
    public float masterAlivePollInterval = 0.5f;

    // Master-only state
    private HashSet<int> insidePlayers = new HashSet<int>(); // actorNumbers who reported inside
    private HashSet<int> ascendees = new HashSet<int>();     // actorNumbers who are participants while rite active
    private bool riteActive = false;
    private Coroutine startDelayCoroutine = null;

    private Collider zoneCollider;

    void Awake()
    {
        zoneCollider = GetComponent<Collider>();
        zoneCollider.isTrigger = true;

        if (perimeterRenderer != null)
            perimeterRenderer.gameObject.SetActive(false);

        UpdateCountLabelLocal(0);
    }

    #region Trigger handlers (clients detect local player's entry/exit and notify master)
    private void OnTriggerEnter(Collider other)
    {
        var pv = other.GetComponentInParent<PhotonView>();
        if (pv != null && pv.IsMine)
        {
            // Only consider it a player if the root object (or colliding object) has the Player tag
            if (IsPhotonViewRootTaggedAsPlayer(pv, other))
            {
                // local player's collider entered the zone — notify master
                photonView.RPC(nameof(RPC_ReportEnter), RpcTarget.MasterClient, pv.OwnerActorNr);
            }
        }
    }

    private void OnTriggerExit(Collider other)
    {
        var pv = other.GetComponentInParent<PhotonView>();
        if (pv != null && pv.IsMine)
        {
            if (IsPhotonViewRootTaggedAsPlayer(pv, other))
            {
                photonView.RPC(nameof(RPC_ReportExit), RpcTarget.MasterClient, pv.OwnerActorNr);
            }
        }
    }
    #endregion

    /// <summary>
    /// Helper: determine whether this PhotonView's root (or the collider) is tagged "Player".
    /// This prevents bullets/projectiles from being counted if they don't use the Player tag.
    /// </summary>
    private bool IsPhotonViewRootTaggedAsPlayer(PhotonView pv, Collider coll)
    {
        // Check PV's gameObject
        if (pv != null)
        {
            var pvGO = pv.gameObject;
            if (pvGO != null && pvGO.CompareTag("Player")) return true;

            // Check PV's root (in case PV is on a child)
            var root = pv.transform.root;
            if (root != null && root.CompareTag("Player")) return true;
        }

        // Also check the collider's GameObject and its root (fallback)
        if (coll != null)
        {
            if (coll.gameObject.CompareTag("Player")) return true;
            if (coll.transform.root != null && coll.transform.root.CompareTag("Player")) return true;
        }

        // Not a Player-tagged object
        return false;
    }

    #region RPCs that clients call on master
    [PunRPC]
    void RPC_ReportEnter(int actorNumber, PhotonMessageInfo info)
    {
        if (!PhotonNetwork.IsMasterClient) return;

        if (!insidePlayers.Contains(actorNumber))
        {
            insidePlayers.Add(actorNumber);
            Debug.Log($"[AscensionZone] Master: actor {actorNumber} entered. inside={insidePlayers.Count}");
            photonView.RPC(nameof(RPC_UpdateWaitingVisual), RpcTarget.All, insidePlayers.Count, requiredPlayersToStart);
        }

        if (autoStartWhenFilled && !riteActive && insidePlayers.Count >= requiredPlayersToStart)
        {
            if (startDelayCoroutine != null) StopCoroutine(startDelayCoroutine);
            startDelayCoroutine = StartCoroutine(StartRiteAfterDelay(fillDelayBeforeStart));
        }
    }

    [PunRPC]
    void RPC_ReportExit(int actorNumber, PhotonMessageInfo info)
    {
        if (!PhotonNetwork.IsMasterClient) return;

        if (insidePlayers.Contains(actorNumber))
        {
            insidePlayers.Remove(actorNumber);
            Debug.Log($"[AscensionZone] Master: actor {actorNumber} exited. inside={insidePlayers.Count}");
            photonView.RPC(nameof(RPC_UpdateWaitingVisual), RpcTarget.All, insidePlayers.Count, requiredPlayersToStart);
        }

        if (!riteActive && insidePlayers.Count < requiredPlayersToStart && startDelayCoroutine != null)
        {
            StopCoroutine(startDelayCoroutine);
            startDelayCoroutine = null;
            Debug.Log("[AscensionZone] Master: cancelled scheduled rite start because players left.");
        }
    }
    #endregion

    private IEnumerator StartRiteAfterDelay(float delay)
    {
        float deadline = Time.realtimeSinceStartup + delay;
        while (Time.realtimeSinceStartup < deadline)
        {
            if (insidePlayers.Count < requiredPlayersToStart) yield break;
            yield return null;
        }
        BeginRiteAsMaster();
    }

    private void BeginRiteAsMaster()
    {
        if (!PhotonNetwork.IsMasterClient) return;
        if (riteActive) return;
        if (insidePlayers.Count < 1) return;

        riteActive = true;
        ascendees = new HashSet<int>(insidePlayers); // copy current insiders

        Debug.Log($"[AscensionZone] Master: BEGIN RITE with {ascendees.Count} ascendees: {string.Join(",", ascendees)}");

        int[] asc = new int[ascendees.Count];
        ascendees.CopyTo(asc);

        // Notify everyone which ascendees and which zone (we use this photonView.ViewID as zone id)
        photonView.RPC(nameof(RPC_BeginRiteOnClients), RpcTarget.All, photonView.ViewID, asc);

        // Start monitoring for deaths/leave
        StartCoroutine(MasterMonitorRite());
    }

    private IEnumerator MasterMonitorRite()
    {
        while (riteActive)
        {
            // prune players who left the room
            var toRemove = new List<int>();
            foreach (var a in ascendees)
            {
                if (PhotonNetwork.CurrentRoom == null || !PhotonNetwork.CurrentRoom.Players.ContainsKey(a))
                    toRemove.Add(a);
            }
            foreach (var r in toRemove) ascendees.Remove(r);

            if (ascendees.Count <= 1)
            {
                EndRiteAsMaster();
                yield break;
            }

            yield return new WaitForSeconds(masterAlivePollInterval);
        }
    }

    private void EndRiteAsMaster()
    {
        if (!PhotonNetwork.IsMasterClient) return;
        if (!riteActive) return;

        int winner = -1;
        if (ascendees.Count == 1)
            foreach (var x in ascendees) { winner = x; break; }

        Debug.Log($"[AscensionZone] Master: END RITE. Winner = {winner}");

        photonView.RPC(nameof(RPC_EndRiteOnClients), RpcTarget.All, winner);

        ascendees.Clear();
        riteActive = false;
        startDelayCoroutine = null;
        insidePlayers.Clear();
    }

    #region RPCs master -> clients
    [PunRPC]
    void RPC_UpdateWaitingVisual(int insideCount, int required)
    {
        bool show = insideCount >= required;
        if (perimeterRenderer != null)
        {
            perimeterRenderer.gameObject.SetActive(show);
            SetRendererColor(perimeterRenderer, waitingColor);
        }
        UpdateCountLabelLocal(insideCount);
    }

    [PunRPC]
    void RPC_BeginRiteOnClients(int zoneViewID, int[] ascendeeActorNumbers)
    {
        // show active perimeter
        if (perimeterRenderer != null)
        {
            perimeterRenderer.gameObject.SetActive(true);
            SetRendererColor(perimeterRenderer, activeColor);
        }

        // update local AscensionState mirror
        AscensionState.SetZoneAscendees(zoneViewID, ascendeeActorNumbers);

        // lock local participant if included
        foreach (var a in ascendeeActorNumbers)
        {
            if (PhotonNetwork.LocalPlayer != null && a == PhotonNetwork.LocalPlayer.ActorNumber)
            {
                var participant = FindLocalParticipant();
                if (participant != null) participant.LockToZone(zoneViewID);
                break;
            }
        }

        UpdateCountLabelLocal(ascendeeActorNumbers?.Length ?? 0);
    }

    [PunRPC]
    void RPC_EndRiteOnClients(int winnerActorNumber)
    {
        // hide visual
        if (perimeterRenderer != null) perimeterRenderer.gameObject.SetActive(false);

        // clear local mirror for this zone
        AscensionState.ClearZone(photonView.ViewID);

        // unlock local participant if locked
        var myParticipant = FindLocalParticipant();
        if (myParticipant != null) myParticipant.UnlockFromZone();

        // If I'm the winner, award a tarot locally by calling TarotSelection.AcquireNextTarot
        if (PhotonNetwork.LocalPlayer != null && winnerActorNumber == PhotonNetwork.LocalPlayer.ActorNumber)
        {
            var ts = FindLocalTarotSelection();
            if (ts != null)
            {
                if (ts.AcquireNextTarot(out var card))
                {
                    Debug.Log($"[AscensionZone] You won the rite — automatically acquired {card}.");
                }
                else
                {
                    Debug.Log("[AscensionZone] You won the rite but could not acquire a tarot (deck empty).");
                }
            }
            else
            {
                Debug.LogWarning("[AscensionZone] You won but no local TarotSelection found on your player prefab.");
            }
        }

        UpdateCountLabelLocal(0);
    }
    #endregion

    #region RPC from clients when a locked participant dies (owner calls this)
    [PunRPC]
    void RPC_ReportParticipantDeath(int deadActorNumber, PhotonMessageInfo info)
    {
        if (!PhotonNetwork.IsMasterClient) return;

        if (ascendees.Contains(deadActorNumber))
        {
            ascendees.Remove(deadActorNumber);
            Debug.Log($"[AscensionZone] Master: participant {deadActorNumber} reported dead. remaining = {ascendees.Count}");
        }

        if (ascendees.Count <= 1) EndRiteAsMaster();
    }
    #endregion

    #region Helpers
    private void UpdateCountLabelLocal(int insideCount)
    {
        if (countLabel == null) return;
        countLabel.text = $"{insideCount} / {requiredPlayersToStart}";
    }

    private void SetRendererColor(Renderer r, Color col)
    {
        if (r == null) return;
        // Using material instance so color changes do not affect shared material in the project
        if (r.material == null) return;
        r.material.color = col;
    }

    private AscensionParticipant FindLocalParticipant()
    {
        foreach (var p in GameObject.FindObjectsOfType<AscensionParticipant>(true))
        {
            var pv = p.GetComponentInParent<PhotonView>();
            if (pv != null && pv.IsMine) return p;
        }
        return null;
    }

    private TarotSelection FindLocalTarotSelection()
    {
        foreach (var ts in GameObject.FindObjectsOfType<TarotSelection>(true))
        {
            var pv = ts.GetComponentInParent<PhotonView>();
            if (pv != null && pv.IsMine) return ts;
        }
        return null;
    }

    public override void OnPlayerLeftRoom(Player otherPlayer)
    {
        if (!PhotonNetwork.IsMasterClient) return;
        if (insidePlayers.Remove(otherPlayer.ActorNumber))
        {
            photonView.RPC(nameof(RPC_UpdateWaitingVisual), RpcTarget.All, insidePlayers.Count, requiredPlayersToStart);
        }
        if (ascendees.Remove(otherPlayer.ActorNumber))
        {
            if (ascendees.Count <= 1) EndRiteAsMaster();
        }
    }
    #endregion
}
