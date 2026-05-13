using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Camera;
using TitanOrbit.Core;
using TitanOrbit.Generation;
using TitanOrbit.Systems;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// Cosmetic-only client bullet visual. Other players' shots are spawned from <see cref="CombatSystem"/>'s server
    /// batch and advance with synced server time. The firing owner uses <see cref="SpawnOwnerPredicted"/> only;
    /// spawn batches for that owner's ship are ignored so there is no second tracer. Hits remain server-authoritative.
    /// </summary>
    public sealed class ClientBulletTracer : MonoBehaviour
    {
        private static Transform s_pool;
        private static UnityEngine.Camera s_cachedMainCamera;
        private static int s_cachedCameraFrame = -1;
        private static UnityEngine.Camera s_cachedGameplayCamera;
        private static readonly Dictionary<uint, ClientBulletTracer> s_bySequence = new Dictionary<uint, ClientBulletTracer>(256);
        private static readonly List<ClientBulletTracer> s_ownerPredicted = new List<ClientBulletTracer>(32);

        private Vector3 logicalSpawn;
        private Vector3 velocity;
        private float serverSpawnTime;
        private float localSpawnTimeFallback;
        private float maxDistance;
        private float lifetime;
        private uint sequence;
        private bool ownerPredictedVisual;

        /// <summary>NetworkObjectId of the local player's ship, or 0 when unavailable.</summary>
        public static ulong GetLocalPlayerOwnedShipNetworkObjectId()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsClient || nm.LocalClient == null || nm.LocalClient.PlayerObject == null)
                return 0;
            return nm.LocalClient.PlayerObject.NetworkObjectId;
        }

        /// <summary>
        /// Owner-only cosmetic bullet: advances by local <see cref="Time.time"/> so it appears at the muzzle
        /// the frame the player fires, instead of waiting for the server spawn batch.
        /// </summary>
        public static GameObject SpawnOwnerPredicted(BulletSpawnPayload payload)
        {
            EnsurePool();
            GameObject go = new GameObject("ClientBulletTracer_Predicted");
            go.transform.SetParent(s_pool, false);

            Vector3 spawn = payload.SpawnPosition;
            spawn.y = 0f;
            Vector3 vel = payload.Velocity;
            vel.y = 0f;

            var tracer = go.AddComponent<ClientBulletTracer>();
            tracer.logicalSpawn = spawn;
            tracer.velocity = vel;
            tracer.serverSpawnTime = 0f;
            tracer.localSpawnTimeFallback = Time.time;
            tracer.maxDistance = Mathf.Max(0.5f, payload.MaxDistance);
            tracer.lifetime = Mathf.Max(0.1f, payload.Lifetime);
            tracer.sequence = 0;
            tracer.ownerPredictedVisual = true;
            s_ownerPredicted.Add(tracer);

            go.transform.position = spawn;
            if (vel.sqrMagnitude > 0.0001f)
                go.transform.rotation = Quaternion.LookRotation(vel.normalized, Vector3.up);

            BulletShape shape = (BulletShape)Mathf.Clamp(payload.ShapeIndex, 0, 2);
            float speedForVisual = vel.magnitude;
            BulletVisualFactory.BuildVisual(
                go.transform,
                payload.VisualPrefabBankIndex,
                (TeamManager.Team)payload.OwnerTeamByte,
                shape,
                payload.ScaleMultiplier,
                speedForVisual,
                payload.NoTrailFlag != 0);

            return go;
        }

        /// <summary>Removes the closest owner-predicted tracer to an impact (no server sequence on those visuals).</summary>
        public static void DespawnOwnerPredictedNearestToImpact(Vector3 impactWorldPos, float maxDist)
        {
            if (s_ownerPredicted.Count == 0) return;
            impactWorldPos.y = 0f;

            ClientBulletTracer best = null;
            float bestD = float.MaxValue;
            for (int i = 0; i < s_ownerPredicted.Count; i++)
            {
                ClientBulletTracer t = s_ownerPredicted[i];
                if (t == null || !t.ownerPredictedVisual) continue;
                Vector3 p = t.transform.position;
                p.y = 0f;
                float d = ToroidalMap.ToroidalDistance(p, impactWorldPos);
                if (d < maxDist && d < bestD)
                {
                    bestD = d;
                    best = t;
                }
            }

            if (best != null)
                Object.Destroy(best.gameObject);
        }

        public static GameObject Spawn(BulletSpawnPayload payload)
        {
            EnsurePool();
            GameObject go = new GameObject("ClientBulletTracer");
            go.transform.SetParent(s_pool, false);

            Vector3 spawn = payload.SpawnPosition;
            spawn.y = 0f;
            Vector3 vel = payload.Velocity;
            vel.y = 0f;

            var tracer = go.AddComponent<ClientBulletTracer>();
            tracer.logicalSpawn = spawn;
            tracer.velocity = vel;
            tracer.serverSpawnTime = payload.ServerSpawnTime;
            tracer.localSpawnTimeFallback = Time.time;
            tracer.maxDistance = Mathf.Max(0.5f, payload.MaxDistance);
            tracer.lifetime = Mathf.Max(0.1f, payload.Lifetime);
            tracer.sequence = payload.Sequence;
            if (payload.Sequence != 0)
                s_bySequence[payload.Sequence] = tracer;

            // Initial visual position uses elapsed since server spawn (one-way client latency),
            // so the bullet appears where the server has already simulated it to instead of at
            // the ship's outdated fire-time origin.
            float elapsed = tracer.GetElapsedSinceServerSpawn();
            Vector3 logical = spawn + vel * elapsed;
            logical.y = 0f;
            go.transform.position = logical;
            if (vel.sqrMagnitude > 0.0001f)
                go.transform.rotation = Quaternion.LookRotation(vel.normalized, Vector3.up);

            BulletShape shape = (BulletShape)Mathf.Clamp(payload.ShapeIndex, 0, 2);
            float speedForVisual = vel.magnitude;
            BulletVisualFactory.BuildVisual(
                go.transform,
                payload.VisualPrefabBankIndex,
                (TeamManager.Team)payload.OwnerTeamByte,
                shape,
                payload.ScaleMultiplier,
                speedForVisual,
                payload.NoTrailFlag != 0);

            return go;
        }

        /// <summary>Removes the tracer matching <paramref name="seq"/> if it is still alive on this client.</summary>
        public static void DespawnBySequence(uint seq)
        {
            if (seq == 0) return;
            if (s_bySequence.TryGetValue(seq, out ClientBulletTracer tracer) && tracer != null)
                Destroy(tracer.gameObject);
        }

        private void OnDestroy()
        {
            if (sequence != 0)
                s_bySequence.Remove(sequence);
            if (ownerPredictedVisual)
                s_ownerPredicted.Remove(this);
        }

        private static void EnsurePool()
        {
            if (s_pool != null) return;
            var poolGo = new GameObject("ClientBulletTracers");
            Object.DontDestroyOnLoad(poolGo);
            s_pool = poolGo.transform;
        }

        private void LateUpdate()
        {
            float elapsed = GetElapsedSinceServerSpawn();
            Vector3 logical = logicalSpawn + velocity * elapsed;
            logical.y = 0f;

            if (elapsed > lifetime
                || ToroidalMap.ToroidalDistance(logical, logicalSpawn) > maxDistance)
            {
                Destroy(gameObject);
                return;
            }

            UnityEngine.Camera cam = ResolveCamera();
            Vector3 displayPos = cam != null
                ? ToroidalMap.GetDisplayPosition(logical, cam.transform.position)
                : logical;
            transform.position = displayPos;
        }

        /// <summary>
        /// Seconds elapsed since the server spawned this bullet, in the synced NGO server-time
        /// domain. Falls back to local time when offline so single-player / tests still work.
        /// </summary>
        private float GetElapsedSinceServerSpawn()
        {
            if (ownerPredictedVisual)
                return Mathf.Max(0f, Time.time - localSpawnTimeFallback);

            var nm = NetworkManager.Singleton;
            if (nm != null && nm.IsListening)
                return Mathf.Max(0f, (float)nm.ServerTime.Time - serverSpawnTime);
            return Time.time - localSpawnTimeFallback;
        }

        private static UnityEngine.Camera ResolveCamera()
        {
            if (Time.frameCount != s_cachedCameraFrame)
            {
                s_cachedCameraFrame = Time.frameCount;
                s_cachedMainCamera = UnityEngine.Camera.main;
                if (s_cachedMainCamera == null || !s_cachedMainCamera.isActiveAndEnabled)
                {
                    if (s_cachedGameplayCamera == null)
                    {
                        var cc = Object.FindFirstObjectByType<CameraController>();
                        if (cc != null) s_cachedGameplayCamera = cc.GetComponent<UnityEngine.Camera>();
                    }
                }
            }
            UnityEngine.Camera cam = s_cachedMainCamera;
            if (cam == null || !cam.isActiveAndEnabled) cam = s_cachedGameplayCamera;
            return cam;
        }
    }
}
