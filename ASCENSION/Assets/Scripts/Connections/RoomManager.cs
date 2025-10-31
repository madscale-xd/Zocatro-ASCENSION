// RoomManager.cs (RoomScene)
using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine.SceneManagement;

public class RoomManager : MonoBehaviourPunCallbacks
{
    [Header("UI")]
    [SerializeField] private Text roomNameText;
    [SerializeField] private Transform playerListContent; // Content RectTransform in Scroll View
    [SerializeField] private GameObject playerListEntryPrefab;
    [SerializeField] private Button leaveButton;

    // NEW: UI Text that shows (current / max)
    [Header("Player Count Display")]
    [SerializeField] private Text playerCountText;

    [Header("Scene Names")]
    [Tooltip("Name of the Lobby scene that contains your Launcher. Set this to your actual lobby scene name in Build Settings.")]
    [SerializeField] private string lobbySceneName = "LobbyScene";

    void Start()
    {
        if (roomNameText != null && PhotonNetwork.CurrentRoom != null)
        {
            roomNameText.text = PhotonNetwork.CurrentRoom.Name;
        }

        if (leaveButton != null)
            leaveButton.onClick.AddListener(OnLeaveClicked);

        RefreshPlayerList();
        UpdatePlayerCountDisplay();
    }

    private void OnDestroy()
    {
        if (leaveButton != null)
            leaveButton.onClick.RemoveListener(OnLeaveClicked);
    }

    private void OnLeaveClicked()
    {
        Debug.Log($"RoomManager: Leave button clicked. InRoom={PhotonNetwork.InRoom}, IsConnected={PhotonNetwork.IsConnected}, InLobby={PhotonNetwork.InLobby}");

        // Prevent double clicks
        if (leaveButton != null) leaveButton.interactable = false;

        // Leave the room (this triggers OnLeftRoom)
        if (PhotonNetwork.InRoom)
        {
            PhotonNetwork.LeaveRoom();
        }
        else
        {
            // Not in a room — just load lobby scene (Launcher will connect/join)
            LoadLobbySceneIfNeeded();
        }
    }

    public override void OnLeftRoom()
    {
        Debug.Log("RoomManager: OnLeftRoom callback received. IsConnected=" + PhotonNetwork.IsConnected + ", InLobby=" + PhotonNetwork.InLobby);
    
        // Update UI before leaving
        UpdatePlayerCountDisplay();

        // We DO NOT call PhotonNetwork.JoinLobby() here (it races if client is still connecting).
        // Instead, load the Lobby scene and let Launcher manage the join/reconnect logic.
        LoadLobbySceneIfNeeded();
    }

    private void LoadLobbySceneIfNeeded()
    {
        if (string.IsNullOrEmpty(lobbySceneName))
        {
            Debug.LogWarning("RoomManager: lobbySceneName is empty. Set lobbySceneName in the inspector to your Lobby scene name.");
            return;
        }

        if (SceneManager.GetActiveScene().name == lobbySceneName)
        {
            Debug.Log("RoomManager: already in lobby scene.");
            return;
        }

        Debug.Log($"RoomManager: Loading lobby scene '{lobbySceneName}' (so Launcher will exist there).");
        SceneManager.LoadScene(lobbySceneName);
    }

    public override void OnPlayerEnteredRoom(Player newPlayer)
    {
        Debug.LogFormat("RoomManager: Player entered {0}", newPlayer.NickName);
        RefreshPlayerList();
        UpdatePlayerCountDisplay();
    }

    public override void OnPlayerLeftRoom(Player otherPlayer)
    {
        Debug.LogFormat("RoomManager: Player left {0}", otherPlayer.NickName);
        RefreshPlayerList();
        UpdatePlayerCountDisplay();
    }

    private void RefreshPlayerList()
    {
        if (playerListContent == null || playerListEntryPrefab == null) return;

        // Clear existing children
        for (int i = playerListContent.childCount - 1; i >= 0; i--)
        {
            Destroy(playerListContent.GetChild(i).gameObject);
        }

        if (PhotonNetwork.CurrentRoom == null) return;

        foreach (var kv in PhotonNetwork.CurrentRoom.Players)
        {
            Player p = kv.Value;
            // instantiate as child and do NOT keep world position (safer for UI)
            GameObject pe = Instantiate(playerListEntryPrefab, playerListContent, false);
            var ple = pe.GetComponent<PlayerListEntry>();
            if (ple != null) ple.SetInfo(p.NickName, p.ActorNumber);
        }

        // after rebuild, ensure the player count reflects the current numbers
        UpdatePlayerCountDisplay();
    }

    // NEW: Updates the player count text in the UI. Format: "X / Y"
    private void UpdatePlayerCountDisplay()
    {
        if (playerCountText == null) return;

        if (PhotonNetwork.CurrentRoom != null)
        {
            int current = PhotonNetwork.CurrentRoom.PlayerCount; // current players in room
            int max = PhotonNetwork.CurrentRoom.MaxPlayers;      // max players allowed
            playerCountText.text = $"{current} / {max}";
        }
        else
        {
            // Not in a room (safety)
            playerCountText.text = $"0 / {0}";
        }
    }
}
