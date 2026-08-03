using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// One planetary defense slot on a planet ghost. Buffer length = planet level when owned
    /// (0 when neutral). Not a separate turret ghost — combat/visuals derive pose from planet
    /// transform + slot index via <c>PlanetaryDefenseMath</c>.
    /// <para>
    /// [NETCODE] Must be baked on the planet ghost prefab. Runtime-only
    /// <c>AddBuffer</c> does <b>not</b> replicate <see cref="GhostField"/> values.
    /// <see cref="InternalBufferCapacityAttribute"/> 6 = MaxPlanetLevel so snapshots can grow
    /// without realloc thrash.
    /// </para>
    /// <para>
    /// [TITAN-ORBIT] <see cref="TurretLevel"/> 0 = empty placeholder (build progress fills toward
    /// level 1). Destroyed turrets and planet captures reset the slot to empty.
    /// </para>
    /// </summary>
    [InternalBufferCapacity(6)]
    public struct PlanetaryDefenseSlotElement : IBufferElementData
    {
        /// <summary>0-based index; angles use this with buffer length as slot count.</summary>
        [GhostField] public byte SlotIndex;

        /// <summary>0 = empty/building; 1..6 = active turret level.</summary>
        [GhostField] public byte TurretLevel;

        /// <summary>Gems contributed toward the next activation or upgrade rung.</summary>
        [GhostField(Quantization = 100)] public float BuildProgress;

        /// <summary>Current HP when <see cref="TurretLevel"/> &gt; 0; ignored when empty.</summary>
        [GhostField(Quantization = 100)] public float Health;

        /// <summary>Max HP for the current turret level (mirrors config at last activate/upgrade).</summary>
        [GhostField(Quantization = 100)] public float MaxHealth;
    }

    /// <summary>
    /// Server-only cache so slot sync can detect ownership / level flips without scanning history.
    /// Not ghosted — clients only need the slot buffer.
    /// </summary>
    public struct PlanetaryDefenseServerCache : IComponentData
    {
        /// <summary>Last ownership applied by <c>PlanetaryDefenseSlotSyncSystem</c>.</summary>
        public TeamId LastOwnership;

        /// <summary>Last planet level that drove buffer length.</summary>
        public int LastPlanetLevel;

        /// <summary>True after the cache has been initialized once.</summary>
        public bool Initialized;
    }

    /// <summary>
    /// Shared slot-buffer helpers used by deposit, combat, capture wipe, and slot sync.
    /// </summary>
    public static class PlanetaryDefenseLogic
    {
        /// <summary>
        /// Clears every slot to an empty placeholder (progress 0, level 0, no HP).
        /// Called on capture, ownership loss, and turret destruction.
        /// </summary>
        public static void WipeAllSlots(DynamicBuffer<PlanetaryDefenseSlotElement> buffer)
        {
            for (int i = 0; i < buffer.Length; i++)
                buffer[i] = CreateEmptySlot((byte)i);
        }

        /// <summary>Resets one slot to empty placeholder.</summary>
        public static void ResetSlot(DynamicBuffer<PlanetaryDefenseSlotElement> buffer, int slotIndex)
        {
            if (slotIndex < 0 || slotIndex >= buffer.Length)
                return;
            buffer[slotIndex] = CreateEmptySlot((byte)slotIndex);
        }

        /// <summary>Empty placeholder element for the given index.</summary>
        public static PlanetaryDefenseSlotElement CreateEmptySlot(byte slotIndex)
        {
            return new PlanetaryDefenseSlotElement
            {
                SlotIndex = slotIndex,
                TurretLevel = 0,
                BuildProgress = 0f,
                Health = 0f,
                MaxHealth = 0f,
            };
        }

        /// <summary>
        /// Ensures buffer length matches <paramref name="desiredCount"/>. Preserves existing
        /// slot data by index when growing; truncates when shrinking. When
        /// <paramref name="wipeExisting"/> is true, all slots become empty placeholders.
        /// </summary>
        public static void EnsureSlotCount(
            DynamicBuffer<PlanetaryDefenseSlotElement> buffer,
            int desiredCount,
            bool wipeExisting)
        {
            desiredCount = math.max(0, desiredCount);

            if (wipeExisting)
            {
                buffer.Clear();
                for (int i = 0; i < desiredCount; i++)
                    buffer.Add(CreateEmptySlot((byte)i));
                return;
            }

            // Grow — new slots start empty; existing turrets keep level/HP/progress.
            while (buffer.Length < desiredCount)
                buffer.Add(CreateEmptySlot((byte)buffer.Length));

            // Shrink — drop highest indices (those slots no longer exist at this planet level).
            while (buffer.Length > desiredCount)
                buffer.RemoveAt(buffer.Length - 1);

            // Re-stamp indices after any mutation.
            for (int i = 0; i < buffer.Length; i++)
            {
                var slot = buffer[i];
                slot.SlotIndex = (byte)i;
                buffer[i] = slot;
            }
        }

        /// <summary>
        /// Seeds a random subset of empty defense pads with active turrets for map start
        /// (home planets and starting owned neutrals).
        /// <paramref name="maxTurretsAndLevel"/> 0 = no-op. Otherwise places a random count of
        /// 0..<paramref name="maxTurretsAndLevel"/> turrets (capped by buffer length), each at a
        /// random level 1..<paramref name="maxTurretsAndLevel"/> (also capped by
        /// <see cref="PlanetaryDefenseMath.GetMaxTurretLevelForPlanet"/>).
        /// </summary>
        /// <param name="buffer">Owned planet slot buffer (already sized / wiped empty).</param>
        /// <param name="rng">Match RNG from map generation (deterministic with fixed seed).</param>
        /// <param name="maxTurretsAndLevel">Designer N from Map Generation Settings.</param>
        /// <param name="planetLevel">Planet level for turret-level cap.</param>
        /// <param name="config">Defense config for max HP at each turret level (nullable → fallback HP).</param>
        public static void SeedRandomStartingTurrets(
            DynamicBuffer<PlanetaryDefenseSlotElement> buffer,
            ref Random rng,
            int maxTurretsAndLevel,
            int planetLevel,
            PlanetaryDefenseConfig config)
        {
            // --- Early outs ---
            if (maxTurretsAndLevel <= 0 || buffer.Length <= 0)
                return;

            int maxCount = math.min(maxTurretsAndLevel, buffer.Length);
            int maxLevel = math.min(
                maxTurretsAndLevel,
                PlanetaryDefenseMath.GetMaxTurretLevelForPlanet(planetLevel));
            if (maxCount < 1 || maxLevel < 1)
                return;

            // "Up to N" includes zero — some claimed neutrals stay empty pads.
            int placeCount = rng.NextInt(0, maxCount + 1);
            if (placeCount <= 0)
                return;

            // --- Pick distinct slot indices (bitmask — max planet level is 6 slots) ---
            int usedMask = 0;
            for (int n = 0; n < placeCount; n++)
            {
                int idx = 0;
                for (int guard = 0; guard < 64; guard++)
                {
                    idx = rng.NextInt(0, buffer.Length);
                    if (((usedMask >> idx) & 1) == 0)
                        break;
                }

                usedMask |= 1 << idx;

                int level = rng.NextInt(1, maxLevel + 1);
                float hp = 40f * level;
                if (config != null)
                    hp = math.max(1f, config.GetLevelStats(level).maxHealth);

                buffer[idx] = new PlanetaryDefenseSlotElement
                {
                    SlotIndex = (byte)idx,
                    TurretLevel = (byte)level,
                    BuildProgress = 0f,
                    Health = hp,
                    MaxHealth = hp,
                };
            }
        }
    }
}
