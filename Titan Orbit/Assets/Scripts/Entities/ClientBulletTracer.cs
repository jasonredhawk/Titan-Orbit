using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Audio;
using TitanOrbit.Camera;
using TitanOrbit.Core;
using TitanOrbit.Generation;
using TitanOrbit.Data;
using TitanOrbit.Systems;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// Cosmetic-only client bullet visual. Other players' shots are spawned from <see cref="CombatSystem"/>'s server
    /// batch and advance with synced server time. The firing owner uses <see cref="SpawnOwnerPredicted"/> only;
    /// spawn batches for that owner's ship are ignored so there is no second tracer. Owner tracers sweep physics
    /// locally for impact VFX (no damage); server impact RPC skips duplicate VFX for that owner. Hits remain
    /// server-authoritative.
    /// </summary>
    public sealed class ClientBulletTracer : MonoBehaviour
    {
        private const float OwnerPredictedBulletRadius = 0.3f;
        private const float OwnerPredictedMinTravelBeforeGenericHit = 0.5f;

        private static Transform s_pool;
        private static UnityEngine.Camera s_cachedMainCamera;
        private static int s_cachedCameraFrame = -1;
        private static UnityEngine.Camera s_cachedGameplayCamera;
        private static readonly Dictionary<uint, ClientBulletTracer> s_bySequence = new Dictionary<uint, ClientBulletTracer>(256);
        private static readonly List<ClientBulletTracer> s_ownerPredicted = new List<ClientBulletTracer>(32);
        private static readonly HashSet<uint> s_ownerImpactVfxAlreadyShown = new HashSet<uint>(64);
        private static readonly RaycastHit[] s_ownerPredictedHits = new RaycastHit[32];

        private Vector3 logicalSpawn;
        private Vector3 velocity;
        private float serverSpawnTime;
        private float localSpawnTimeFallback;
        private float maxDistance;
        private float lifetime;
        private uint sequence;
        private bool ownerPredictedVisual;

        private Vector3 lastLogicalWorld;
        private ulong ownerShipNetworkId;
        private TeamManager.Team ownerTeam;
        private float damageForImpactPitch;
        private int visualPrefabBankIndex;
        private float bulletVisualScaleMultiplier = 1f;
        private uint assignedServerSequence;
        private bool pendingImpactVfxShown;
        private bool impactVfxShown;
        private ClientBulletStretchVisual stretchVisual;

        /// <summary>
        /// Called when the server spawn batch arrives so owner-predicted tracers can link to their
        /// authoritative sequence (used to avoid duplicate impact VFX).
        /// </summary>
        public static void AssociateOwnerPredictedWithServerSequence(uint sequence)
        {
            if (sequence == 0) return;
            for (int i = 0; i < s_ownerPredicted.Count; i++)
            {
                ClientBulletTracer tracer = s_ownerPredicted[i];
                if (tracer == null || tracer.assignedServerSequence != 0)
                    continue;

                tracer.assignedServerSequence = sequence;
                s_bySequence[sequence] = tracer;
                if (tracer.pendingImpactVfxShown || tracer.impactVfxShown)
                    s_ownerImpactVfxAlreadyShown.Add(sequence);
                return;
            }
        }

        /// <summary>
        /// True when owner-predicted VFX already ran for this server bullet (skip duplicate RPC VFX).
        /// </summary>
        public static bool ShouldSkipOwnerServerImpactVfx(uint sequence)
        {
            if (sequence != 0 && s_ownerImpactVfxAlreadyShown.Remove(sequence))
                return true;

            for (int i = 0; i < s_ownerPredicted.Count; i++)
            {
                ClientBulletTracer tracer = s_ownerPredicted[i];
                if (tracer != null && tracer.pendingImpactVfxShown)
                    return true;
            }

            return false;
        }

        private void MarkOwnerImpactVfxShown()
        {
            impactVfxShown = true;
            if (assignedServerSequence != 0)
                s_ownerImpactVfxAlreadyShown.Add(assignedServerSequence);
            else
                pendingImpactVfxShown = true;
        }

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
            tracer.lastLogicalWorld = spawn;
            tracer.ownerShipNetworkId = payload.OwnerShipNetworkId;
            tracer.ownerTeam = (TeamManager.Team)payload.OwnerTeamByte;
            tracer.damageForImpactPitch = Mathf.Max(0.01f, payload.Damage);
            tracer.visualPrefabBankIndex = payload.VisualPrefabBankIndex;
            tracer.bulletVisualScaleMultiplier = Mathf.Max(0.1f, payload.ScaleMultiplier);
            s_ownerPredicted.Add(tracer);

            go.transform.position = spawn;
            if (vel.sqrMagnitude > 0.0001f)
                go.transform.rotation = Quaternion.LookRotation(vel.normalized, Vector3.up);

            tracer.SetupVisual(
                payload.VisualPrefabBankIndex,
                (BulletShape)Mathf.Clamp(payload.ShapeIndex, 0, 2),
                vel.magnitude,
                payload.ScaleMultiplier,
                payload.NoTrailFlag != 0);

            return go;
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
            tracer.bulletVisualScaleMultiplier = Mathf.Max(0.1f, payload.ScaleMultiplier);
            tracer.visualPrefabBankIndex = payload.VisualPrefabBankIndex;
            tracer.ownerTeam = (TeamManager.Team)payload.OwnerTeamByte;

            // Initial visual position uses elapsed since server spawn (one-way client latency),
            // so the bullet appears where the server has already simulated it to instead of at
            // the ship's outdated fire-time origin.
            float elapsed = tracer.GetElapsedSinceServerSpawn();
            Vector3 logical = spawn + vel * elapsed;
            logical.y = 0f;
            go.transform.position = logical;
            if (vel.sqrMagnitude > 0.0001f)
                go.transform.rotation = Quaternion.LookRotation(vel.normalized, Vector3.up);

            tracer.SetupVisual(
                payload.VisualPrefabBankIndex,
                (BulletShape)Mathf.Clamp(payload.ShapeIndex, 0, 2),
                vel.magnitude,
                payload.ScaleMultiplier,
                payload.NoTrailFlag != 0,
                logical);

            return go;
        }

        /// <summary>Removes the tracer matching <paramref name="seq"/> if it is still alive on this client.</summary>
        public static void DespawnBySequence(uint seq)
        {
            if (seq == 0) return;
            if (s_bySequence.TryGetValue(seq, out ClientBulletTracer tracer) && tracer != null)
            {
                Destroy(tracer.gameObject);
                return;
            }

            for (int i = s_ownerPredicted.Count - 1; i >= 0; i--)
            {
                ClientBulletTracer predicted = s_ownerPredicted[i];
                if (predicted != null && predicted.assignedServerSequence == seq)
                    Destroy(predicted.gameObject);
            }
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

        private void SetupVisual(
            int bankIndex,
            BulletShape shape,
            float bulletSpeed,
            float scaleMultiplier,
            bool noTrail,
            Vector3? initialLogical = null)
        {
            GameObject visual = BulletVisualFactory.BuildVisual(
                transform,
                bankIndex,
                ownerTeam,
                shape,
                scaleMultiplier,
                bulletSpeed,
                noTrail);

            if (BulletBankProfileUtility.TryGetProfile(bankIndex, out BulletBankProfile profile)
                && profile != null
                && profile.TryGetStretchLengthFactors(out float startFactor, out float endFactor)
                && ClientBulletStretchVisual.TryAttach(transform, visual, startFactor, endFactor))
            {
                stretchVisual = GetComponent<ClientBulletStretchVisual>();
            }

            if (stretchVisual != null)
            {
                Vector3 logical = initialLogical ?? logicalSpawn;
                float progress = GetTravelProgress(logical);
                stretchVisual.ApplyTravelProgress(progress);
            }
        }

        private float GetTravelProgress(Vector3 logicalPosition)
        {
            float travelled = ToroidalMap.ToroidalDistance(logicalPosition, logicalSpawn);
            return travelled / Mathf.Max(0.5f, maxDistance);
        }

        private void LateUpdate()
        {
            float elapsed = GetElapsedSinceServerSpawn();
            Vector3 logical = logicalSpawn + velocity * elapsed;
            logical.y = 0f;

            if (stretchVisual != null)
                stretchVisual.ApplyTravelProgress(GetTravelProgress(logical));

            if (ownerPredictedVisual)
            {
                if (TryOwnerPredictedCosmeticHit(logical))
                {
                    Destroy(gameObject);
                    return;
                }
            }

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

            if (ownerPredictedVisual)
                lastLogicalWorld = logical;
        }

        /// <summary>
        /// Client-only sweep: mirrors server bullet spherecast + toroidal asteroid fallback for impact VFX only.
        /// </summary>
        private bool TryOwnerPredictedCosmeticHit(Vector3 logicalCurrent)
        {
            Vector3 from = lastLogicalWorld;
            Vector3 to = logicalCurrent;
            float pathLen = Vector3.Distance(from, to);
            if (pathLen < 0.001f)
                return false;

            var nm = NetworkManager.Singleton;
            bool pureClient = nm != null && nm.IsClient && !nm.IsServer;

            // Pass 1: physics spherecast (matches server StepBullet). Host colliders are logical; pure clients skip asteroid colliders here.
            if (TryOwnerPredictedSpherecastHit(from, to, pathLen, pureClient, out Vector3 sphereImpact, out BulletHitResolver.BulletHitPopupInfo spherePopup, out Starship burnAttachShip))
            {
                PlayOwnerPredictedImpact(sphereImpact, spherePopup, burnAttachShip);
                return true;
            }

            // Pass 2: toroidal asteroids when display-shifted colliders miss (pure client) or as server fallback.
            if (BulletHitResolver.TryToroidalAsteroidSegmentCosmeticOnly(
                    from, to, OwnerPredictedBulletRadius, out Vector3 toroidalImpact))
            {
                toroidalImpact.y = 0f;
                PlayOwnerPredictedImpact(
                    toroidalImpact,
                    new BulletHitResolver.BulletHitPopupInfo(
                        true,
                        FloatingCountChannel.DamageAsteroid,
                        damageForImpactPitch,
                        isAsteroidHit: true));
                return true;
            }

            if (BulletHitResolver.TryToroidalGemMoonSegmentCosmeticOnly(
                    from, to, OwnerPredictedBulletRadius, ownerTeam, out Vector3 moonImpact))
            {
                moonImpact.y = 0f;
                PlayOwnerPredictedImpact(
                    moonImpact,
                    new BulletHitResolver.BulletHitPopupInfo(
                        true,
                        FloatingCountChannel.DamageMoon,
                        damageForImpactPitch));
                return true;
            }

            if (BulletHitResolver.TryToroidalShipSegmentCosmeticOnly(
                    from, to, OwnerPredictedBulletRadius, ownerTeam, ownerShipNetworkId, out Vector3 shipImpact))
            {
                shipImpact.y = 0f;
                PlayOwnerPredictedImpact(
                    shipImpact,
                    new BulletHitResolver.BulletHitPopupInfo(
                        true,
                        FloatingCountChannel.DamageShipOrDrone,
                        damageForImpactPitch));
                return true;
            }

            return false;
        }

        private bool TryOwnerPredictedSpherecastHit(
            Vector3 from,
            Vector3 to,
            float pathLen,
            bool pureClient,
            out Vector3 impactPos,
            out BulletHitResolver.BulletHitPopupInfo popupInfo,
            out Starship burnAttachShip)
        {
            impactPos = to;
            popupInfo = default;
            burnAttachShip = null;

            Vector3 rayDir = (to - from) / pathLen;
            int hitCount = Physics.SphereCastNonAlloc(
                from,
                OwnerPredictedBulletRadius,
                rayDir,
                s_ownerPredictedHits,
                pathLen,
                ~0,
                QueryTriggerInteraction.Ignore);

            if (hitCount > 1)
                SortOwnerPredictedHitsByDistance(hitCount);

            for (int i = 0; i < hitCount; i++)
            {
                RaycastHit hit = s_ownerPredictedHits[i];
                if (hit.collider == null) continue;
                if (BulletHitResolver.IsColliderOnFiringShipNetworkObject(hit.collider, ownerShipNetworkId))
                    continue;
                if (pureClient && hit.collider.GetComponentInParent<Asteroid>() != null)
                    continue;

                if (BulletHitResolver.IsCosmeticBulletImpactTarget(hit.collider, ownerTeam, visualPrefabBankIndex))
                {
                    bool isAsteroid = hit.collider.GetComponentInParent<Asteroid>() != null;
                    if (isAsteroid)
                    {
                        popupInfo = new BulletHitResolver.BulletHitPopupInfo(
                            true,
                            FloatingCountChannel.DamageAsteroid,
                            damageForImpactPitch,
                            isAsteroidHit: true);
                    }
                    else
                    {
                        Starship friendly = hit.collider.GetComponentInParent<Starship>();
                        if (friendly != null && friendly.ShipTeam == ownerTeam
                            && BulletBankProfileUtility.TryGetProfile(visualPrefabBankIndex, out BulletBankProfile profile)
                            && profile != null
                            && profile.HasAbility(BulletBankAbilityType.HealFriendly))
                        {
                            popupInfo = new BulletHitResolver.BulletHitPopupInfo(
                                true, FloatingCountChannel.Healing, damageForImpactPitch);
                        }
                        else
                        {
                            FloatingCountChannel channel = FloatingCountChannel.DamageShipOrDrone;
                            BulletHitResolver.TryGetBulletDamageChannel(hit.collider, ownerTeam, out channel);
                            popupInfo = new BulletHitResolver.BulletHitPopupInfo(true, channel, damageForImpactPitch);
                        }
                    }

                    burnAttachShip = hit.collider.GetComponentInParent<Starship>();
                    if (burnAttachShip != null && (burnAttachShip.IsDead || burnAttachShip.ShipTeam == ownerTeam))
                        burnAttachShip = null;
                    impactPos = hit.point;
                    return true;
                }

                Vector3 along = from + rayDir * hit.distance;
                float travelled = ToroidalMap.ToroidalDistance(along, logicalSpawn);
                if (travelled >= OwnerPredictedMinTravelBeforeGenericHit)
                {
                    impactPos = hit.point;
                    return true;
                }
            }

            return false;
        }

        private static void SortOwnerPredictedHitsByDistance(int n)
        {
            for (int i = 1; i < n; i++)
            {
                RaycastHit key = s_ownerPredictedHits[i];
                float kd = key.distance;
                int j = i - 1;
                while (j >= 0 && s_ownerPredictedHits[j].distance > kd)
                {
                    s_ownerPredictedHits[j + 1] = s_ownerPredictedHits[j];
                    j--;
                }
                s_ownerPredictedHits[j + 1] = key;
            }
        }

        private void PlayOwnerPredictedImpact(
            Vector3 position,
            BulletHitResolver.BulletHitPopupInfo popupInfo = default,
            Starship attachBurnToShip = null)
        {
            position.y = 0f;
            position = BulletVisualFactory.ResolveClientImpactWorldPosition(position);
            float pitch = BulletHitResolver.GetImpactSoundPitch(damageForImpactPitch);
            float impactScale = BulletVisualFactory.GetImpactScale(bulletVisualScaleMultiplier, popupInfo.IsAsteroidHit);

            GameObject prefab = null;
            if (visualPrefabBankIndex >= 0 && CombatSystem.Instance != null)
                prefab = CombatSystem.Instance.GetImpactPrefabFromBank(visualPrefabBankIndex, ownerTeam);

            float burnDur = 0f;
            if (visualPrefabBankIndex >= 0
                && BulletBankProfileUtility.TryGetProfile(visualPrefabBankIndex, out BulletBankProfile profile)
                && profile != null
                && profile.HasBurn)
            {
                burnDur = profile.GetBurnDuration();
            }

            Transform attach = null;
            Vector3 localOff = Vector3.zero;
            if (burnDur > 0.05f && attachBurnToShip != null)
            {
                attach = attachBurnToShip.GetBurnVfxAttachTransform();
                if (attach != null)
                {
                    localOff = attach.InverseTransformPoint(position);
                    localOff.y = 0f;
                }
            }

            BulletVisualFactory.SpawnBulletImpactVfx(
                position,
                prefab,
                ownerTeam,
                pitch,
                impactScale,
                BulletVisualFactory.DefaultImpactDuration,
                attach,
                localOff,
                burnDur);

            MarkOwnerImpactVfxShown();

            if (AudioManager.Instance != null)
                AudioManager.Instance.PlayImpactSound(pitch);

            if (!popupInfo.IsAsteroidHit && (popupInfo.HasPopup || popupInfo.HasAsteroidFeedback))
                BulletHitResolver.SpawnBulletHitFeedbackLocal(position, popupInfo, ownerTeam);
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
