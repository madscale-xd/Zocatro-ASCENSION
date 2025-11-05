// CompactRoomSessionStarter.cs
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Photon.Pun;
using Photon.Realtime;
using ExitGames.Client.Photon;
using Hashtable = ExitGames.Client.Photon.Hashtable;
using TMPro;

/// <summary>
/// Robust room -> session starter:
/// - when countdown expires, ask EVERY client to lock their triad locally (RPC),
/// - wait for all players to report a triad (or timeout),
/// - then master triggers PhotonNetwork.LoadLevel(sessionSceneName).
/// Includes diagnostic logs to help trace issues.
/// </summary>
public class CompactRoomSessionStarter : MonoBehaviourPunCallbacks
{
    [Header("Scene")]
    [SerializeField] string sessionSceneName = "SessionScene";

    [Header("Buttons")]
    [SerializeField] Button startSessionButton;
    [SerializeField] Button cancelCountdownButton;
    [SerializeField] Button setNameButton;

    [Header("Player Name Input")]
    [SerializeField] TMP_InputField playerNameInput;

    [Header("Countdown UI")]
    [SerializeField] GameObject countdownPanel;
    [SerializeField] TextMeshProUGUI countdownText;
    [SerializeField] float uiUpdateInterval = 0.25f;

    [Header("Player List UI")]
    [SerializeField] Transform playerListContainer;
    [SerializeField] GameObject playerNamePrefab;
    [SerializeField] Color hostColor = Color.yellow;
    [SerializeField] Color normalColor = Color.white;

    [Header("Timing / Sync")]
    [Tooltip("How long (seconds) the master waits for all clients to write their triad before forcing load. Increase if clients are slow.")]
    public float triadLockTimeout = 3.0f;

    [Tooltip("How long (seconds) to wait between property checks when waiting for all clients to report triad.")]
    public float triadLockPollInterval = 0.05f;

    const string K_START = "session_countdown_start";
    const string K_DUR = "session_countdown_duration";

    Coroutine masterWatcher;
    Coroutine uiUpdater;

    // tracking reports (master only)
    private HashSet<int> triadReportedActors = new HashSet<int>();
    private object triadReportLock = new object();

    void Start()
    {
        PhotonNetwork.AutomaticallySyncScene = true;

        // Button listeners
        if (startSessionButton) startSessionButton.onClick.AddListener(OnStartClicked);
        if (cancelCountdownButton) cancelCountdownButton.onClick.AddListener(OnCancelClicked);
        if (setNameButton) setNameButton.onClick.AddListener(OnSetNameClicked);

        UpdateButtonInteractables();

        if (uiUpdater != null) StopCoroutine(uiUpdater);
        uiUpdater = StartCoroutine(CountdownUIUpdater());

        if (countdownPanel) countdownPanel.SetActive(false);

        if (playerNameInput != null)
            playerNameInput.text = PhotonNetwork.NickName;

        if (PhotonNetwork.InRoom)
        {
            RefreshCountdownFromRoom();
            RefreshPlayerList();
        }

        TriadTransferManager.EnsureInstance();
    }

    void OnDestroy()
    {
        if (startSessionButton) startSessionButton.onClick.RemoveListener(OnStartClicked);
        if (cancelCountdownButton) cancelCountdownButton.onClick.RemoveListener(OnCancelClicked);
        if (setNameButton) setNameButton.onClick.RemoveListener(OnSetNameClicked);

        if (uiUpdater != null) StopCoroutine(uiUpdater);
    }

    // ---------------------------
    // BUTTON LOGIC
    // ---------------------------
    void OnStartClicked()
    {
        if (!PhotonNetwork.IsMasterClient) return;

        StartCountdown(10f); // Host starts 10s countdown

        // Lock name changes once countdown starts
        if (playerNameInput) playerNameInput.interactable = false;
        if (setNameButton) setNameButton.interactable = false;
    }

    public void OnCancelClicked()
    {
        if (!PhotonNetwork.InRoom) return;
        photonView.RPC(nameof(RPC_CancelCountdown), RpcTarget.All);
    }

