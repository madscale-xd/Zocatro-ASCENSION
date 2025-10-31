using UnityEngine;
using UnityEngine.Events;
using TMPro;
using Photon.Pun;
using UnityEngine.SceneManagement;

/// <summary>
/// PlayerHealth: handles local authoritative damage, HUD updates, and (on death) triggers a safe leave-to-lobby flow.
/// When HP <= 0 on the owner's client, this will create (or use) a persistent LeaveRoomHandler that calls PhotonNetwork.LeaveRoom()
/// and loads the configured lobby scene when leaving completes.
/// </summary>
public class PlayerHealth : MonoBehaviourPun
{
    [Header("HP")]
    [Tooltip("Maximum health. Default 150.")]
    public int maxHealth = 150;

    private int currentHealth;

    [Header("Body parts (assign Colliders)")]
    [Tooltip("Collider used to detect headshots.")]
    public Collider headCollider;
    [Tooltip("Optional: collider used for the body (not strictly required).")]
    public Collider bodyCollider;

    [Header("Events (optional)")]
    public UnityEvent onDamage;
    public UnityEvent onDeath;

    [Header("UI (optional)")]
    [Tooltip("Optional TextMeshPro element to display current HP (accepts TextMeshProUGUI or TextMeshPro).")]
    public TMP_Text hpText;

    [Tooltip("If true, HP text will be hidden on non-owned instances (recommended for screen HUDs).")]
    public bool hideOnRemoteInstances = true;

    [Tooltip("If true, HP text will be hidden on death.")]
    public bool hideOnDeath = false;

    [Header("On Death: Lobby")]
    [Tooltip("Name of the scene to load after leaving the room. Make sure this scene is added to Build Settings.")]
    public string lobbySceneName = "LobbyScene";

    // Optional delay before initiating leave (useful to play death animation/sound)
    [Tooltip("Optional delay (seconds) before leaving the room on death.")]
    public float leaveDelaySeconds = 0.5f;

    void Awake()
    {
        currentHealth = maxHealth;

        // Hide the prefab's screen-space HUD on remote instances (so only the local player's HUD is visible).
        if (PhotonNetwork.InRoom)
        {
            PhotonView pv = GetComponent<PhotonView>();
            if (pv != null && !pv.IsMine && hideOnRemoteInstances && hpText != null)
            {
                hpText.gameObject.SetActive(false);
            }
        }

        UpdateHpText();
    }

    /// <summary>
    /// Apply damage to this player (local call on the owner).
    /// </summary>
    /// <param name="amount">Damage amount (already adjusted for headshot, if any).</param>
    /// <param name="isHeadHit">True when this damage was from the head collider.</param>
    public void TakeDamage(int amount, bool isHeadHit = false)
    {
        currentHealth -= amount;
        currentHealth = Mathf.Clamp(currentHealth, 0, maxHealth);

        Debug.Log($"{name} took {amount} damage{(isHeadHit ? " (HEADSHOT)" : "")}. HP: {Mathf.Max(currentHealth,0)}/{maxHealth}");

        UpdateHpText();

        onDamage?.Invoke();

        // If this is the owner instance, broadcast the new HP to others so their remote nameplates/HUDs can show it.
        if (photonView != null && photonView.IsMine)
        {
            try
            {
                photonView.RPC("RPC_BroadcastHealth", RpcTarget.Others, currentHealth);
            }
            catch { /* ignore network issues */ }
        }

        if (currentHealth <= 0)
            Die();
    }

    public int GetCurrentHealth() => currentHealth;
    public float GetHealthNormalized() => (float)currentHealth / maxHealth;

    void Die()
    {
        Debug.Log($"{name} died.");
        onDeath?.Invoke();

        if (hideOnDeath && hpText != null)
            hpText.gameObject.SetActive(false);

        // Start the leave-to-lobby flow only on the owning client (owner is authoritative for its own death)
        if (photonView != null && photonView.IsMine)
        {
            // Optionally wait a frame or a short delay for death animation/sfx
            if (leaveDelaySeconds > 0f)
                Invoke(nameof(StartLeaveRoomFlow), leaveDelaySeconds);
            else
                StartLeaveRoomFlow();
        }

        // Default behavior for the object after death is game-specific.
        // We avoid destroying the player object here because we want the network leave flow to run first.
    }

    private void StartLeaveRoomFlow()
    {
        // If already in a leave flow, do nothing
        if (LeaveRoomHandler.Exists)
        {
            Debug.Log("[PlayerHealth] LeaveRoomHandler already exists — invoking Leave now.");
            LeaveRoomHandler.Instance.BeginLeaveRoom(lobbySceneName);
            return;
        }

        // Create a persistent handler that survives scene loads and will handle Photon callbacks reliably.
        GameObject go = new GameObject("LeaveRoomHandler");
        var handler = go.AddComponent<LeaveRoomHandler>();
        DontDestroyOnLoad(go);
        handler.BeginLeaveRoom(lobbySceneName);
    }

