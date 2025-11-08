using System;
using UnityEngine;
using UnityEngine.Events;
using TMPro;
using Photon.Pun;
using UnityEngine.SceneManagement;

/// <summary>
/// PlayerHealth: handles local authoritative damage, HUD updates, and (on death) triggers a safe leave-to-lobby flow.
/// Adds a static OnAnyPlayerDied event so other systems can react to deaths reliably across network spawn timing.
/// </summary>
public class PlayerHealth : MonoBehaviourPun
{
    [Header("HP")]
    [Tooltip("Maximum health. Default 150.")]
    public int maxHealth = 150;

    private int originalMaxHealth = -1;
    private int currentHealth;

    [Header("Body parts (assign Colliders)")]
    [Tooltip("Collider used to detect headshots.")]
    public Collider headCollider;
    [Tooltip("Optional: collider used for the body (not strictly required).")]
    public Collider bodyCollider;

    [Header("Events (optional)")]
    public UnityEvent onDamage;
    public UnityEvent onHeal;
    public UnityEvent onDeath;

    [Header("UI (optional)")]
    public TMP_Text hpText;
    [Tooltip("If true, HP text will be hidden on non-owned instances (recommended for screen HUDs).")]
    public bool hideOnRemoteInstances = true;
    [Tooltip("If true, HP text will be hidden on death.")]
    public bool hideOnDeath = false;

    [Header("On Death: Lobby")]
    [Tooltip("Name of the scene to load after leaving the room. Make sure this scene is added to Build Settings.")]
    public string lobbySceneName = "LobbyScene";

    [Tooltip("Optional delay (seconds) before leaving the room on death.")]
    public float leaveDelaySeconds = 0.5f;

    // --- NEW: global static event raised on every client when any player dies ---
    // Parameter: actorNumber of the player who died (may be -1 if unknown)
    public static event Action<int> OnAnyPlayerDied;

