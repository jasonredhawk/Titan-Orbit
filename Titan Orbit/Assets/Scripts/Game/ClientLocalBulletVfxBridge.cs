using TitanOrbit.Core;
using TitanOrbit.ECS;
using TitanOrbit.Input;
using TitanOrbit.NetCode;
using TitanOrbit.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Local-owner bullet anticipation: enqueues cosmetic tracers into <see cref="BulletVfxBridge"/>
    /// from the Starblast muzzle (<see cref="BulletMuzzlePresentation"/>) so shots leave the nose
    /// immediately. Server remains authoritative for damage (<see cref="BulletSimulationSystem"/>).
    /// When <see cref="BulletSpawnRpc"/> arrives, <see cref="BulletVfxDriver"/> binds Sequence
    /// without snapping pose back to the lagged server muzzle.
    /// <para>
    /// [UNITY] LateUpdate after <see cref="EcsWorldVisualizer"/> (66000) so presentation pose
    /// is published; velocity uses kinematics or hull pose-delta (does not hard-require ShipKinematics).
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
        /// Enqueues anticipation spawns for the local ship (host + dedicated client).
        /// Prefers ECS <see cref="ShipInput.Fire"/>; falls back to <see cref="PlayerInputHandler.ShootPressed"/>.
        /// </summary>
        void LateUpdate()
        {
            // --- Per-frame refresh ---
            // [UNITY] _input is optional — ECS ShipInput.Fire can arm anticipation without it.
            if (!TitanOrbitDedicatedServerAutoBoot.ShouldRunClientPresentation())
                return;

            var world = EcsGameBridge.ClientWorld;
            if (world == null || !world.IsCreated ||
                !TitanOrbitSessionManager.IsClientGameplayReady(world))
                return;

            // Skip Instantiates window — driver also gates; avoid queueing anticipation during join.
            if (ClientJoinSettleCache.Settling || ClientJoinSettleCache.GhostSpawnBacklog)
                return;

            if (MoonOrbitClientState.IsOrbitMenuVisible)
                return;

            float dt = Time.deltaTime;
            if (_fireCooldown > 0f)
                _fireCooldown = Mathf.Max(0f, _fireCooldown - dt);

            if (_fireCooldown > 0f)
                return;

            if (!TryGetLocalShipCombatState(world.EntityManager, out Entity shipEntity, out ShipWeaponConfig weaponCfg,
                    out ShipState shipState, out int ownerNetworkId, out int bankIndex, out bool fireHeld))
                return;

            if (!fireHeld)
                return;

            if (shipState.IsDead || shipState.AwaitingTeamSelection)
                return;

            // Energy is server-authoritative; optional soft gate so we do not spam when empty.
            float energyCost = weaponCfg.EnergyCostPerShot > 0f
                ? weaponCfg.EnergyCostPerShot
                : weaponCfg.BulletDamage;
            if (shipState.CurrentEnergy < energyCost)
                return;

            // --- Starblast muzzle (predicted when soft-track lags; velocity includes shipVel) ---
            if (!BulletMuzzlePresentation.TryResolveMuzzle(
                    world.EntityManager, shipEntity, _nextMountIndex,
                    out float3 fireOrigin, out float3 fireForward, out bool displaySpace, out float3 shipVel))
                return;

            // Advance round-robin only after a successful resolve (armed ships only).
            if (world.EntityManager.HasBuffer<ShipWeaponMountElement>(shipEntity))
            {
                int mountCount = world.EntityManager.GetBuffer<ShipWeaponMountElement>(shipEntity).Length;
                if (mountCount > 0)
                    _nextMountIndex = (_nextMountIndex + 1) % mountCount;
            }

            // Shared helper: kinematics or pose-delta — never leave shipVel=0 while hull moves.
            float3 bulletVel = BulletMuzzlePresentation.BuildBulletWorldVelocity(
                fireForward, weaponCfg.BulletSpeed, shipVel);

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

            BulletVfxBridge.TryEnqueueSpawn(new BulletVfxBridge.SpawnRequest
            {
                Sequence = 0,
                SpawnPosition = fireOrigin,
                Velocity = bulletVel,
                Lifetime = math.max(0.1f, weaponCfg.BulletLifetime),
                MaxDistance = math.max(10f, weaponCfg.BulletMaxDistance),
                Damage = math.max(1f, weaponCfg.BulletDamage),
                OwnerTeam = (byte)shipState.Team,
                OwnerNetworkId = ownerNetworkId,
                BankIndex = bankIndex,
                ScaleMultiplier = visualScale,
                IsAnticipation = true,
                IsDisplaySpace = displaySpace,
            });

            _fireCooldown = 1f / math.max(0.1f, weaponCfg.FireRate);
        }

        /// <summary>
        /// Reads local ship combat components and whether fire is held this frame.
        /// Does not require <see cref="ShipKinematics"/> — velocity falls back in the muzzle helper.
        /// </summary>
        bool TryGetLocalShipCombatState(
            EntityManager em,
            out Entity shipEntity,
            out ShipWeaponConfig weaponCfg,
            out ShipState shipState,
            out int ownerNetworkId,
            out int bankIndex,
            out bool fireHeld)
        {
            shipEntity = Entity.Null;
            weaponCfg = default;
            shipState = default;
            ownerNetworkId = 0;
            bankIndex = 0;
            fireHeld = false;

            if (!BulletMuzzlePresentation.TryGetLocalShipEntity(em, out shipEntity) ||
                !em.Exists(shipEntity))
                return false;

            if (!em.HasComponent<ShipWeaponConfig>(shipEntity) ||
                !em.HasComponent<ShipState>(shipEntity))
                return false;

            weaponCfg = em.GetComponentData<ShipWeaponConfig>(shipEntity);
            shipState = em.GetComponentData<ShipState>(shipEntity);

            if (em.HasComponent<GhostOwner>(shipEntity))
                ownerNetworkId = em.GetComponentData<GhostOwner>(shipEntity).NetworkId;
            if (ownerNetworkId <= 0)
                ownerNetworkId = EcsGameBridge.GetLocalNetworkId();

            if (em.HasComponent<ShipLoadoutState>(shipEntity))
                bankIndex = math.max(0, em.GetComponentData<ShipLoadoutState>(shipEntity).RuntimeBulletIndex);

            // --- Fire gate: ECS Fire InputEvent when present, else raw input ---
            if (em.HasComponent<ShipInput>(shipEntity) && em.GetComponentData<ShipInput>(shipEntity).Fire.IsSet)
                fireHeld = true;
            else if (_input != null && _input.ShootPressed)
                fireHeld = true;

            return true;
        }
    }
}
