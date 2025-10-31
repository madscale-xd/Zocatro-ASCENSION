using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Global bullet pool. Put this on a single GameObject in your scene (or let it create itself).
/// </summary>
public class BulletPool : MonoBehaviour
{
    public static BulletPool Instance { get; private set; }

    [Tooltip("Prefab used by the pool. Can be set at runtime via Setup/EnsureInitialized.")]
    public GameObject bulletPrefab;

    [Tooltip("Enable pooling (if false, bullets are always instantiated/destroyed).")]
    public bool usePooling = true;

    [Tooltip("Number of pooled bullets kept around.")]
    public int poolSize = 20;

    [Tooltip("If true, pool GameObject won't be destroyed between scenes.")]
    public bool persistent = false;

    private List<GameObject> pool;
    private Transform poolParent;

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        poolParent = new GameObject("BulletPool").transform;
        poolParent.SetParent(null, true);
        if (persistent)
            DontDestroyOnLoad(gameObject);

        if (bulletPrefab != null && usePooling)
            InitPool();
    }

    /// <summary>
    /// Call to set or change the prefab and pool parameters at runtime.
    /// </summary>
    public void Setup(GameObject prefab, int poolSize = 20, bool usePooling = true, bool persistent = false)
    {
        this.bulletPrefab = prefab;
        this.poolSize = Mathf.Max(0, poolSize);
        this.usePooling = usePooling;
        this.persistent = persistent;

        if (poolParent == null)
        {
            poolParent = new GameObject("BulletPool").transform;
            poolParent.SetParent(null, true);
        }

        // Re-init pool if using pooling
        if (usePooling)
            InitPool();
    }

    void InitPool()
    {
        // Destroy previous pool entries/parent if any
        if (pool != null)
        {
            for (int i = 0; i < pool.Count; i++)
                if (pool[i] != null) Destroy(pool[i]);
        }

        pool = new List<GameObject>(poolSize);

        for (int i = 0; i < poolSize; i++)
        {
            if (bulletPrefab == null)
            {
                Debug.LogWarning("BulletPool: bulletPrefab is null - cannot initialize pool.");
                break;
            }

            GameObject g = Instantiate(bulletPrefab, poolParent);
            g.SetActive(false);
            if (g.GetComponent<Bullet>() == null)
                g.AddComponent<Bullet>();
            pool.Add(g);
        }
    }

    /// <summary>
    /// Returns a bullet GameObject and whether it's from the pool. If pooling disabled or pool exhausted, returns a temporary instance.
    /// </summary>
    public GameObject GetBullet(out bool isPooled)
    {
        if (bulletPrefab == null)
        {
            isPooled = false;
            Debug.LogWarning("BulletPool: bulletPrefab not set. Returning null.");
            return null;
        }

        if (!usePooling)
        {
            isPooled = false;
            GameObject newB = Instantiate(bulletPrefab, poolParent);
            if (newB.GetComponent<Bullet>() == null) newB.AddComponent<Bullet>();
            newB.SetActive(false);
            return newB;
        }

        // Find inactive pooled bullet
        for (int i = 0; i < pool.Count; i++)
        {
            if (pool[i] != null && !pool[i].activeInHierarchy)
            {
                isPooled = true;
                return pool[i];
            }
        }

        // Pool exhausted -> create temporary, not added to pool
        isPooled = false;
        GameObject temp = Instantiate(bulletPrefab, poolParent);
        if (temp.GetComponent<Bullet>() == null) temp.AddComponent<Bullet>();
        temp.SetActive(false);
        return temp;
    }

    /// <summary>
    /// Utility to return pool's transform for parenting when bullets deactivate/are temporary.
    /// </summary>
    public Transform PoolTransform => poolParent;
}
