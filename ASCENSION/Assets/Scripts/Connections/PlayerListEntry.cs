using UnityEngine;
using UnityEngine.UI;

public class PlayerListEntry : MonoBehaviour
{
    [SerializeField] private Image avatarImage;
    [SerializeField] private Text nameText;

    public void SetInfo(string playerName, int actorNumber)
    {
        if (nameText != null) nameText.text = playerName;
        if (avatarImage != null)
        {
            float hue = (actorNumber % 10) / 10f;
            Color c = Color.HSVToRGB(hue, 0.6f, 0.9f);
            avatarImage.color = c;
        }
    }
}
