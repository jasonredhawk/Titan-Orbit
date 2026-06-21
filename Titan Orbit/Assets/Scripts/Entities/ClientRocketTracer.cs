using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Generation;
using TitanOrbit.Systems;

namespace TitanOrbit.Entities
{
    /// <summary>Cosmetic rocket tracer driven by synced server time.</summary>
    public sealed class ClientRocketTracer : MonoBehaviour
    {
        private static Transform s_pool;
        private Vector3 logicalSpawn;
        private Vector3 velocity;
        private float serverSpawnTime;
        private float maxDistance;
        private float lifetime;

        public static GameObject Spawn(RocketSpawnPayload payload)
        {
            EnsurePool();
            var go = new GameObject("ClientRocketTracer");
            go.transform.SetParent(s_pool, false);
            var tracer = go.AddComponent<ClientRocketTracer>();
            tracer.logicalSpawn = payload.SpawnPosition;
            tracer.logicalSpawn.y = 0f;
            tracer.velocity = payload.Velocity;
            tracer.velocity.y = 0f;
            tracer.serverSpawnTime = payload.ServerSpawnTime;
            tracer.maxDistance = payload.MaxDistance;
            tracer.lifetime = payload.Lifetime;
            go.transform.position = tracer.GetLogicalPosition();
            if (tracer.velocity.sqrMagnitude > 0.0001f)
                go.transform.rotation = Quaternion.LookRotation(tracer.velocity.normalized, Vector3.up);

            var visual = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            visual.transform.SetParent(go.transform, false);
            visual.transform.localScale = Vector3.one * (payload.IsLargeFlag != 0 ? 0.5f : 0.35f);
            var col = visual.GetComponent<Collider>();
            if (col != null) Destroy(col);
            return go;
        }

        private static void EnsurePool()
        {
            if (s_pool != null) return;
            var poolGo = new GameObject("ClientRocketTracers");
            Object.DontDestroyOnLoad(poolGo);
            s_pool = poolGo.transform;
        }

        private Vector3 GetLogicalPosition()
        {
            float elapsed = GetElapsed();
            Vector3 pos = logicalSpawn + velocity * elapsed;
            pos.y = 0f;
            return pos;
        }

        private float GetElapsed()
        {
            var nm = NetworkManager.Singleton;
            if (nm != null && nm.IsListening)
                return Mathf.Max(0f, (float)nm.ServerTime.Time - serverSpawnTime);
            return Time.time - serverSpawnTime;
        }

        private void LateUpdate()
        {
            Vector3 logical = GetLogicalPosition();
            if (GetElapsed() > lifetime
                || ToroidalMap.ToroidalDistance(logical, logicalSpawn) > maxDistance)
            {
                Destroy(gameObject);
                return;
            }

            var cam = UnityEngine.Camera.main;
            transform.position = cam != null
                ? ToroidalMap.GetDisplayPosition(logical, cam.transform.position)
                : logical;
        }
    }
}
