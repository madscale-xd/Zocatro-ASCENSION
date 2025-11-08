using UnityEngine;
using Photon.Pun;
using System.Collections.Generic;

/// <summary>
/// Owner-only component: subscribes only to PlayerHealth.OnAnyPlayerDied static event.
/// When any death occurs, evaluates if the local player is the only alive; if so, triggers the win flow.
/// Minimal and no polling.
/// Attach to the player prefab (same object as PhotonView/PlayerHealth).
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(PhotonView))]
public class WinCondition : MonoBehaviour
{
    [Tooltip("Scene name to load when this player is the last alive.")]
    public string winSceneName = "WinScene";

    [Tooltip("Require that local player is alive to trigger the win. If false, the component will still trigger if the single alive actor equals local actor number.")]
    public bool requireLocalAlive = true;

    private bool isOwnerInstance = false;
    private bool winTriggered = false;

    void Awake()
    {
        PhotonView pv = GetComponent<PhotonView>();
        isOwnerInstance = (pv == null) || !PhotonNetwork.InRoom || pv.IsMine;
    }

    void Update()
    {
    }

    void OnEnable()
    {
        if (!isOwnerInstance) return;
        PlayerHealth.OnAnyPlayerDied += HandleAnyPlayerDied;
    }

    void OnDisable()
    {
        if (!isOwnerInstance) return;
        PlayerHealth.OnAnyPlayerDied -= HandleAnyPlayerDied;
    }

    private void HandleAnyPlayerDied(int deadActorNumber)
    {
        if (winTriggered) return;
        EvaluateVictory();
    }

    private void EvaluateVictory()
    {
        if (!PhotonNetwork.InRoom) return;
        if (!isOwnerInstance) return;

        // Build mapping from actorNumber -> PlayerHealth (if present in scene)
        PlayerHealth[] phs = GameObject.FindObjectsOfType<PlayerHealth>(true);
        var actorToHealth = new Dictionary<int, PlayerHealth>();
        foreach (var ph in phs)
        {
            if (ph == null) continue;
            var pv = ph.GetComponent<PhotonView>();
            if (pv != null && pv.Owner != null)
                actorToHealth[pv.Owner.ActorNumber] = ph;
        }

        int aliveCount = 0;
        int lastAliveActor = -1;

        foreach (var p in PhotonNetwork.PlayerList)
        {
            if (p == null) continue;

            if (actorToHealth.TryGetValue(p.ActorNumber, out PlayerHealth ph))
            {
                if (ph != null && ph.GetCurrentHealth() > 0)
                {
                    aliveCount++;
                    lastAliveActor = p.ActorNumber;
                    if (aliveCount > 1) break;
                }
            }
            else
            {
                // No PlayerHealth present for that actor -> treat as not-alive/absent.
            }
        }

        if (aliveCount != 1) return;

        int myActor = PhotonNetwork.LocalPlayer != null ? PhotonNetwork.LocalPlayer.ActorNumber : -1;
        bool myAlive = false;

        var myHealth = GetComponent<PlayerHealth>() ?? GetComponentInChildren<PlayerHealth>(true);
        if (myHealth != null)
            myAlive = myHealth.GetCurrentHealth() > 0;
        else
            myAlive = (lastAliveActor == myActor);

        if (requireLocalAlive)
        {
            if (!myAlive) return;
        }
        else
        {
            if (!myAlive && lastAliveActor != myActor) return;
        }

        TriggerWin();
    }

    private void TriggerWin()
    {
        if (winTriggered) return;
        winTriggered = true;

        Debug.Log($"[WinCondition] Actor {PhotonNetwork.LocalPlayer?.ActorNumber ?? -1} is last alive -> triggering win (loading '{winSceneName}').");

        var existing = GameObject.FindObjectOfType<LeaveRoomHandler>();
        if (existing != null)
        {
            try
            {
                existing.BeginLeaveRoom(winSceneName);
                return;
            }
            catch { /* fallback */ }
        }

        var go = new GameObject("Win_LeaveRoomHandler");
        var handler = go.AddComponent<LeaveRoomHandler>();
        DontDestroyOnLoad(go);
        handler.BeginLeaveRoom(winSceneName);
    }
}