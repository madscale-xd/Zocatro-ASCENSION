using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Photon.Pun;

[RequireComponent(typeof(SphereCollider))]
[RequireComponent(typeof(Rigidbody))]
public class ExpandingPulseBehavior : MonoBehaviourPun
{
    [Header("Expansion")]
    [Tooltip("Maximum radius the pulse will reach (world units).")]
    public float targetRadius = 16f;

    [Tooltip("How many seconds the pulse will expand (default 2s).")]
    public float expandDuration = 3f;

    [Tooltip("After expansion, hold the final radius this long before destruction.")]
    public float holdTime = 0.5f;

    [Header("Effect")]
    [Tooltip("Root duration applied to affected players.")]
    public float rootDuration = 1.5f;
    [Tooltip("If true, also tell victims to StopHealing for the root duration.")]
    public bool stopHealing = true;

    [Header("Owner / safety")]
    public int ownerActorNumber = -1;
    public GameObject ownerGameObject;

    [Header("Visual / collider options")]
    [Tooltip("If true, only the visual child is scaled and the SphereCollider is left at colliderFixedRadius (cosmetic only).")]
    public bool visualOnly = false;

    [Tooltip("When visualOnly==true, keep the SphereCollider radius at this value (local units).")]
    public float colliderFixedRadius = 0.5f;

    [Tooltip("Optional: assign the visual transform (child) to scale independently. If null, script will attempt to find a child MeshRenderer.")]
    public Transform visualTarget;

    [Header("Misc")]
    public bool networkVisual = false;

    // internals
    SphereCollider sphere;
    Rigidbody rb;

    HashSet<int> damagedActorNumbers = new HashSet<int>();
    HashSet<int> damagedInstanceIds = new HashSet<int>();

    Collider[] ownerColliders;
    Collider[] myColliders;

    Vector3 visualBaseScale = Vector3.one;
    float visualBaseRadius = 0.5f; // default assumed radius in the visual (Unity Sphere primitive has radius 0.5)

    bool expanding = true;
    float elapsed = 0f;

    void Awake()
    {
        // components
        sphere = GetComponent<SphereCollider>();
        if (sphere == null) sphere = gameObject.AddComponent<SphereCollider>();
        sphere.isTrigger = true;

        rb = GetComponent<Rigidbody>();
        if (rb == null) rb = gameObject.AddComponent<Rigidbody>();
        rb.isKinematic = true;
        rb.useGravity = false;

        myColliders = GetComponentsInChildren<Collider>(true);

        // find visual target if not assigned
        if (visualTarget == null)
        {
            // try to find child MeshRenderer or a child named "Visual"
            var named = transform.Find("Visual");
            if (named != null) visualTarget = named;
            else
            {
                var mr = GetComponentInChildren<MeshRenderer>(true);
                if (mr != null) visualTarget = mr.transform;
            }
        }

        if (visualTarget != null)
        {
            visualBaseScale = visualTarget.localScale;
            // if you know the mesh's base radius (unity sphere primitive uses 0.5), you can set visualBaseRadius accordingly.
            // We assume 0.5 here (Unity sphere primitive default radius).
            visualBaseRadius = 0.5f;
        }

        // default: place collider radius small initially
        if (visualOnly)
            sphere.radius = colliderFixedRadius;
        else
            sphere.radius = 0.01f;

        // Read instantiation data (only positive values override prefab defaults)
        var pv = GetComponent<PhotonView>();
        if (pv != null && pv.InstantiationData != null)
        {
            try
            {
                object[] data = pv.InstantiationData;
                if (data.Length >= 1 && int.TryParse(data[0].ToString(), out int parsedOwner))
                    ownerActorNumber = parsedOwner;

                if (data.Length >= 2 && float.TryParse(data[1].ToString(), out float parsedRadius) && parsedRadius > 0f)
                    targetRadius = parsedRadius;

                if (data.Length >= 3 && float.TryParse(data[2].ToString(), out float parsedRoot) && parsedRoot > 0f)
                    rootDuration = parsedRoot;

                if (data.Length >= 4 && int.TryParse(data[3].ToString(), out int parsedStop))
                    stopHealing = (parsedStop != 0);

                if (data.Length >= 5 && float.TryParse(data[4].ToString(), out float parsedExpand) && parsedExpand > 0f)
                    expandDuration = parsedExpand;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"ExpandingPulseBehavior: bad instantiation data: {ex.Message}");
            }
        }

        // owner colliders lookup (existing logic)
        if (ownerActorNumber >= 0)
        {
            var allPVs = FindObjectsOfType<PhotonView>();
            foreach (var p in allPVs)
            {
                if (p.Owner != null && p.Owner.ActorNumber == ownerActorNumber)
                {
                    ownerColliders = p.GetComponentsInChildren<Collider>(true);
                    break;
                }
            }
        }

        var oe = GetComponent<OwnedEntity>();
        if (oe != null && oe.ownerGameObject != null)
        {
            ownerGameObject = oe.ownerGameObject;
            ownerActorNumber = (oe.ownerActor >= 0) ? oe.ownerActor : ownerActorNumber;
            ownerColliders = ownerGameObject.GetComponentsInChildren<Collider>(true);
        }

        if (ownerColliders != null && ownerColliders.Length > 0 && myColliders != null)
        {
            foreach (var a in myColliders)
                foreach (var b in ownerColliders)
                    if (a != null && b != null) Physics.IgnoreCollision(a, b, true);
            StartCoroutine(ReenableOwnerCollisionsAfter(0.18f));
        }

