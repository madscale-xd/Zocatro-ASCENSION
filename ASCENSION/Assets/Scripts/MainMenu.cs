using UnityEngine;
using UnityEngine.UI;
using UnityEngine.SceneManagement;

public class MainMenu : MonoBehaviour
{
    [Header("Buttons")]
    public Button startButton;
    public Button exitButton;

    [Header("Audio")]
    public AudioClip bgmClip;          // assign your BGM here in Inspector
    private AudioSource bgmSource;

    private void Start()
    {
        // setup buttons
        startButton.onClick.AddListener(StartGame);
        exitButton.onClick.AddListener(ExitGame);

        Cursor.lockState = CursorLockMode.None;
        Cursor.visible = true;

        // 🎵 setup BGM source
        bgmSource = gameObject.AddComponent<AudioSource>();
        bgmSource.clip = bgmClip;
        bgmSource.loop = true;
        bgmSource.volume = 0.7f;
        bgmSource.spatialBlend = 0f; // 2D sound
        bgmSource.Play(); // ✅ play immediately
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
