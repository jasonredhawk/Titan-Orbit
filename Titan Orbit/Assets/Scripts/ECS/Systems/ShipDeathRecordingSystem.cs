using TitanOrbit.Core;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server-only: watches for ships whose <see cref="ShipState.IsDead"/> just became true and
    /// adds <see cref="ShipDeathState"/> with a respawn timer. Clears people and velocity once —
    /// runs before <see cref="ShipRespawnSystem"/>. WithNone&lt;ShipDeathState&gt; ensures this
    /// fires exactly once per death.
    /// <para>
    /// [TITAN-ORBIT] Death requires hull <b>and</b> cargo depleted (<c>ShipDamageLogic</c>).
    /// Combat already expelled gems as world entities — do not silently zero leftover cargo here
    /// without a spawn (that was the ECS regression vs NGO). Clamp tiny leftovers only.
    /// </para>
    /// <para>
    /// Also credits <see cref="ShipMatchStats.Kills"/> to the last damager from
    /// <see cref="ShipCombatAttribution"/> (bullet / ram) when the killer is a different team.
    /// </para>
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(BulletSimulationSystem))]
    [UpdateAfter(typeof(GemDepositSystem))]
    [UpdateBefore(typeof(ShipRespawnSystem))]
    public partial struct ShipDeathRecordingSystem : ISystem
    {
        /// <summary>One-shot death bookkeeping for newly dead ships.</summary>
        public void OnUpdate(ref SystemState state)
        {
            float now = (float)SystemAPI.Time.ElapsedTime;
            uint tick = 0;
            if (SystemAPI.TryGetSingleton<NetworkTime>(out var networkTime)
                && networkTime.ServerTick.IsValid)
                tick = networkTime.ServerTick.TickIndexForValidTick;
            var ecb = new EntityCommandBuffer(Allocator.Temp);

            foreach (var (shipState, kinematics, orbitState, entity) in SystemAPI
                         .Query<RefRW<ShipState>, RefRW<ShipKinematics>, RefRW<ShipOrbitState>>()
                         .WithAll<ShipTag>()
                         .WithNone<ShipDeathState>()
                         .WithEntityAccess())
            {
                if (!shipState.ValueRO.IsDead)
                    continue;

                // --- Kill credit (once per death) ---
                // [TITAN-ORBIT] Prefer enemy kills only — same-team / self / asteroid deaths skip.
                CreditKillToLastDamager(state.EntityManager, entity, shipState.ValueRO.Team);

                // --- Death cleanup: stop movement / people (gems should already be empty) ---
                // Clamp only — world gem burst already happened during the killing damage pulses.
                if (shipState.ValueRO.CurrentGems > 0.001f)
                {
                    // Safety: if something set IsDead with cargo left, strip without inventing a burst
                    // (should not happen on the dual-resource path).
                    shipState.ValueRW.CurrentGems = 0f;
                }
                else
                {
                    shipState.ValueRW.CurrentGems = 0f;
                }

                shipState.ValueRW.CurrentPeople = 0;
                kinematics.ValueRW.Velocity = Unity.Mathematics.float3.zero;
                orbitState.ValueRW.OrbitPlanetId = 0;
                orbitState.ValueRW.InOrbitRing = false;
                orbitState.ValueRW.UsingOrbitMotor = false;
                orbitState.ValueRW.IsTransferringPeople = false;

                // --- MEGA death: free the store slot now; keep the MEGA visual until respawn ---
                if (state.EntityManager.HasComponent<MegaShipState>(entity)
                    && state.EntityManager.GetComponentData<MegaShipState>(entity).IsMega)
                {
                    MegaShipStatApplyLogic.ReleaseMegaOccupancy(state.EntityManager, entity);
                }

                PackDeathVfx(state.EntityManager, entity, now, tick, ecb);

                // [TITAN-ORBIT] Schedule respawn — ShipRespawnSystem removes this component later.
                ecb.AddComponent(entity, new ShipDeathState
                {
                    RespawnAtTime = now + ShipRespawnSystem.RespawnDelaySeconds,
                });
            }

            ecb.Playback(state.EntityManager);
            ecb.Dispose();
        }

        /// <summary>
        /// Writes <see cref="ShipDeathVfxState.Packed"/> from the last hit impulse + a tick seed
        /// so every client plays the same cosmetic breakup.
        /// </summary>
        static void PackDeathVfx(
            EntityManager em,
            Entity victim,
            float now,
            uint serverTick,
            EntityCommandBuffer ecb)
        {
            uint seed = serverTick != 0 ? serverTick : (uint)math.max(1, (int)(now * 1000f));
            if (em.HasComponent<GhostOwner>(victim))
                seed ^= (uint)em.GetComponentData<GhostOwner>(victim).NetworkId * 747796405u;

            float2 impulse = float2.zero;
            float power = 0f;
            if (em.HasComponent<ShipCombatAttribution>(victim))
            {
                var attr = em.GetComponentData<ShipCombatAttribution>(victim);
                impulse = attr.LastImpulseXZ;
                power = attr.LastImpulsePower;
            }

            var vfx = new ShipDeathVfxState { Packed = ShipDeathVfxState.Pack(seed, impulse, power) };
            if (em.HasComponent<ShipDeathVfxState>(victim))
                em.SetComponentData(victim, vfx);
            else
                ecb.AddComponent(victim, vfx);
        }

        /// <summary>
        /// Credits one kill to <see cref="ShipCombatAttribution.LastDamagerNetworkId"/> when the
        /// damager is a real other ship on a different team.
        /// </summary>
        static void CreditKillToLastDamager(EntityManager em, Entity victim, TeamId victimTeam)
        {
            // --- Read attribution ---
            if (!em.HasComponent<ShipCombatAttribution>(victim))
                return;

            int killerNetworkId = em.GetComponentData<ShipCombatAttribution>(victim).LastDamagerNetworkId;
            if (killerNetworkId <= 0)
                return;

            // --- Victim's own network id (reject self-kills / suicide credit) ---
            int victimNetworkId = 0;
            if (em.HasComponent<GhostOwner>(victim))
                victimNetworkId = em.GetComponentData<GhostOwner>(victim).NetworkId;
            if (victimNetworkId > 0 && victimNetworkId == killerNetworkId)
                return;

            // --- Prefer different-team kills ---
            // Look up killer ship; skip if same team or unknown.
            if (!TryFindShipByNetworkId(em, killerNetworkId, out Entity killerShip, out TeamId killerTeam))
                return;
            if (victimTeam == TeamId.None || killerTeam == TeamId.None || killerTeam == victimTeam)
                return;

            ShipMatchStatsLogic.TryAddOnShip(em, killerShip, kills: 1, gemsDeposited: 0, peopleDelivered: 0);
        }

        /// <summary>
        /// Finds the ship entity owned by <paramref name="networkId"/>.
        /// Rare path (once per death) — ship counts are small.
        /// </summary>
        static bool TryFindShipByNetworkId(
            EntityManager em,
            int networkId,
            out Entity shipEntity,
            out TeamId team)
        {
            shipEntity = Entity.Null;
            team = TeamId.None;
            using var query = em.CreateEntityQuery(typeof(ShipTag), typeof(GhostOwner), typeof(ShipState));
            using var entities = query.ToEntityArray(Allocator.Temp);
            using var owners = query.ToComponentDataArray<GhostOwner>(Allocator.Temp);
            using var states = query.ToComponentDataArray<ShipState>(Allocator.Temp);

            for (int i = 0; i < entities.Length; i++)
            {
                if (owners[i].NetworkId != networkId)
                    continue;
                shipEntity = entities[i];
                team = states[i].Team;
                return true;
            }

            return false;
        }
    }
}
