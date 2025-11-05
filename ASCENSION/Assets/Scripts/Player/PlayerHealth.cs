// PlayerHealth.cs
using UnityEngine;
using UnityEngine.Events;
using TMPro;
using Photon.Pun;
using UnityEngine.SceneManagement;

/// <summary>
/// PlayerHealth: handles local authoritative damage, HUD updates, and (on death) triggers a safe leave-to-lobby flow.
/// Integrated with AscensionParticipant to enforce ascension PvP rules: players cannot damage each other unless both are ascendees
/// in the same active rite (ascension zone).
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
    /// This is the place to hook incoming-side modifiers or effects (like Lovers linking).
    /// </summary>
    /// <param name="amount">Damage amount (already adjusted for headshot, if any).</param>
    /// <param name="isHeadHit">True when this damage was from the head collider.</param>
    public void TakeDamage(int amount, bool isHeadHit = false)
    {
        ApplyDamage(amount, isHeadHit);
    }

    /// <summary>
    /// Centralized damage application - good place to add incoming-side modifiers (e.g. Lovers, Devil, etc).
    /// Currently simply subtracts HP, clamps, and invokes events.
    /// </summary>
    public void ApplyDamage(int amount, bool isHeadHit = false)
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
            // Inform AscensionParticipant (if present) about local death so it can report to master
            var participant = GetComponent<AscensionParticipant>();
            if (participant != null)
            {
                participant.OnLocalDeath();
            }

            // Optionally wait a frame or a short delay for death animation/sfx
            if (leaveDelaySeconds > 0f)
                Invoke(nameof(StartLeaveRoomFlow), leaveDelaySeconds);
            else
                StartLeaveRoomFlow();
        }

        // Default behavior for the object after death is game-specific.
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
    /// Attacker must call the target's RPC with attackerActorNumber = PhotonNetwork.LocalPlayer.ActorNumber.
    /// </summary>
    [PunRPC]
    public void RPC_TakeDamage(int amount, bool isHead, int attackerActorNumber)
    {
        // Only the owner should execute damage locally.
        if (photonView != null && !photonView.IsMine) return;

        Debug.Log($"[PlayerHealth] RPC_TakeDamage received on actor {PhotonNetwork.LocalPlayer?.ActorNumber ?? -1}: amount={amount}, isHead={isHead}, attacker={attackerActorNumber}");

        // Enforce ascension rules: if an AscensionParticipant exists, consult it. Otherwise, disallow PvP by default.
        var participant = GetComponent<AscensionParticipant>();
        if (participant != null)
        {
            bool allowed = participant.CanBeDamagedBy(attackerActorNumber);
            if (!allowed)
            {
                Debug.Log($"[PlayerHealth] Damage ignored — ascension rules prevent attacker {attackerActorNumber} from damaging this target.");
                return;
            }
        }
        else
        {
            // If the player prefab does not include AscensionParticipant, treat as not in rite => disallow PvP.
            Debug.LogWarning("[PlayerHealth] No AscensionParticipant on player - ignoring PvP damage by default.");
            return;
        }

        ApplyDamage(amount, isHead);
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

    

    /// <summary>
    /// Called by local code (owner) to request taking damage from attackerActorNumber.
    /// This enforces AscensionParticipant rules the same way RPC_TakeDamage does.
    /// Use for self-damage or any local application of incoming damage.
    /// </summary>
    public void RequestTakeDamageFrom(int attackerActorNumber, int amount, bool isHead = false)
    {
        // Only owner executes local damage application
        if (photonView != null && !photonView.IsMine)
            return;

        // Enforce ascension rules (same check as RPC_TakeDamage)
        var participant = GetComponent<AscensionParticipant>();
        if (participant != null)
        {
            if (!participant.CanBeDamagedBy(attackerActorNumber))
            {
                Debug.Log($"[PlayerHealth] Local damage denied by ascension rules. attacker={attackerActorNumber}");
                return;
            }
        }
        else
        {
            // If no AscensionParticipant on the target, you treat PvP as disallowed by default.
            Debug.LogWarning("[PlayerHealth] No AscensionParticipant - ignoring PvP by default.");
            return;
        }

        ApplyDamage(amount, isHead);
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
            PhotonNetwork.LeaveRoom();
        }
        else
        {
            LoadLobbyNow();
        }
    }

    public override void OnLeftRoom()
    {
        Debug.Log("[LeaveRoomHandler] OnLeftRoom triggered.");
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
        if (PhotonNetwork.IsConnected)
        {
            PhotonNetwork.LoadLevel(lobbySceneToLoad);
        }
        else
        {
            SceneManager.LoadScene(lobbySceneToLoad);
        }

        Destroy(gameObject, 2f);
    }

    public override void OnDisconnected(Photon.Realtime.DisconnectCause cause)
    {
        Debug.LogWarning("[LeaveRoomHandler] OnDisconnected: " + cause);
        if (!string.IsNullOrEmpty(lobbySceneToLoad))
            SceneManager.LoadScene(lobbySceneToLoad);
    }
}
