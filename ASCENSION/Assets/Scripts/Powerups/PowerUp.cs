using UnityEngine;
using Photon.Pun;

public enum PowerUpType { SpeedBoost, JumpBoost }

public class PowerUp : MonoBehaviourPun
{
    public PowerUpType type;

    [Header("Speed Boost Settings")]
    public float speedMultiplier = 5f;
    public float speedDuration = 5f;

    [Header("Jump Boost Settings")]
    public float jumpMultiplier = 5f;
    public float jumpDuration = 5f;

    [Header("Audio")]
    public AudioClip pickupSound; // assign in Inspector per prefab

    private void OnTriggerEnter(Collider other)
    {
        Debug.Log($"[PowerUp] {name} trigger entered by {other.name}, tag={other.tag}");

        if (!other.CompareTag("Player")) return;

        PhotonView playerPV = other.GetComponent<PhotonView>();
        if (playerPV != null && playerPV.IsMine)
        {
            PlayerMovement3D player = other.GetComponent<PlayerMovement3D>();
            if (player != null)
            {
                if (type == PowerUpType.SpeedBoost)
                {
                    Debug.Log("[PowerUp] Applying Speed Boost");
                    player.ApplySpeedBoost(speedMultiplier, speedDuration);
                }
                else if (type == PowerUpType.JumpBoost)
                {
                    Debug.Log("[PowerUp] Applying Jump Boost");
                    player.ApplyJumpBoost(jumpMultiplier, jumpDuration);
                }

                // 🔊 Play pickup sound locally
                if (pickupSound != null)
                {
                    player.PlayPickupSound(pickupSound);
                }
            }

            // 🔹 Take ownership before destroying
            if (!photonView.IsMine)
            {
                Debug.Log("[PowerUp] Requesting ownership...");
                photonView.TransferOwnership(playerPV.Owner);
            }

            Debug.Log("[PowerUp] Destroying power-up across network...");
            PhotonNetwork.Destroy(gameObject);
        }
    }
}
