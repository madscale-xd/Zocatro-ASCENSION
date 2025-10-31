using UnityEngine;
using UnityEngine.UI;
using Photon.Pun;

[RequireComponent(typeof(InputField))]
public class PlayerNameInputField : MonoBehaviour
{
    const string playerNamePrefKey = "PlayerName";

    private InputField inputField;

    void Start()
    {
        inputField = GetComponent<InputField>();

        // Load stored name (or blank if none)
        string defaultName = PlayerPrefs.GetString(playerNamePrefKey, "");
        inputField.text = defaultName;

        // Only set PhotonNetwork.NickName if there’s a stored name
        if (!string.IsNullOrEmpty(defaultName))
        {
            PhotonNetwork.NickName = defaultName;
        }
    }

    // Called by InputField OnEndEdit
    public void SetPlayerName(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            Debug.LogError("Player Name is null or empty");
            return;
        }

        PhotonNetwork.NickName = value;
        PlayerPrefs.SetString(playerNamePrefKey, value);
    }
}
