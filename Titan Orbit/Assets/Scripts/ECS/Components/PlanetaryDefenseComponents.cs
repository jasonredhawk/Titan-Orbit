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
    /// <see cref="InternalBufferCapacityAttribute"/> 6 = max pad count (planet level), not turret
    /// level — crown Lv7 is still one slot with <c>TurretLevel == 7</c>. Every field on this buffer
    /// <b>must</b> be a <see cref="GhostField"/> (NetCode rule for ghost buffers). Regen clocks live
    /// on the server-only <see cref="PlanetaryDefenseSlotRegenElement"/> buffer instead.
    /// </para>
    /// <para>
    /// [TITAN-ORBIT] <see cref="TurretLevel"/> 0 = empty placeholder (build progress fills toward
    /// level 1). Destroyed turrets and planet captures reset the slot to empty.
    /// <see cref="OccupiedByNetworkId"/> is the GhostOwner NetworkId of the player currently
    /// controlling this turret (0 = free). Only one player may occupy a slot.
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

        /// <summary>
        /// Current HP when <see cref="TurretLevel"/> &gt; 0; ignored when empty.
        /// Server combat writes this. On clients, live HP is
        /// <see cref="PlanetaryDefenseClientHealthSync"/> (HitRpc); this ghost field is
        /// layout seed / regen, not the combat channel.
        /// </summary>
        [GhostField(Quantization = 100)] public float Health;

        /// <summary>Max HP for the current turret level (mirrors config at last activate/upgrade).</summary>
        [GhostField(Quantization = 100)] public float MaxHealth;

        /// <summary>
        /// [NETCODE] GhostOwner.NetworkId of the player piloting this turret, or 0 when free.
        /// Clients use this to hide Take Control when another player already occupies the pad.
        /// </summary>
        [GhostField] public int OccupiedByNetworkId;
    }

    /// <summary>
    /// Server-only per-slot regen clock, indexed like <see cref="PlanetaryDefenseSlotElement"/>.
    /// <para>
    /// [NETCODE] Not a ghost buffer — last-damage times must not be forced onto the ghosted
    /// slot element (ghost buffers require every field to be a <see cref="GhostField"/>).
    /// Clients only need replicated Health for the HP bar.
    /// </para>
    /// </summary>
    [InternalBufferCapacity(6)]
    public struct PlanetaryDefenseSlotRegenElement : IBufferElementData
    {
        /// <summary>
        /// Server <c>World.ElapsedTime</c> of the last HP damage on this slot.
        /// Same idea as <see cref="ShipVitalsState.LastHullDamageTime"/>.
        /// </summary>
        public double LastDamageServerTime;
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
                OccupiedByNetworkId = 0,
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
        /// Ensures the server-only regen buffer matches <paramref name="desiredCount"/> slot indices.
        /// When <paramref name="wipeExisting"/> is true, all last-damage times reset to 0.
        /// </summary>
        public static void EnsureRegenSlotCount(
            DynamicBuffer<PlanetaryDefenseSlotRegenElement> regen,
            int desiredCount,
            bool wipeExisting)
        {
            desiredCount = math.max(0, desiredCount);

            if (wipeExisting)
            {
                regen.Clear();
                for (int i = 0; i < desiredCount; i++)
                    regen.Add(new PlanetaryDefenseSlotRegenElement { LastDamageServerTime = 0.0 });
                return;
            }

            while (regen.Length < desiredCount)
                regen.Add(new PlanetaryDefenseSlotRegenElement { LastDamageServerTime = 0.0 });

            while (regen.Length > desiredCount)
                regen.RemoveAt(regen.Length - 1);
        }

        /// <summary>
        /// Creates / resizes the server-only regen buffer to match the ghosted slot buffer length.
        /// </summary>
        public static DynamicBuffer<PlanetaryDefenseSlotRegenElement> EnsureRegenBuffer(
            EntityManager em,
            Entity planetEntity,
            int slotCount,
            bool wipeExisting)
        {
            if (!em.HasBuffer<PlanetaryDefenseSlotRegenElement>(planetEntity))
                em.AddBuffer<PlanetaryDefenseSlotRegenElement>(planetEntity);

            var regen = em.GetBuffer<PlanetaryDefenseSlotRegenElement>(planetEntity);
            EnsureRegenSlotCount(regen, slotCount, wipeExisting);
            return regen;
        }

        /// <summary>Stamps last-damage time for one slot (no-op when index is out of range).</summary>
        public static void StampLastDamage(
            DynamicBuffer<PlanetaryDefenseSlotRegenElement> regen,
            int slotIndex,
            double serverElapsed)
        {
            if (slotIndex < 0 || slotIndex >= regen.Length)
                return;

            var entry = regen[slotIndex];
            entry.LastDamageServerTime = serverElapsed;
            regen[slotIndex] = entry;
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
                // Fallback when config is missing — ~Lv1 scale × level (matches ×3 HP ladder).
                float hp = 165f * level;
                if (config != null)
                    hp = math.max(1f, config.GetLevelStats(level).maxHealth);

                buffer[idx] = new PlanetaryDefenseSlotElement
                {
                    SlotIndex = (byte)idx,
                    TurretLevel = (byte)level,
                    BuildProgress = 0f,
                    Health = hp,
                    MaxHealth = hp,
                    OccupiedByNetworkId = 0,
                };
            }
        }
    }
}