    public void OnSetNameClicked()
    {
        if (playerNameInput == null) return;

        string newName = playerNameInput.text.Trim();
        if (string.IsNullOrEmpty(newName)) return;

        Hashtable props = new Hashtable
        {
            { "playerName_" + PhotonNetwork.LocalPlayer.ActorNumber, newName }
        };
        PhotonNetwork.CurrentRoom.SetCustomProperties(props);
        PhotonNetwork.LocalPlayer.NickName = newName;
    }

    // ---------------------------
    // RPCs
    // ---------------------------
    [PunRPC]
    void RPC_CancelCountdown()
    {
        if (PhotonNetwork.IsMasterClient && masterWatcher != null)
        {
            StopCoroutine(masterWatcher);
            masterWatcher = null;
        }

        if (PhotonNetwork.InRoom)
        {
            PhotonNetwork.CurrentRoom.SetCustomProperties(new Hashtable
            {
                { K_START, null },
                { K_DUR, null }
            });
        }

        UpdateCountdownText(0f);
    }

    // Called on ALL clients by master. Each client will lock triad locally and REPORT back to master.
    [PunRPC]
    void RPC_RequestLockTriadLocal()
    {
        Debug.Log("[CompactRoomSessionStarter] RPC_RequestLockTriadLocal received — locking local triad now.");

        // Run local lock routine (synchronous) and get triad
        int[] tri = TriadTransferManager.EnsureInstance().LockTriadLocal();
        int a = tri != null && tri.Length > 0 ? tri[0] : -1;
        int b = tri != null && tri.Length > 1 ? tri[1] : -1;
        int c = tri != null && tri.Length > 2 ? tri[2] : -1;

        // Report back to master. Use MasterClient target so this runs only on the master.
        if (PhotonNetwork.IsConnected && PhotonNetwork.LocalPlayer != null)
        {
            try
            {
                photonView.RPC(nameof(RPC_ReportTriadLocked), RpcTarget.MasterClient, PhotonNetwork.LocalPlayer.ActorNumber, a, b, c);
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[CompactRoomSessionStarter] Failed to RPC_ReportTriadLocked: " + ex.Message);
            }
        }
        else
        {
            Debug.LogWarning("[CompactRoomSessionStarter] Not connected to Photon; cannot report triad to master via RPC.");
        }
    }

    // Master receives this when clients report they locked triads.
    [PunRPC]
    void RPC_ReportTriadLocked(int actorNumber, int t0, int t1, int t2, PhotonMessageInfo info = default)
    {
        if (!PhotonNetwork.IsMasterClient)
        {
            // Only master should process these, but don't crash if someone else receives it.
            return;
        }

        lock (triadReportLock)
        {
            if (!triadReportedActors.Contains(actorNumber))
            {
                triadReportedActors.Add(actorNumber);
                Debug.Log($"[CompactRoomSessionStarter] Received triad report from actor {actorNumber} -> ({t0},{t1},{t2}). ReportedCount={triadReportedActors.Count}");
            }
            else
            {
                Debug.Log($"[CompactRoomSessionStarter] Duplicate triad report received from actor {actorNumber} — ignoring.");
            }
        }
    }

    // ---------------------------
    // COUNTDOWN
    // ---------------------------
    void StartCountdown(float duration)
    {
        if (!PhotonNetwork.IsMasterClient) return;

        double start = PhotonNetwork.Time;
        PhotonNetwork.CurrentRoom?.SetCustomProperties(new Hashtable
        {
            { K_START, start },
            { K_DUR, duration }
        });

        if (masterWatcher != null) StopCoroutine(masterWatcher);
        masterWatcher = StartCoroutine(MasterWatchAndLoad());
    }

