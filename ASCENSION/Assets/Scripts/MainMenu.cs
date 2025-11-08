using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;
using TMPro;

public class MainMenu : MonoBehaviour
{
    [Header("Buttons")]
    public Button startButton;
    public Button exitButton;

    private void Start()
    {
        // Assign button listeners
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
        SceneManager.LoadScene("LobbyScene");
    }

    void ExitGame()
    {
        Debug.Log("Exiting game...");
        Application.Quit();
    }
}