        Debug.Log($"[Pulse Awake] visualOnly={visualOnly} colliderFixedRadius={colliderFixedRadius} targetRadius={targetRadius} expandDuration={expandDuration}");
    }

    IEnumerator ReenableOwnerCollisionsAfter(float delay)
    {
        yield return new WaitForSeconds(delay);
        if (ownerColliders == null || myColliders == null) yield break;
        foreach (var a in myColliders)
            foreach (var b in ownerColliders)
                if (a != null && b != null) Physics.IgnoreCollision(a, b, false);
    }

    void Start()
    {
        StartCoroutine(ExpandAndDestroy());
    }

    IEnumerator ExpandAndDestroy()
    {
        expanding = true;
        elapsed = 0f;

        // Expand from small -> targetRadius over expandDuration
        while (elapsed < expandDuration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / Mathf.Max(0.0001f, expandDuration));
            float r = Mathf.Lerp(0.01f, targetRadius, t);

            if (!visualOnly)
            {
                // update collider radius to reflect effect area
                sphere.radius = r;
            }
            else
            {
                // keep collider fixed
                sphere.radius = colliderFixedRadius;
            }

            // scale only the visual target (so collider is unaffected)
            if (visualTarget != null)
            {
                // visualBaseRadius is assumed base radius (0.5 for default Unity sphere). If your visual differs, adjust.
                float scaleFactor = (r / visualBaseRadius);
                visualTarget.localScale = Vector3.Scale(visualBaseScale, Vector3.one * scaleFactor);
            }
            else
            {
                // If no visual target assigned, DO NOT scale root (to avoid changing collider).
                // But we still set transform.localScale lightly if you absolutely must - here we avoid it.
            }

            yield return null;
        }

        // hold final radius for a short time
        expanding = false;

        // set final values
        if (!visualOnly) sphere.radius = targetRadius;
        else sphere.radius = colliderFixedRadius;

        if (visualTarget != null)
        {
            float finalScaleFactor = (targetRadius / visualBaseRadius);
            visualTarget.localScale = Vector3.Scale(visualBaseScale, Vector3.one * finalScaleFactor);
        }

        yield return new WaitForSeconds(holdTime);
        DestroySelfNetworkSafe();
    }

    void OnTriggerEnter(Collider other)
    {
        if (other == null) return;
        if (other.gameObject == gameObject) return;
        if (DamageUtils.IsSameOwner(other.gameObject, ownerActorNumber, ownerGameObject)) return;

        var targetPv = other.GetComponentInParent<PhotonView>();
        var ps = other.GetComponentInParent<PlayerStatus>();
        var ph = other.GetComponentInParent<PlayerHealth>();

        if (targetPv != null && targetPv.Owner != null && ps != null)
        {
            int actorNum = targetPv.Owner.ActorNumber;
            if (damagedActorNumbers.Contains(actorNum)) return;

            try
            {
                targetPv.RPC("RPC_ApplyRoot", targetPv.Owner, rootDuration, ownerActorNumber);
                if (stopHealing)
                    targetPv.RPC("RPC_StopHealing", targetPv.Owner, rootDuration, ownerActorNumber);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"ExpandingPulseBehavior: RPC to actor {actorNum} failed: {ex.Message}. Falling back to local call.");
                ps.ApplyRootLocal(rootDuration);
                if (stopHealing) ps.StopHealingLocal(rootDuration);
            }

            damagedActorNumbers.Add(actorNum);
            return;
        }

        if (ps != null)
        {
            int id = ps.gameObject.GetInstanceID();
            if (damagedInstanceIds.Contains(id)) return;
            ps.ApplyRootLocal(rootDuration);
            if (stopHealing) ps.StopHealingLocal(rootDuration);
            damagedInstanceIds.Add(id);
            return;
        }

        if (ph != null)
        {
            int id = ph.gameObject.GetInstanceID();
            if (damagedInstanceIds.Contains(id)) return;
            ph.SendMessage("ApplyRoot", rootDuration, SendMessageOptions.DontRequireReceiver);
            if (stopHealing) ph.SendMessage("StopHealing", rootDuration, SendMessageOptions.DontRequireReceiver);
            damagedInstanceIds.Add(id);
            return;
        }
    }

    void DestroySelfNetworkSafe()
    {
        var pv = GetComponent<PhotonView>();
        if (PhotonNetwork.InRoom && pv != null && pv.IsMine)
        {
            try { PhotonNetwork.Destroy(gameObject); return; } catch { }
        }
        Destroy(gameObject);
    }

    public void InitializeFromSpawner(int ownerActor, GameObject ownerGO, float radius, float rootDur, bool stopHeal, float expandDur = -1f)
    {
        ownerActorNumber = ownerActor;
        ownerGameObject = ownerGO;

        if (radius > 0f) targetRadius = radius;
        if (rootDur > 0f) rootDuration = rootDur;
        stopHealing = stopHeal;
        if (expandDur > 0f) expandDuration = expandDur;

        // Set initial collider radius depending on visualOnly
        if (visualOnly) sphere.radius = colliderFixedRadius;
        else sphere.radius = 0.01f;

        if (ownerGameObject != null)
        {
            ownerColliders = ownerGameObject.GetComponentsInChildren<Collider>(true);
            if (ownerColliders != null && ownerColliders.Length > 0 && myColliders != null)
            {
                foreach (var a in myColliders)
                    foreach (var b in ownerColliders)
                        if (a != null && b != null) Physics.IgnoreCollision(a, b, true);

                StartCoroutine(ReenableOwnerCollisionsAfter(0.18f));
            }
        }

        StopAllCoroutines();
        StartCoroutine(ExpandAndDestroy());
    }

    void OnDrawGizmosSelected()
    {
        if (sphere != null)
            Gizmos.DrawWireSphere(transform.position, sphere.radius);
        else
            Gizmos.DrawWireSphere(transform.position, targetRadius);
    }
}
