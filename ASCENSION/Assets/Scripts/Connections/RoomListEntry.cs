using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// UI helper for a single room in the lobby list.
/// Prefab should have: Text roomNameText, Text playerCountText, Button joinButton.
/// </summary>
public class RoomListEntry : MonoBehaviour
{
    [SerializeField] private Text roomNameText;
    [SerializeField] private Text playerCountText;
    [SerializeField] private Button joinButton;

    private string roomName;

    public void SetInfo(string name, int playerCount, int maxPlayers)
    {
        roomName = name;
        if (roomNameText != null) roomNameText.text = name;
        if (playerCountText != null) playerCountText.text = string.Format("{0}/{1}", playerCount, maxPlayers);

        if (joinButton != null)
        {
            joinButton.onClick.RemoveAllListeners();
            joinButton.onClick.AddListener(() => OnJoinClicked());
        }
    }

    private void OnJoinClicked()
    {
        // call launcher to join
        Com.MyCompany.MyGame.Launcher.Instance.JoinRoomByName(roomName);
    }
}
