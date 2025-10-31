using UnityEngine;
using Photon.Pun;
using Photon.Realtime;
using System.Collections;

[RequireComponent(typeof(CharacterController))]
[RequireComponent(typeof(PhotonView))]
[RequireComponent(typeof(AudioSource))] // NEW: require audio for pickup sounds
public class PlayerMovement3D : MonoBehaviourPun, IPunObservable
{
    Animator animator;
    AudioSource audioSource; // NEW: reference to play sounds

    [Header("Movement")]
    public float walkSpeed = 6f;
    public float runSpeed = 9f;
    public float acceleration = 20f; 
    [Tooltip("How quickly remote players interpolate to networked position")]
    public float positionLerpSpeed = 10f;
    [Tooltip("How quickly remote players interpolate to networked rotation")]
    public float rotationLerpSpeed = 10f;

    [Header("Jump & Gravity")]
    public float jumpHeight = 1.6f;
    public float gravity = -9.81f;
    public float groundedStick = -2f;

    [Header("Ground Check (optional)")]
    public LayerMask groundLayers;
    public Transform groundCheck;
    public float groundCheckRadius = 0.4f;

    [Header("Misc")]
    public bool toggleRunWithLeftShift = true;
    public bool lockCursor = true;

    // Internals
    CharacterController cc;
    float currentSpeed;
    Vector3 velocity; 
    bool running = false;
    bool runToggled = false;

    // Power-up multipliers
    public float speedMultiplier = 1f;
    public float jumpMultiplier = 1f;

    // Networked values
    Vector3 networkPosition;
    Quaternion networkRotation;
    float networkVerticalVelocity = 0f;
    bool firstNetworkUpdate = true;

    void Awake()
    {
        animator = GetComponentInChildren<Animator>();
        cc = GetComponent<CharacterController>();
        audioSource = GetComponent<AudioSource>(); // NEW: grab the AudioSource
        currentSpeed = walkSpeed;

        if (groundCheck == null)
        {
            GameObject go = new GameObject("GroundCheck");
            go.transform.SetParent(transform);
            go.transform.localPosition = new Vector3(0f, 0.05f, 0f);
            groundCheck = go.transform;
        }

        networkPosition = transform.position;
        networkRotation = transform.rotation;
    }

    void Start()
    {
        if (lockCursor && photonView.IsMine)
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
    }

    void Update()
    {
        if (photonView.IsMine)
        {
            HandleRunInput();
            MovePlayer();
        }
        else
        {
            // Smooth remote player interpolation
            transform.position = Vector3.Lerp(transform.position, networkPosition, Time.deltaTime * positionLerpSpeed);
            transform.rotation = Quaternion.Slerp(transform.rotation, networkRotation, Time.deltaTime * rotationLerpSpeed);
        }
    }

    void HandleRunInput()
    {
        if (toggleRunWithLeftShift)
        {
            running = Input.GetKey(KeyCode.LeftShift);
        }
        else
        {
            if (Input.GetKeyDown(KeyCode.LeftShift))
                runToggled = !runToggled;
            running = runToggled;
        }
    }

    bool IsGrounded()
    {
        if (cc.isGrounded) return true;
        if (groundCheck != null)
            return Physics.CheckSphere(groundCheck.position, groundCheckRadius, groundLayers, QueryTriggerInteraction.Ignore);
        return false;
    }

    void MovePlayer()
    {
        float h = Input.GetAxisRaw("Horizontal");
        float v = Input.GetAxisRaw("Vertical");

        Vector3 input = new Vector3(h, 0f, v);
        input = Vector3.ClampMagnitude(input, 1f);

        Vector3 desiredMove = transform.TransformDirection(input);

        float targetSpeed = (running ? runSpeed : walkSpeed) * speedMultiplier;
        currentSpeed = Mathf.MoveTowards(currentSpeed, targetSpeed * input.magnitude, acceleration * Time.deltaTime);

        if (IsGrounded())
        {
            if (velocity.y < 0)
                velocity.y = groundedStick;

            if (Input.GetButtonDown("Jump"))
            {
                velocity.y = Mathf.Sqrt(jumpHeight * jumpMultiplier * -2f * gravity);
            }
        }
        else
        {
            velocity.y += gravity * Time.deltaTime;
        }

        Vector3 move = desiredMove * currentSpeed + Vector3.up * velocity.y;
        cc.Move(move * Time.deltaTime);

        float moveMagnitude = input.magnitude;
        if (animator) animator.SetFloat("Speed", moveMagnitude, 0.1f, Time.deltaTime * 10f);
    }

    // === Power-up Methods ===
    public void ApplySpeedBoost(float multiplier, float duration)
    {
        StopCoroutine("SpeedBoostRoutine");
        StartCoroutine(SpeedBoostRoutine(multiplier, duration));
    }

    IEnumerator SpeedBoostRoutine(float multiplier, float duration)
    {
        speedMultiplier = multiplier;
        yield return new WaitForSeconds(duration);
        speedMultiplier = 1f;
    }

    public void ApplyJumpBoost(float multiplier, float duration)
    {
        StopCoroutine("JumpBoostRoutine");
        StartCoroutine(JumpBoostRoutine(multiplier, duration));
    }

    IEnumerator JumpBoostRoutine(float multiplier, float duration)
    {
        jumpMultiplier = multiplier;
        yield return new WaitForSeconds(duration);
        jumpMultiplier = 1f;
    }

    // === NEW: Play pickup sound ===
    public void PlayPickupSound(AudioClip clip)
    {
        if (photonView.IsMine && audioSource != null && clip != null)
        {
            audioSource.PlayOneShot(clip);
        }
    }

    // === Networking ===
    public void OnPhotonSerializeView(PhotonStream stream, PhotonMessageInfo info)
    {
        if (stream.IsWriting)
        {
            stream.SendNext(transform.position);
            stream.SendNext(transform.rotation);
            stream.SendNext(velocity.y);
        }
        else
        {
            networkPosition = (Vector3)stream.ReceiveNext();
            networkRotation = (Quaternion)stream.ReceiveNext();
            networkVerticalVelocity = (float)stream.ReceiveNext();

            if (firstNetworkUpdate)
            {
                transform.position = networkPosition;
                transform.rotation = networkRotation;
                firstNetworkUpdate = false;
            }
        }
    }

    void OnDrawGizmosSelected()
    {
        if (groundCheck != null)
        {
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere(groundCheck.position, groundCheckRadius);
        }

        Gizmos.color = Color.cyan;
        Gizmos.DrawLine(transform.position, transform.position + transform.forward * 1.5f);
    }
}