    IEnumerator MasterWatchAndLoad()
    {
        while (true)
        {
            if (!PhotonNetwork.InRoom) yield break;

            var props = PhotonNetwork.CurrentRoom.CustomProperties;
            double start = props.ContainsKey(K_START) ? Convert.ToDouble(props[K_START]) : 0.0;
            float dur = props.ContainsKey(K_DUR) ? Convert.ToSingle(props[K_DUR]) : 0f;
            double remaining = (dur > 0f) ? (start + dur - PhotonNetwork.Time) : -1.0;

            if (remaining <= 0.0 && dur > 0f)
            {
                Debug.Log("[CompactRoomSessionStarter] Countdown expired — requesting triad lock on all clients.");

                // Cancel countdown props first (cleans up UI)
                photonView.RPC(nameof(RPC_CancelCountdown), RpcTarget.All);

                // Clear previous reports
                lock (triadReportLock)
                {
                    triadReportedActors.Clear();
                }

                // 1) Ask all clients to lock their triad locally (each client will RPC back to master)
                try
                {
                    photonView.RPC(nameof(RPC_RequestLockTriadLocal), RpcTarget.All);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("[CompactRoomSessionStarter] RPC_RequestLockTriadLocal failed: " + ex.Message);
                }

                // 2) Wait until either all players have reported PROP_TRIAD in their custom properties OR we receive reports from all players OR triadLockTimeout reached.
                float deadline = Time.realtimeSinceStartup + triadLockTimeout;
                bool allReady = false;

                while (Time.realtimeSinceStartup < deadline)
                {
                    // quick check: are we master and in room?
                    if (!PhotonNetwork.InRoom) break;

                    // Compute readiness
                    allReady = true;
                    var players = PhotonNetwork.PlayerList;
                    foreach (var p in players)
                    {
                        bool reported = false;
                        lock (triadReportLock)
                        {
                            reported = triadReportedActors.Contains(p.ActorNumber);
                        }

                        // also consider them ready if they already had the triad in their custom props
                        bool hasProp = p.CustomProperties != null && p.CustomProperties.ContainsKey(PhotonKeys.PROP_TRIAD);

                        if (!reported && !hasProp)
                        {
                            allReady = false;
                            break;
                        }
                    }

                    if (allReady) break;

                    yield return new WaitForSeconds(triadLockPollInterval);
                }

                if (allReady)
                    Debug.Log("[CompactRoomSessionStarter] All players reported triads (or had triad props). Proceeding to scene load.");
                else
                    Debug.LogWarning("[CompactRoomSessionStarter] Not all players reported triads before timeout; proceeding to scene load anyway.");

                // 3) Master triggers the synchronized level load (only master calls this)
                if (PhotonNetwork.IsMasterClient)
                {
                    Debug.Log("[CompactRoomSessionStarter] Master is loading the session scene via PhotonNetwork.LoadLevel.");
                    PhotonNetwork.LoadLevel(sessionSceneName);
                }
                else
                {
                    Debug.LogWarning("[CompactRoomSessionStarter] This client is no longer master; aborting master load. New master should handle load.");
                }

                yield break;
            }

            yield return new WaitForSeconds(0.5f);
        }
    }

    void RefreshCountdownFromRoom()
    {
        if (!PhotonNetwork.InRoom || countdownPanel == null) return;

        var props = PhotonNetwork.CurrentRoom.CustomProperties;
        double start = props.ContainsKey(K_START) ? Convert.ToDouble(props[K_START]) : 0.0;
        float dur = props.ContainsKey(K_DUR) ? Convert.ToSingle(props[K_DUR]) : 0f;

        if (start > 0 && dur > 0)
        {
            countdownPanel.SetActive(true);
            float remaining = Mathf.Max(0f, (float)(start + dur - PhotonNetwork.Time));
            UpdateCountdownText(remaining);
        }
        else
        {
            countdownPanel.SetActive(false);
            UpdateCountdownText(0f);
        }
    }

    IEnumerator CountdownUIUpdater()
    {
        while (true)
        {
            float remaining = PhotonNetwork.InRoom ? GetRemainingSeconds() : 0f;
            UpdateCountdownText(remaining);
            yield return new WaitForSeconds(uiUpdateInterval);
        }
    }

    void UpdateCountdownText(float secondsLeft)
    {
        if (countdownText == null || countdownPanel == null) return;

        if (secondsLeft <= 0f)
        {
            countdownText.text = "";
            countdownPanel.SetActive(false);
            return;
        }

        int secs = Mathf.CeilToInt(secondsLeft);
        countdownText.text = secs.ToString();

        if (!countdownPanel.activeSelf)
            countdownPanel.SetActive(true);
    }

