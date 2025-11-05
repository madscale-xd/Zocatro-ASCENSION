// CompactRoomSessionStarter.cs
using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using Photon.Pun;
using Photon.Realtime;
using ExitGames.Client.Photon;
using Hashtable = ExitGames.Client.Photon.Hashtable;
using TMPro;

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

    const string K_START = "session_countdown_start";
    const string K_DUR = "session_countdown_duration";

    Coroutine masterWatcher;
    Coroutine uiUpdater;

    void Start()
    {
        // Button listeners
        if (startSessionButton) startSessionButton.onClick.AddListener(OnStartClicked);
        if (cancelCountdownButton) cancelCountdownButton.onClick.AddListener(OnCancelClicked);
        if (setNameButton) setNameButton.onClick.AddListener(OnSetNameClicked);

        UpdateButtonInteractables();

        // Countdown UI updater
        if (uiUpdater != null) StopCoroutine(uiUpdater);
        uiUpdater = StartCoroutine(CountdownUIUpdater());

        if (countdownPanel) countdownPanel.SetActive(false);

        // Init input field
        if (playerNameInput != null)
            playerNameInput.text = PhotonNetwork.NickName;

        if (PhotonNetwork.InRoom)
        {
            RefreshCountdownFromRoom();
            RefreshPlayerList();
        }

        // Ensure TriadTransferManager exists (so host can call it)
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

        // Anyone cancels countdown via RPC
        photonView.RPC(nameof(RPC_CancelCountdown), RpcTarget.All);
    }

    public void OnSetNameClicked()
    {
        if (playerNameInput == null) return;

        string newName = playerNameInput.text.Trim();
        if (string.IsNullOrEmpty(newName)) return;

        // Update room properties (keeps backward compatibility with your existing UI)
        Hashtable props = new Hashtable
        {
            { "playerName_" + PhotonNetwork.LocalPlayer.ActorNumber, newName }
        };
        PhotonNetwork.CurrentRoom.SetCustomProperties(props);

        // Also update local NickName (optional)
        PhotonNetwork.LocalPlayer.NickName = newName;
    }

    // ---------------------------
    // RPCs
    // ---------------------------
    [PunRPC]
    void RPC_CancelCountdown()
    {
        // Stop host watcher if master
        if (PhotonNetwork.IsMasterClient && masterWatcher != null)
        {
            StopCoroutine(masterWatcher);
            masterWatcher = null;
        }

        // Clear countdown properties (remove keys)
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

    [PunRPC]
    void RPC_UpdatePlayerName(int actorNumber, string newName)
    {
        if (PhotonNetwork.CurrentRoom.Players.TryGetValue(actorNumber, out Player player))
        {
            player.NickName = newName;
            RefreshPlayerList();
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
                // Cancel countdown props first (cleans up UI)
                photonView.RPC(nameof(RPC_CancelCountdown), RpcTarget.All);

                // LOCK TRIAD FROM CURRENT (ROOM) SCENE and LOAD sessionSceneName
                TriadTransferManager.EnsureInstance().LockTriadAndLoad(sessionSceneName, true);

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
