using UnityEngine;

/// <summary>
/// Base class for a MonoBehaviour that expects exactly one instance in the scene, found lazily on
/// first access. Does not auto-create one: a subclass like ObjectPoolManager has its pool contents
/// configured in the Inspector, so a spawned-on-demand instance would come up with nothing in it.
/// Every caller here already treats a missing instance as a normal outcome, not an error --
/// PoolObject.Release and ClimbingWall.AddHold/ClearHolds all fall back gracefully without one.
/// </summary>
public class Singleton<T> : MonoBehaviour where T : MonoBehaviour
{
    static T s_Instance;

    public static T Instance
    {
        get
        {
            if (s_Instance == null)
            {
                s_Instance = FindFirstObjectByType<T>();
            }

            return s_Instance;
        }
    }
}
