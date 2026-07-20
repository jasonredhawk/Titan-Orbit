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
    /// from live weapon component transforms (<see cref="BulletMuzzlePresentation"/>) so muzzle
    /// flash matches the drawn barrel (including BankPivot). Server remains authoritative for
    /// damage (<see cref="BulletSimulationSystem"/>).
    /// <para>
    /// [TITAN-ORBIT] Multi-cannon hulls fire a <b>volley</b> — one anticipation tracer per weapon
    /// mount from that mount's pose (same order as the server). When <see cref="BulletSpawnRpc"/>
    /// arrives, <see cref="BulletVfxDriver"/> binds Sequence without snapping pose back to the
    /// lagged server muzzle.
    /// </para>
    /// <para>
    /// [UNITY] LateUpdate after <see cref="EcsWorldVisualizer"/> (66000) so the hull / bank pose
    /// is published; velocity uses kinematics or hull pose-delta.
    /// </para>
    /// </summary>
    [DefaultExecutionOrder(66100)]
    public class ClientLocalBulletVfxBridge : MonoBehaviour
    {
        /// <summary>Client-side fire-rate gate mirroring server FireRate.</summary>
        float _fireCooldown;

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

            // --- Mount count: live GO barrels first (matches TryResolveMuzzle), else ECS buffer ---
            int mountCount = BulletMuzzlePresentation.GetLiveWeaponMountCount(
                world.EntityManager, shipEntity);
            if (mountCount <= 0 &&
                world.EntityManager.HasBuffer<ShipWeaponMountElement>(shipEntity))
            {
                mountCount = world.EntityManager.GetBuffer<ShipWeaponMountElement>(shipEntity).Length;
            }

            if (mountCount <= 0)
                return;

            // --- Cap pending anticipations for the whole volley ---
            // [TITAN-ORBIT] Do not advance _fireCooldown here — retry next frame when adopt frees slots.
            if (!BulletVfxBridge.CanEnqueueAnticipation(mountCount))
                return;

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

            // firePower is summed across weapons — split cosmetic damage like the server volley.
            float damagePerBullet = math.max(1f, weaponCfg.BulletDamage / mountCount);
            int enqueued = 0;

            // --- Volley: one anticipation tracer per weapon mount pose ---
            for (int mountIdx = 0; mountIdx < mountCount; mountIdx++)
            {
                if (!BulletMuzzlePresentation.TryResolveMuzzle(
                        world.EntityManager, shipEntity, mountIdx,
                        out float3 fireOrigin, out float3 fireForward, out bool displaySpace,
                        out float3 shipVel))
                    continue;

                float3 bulletVel = BulletMuzzlePresentation.BuildBulletWorldVelocity(
                    fireForward, weaponCfg.BulletSpeed, shipVel);

                if (!BulletVfxBridge.TryEnqueueSpawn(new BulletVfxBridge.SpawnRequest
                {
                    Sequence = 0,
                    SpawnPosition = fireOrigin,
                    Velocity = bulletVel,
                    Lifetime = math.max(0.1f, weaponCfg.BulletLifetime),
                    MaxDistance = math.max(10f, weaponCfg.BulletMaxDistance),
                    Damage = damagePerBullet,
                    OwnerTeam = (byte)shipState.Team,
                    OwnerNetworkId = ownerNetworkId,
                    BankIndex = bankIndex,
                    ScaleMultiplier = visualScale,
                    MountIndex = mountIdx,
                    IsAnticipation = true,
                    IsDisplaySpace = displaySpace,
                }))
                    break;

                enqueued++;
            }

            // Only start cooldown when at least one muzzle queued — otherwise retry next frame.
            if (enqueued > 0)
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
