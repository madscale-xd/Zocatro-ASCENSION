using UnityEngine;
using UnityEngine.SceneManagement;
using Photon.Pun;

public class AudioManager : MonoBehaviour
{
    public static AudioManager Instance;

    [Header("Audio Sources")]
    public AudioSource bgmSource;
    public AudioSource sfxSource;

    [Header("Audio Clips")]
    public AudioClip menuBGM;
    public AudioClip buttonPressSFX;

    [Header("Skill SFX")]
    public AudioClip mayhemSkill1SFX;
    public AudioClip mayhemSkill2SFX;
    public AudioClip ivySkill1SFX;
    public AudioClip ivySkill2SFX;
    public AudioClip regaliaSkill1SFX;
    public AudioClip regaliaSkill2SFX;
    public AudioClip sigilSkill1SFX;
    public AudioClip sigilSkill2SFX;
    public AudioClip shootSFX;

    private float masterVolume = 1f;
    private float bgmVolume = 0.2f;
    private float sfxVolume = 1f;

    private void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }
        else Destroy(gameObject);
    }

    private void Start()
    {
        SceneManager.sceneLoaded += OnSceneLoaded;
        PlayBGM(menuBGM);
    }

    private void OnDestroy()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        // Stop BGM when SessionScene loads
        if (scene.name == "SessionScene")
            StopBGM();
    }

    public void PlayBGM(AudioClip clip)
    {
        if (bgmSource.clip == clip && bgmSource.isPlaying) return;

        bgmSource.clip = clip;
        bgmSource.loop = true;
        bgmSource.volume = bgmVolume * masterVolume;
        bgmSource.Play();
    }

    public void StopBGM()
    {
        if (bgmSource.isPlaying)
            bgmSource.Stop();
    }

    public void PlaySFX(AudioClip clip)
    {
        sfxSource.PlayOneShot(clip, sfxVolume * masterVolume);
    }

    // --- General SFX ---
    public void PlayButtonPress()
    {
        PlaySFX(buttonPressSFX);
    }

    // --- Mayhem Skills ---
    public void PlayMayhemSkill1()
    {
        PlaySFX(mayhemSkill1SFX);
    }

    public void PlayMayhemSkill2()
    {
        PlaySFX(mayhemSkill2SFX);
    }

    // --- Ivy Skills ---
    public void PlayIvySkill1()
    {
        PlaySFX(ivySkill1SFX);
    }

    public void PlayIvySkill2()
    {
        PlaySFX(ivySkill2SFX);
    }

    // --- Regalia Skills ---
    public void PlayRegaliaSkill1()
    {
        PlaySFX(regaliaSkill1SFX);
    }

    public void PlayRegaliaSkill2()
    {
        PlaySFX(regaliaSkill2SFX);
    }

    // --- Sigil Skills ---
    public void PlaySigilSkill1()
    {
        PlaySFX(sigilSkill1SFX);
    }

    public void PlaySigilSkill2()
    {
        PlaySFX(sigilSkill2SFX);
    }

     public void PlayshootSFX()
    {
        PlaySFX(shootSFX);
    }

    // --- Volume Control ---
    public void SetMasterVolume(float value)
    {
        masterVolume = value;
        UpdateVolumes();
    }

    public void SetBGMVolume(float value)
    {
        bgmVolume = value;
        UpdateVolumes();
    }

    public void SetSFXVolume(float value)
    {
        sfxVolume = value;
        UpdateVolumes();
    }

    private void UpdateVolumes()
    {
        bgmSource.volume = bgmVolume * masterVolume;
        sfxSource.volume = sfxVolume * masterVolume;
    }

    [PunRPC]
    public void RPC_PlaySFX(string clipName)
    {
        AudioClip clip = GetClipByName(clipName);
        if (clip != null)
            PlaySFX(clip);
    }

    private AudioClip GetClipByName(string name)
    {
        switch (name)
        {
            // --- Mayhem ---
            case "MayhemSkill1": return mayhemSkill1SFX;
            case "MayhemSkill2": return mayhemSkill2SFX;

            // --- Ivy ---
            case "IvySkill1": return ivySkill1SFX;
            case "IvySkill2": return ivySkill2SFX;

            // --- Regalia ---
            case "RegaliaSkill1": return regaliaSkill1SFX;
            case "RegaliaSkill2": return regaliaSkill2SFX;

            // --- Sigil ---
            case "SigilSkill1": return sigilSkill1SFX;
            case "SigilSkill2": return sigilSkill2SFX;

            default: return null;
        }
    }

    public void PlayNetworkedSFX(string clipName)
    {
        AudioClip clip = GetClipByName(clipName);
        if (clip != null)
            PlaySFX(clip);
    }
    
}
