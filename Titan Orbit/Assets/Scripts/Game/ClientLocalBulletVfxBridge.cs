using TitanOrbit.Core;
using TitanOrbit.ECS;
using TitanOrbit.Input;
using TitanOrbit.NetCode;
using TitanOrbit.Shared;
using TitanOrbit.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Local-owner bullet anticipation: enqueues cosmetic tracers into <see cref="BulletVfxBridge"/>
    /// as soon as the player fires, so muzzle/tracer feel immediate. Server remains authoritative
    /// for damage (<see cref="BulletSimulationSystem"/>). When the matching <see cref="BulletSpawnRpc"/>
    /// arrives, <see cref="BulletVfxDriver"/> adopts this anticipation by OwnerNetworkId.
    /// <para>
    /// Runs on host and dedicated clients. Unarmed ships (empty mount buffer) never anticipate.
    /// Uses live <see cref="ShipWeaponConfig"/> and ghosted <see cref="ShipLoadoutState.RuntimeBulletIndex"/>.
    /// </para>
    /// </summary>
    [DefaultExecutionOrder(66100)]
    public class ClientLocalBulletVfxBridge : MonoBehaviour
    {
        /// <summary>Client-side fire-rate gate mirroring server FireRate.</summary>
        float _fireCooldown;

        /// <summary>Round-robin mount index (mirrors server NextMountIndex locally).</summary>
        int _nextMountIndex;

        /// <summary>Cached reference to scene input — resolved in Start.</summary>
        PlayerInputHandler _input;

        /// <summary>[UNITY] Auto-install when session manager exists in scene.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void EnsureInstalled()
        {
            if (FindAnyObjectByType<ClientLocalBulletVfxBridge>() != null)
                return;

            var session = FindAnyObjectByType<TitanOrbitSessionManager>();
            if (session != null)
                session.gameObject.AddComponent<ClientLocalBulletVfxBridge>();
        }

        void Start()
        {
            _input = FindAnyObjectByType<PlayerInputHandler>();
        }

        /// <summary>
        /// Enqueues anticipation spawns on shoot input for the local ship (host + dedicated client).
        /// </summary>
        void LateUpdate()
        {
            // --- Per-frame refresh ---
            if (_input == null)
                return;

            if (!TitanOrbitDedicatedServerAutoBoot.ShouldRunClientPresentation())
                return;

            var world = EcsGameBridge.ClientWorld;
            if (world == null || !world.IsCreated ||
                !TitanOrbitSessionManager.IsClientGameplayReady(world))
                return;

            // Skip Instantiates window — driver also gates; avoid queueing anticipation during join.
            if (ClientJoinSettleCache.Settling || ClientJoinSettleCache.GhostSpawnBacklog)
                return;

            if (!_input.ShootPressed || MoonOrbitClientState.IsOrbitMenuVisible)
                return;

            float dt = Time.deltaTime;
            if (_fireCooldown > 0f)
                _fireCooldown = Mathf.Max(0f, _fireCooldown - dt);

            if (_fireCooldown > 0f)
                return;

            if (!TryGetLocalShipCombatState(world.EntityManager, out Entity shipEntity, out ShipWeaponConfig weaponCfg,
                    out ShipState shipState, out ShipKinematics kinematics, out int ownerNetworkId, out int bankIndex))
                return;

            if (shipState.IsDead || shipState.AwaitingTeamSelection)
                return;

            // Energy is server-authoritative; optional soft gate so we do not spam when empty.
            float energyCost = weaponCfg.EnergyCostPerShot > 0f
                ? weaponCfg.EnergyCostPerShot
                : weaponCfg.BulletDamage;
            if (shipState.CurrentEnergy < energyCost)
                return;

            if (!TryResolveMuzzlePose(world.EntityManager, shipEntity, out Vector3 fireOrigin, out Vector3 fireForward))
                return;

            Vector3 shipVel = kinematics.Velocity;
            shipVel.y = 0f;
            Vector3 bulletVel = fireForward * Mathf.Max(1f, weaponCfg.BulletSpeed) + shipVel;
            float visualScale = BulletVisualScale.ComputePerShotScale(
                weaponCfg.BulletScale,
                weaponCfg.BulletDamage,
                weaponCfg.BulletSpeed,
                weaponCfg.ReferenceBulletDamage > 0f
                    ? weaponCfg.ReferenceBulletDamage
                    : BulletVisualScale.DefaultReferenceBulletDamage,
                weaponCfg.ReferenceBulletSpeed > 0f
                    ? weaponCfg.ReferenceBulletSpeed
                    : BulletVisualScale.DefaultReferenceBulletSpeed);

            // Prefer presentation-space muzzle when ShipDisplayPose is available (display-space flag).
            bool displaySpace = ShipDisplayPose.HasLocalPose;

            BulletVfxBridge.TryEnqueueSpawn(new BulletVfxBridge.SpawnRequest
            {
                Sequence = 0,
                SpawnPosition = fireOrigin,
                Velocity = bulletVel,
                Lifetime = Mathf.Max(0.1f, weaponCfg.BulletLifetime),
                MaxDistance = Mathf.Max(10f, weaponCfg.BulletMaxDistance),
                Damage = Mathf.Max(1f, weaponCfg.BulletDamage),
                OwnerTeam = (byte)shipState.Team,
                OwnerNetworkId = ownerNetworkId,
                BankIndex = bankIndex,
                ScaleMultiplier = visualScale,
                IsAnticipation = true,
                IsDisplaySpace = displaySpace,
            });

            _fireCooldown = 1f / Mathf.Max(0.1f, weaponCfg.FireRate);
        }

        static bool TryGetLocalShipCombatState(
            EntityManager em,
            out Entity shipEntity,
            out ShipWeaponConfig weaponCfg,
            out ShipState shipState,
            out ShipKinematics kinematics,
            out int ownerNetworkId,
            out int bankIndex)
        {
            shipEntity = Entity.Null;
            weaponCfg = default;
            shipState = default;
            kinematics = default;
            ownerNetworkId = 0;
            bankIndex = 0;

            if (!EcsGameBridge.TryGetLocalShipEntityOnWorld(EcsGameBridge.ClientWorld, out shipEntity) ||
                !em.Exists(shipEntity))
                return false;

            if (!em.HasComponent<ShipWeaponConfig>(shipEntity) ||
                !em.HasComponent<ShipState>(shipEntity) ||
                !em.HasComponent<ShipKinematics>(shipEntity))
                return false;

            weaponCfg = em.GetComponentData<ShipWeaponConfig>(shipEntity);
            shipState = em.GetComponentData<ShipState>(shipEntity);
            kinematics = em.GetComponentData<ShipKinematics>(shipEntity);

            if (em.HasComponent<GhostOwner>(shipEntity))
                ownerNetworkId = em.GetComponentData<GhostOwner>(shipEntity).NetworkId;

            if (em.HasComponent<ShipLoadoutState>(shipEntity))
                bankIndex = math.max(0, em.GetComponentData<ShipLoadoutState>(shipEntity).RuntimeBulletIndex);

            return true;
        }

        /// <summary>
        /// Resolves muzzle from mount buffer + presentation pose when available.
        /// Returns false when the ship has no mounts (intentional unarmed).
        /// </summary>
        bool TryResolveMuzzlePose(
            EntityManager em,
            Entity shipEntity,
            out Vector3 fireOrigin,
            out Vector3 fireForward)
        {
            fireOrigin = default;
            fireForward = Vector3.forward;

            if (!em.HasBuffer<ShipWeaponMountElement>(shipEntity))
                return false;

            var mounts = em.GetBuffer<ShipWeaponMountElement>(shipEntity);
            if (mounts.Length == 0)
                return false;

            int mountIdx = _nextMountIndex % mounts.Length;
            if (mountIdx < 0)
                mountIdx = 0;
            var mount = mounts[mountIdx];
            _nextMountIndex = (mountIdx + 1) % mounts.Length;

            LocalTransform shipTransform;
            if (ShipDisplayPose.HasLocalPose)
            {
                // [HYBRID] Presentation pose — same space as the rendered hull.
                shipTransform = LocalTransform.FromPositionRotationScale(
                    ShipDisplayPose.LocalPosition,
                    ShipDisplayPose.LocalRotation,
                    em.HasComponent<LocalTransform>(shipEntity)
                        ? em.GetComponentData<LocalTransform>(shipEntity).Scale
                        : 1f);
            }
            else if (em.HasComponent<LocalTransform>(shipEntity))
            {
                shipTransform = em.GetComponentData<LocalTransform>(shipEntity);
            }
            else
                return false;

            if (!ShipWeaponPose.TryResolve(shipTransform, mount, out float3 origin, out float3 forward))
                return false;

            fireOrigin = origin;
            fireForward = forward;
            return true;
        }
    }
}
