using TitanOrbit;
using TitanOrbit.Data;
using TitanOrbit.Generation;
using TitanOrbit.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server-authoritative E mine place. Consumes one charge from the selected mine
    /// equipment slot (unless <see cref="TitanOrbitDebugFlags.InfiniteMines"/>), then
    /// appends a <see cref="DeployedMineElement"/> on the owner ship ghost.
    /// <para>
    /// [TITAN-ORBIT] Drop cooldown comes from <see cref="MineCatalog.LevelStats.deployCooldown"/>.
    /// <see cref="ShipLoadoutState.NextMinePlaceTime"/> is ghosted so the HUD can show the wait.
    /// Infinite-mine debug skips the charge consume, keeps the cooldown, and places at
    /// the ship's live level (not the pack's stamped <c>ItemLevel</c>).
    /// </para>
    /// Paired with <see cref="MineSimulationSystem"/> (detonate) and <c>RocketLoadoutHUD</c>
    /// (client readout). World: ServerSimulation.
    /// </summary>
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(PredictedFixedStepSimulationSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    public partial struct ShipMineDeploySystem : ISystem
    {
        /// <summary>Wait until the match is in-game before accepting place input.</summary>
        public void OnCreate(ref SystemState state)
        {
            state.RequireForUpdate<NetworkStreamInGame>();
        }

        /// <summary>
        /// For each ship with PlaceMine this tick: validate, consume, append a deployed mine.
        /// </summary>
        public void OnUpdate(ref SystemState state)
        {
            if (!SystemAPI.TryGetSingleton<MapStateSingleton>(out var mapState) ||
                !ToroidalMapEcs.IsValidMapSize(mapState.MapWidth, mapState.MapHeight))
                return;

            double serverElapsed = SystemAPI.Time.ElapsedTime;

            foreach (var (input, shipState, loadout, transform, ghostOwner, entity) in SystemAPI
                         .Query<RefRO<ShipInput>, RefRO<ShipState>, RefRW<ShipLoadoutState>,
                             RefRO<LocalTransform>, RefRO<GhostOwner>>()
                         .WithAll<ShipTag>()
                         .WithEntityAccess())
            {
                if (!input.ValueRO.PlaceMine.IsSet)
                    continue;
                if (shipState.ValueRO.IsDead || shipState.ValueRO.AwaitingTeamSelection)
                    continue;

                // --- Possession / orbit gates (same as rockets) ---
                if (SystemAPI.HasComponent<ShipTurretControlState>(entity) &&
                    SystemAPI.GetComponentRO<ShipTurretControlState>(entity).ValueRO.IsControlling)
                    continue;
                if (SystemAPI.HasComponent<ShipOrbitState>(entity) &&
                    SystemAPI.GetComponentRO<ShipOrbitState>(entity).ValueRO.InOrbitRing)
                    continue;

                bool infinite = TitanOrbitDebugFlags.InfiniteMines;
                if (!TryResolveMineDrop(
                        state.EntityManager, entity, infinite,
                        input.ValueRO.SelectedMineSlot,
                        out int itemLevel, out int consumeSlot))
                    continue;

                MineShotMath.Resolve(itemLevel, out var stats, out float damage, out float visualScale);

                if (loadout.ValueRO.NextMinePlaceTime > serverElapsed + 0.0001)
                    continue;

                if (!state.EntityManager.HasBuffer<DeployedMineElement>(entity))
                    state.EntityManager.AddBuffer<DeployedMineElement>(entity);

                // --- Consume one charge (debug infinite skips this) ---
                if (!infinite && consumeSlot >= 0)
                    ConsumeMineCharge(state.EntityManager, entity, consumeSlot);

                loadout.ValueRW.NextMinePlaceTime = serverElapsed + stats.deployCooldown;

                // --- Drop at the hull (flight-plane Y). Friendly ships do not trigger. ---
                float3 pos = transform.ValueRO.Position;
                pos.y = transform.ValueRO.Position.y;

                var mines = state.EntityManager.GetBuffer<DeployedMineElement>(entity);
                mines.Add(new DeployedMineElement
                {
                    Position = pos,
                    OwnerTeam = (byte)shipState.ValueRO.Team,
                    OwnerNetworkId = ghostOwner.ValueRO.NetworkId,
                    ItemLevel = math.max(1, itemLevel),
                    Sequence = BulletVfxBridge.NextSequence(),
                    ExpireTime = serverElapsed + math.max(0.1f, stats.lifetime),
                    PlaceTime = serverElapsed,
                    Damage = math.max(0.1f, damage),
                    HitRadius = math.max(0.1f, stats.hitRadius),
                    BlastRadius = math.max(0.1f, stats.blastRadius),
                    BlastForce = math.max(0.1f, stats.blastForce),
                    VisualScale = math.max(0.05f, visualScale),
                    ExplosionVfxScale = math.max(0.05f, stats.explosionVfxScale),
                });
            }
        }

        /// <summary>
        /// Picks the HUD-selected mine pack. Infinite debug skips the charge consume;
        /// a selected pack still places at that pack's stamped level. Empty loadout + infinite
        /// uses the ship's live level.
        /// </summary>
        static bool TryResolveMineDrop(
            EntityManager em,
            Entity shipEntity,
            bool infinite,
            int selectedHudIndex,
            out int itemLevel,
            out int consumeSlot)
        {
            itemLevel = 1;
            consumeSlot = -1;

            int shipLevel = 1;
            if (em.HasComponent<ShipState>(shipEntity))
                shipLevel = math.max(1, em.GetComponentData<ShipState>(shipEntity).ShipLevel);

            if (!em.HasBuffer<EquippedEquipmentElement>(shipEntity))
            {
                if (!infinite)
                    return false;
                itemLevel = shipLevel;
                return true;
            }

            var buffer = em.GetBuffer<EquippedEquipmentElement>(shipEntity);
            int want = math.max(0, selectedHudIndex);
            int mineCount = 0;
            int lastBuffer = -1;
            int lastLevel = 1;
            int matchBuffer = -1;
            int matchLevel = 1;
            for (int i = 0; i < buffer.Length; i++)
            {
                var entry = buffer[i];
                if (!StoreItemData.IsMine((StoreItemType)entry.ItemType))
                    continue;
                if (entry.RemainingCharges <= 0)
                    continue;

                int packLevel = math.max(1, entry.ItemLevel);
                lastBuffer = i;
                lastLevel = packLevel;
                if (mineCount == want)
                {
                    matchBuffer = i;
                    matchLevel = packLevel;
                }

                mineCount++;
            }

            if (mineCount > 0)
            {
                // Past-the-end caret clamps to the last pack.
                itemLevel = matchBuffer >= 0 ? matchLevel : lastLevel;
                consumeSlot = matchBuffer >= 0 ? matchBuffer : lastBuffer;
                return true;
            }

            if (!infinite)
                return false;

            itemLevel = shipLevel;
            return true;
        }

        /// <summary>Decrements charges; removes the slot when the pack is empty so it frees.</summary>
        static void ConsumeMineCharge(EntityManager em, Entity shipEntity, int slotIndex)
        {
            var buffer = em.GetBuffer<EquippedEquipmentElement>(shipEntity);
            if (slotIndex < 0 || slotIndex >= buffer.Length)
                return;

            var entry = buffer[slotIndex];
            entry.RemainingCharges = math.max(0, entry.RemainingCharges - 1);
            if (entry.RemainingCharges <= 0)
                buffer.RemoveAt(slotIndex);
            else
                buffer[slotIndex] = entry;
        }
    }
}