    void Awake()
    {
        currentHealth = maxHealth;
        if (originalMaxHealth <= 0) originalMaxHealth = maxHealth;

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
    public void TakeDamage(int amount, bool isHeadHit = false)
    {
        ApplyDamage(amount, isHeadHit);
    }

    /// <summary>
    /// Centralized damage application - owner executes this locally.
    /// </summary>
    public void ApplyDamage(int amount, bool isHeadHit = false)
    {
        currentHealth -= amount;
        currentHealth = Mathf.Clamp(currentHealth, 0, maxHealth);

        Debug.Log($"{name} took {amount} damage{(isHeadHit ? " (HEADSHOT)" : "")}. HP: {Mathf.Max(currentHealth, 0)}/{maxHealth}");

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

    /// <summary>
    /// Apply a heal on the owner.
    /// </summary>
    public void ApplyHeal(int amount)
    {
        if (amount <= 0) return;

        int prevHealth = currentHealth;
        currentHealth += amount;
        currentHealth = Mathf.Clamp(currentHealth, 0, maxHealth);

        Debug.Log($"{name} healed {amount}. HP: {currentHealth}/{maxHealth} (was {prevHealth})");

        UpdateHpText();

        onHeal?.Invoke();

        if (photonView != null && photonView.IsMine)
        {
            try
            {
                photonView.RPC("RPC_BroadcastHealth", RpcTarget.Others, currentHealth);
            }
            catch { /* ignore network issues */ }
        }
    }

    public int GetCurrentHealth() => currentHealth;
    public float GetHealthNormalized() => (float)currentHealth / maxHealth;

    void Die()
    {
        Debug.Log($"{name} died.");
        onDeath?.Invoke();

        if (hideOnDeath && hpText != null)
            hpText.gameObject.SetActive(false);

        // raise static event on this client to notify global listeners
        int actor = GetActorNumberForThisInstance();
        try { OnAnyPlayerDied?.Invoke(actor); } catch { }

        // Owner-specific cleanup / leave flow
        if (photonView != null && photonView.IsMine)
        {
            var participant = GetComponent<AscensionParticipant>();
            if (participant != null)
            {
                participant.OnLocalDeath();
            }

            if (leaveDelaySeconds > 0f)
                Invoke(nameof(StartLeaveRoomFlow), leaveDelaySeconds);
            else
                StartLeaveRoomFlow();
        }

        // Default behavior for the object after death is game-specific.
    }

    private void StartLeaveRoomFlow()
    {
        if (LeaveRoomHandler.Exists)
        {
            Debug.Log("[PlayerHealth] LeaveRoomHandler already exists — invoking Leave now.");
            LeaveRoomHandler.Instance.BeginLeaveRoom(lobbySceneName);
            return;
        }

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
    /// </summary>
    [PunRPC]
    public void RPC_TakeDamage(int amount, bool isHead, int attackerActorNumber)
    {
        if (photonView != null && !photonView.IsMine) return;

        Debug.Log($"[PlayerHealth] RPC_TakeDamage received on actor {PhotonNetwork.LocalPlayer?.ActorNumber ?? -1}: amount={amount}, isHead={isHead}, attacker={attackerActorNumber}");

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
            Debug.LogWarning("[PlayerHealth] No AscensionParticipant on player - ignoring PvP damage by default.");
            return;
        }

        ApplyDamage(amount, isHead);
    }

    /// <summary>
    /// RPC invoked on the owner of this PlayerHealth to apply healing authoritatively.
    /// </summary>
    [PunRPC]
    public void RPC_Heal(int amount, int healerActorNumber)
    {
        if (photonView != null && !photonView.IsMine) return;

        Debug.Log($"[PlayerHealth] RPC_Heal received on actor {PhotonNetwork.LocalPlayer?.ActorNumber ?? -1}: amount={amount}, healer={healerActorNumber}");

        ApplyHeal(amount);
    }

    /// <summary>
    /// Called on remote clients to update health display. Also raises onDeath / static events if health is <= 0.
    /// </summary>
    [PunRPC]
    public void RPC_BroadcastHealth(int newHealth)
    {
        // Only update display on remote clients (owner already updated locally).
        if (photonView != null && photonView.IsMine)
            return;

        currentHealth = newHealth;
        UpdateHpText();

        if (currentHealth <= 0)
        {
            onDeath?.Invoke();

            // Raise static event on this client for the owner's death
            int actor = GetActorNumberForThisInstance();
            try { OnAnyPlayerDied?.Invoke(actor); } catch { }
        }
    }

    /// <summary>
    /// Called by local code (owner) to request taking damage from attackerActorNumber.
    /// Same ascension checks as RPC_TakeDamage.
    /// </summary>
    public void RequestTakeDamageFrom(int attackerActorNumber, int amount, bool isHead = false)
    {
        if (photonView != null && !photonView.IsMine)
            return;

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
            Debug.LogWarning("[PlayerHealth] No AscensionParticipant - ignoring PvP by default.");
            return;
        }

        ApplyDamage(amount, isHead);
    }

    public void RequestHealFrom(int healerActorNumber, int amount)
    {
        if (photonView != null && !photonView.IsMine)
            return;

        ApplyHeal(amount);
    }

    // Strength boost methods omitted for brevity - keep your existing ones if needed
    public void ApplyStrengthBoost(float healthMultiplier)
    {
        if (originalMaxHealth <= 0) originalMaxHealth = maxHealth;

        int newMax = Mathf.Max(1, Mathf.RoundToInt(originalMaxHealth * healthMultiplier));
        maxHealth = newMax;

        currentHealth = maxHealth;
        UpdateHpText();

        Debug.Log($"[PlayerHealth] Strength applied. newMax={maxHealth}, current={currentHealth}");

        if (photonView != null && photonView.IsMine)
        {
            try
            {
                photonView.RPC("RPC_BroadcastMaxHealth", RpcTarget.Others, maxHealth);
                photonView.RPC("RPC_BroadcastHealth", RpcTarget.Others, currentHealth);
            }
            catch { /* ignore network issues */ }
        }
    }

    [PunRPC]
    public void RPC_BroadcastMaxHealth(int newMax)
    {
        if (photonView != null && photonView.IsMine) return;
        maxHealth = Mathf.Max(1, newMax);
        currentHealth = Mathf.Clamp(currentHealth, 0, maxHealth);
        UpdateHpText();
        Debug.Log($"[PlayerHealth] RPC_BroadcastMaxHealth received. newMax={newMax}");
    }

    // Helper to get an actor number representing this PlayerHealth instance
    private int GetActorNumberForThisInstance()
    {
        int actor = -1;
        if (photonView != null && photonView.Owner != null)
            actor = photonView.Owner.ActorNumber;

        // fallback to PlayerIdentity if present
        if (actor < 0)
        {
            var pid = GetComponent<PlayerIdentity>() ?? GetComponentInParent<PlayerIdentity>();
            if (pid != null && pid.actorNumber > 0) actor = pid.actorNumber;
        }

        return actor;
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
