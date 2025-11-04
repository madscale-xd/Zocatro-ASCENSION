// Launcher.cs (LobbyScene)
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Photon.Pun;
using Photon.Realtime;
using ExitGames.Client.Photon; // for Hashtable
using UnityEngine.EventSystems; // <-- needed for clearing selection

namespace Com.MyCompany.MyGame
{
    public class Launcher : MonoBehaviourPunCallbacks
    {
        [SerializeField] private byte maxPlayersPerRoom = 4;
        string gameVersion = "1";
        bool isConnecting;

        [Header("General UI")]
        [SerializeField] private GameObject controlPanel;
        [SerializeField] private GameObject progressLabel;

        [Header("Lobby UI")]
        [SerializeField] private GameObject lobbyPanel;
        [SerializeField] private InputField createRoomInput;
        [SerializeField] private Transform roomListContent;
        [SerializeField] private GameObject roomListEntryPrefab;

        [Header("Room Scene")]
        [SerializeField] private string roomSceneName = "RoomScene";

        private Dictionary<string, GameObject> roomListEntries = new Dictionary<string, GameObject>();
        public static Launcher Instance { get; private set; }

        void Awake()
        {
            PhotonNetwork.AutomaticallySyncScene = true;
            if (Instance == null) Instance = this;
            else Destroy(gameObject);
            TryEnterLobbyFlow();
        }

        void Start()
        {
            if (progressLabel != null) progressLabel.SetActive(true);
            if (controlPanel != null) controlPanel.SetActive(true);
            if (lobbyPanel != null) lobbyPanel.SetActive(false);
        }

        public void Connect() => TryEnterLobbyFlow();

        private void TryEnterLobbyFlow()
        {
            if (progressLabel != null) progressLabel.SetActive(false);
            if (controlPanel != null) controlPanel.SetActive(false);

            PhotonNetwork.GameVersion = gameVersion;

            if (!PhotonNetwork.IsConnected)
            {
                isConnecting = PhotonNetwork.ConnectUsingSettings();
                return;
            }

            if (PhotonNetwork.InLobby)
            {
                OnJoinedLobby();
                return;
            }

            if (PhotonNetwork.IsConnectedAndReady)
            {
                PhotonNetwork.JoinLobby();
            }
            else
            {
                isConnecting = true;
            }
        }

        // ONLY room logic; never touches player name.
        public void CreateRoomFromInput()
        {
            Debug.Log(">>> CreateRoomFromInput CALLED");

            // Force InputField to commit edits by clearing selection (safe if EventSystem exists)
            if (EventSystem.current != null)
            {
                EventSystem.current.SetSelectedGameObject(null);
            }
            else
            {
                Debug.LogWarning("[Launcher] EventSystem.current is null — input focus may not be cleared.");
            }

            // Room name only
            string rn = (createRoomInput != null) ? createRoomInput.text.Trim() : "";
            if (string.IsNullOrEmpty(rn))
            {
                rn = "Room_" + Random.Range(1000, 9999);
            }
            Debug.Log(">>> Room name will be " + rn);

            ExitGames.Client.Photon.Hashtable props = new ExitGames.Client.Photon.Hashtable { { "scene", roomSceneName } };
            RoomOptions options = new RoomOptions
            {
                MaxPlayers = maxPlayersPerRoom,
                CustomRoomProperties = props,
                CustomRoomPropertiesForLobby = new string[] { "scene" }
            };

            PhotonNetwork.CreateRoom(rn, options);
        }

        public void JoinRoomByName(string roomName)
        {
            if (string.IsNullOrEmpty(roomName)) return;
            PhotonNetwork.JoinRoom(roomName);
        }
        //#endregion

        #region Photon Callbacks
        public override void OnConnectedToMaster()
        {
            if (isConnecting)
            {
                PhotonNetwork.JoinLobby();
            }
        }

        public override void OnDisconnected(DisconnectCause cause)
        {
            isConnecting = false;
            if (progressLabel != null) progressLabel.SetActive(false);
            if (controlPanel != null) controlPanel.SetActive(true);
            if (lobbyPanel != null) lobbyPanel.SetActive(false);
            UnlockCursorForLobby();
        }

        public override void OnJoinedLobby()
        {
            isConnecting = false;
            if (progressLabel != null) progressLabel.SetActive(false);
            if (controlPanel != null) controlPanel.SetActive(false);
            if (lobbyPanel != null) lobbyPanel.SetActive(true);
            UnlockCursorForLobby();
        }

        public override void OnRoomListUpdate(List<RoomInfo> roomList)
        {
            foreach (RoomInfo info in roomList)
            {
                if (info.RemovedFromList)
                {
                    if (roomListEntries.ContainsKey(info.Name))
                    {
                        Destroy(roomListEntries[info.Name]);
                        roomListEntries.Remove(info.Name);
                    }
                }
                else
                {
                    if (!roomListEntries.ContainsKey(info.Name))
                    {
                        GameObject entry = Instantiate(roomListEntryPrefab, roomListContent);
                        var comp = entry.GetComponent<RoomListEntry>();
                        if (comp != null) comp.SetInfo(info.Name, info.PlayerCount, info.MaxPlayers);
                        roomListEntries.Add(info.Name, entry);
                    }
                    else
                    {
                        var comp = roomListEntries[info.Name].GetComponent<RoomListEntry>();
                        if (comp != null) comp.SetInfo(info.Name, info.PlayerCount, info.MaxPlayers);
                    }
                }
            }
        }

        public override void OnCreateRoomFailed(short returnCode, string message)
        {
            Debug.LogErrorFormat("Launcher: CreateRoomFailed {0} {1}", returnCode, message);
        }

        public override void OnJoinedRoom()
        {
            if (PhotonNetwork.IsMasterClient)
            {
                string sceneToLoad = roomSceneName;
                if (PhotonNetwork.CurrentRoom.CustomProperties.ContainsKey("scene"))
                {
                    object obj = PhotonNetwork.CurrentRoom.CustomProperties["scene"];
                    if (obj is string s && !string.IsNullOrEmpty(s)) sceneToLoad = s;
                }
                PhotonNetwork.LoadLevel(sceneToLoad);
            }
        }

        public override void OnJoinRoomFailed(short returnCode, string message)
        {
            Debug.LogWarningFormat("Launcher: OnJoinRoomFailed {0} {1}", returnCode, message);
        }

        public override void OnLeftLobby()
        {
            foreach (var kv in roomListEntries) Destroy(kv.Value);
            roomListEntries.Clear();
        }
        #endregion

        private void ShowLobbyUIImmediate()
        {
            if (progressLabel != null) progressLabel.SetActive(false);
            if (controlPanel != null) controlPanel.SetActive(false);
            if (lobbyPanel != null) lobbyPanel.SetActive(true);
            UnlockCursorForLobby();
        }

        // put this inside the Launcher class (near other helpers)
        private void UnlockCursorForLobby()
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;

            // Clear any focused UI element so InputField edits are committed and nothing stays "selected".
            if (EventSystem.current != null)
                EventSystem.current.SetSelectedGameObject(null);
        }
    }
}