    float GetRemainingSeconds()
    {
        if (!PhotonNetwork.InRoom) return 0f;

        var props = PhotonNetwork.CurrentRoom.CustomProperties;
        double start = props.ContainsKey(K_START) ? Convert.ToDouble(props[K_START]) : 0.0;
        float dur = props.ContainsKey(K_DUR) ? Convert.ToSingle(props[K_DUR]) : 0f;
        double remaining = (dur > 0f) ? (start + dur - PhotonNetwork.Time) : 0.0;
        return Mathf.Max(0f, (float)remaining);
    }

    // ---------------------------
    // PLAYER LIST
    // ---------------------------
    void RefreshPlayerList()
    {
        if (playerListContainer == null || playerNamePrefab == null || PhotonNetwork.CurrentRoom == null) return;

        foreach (Transform child in playerListContainer)
            Destroy(child.gameObject);

        foreach (var kvp in PhotonNetwork.CurrentRoom.Players)
        {
            Player p = kvp.Value;

            GameObject entryObj = Instantiate(playerNamePrefab, playerListContainer);

            var tmp = entryObj.GetComponentInChildren<TextMeshProUGUI>();
            if (tmp != null)
            {
                string customKey = "playerName_" + p.ActorNumber;
                string displayName = PhotonNetwork.CurrentRoom.CustomProperties.ContainsKey(customKey)
                    ? (string)PhotonNetwork.CurrentRoom.CustomProperties[customKey]
                    : p.NickName;
                tmp.text = displayName;
                tmp.color = (p.ActorNumber == PhotonNetwork.MasterClient.ActorNumber) ? hostColor : normalColor;
            }
            else
            {
                var old = entryObj.GetComponentInChildren<UnityEngine.UI.Text>();
                if (old != null)
                {
                    string customKey = "playerName_" + p.ActorNumber;
                    string displayName = PhotonNetwork.CurrentRoom.CustomProperties.ContainsKey(customKey)
                        ? (string)PhotonNetwork.CurrentRoom.CustomProperties[customKey]
                        : p.NickName;
                    old.text = displayName;
                    old.color = (p.ActorNumber == PhotonNetwork.MasterClient.ActorNumber) ? hostColor : normalColor;
                }
            }
        }
    }

    // ---------------------------
    // PHOTON CALLBACKS
    // ---------------------------
    public override void OnJoinedRoom()
    {
        base.OnJoinedRoom();
        RefreshCountdownFromRoom();
        RefreshPlayerList();
        UpdateButtonInteractables();
    }

    public override void OnRoomPropertiesUpdate(Hashtable propertiesThatChanged)
    {
        base.OnRoomPropertiesUpdate(propertiesThatChanged);
        RefreshPlayerList();
    }

    public override void OnPlayerEnteredRoom(Player newPlayer) => RefreshPlayerList();
    public override void OnPlayerLeftRoom(Player otherPlayer) => RefreshPlayerList();

    public override void OnMasterClientSwitched(Player newMaster)
    {
        RefreshPlayerList();
        UpdateButtonInteractables();

        // If I'm the new master and a countdown is active, start the master watcher
        if (PhotonNetwork.IsMasterClient && PhotonNetwork.InRoom)
        {
            var props = PhotonNetwork.CurrentRoom.CustomProperties;
            double start = props.ContainsKey(K_START) ? Convert.ToDouble(props[K_START]) : 0.0;
            float dur = props.ContainsKey(K_DUR) ? Convert.ToSingle(props[K_DUR]) : 0f;
            if (start > 0 && dur > 0f)
            {
                if (masterWatcher != null) StopCoroutine(masterWatcher);
                masterWatcher = StartCoroutine(MasterWatchAndLoad());
            }
        }
    }

    void UpdateButtonInteractables()
    {
        if (startSessionButton) startSessionButton.interactable = PhotonNetwork.IsMasterClient;
        if (cancelCountdownButton) cancelCountdownButton.interactable = PhotonNetwork.InRoom;
        if (setNameButton) setNameButton.interactable = PhotonNetwork.InRoom;
    }
}
