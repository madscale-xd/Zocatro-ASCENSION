using Photon.Pun;
using Photon.Realtime;
using UnityEngine;

namespace Com.MyCompany.MyGame
{
    public class GameManager : MonoBehaviourPunCallbacks
    {
        public override void OnLeftRoom()
        {
            // Instead of loading a scene, return to Launcher lobby UI
            if (Launcher.Instance != null)
            {
                // Launcher.OnLeftRoom handles UI; just ensure lobby is shown
                // (Launcher.OnLeftRoom is called by PUN before this callback typically)
            }
        }

        // Keep this public so UI can call it
        
        public void LeaveRoom()
        {
            if (PhotonNetwork.InRoom)
            {
                Debug.Log("Leaving room...");
                PhotonNetwork.LeaveRoom();
            }
            else
            {
                Debug.LogWarning("LeaveRoom() called but not currently in a room.");
            }
        }

        public override void OnPlayerEnteredRoom(Player other)
        {
            Debug.LogFormat("OnPlayerEnteredRoom() {0}", other.NickName);
            // if we are master client, we could do additional logic
            if (PhotonNetwork.IsMasterClient)
            {
                Debug.LogFormat("OnPlayerEnteredRoom IsMasterClient {0}", PhotonNetwork.IsMasterClient);
            }
        }

        public override void OnPlayerLeftRoom(Player other)
        {
            Debug.LogFormat("OnPlayerLeftRoom() {0}", other.NickName);
            if (PhotonNetwork.IsMasterClient)
            {
                Debug.LogFormat("OnPlayerLeftRoom IsMasterClient {0}", PhotonNetwork.IsMasterClient);
            }
        }
    }
}