    private void UpdateHpText()
    {
        if (hpText == null) return;
        hpText.text = $"{currentHealth}";
    }

    // ---------------- Photon RPCs ----------------

    /// <summary>
    /// RPC invoked on the owner of this PlayerHealth to apply damage authoritatively.
    /// We target this RPC to the player who owns this PhotonView.
    /// </summary>
    [PunRPC]
    public void RPC_TakeDamage(int amount, bool isHead, int attackerActorNumber)
    {
        // Only the owner should execute damage locally.
        if (photonView != null && !photonView.IsMine) return;

        Debug.Log($"[PlayerHealth] RPC_TakeDamage received on actor {PhotonNetwork.LocalPlayer?.ActorNumber ?? -1}: amount={amount}, isHead={isHead}, attacker={attackerActorNumber}");
        TakeDamage(amount, isHead);
    }

    // Sent by the owner to all other clients so they can update remote nameplates/HUDs for this player.
    [PunRPC]
    public void RPC_BroadcastHealth(int newHealth)
    {
        // Only update display on remote clients (owner already updated locally).
        if (photonView != null && photonView.IsMine)
            return;

        currentHealth = newHealth;
        UpdateHpText();

        if (currentHealth <= 0)
            onDeath?.Invoke();
    }
}

/// <summary>
/// LeaveRoomHandler: persistent helper that calls PhotonNetwork.LeaveRoom() and loads the lobby scene when leaving completes.
/// This lives on a DontDestroyOnLoad GameObject so it reliably receives Photon callbacks even if the player prefab is cleaned up.
/// </summary>
public class LeaveRoomHandler : MonoBehaviourPunCallbacks
{
    public static LeaveRoomHandler Instance { get; private set; }
    public static bool Exists => Instance != null;

    private string lobbySceneToLoad = "LobbyScene";
    private bool leaving = false;

    void Awake()
    {
        // Basic singleton enforcement
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    /// <summary>
    /// Begin the leaving flow. Call this once — it will call PhotonNetwork.LeaveRoom() and wait for OnLeftRoom.
    /// If not in a Photon room, it will immediately load the lobby scene.
    /// </summary>
    public void BeginLeaveRoom(string lobbySceneName)
    {
        if (leaving) return;
        leaving = true;
        lobbySceneToLoad = string.IsNullOrEmpty(lobbySceneName) ? "LobbyScene" : lobbySceneName;

        Debug.Log($"[LeaveRoomHandler] BeginLeaveRoom. InRoom={PhotonNetwork.InRoom}, Connected={PhotonNetwork.IsConnected}. LobbyScene='{lobbySceneToLoad}'");

        if (PhotonNetwork.InRoom)
        {
            // Ask Photon to leave the room — OnLeftRoom will be invoked on this handler.
            PhotonNetwork.LeaveRoom();
        }
        else
        {
            // Not in a room — just load the Lobby directly
            LoadLobbyNow();
        }
    }

    public override void OnLeftRoom()
    {
        Debug.Log("[LeaveRoomHandler] OnLeftRoom triggered.");
        // Safely destroy player-owned instantiated objects if any remain
        try
        {
            if (PhotonNetwork.LocalPlayer != null)
            {
                PhotonNetwork.DestroyPlayerObjects(PhotonNetwork.LocalPlayer);
            }
        }
        catch { /* ignore errors */ }

        LoadLobbyNow();
    }

    private void LoadLobbyNow()
    {
        Debug.Log("[LeaveRoomHandler] Loading lobby scene: " + lobbySceneToLoad);
        // If still connected to Photon, use PhotonNetwork.LoadLevel to have Photon optionally manage scene loading/sync.
        if (PhotonNetwork.IsConnected)
        {
            // Use LoadLevel to allow Photon to hold the connection if desired.
            PhotonNetwork.LoadLevel(lobbySceneToLoad);
        }
        else
        {
            SceneManager.LoadScene(lobbySceneToLoad);
        }

        // We keep this handler alive for a short moment in case the scene load triggers OnDisconnected etc.
        // Optionally destroy after a delay
        Destroy(gameObject, 2f);
    }

    public override void OnDisconnected(Photon.Realtime.DisconnectCause cause)
    {
        Debug.LogWarning("[LeaveRoomHandler] OnDisconnected: " + cause);
        // If disconnected unexpectedly, just load the lobby scene locally as fallback
        if (!string.IsNullOrEmpty(lobbySceneToLoad))
            SceneManager.LoadScene(lobbySceneToLoad);
    }
}
