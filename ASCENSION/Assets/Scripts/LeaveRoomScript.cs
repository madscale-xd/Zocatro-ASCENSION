using UnityEngine;
using Photon.Pun;
using UnityEngine.SceneManagement;

public class LeaveRoomScript : MonoBehaviourPunCallbacks
{
    // Attach this script to a scene GameObject (not a Photon-instantiated player prefab).
    // Hook OnLeaveRoomButtonClicked() to Button.OnClick.

    public void OnLeaveRoomButtonClicked()
    {
        Debug.Log("[LeaveRoom] Button clicked. InRoom=" + PhotonNetwork.InRoom);
        if (PhotonNetwork.InRoom)
        {
            Debug.Log("[LeaveRoom] Calling PhotonNetwork.LeaveRoom()");
            PhotonNetwork.LeaveRoom();
        }
        else
        {
            Debug.Log("[LeaveRoom] Not in room - loading LobbyScene directly.");
            SceneManager.LoadScene("LobbyScene");
        }
    }

    public override void OnLeftRoom()
    {
        Debug.Log("[LeaveRoom] OnLeftRoom fired.");
        // Now it's safe to destroy player-owned instantiated objects
        if (PhotonNetwork.LocalPlayer != null)
        {
            Debug.Log("[LeaveRoom] Destroying local player objects now.");
            PhotonNetwork.DestroyPlayerObjects(PhotonNetwork.LocalPlayer);
        }

        // Load the Lobby scene. Use PhotonNetwork.LoadLevel if you are still connected and want Photon to sync scene.
        if (PhotonNetwork.IsConnected)
            PhotonNetwork.LoadLevel("LobbyScene");
        else
            SceneManager.LoadScene("LobbyScene");
    }

    public override void OnDisconnected(Photon.Realtime.DisconnectCause cause)
    {
        Debug.LogWarning("[LeaveRoom] OnDisconnected: " + cause);
    }
}
