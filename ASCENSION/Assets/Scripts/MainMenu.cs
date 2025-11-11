using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;

public class MainMenu : MonoBehaviour
{
    [Header("Buttons")]
    public Button startButton;
    public Button exitButton;

    private void Start()
    {
        startButton.onClick.AddListener(StartGame);
        exitButton.onClick.AddListener(ExitGame);

        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }

    private void Update()
    {
        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;
    }

    void StartGame()
    {
        AudioManager.Instance.PlayButtonPress();
        SceneManager.LoadScene("LobbyScene");
    }

    void ExitGame()
    {
        AudioManager.Instance.PlayButtonPress();
        Debug.Log("Exiting game...");
        Application.Quit();
    }
}
