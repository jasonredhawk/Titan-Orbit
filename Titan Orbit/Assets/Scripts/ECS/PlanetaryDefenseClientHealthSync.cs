using System.Collections.Generic;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Client combat-HP channel for planetary-defense turrets — the asteroid equivalent of
    /// <see cref="ClientLocalAsteroidCombatSync"/> writing <c>AsteroidHealthAfter</c>.
    /// <para>
    /// Turrets are not their own ghosts. Pad pose, turret level, max HP, and occupancy still
    /// live on the planet ghost buffer (<see cref="PlanetaryDefenseSlotElement"/>). Live combat
    /// HP does not: that number arrives on <see cref="BulletHitRpc.PlanetaryDefenseHealthAfter"/>
    /// and is stored here. <see cref="BulletHitRpcClientSystem"/> applies it the frame the
    /// broadcast RPC arrives. The health bar and cosmetic hit spheres read this store.
    /// </para>
    /// <para>
    /// [TITAN-ORBIT] Asteroids work because HitRpc writes local rock Health and nothing else
    /// owns that field. Ships work because ship ghosts replicate Health at high Importance.
    /// Turrets get the asteroid treatment: HitRpc is the HP wire. Planet ghosts stay a layout
    /// channel (who owns the pad, how many slots, what level the gun is).
    /// </para>
    /// <para>
    /// [NETCODE] Ghost Health may seed a pad the first time you see it (no HitRpc yet). After
    /// any HitRpc for that planet×slot, this store is HP truth and is never replaced by a
    /// higher ghost value (that is a stale spawn snapshot, not a heal). Regen is the one
    /// exception: ghost HP that is already below max and rising is a real out-of-combat heal.
    /// Empty slots, capture wipes, and MaxHealth upgrades drop the entry so the next gun seeds
    /// from ghost again.
    /// </para>
    /// World: client only. Buffer writes walk <see cref="PlanetClientEntityRegistry"/> —
    /// never a planet <c>ToEntityArray</c>.
    /// </summary>
    public static class PlanetaryDefenseClientHealthSync
    {
        /// <summary>How long (seconds) the bar/turret flash lasts after a HitRpc apply.</summary>
        public const float HitFlashSeconds = 0.22f;

        /// <summary>
        /// Match window vs quantized ghost Health (GhostField Quantization = 100 → 0.01).
        /// Used for regen / upgrade detection — not a timeout.
        /// </summary>
        const float HealthEpsilon = 0.75f;

        /// <summary>planetId×slot → last HitRpc remaining HP.</summary>
        static readonly Dictionary<long, SlotHp> HpBySlot = new Dictionary<long, SlotHp>(64);

        /// <summary>Scratch for <see cref="PlanetClientEntityRegistry.CopyLive"/> (no ToEntityArray).</summary>
        static readonly List<Entity> RegistryScratch = new List<Entity>(64);

        /// <summary>Scratch keys for <see cref="ClearPlanet"/> (cannot mutate the dictionary during foreach).</summary>
        static readonly List<long> KeyScratch = new List<long>(8);

        /// <summary>
        /// One pad’s client combat HP. Not ghosted — planet snapshots cannot overwrite this.
        /// </summary>
        struct SlotHp
        {
            /// <summary>Remaining HP from the last HitRpc (0 = destroyed this hit).</summary>
            public float Health;

            /// <summary>
            /// Ghost MaxHealth when presentation last sampled this pad.
            /// A jump means upgrade / rebuild — seed from ghost again.
            /// </summary>
            public float MaxHealthAtApply;

            /// <summary>Unity <c>Time.time</c> until the hit flash ends.</summary>
            public float FlashUntil;

            /// <summary>
            /// Ghost Health last time presentation sampled it — rising while below max is regen.
            /// </summary>
            public float LastSeenGhostHealth;
        }

        /// <summary>
        /// Packs planet id + slot into one dictionary key (planet in high bits, slot in low byte).
        /// </summary>
        static long MakeKey(int planetId, int slotIndex) =>
            ((long)planetId << 8) | (byte)math.clamp(slotIndex, 0, 255);

        /// <summary>
        /// Writes server remaining HP for one pad. Called from <see cref="BulletHitRpcClientSystem"/>
        /// the same way asteroid HitRpcs write local rock Health. Rapid-fire RPCs keep the lowest HP.
        /// </summary>
        /// <param name="em">Client world EntityManager (best-effort buffer write).</param>
        /// <param name="planetId">Stable <see cref="PlanetState.PlanetId"/>.</param>
        /// <param name="slotIndex">Index in the planet’s defense buffer.</param>
        /// <param name="healthAfter">Health after this hit (0 = destroyed / empty placeholder).</param>
        public static void ApplyHitRpc(
            EntityManager em,
            int planetId,
            int slotIndex,
            float healthAfter)
        {
            if (planetId <= 0 || slotIndex < 0)
                return;

            long key = MakeKey(planetId, slotIndex);
            float rpcHp = math.max(0f, healthAfter);
            float now = Time.time;

            // --- Lowest remaining HP wins when several hits land in one tick ---
            // [TITAN-ORBIT] Same idea as asteroid ApplyAuthoritativeHealth: never heal from an
            // older RPC that was processed after a later, more damaged one.
            if (HpBySlot.TryGetValue(key, out var existing))
                rpcHp = math.min(rpcHp, existing.Health);

            HpBySlot[key] = new SlotHp
            {
                Health = rpcHp,
                MaxHealthAtApply = existing.MaxHealthAtApply,
                FlashUntil = now + HitFlashSeconds,
                LastSeenGhostHealth = existing.LastSeenGhostHealth,
            };

            // --- Best-effort write onto the client planet buffer ---
            // [NETCODE] The next planet snapshot overwrites Health. Same-frame ECS readers still
            // see this value. The bar always reads this store, not the buffer.
            TryWriteSlotHealth(em, planetId, slotIndex, rpcHp);
        }

        /// <summary>
        /// True when this pad has a HitRpc HP sample. Cosmetic spheres use it to skip a gun
        /// whose ghost Health is still spawn-full after a kill.
        /// </summary>
        /// <param name="planetId">Stable planet id.</param>
        /// <param name="slotIndex">Defense slot index.</param>
        /// <param name="health">Stored remaining HP when this returns true.</param>
        public static bool TryGetHealth(int planetId, int slotIndex, out float health)
        {
            health = 0f;
            if (planetId <= 0 || slotIndex < 0)
                return false;

            if (!HpBySlot.TryGetValue(MakeKey(planetId, slotIndex), out var slot))
                return false;

            health = slot.Health;
            return true;
        }

        /// <summary>
        /// HP the bar should show this frame. After the first HitRpc this store is truth.
        /// Ghost Health is only a seed before any shot, a regen sample (rising while below max),
        /// or a rebuilt gun (empty slot / MaxHealth change).
        /// </summary>
        /// <param name="planetId">Stable planet id.</param>
        /// <param name="slotIndex">Defense slot index.</param>
        /// <param name="slot">Current ghosted buffer element (layout / occupancy / seed HP).</param>
        /// <param name="now">Unity <c>Time.time</c> from the presentation LateUpdate.</param>
        /// <param name="hitFlash">True while the HitRpc flash window is live.</param>
        /// <param name="flashT">1 at flash peak, 0 when done.</param>
        /// <param name="overlayDestroyed">
        /// True when HitRpc said HP 0 but the ghost still shows a live turret (drain the bar).
        /// </param>
        /// <returns>Health to draw (0..MaxHealth).</returns>
        public static float ResolveDisplayHealth(
            int planetId,
            int slotIndex,
            in PlanetaryDefenseSlotElement slot,
            float now,
            out bool hitFlash,
            out float flashT,
            out bool overlayDestroyed)
        {
            hitFlash = false;
            flashT = 0f;
            overlayDestroyed = false;

            float ghostHealth = slot.Health;
            float ghostMax = slot.MaxHealth;
            bool ghostAlive = slot.TurretLevel > 0 && ghostMax > 0.01f;

            long key = MakeKey(planetId, slotIndex);
            if (!HpBySlot.TryGetValue(key, out var stored))
                return ghostHealth;

            // --- Empty pad / destroyed placeholder ---
            // [TITAN-ORBIT] Capture wipe and HP-to-0 reset TurretLevel to 0. The next built gun
            // is a new turret — drop the old HitRpc lock so it seeds from ghost MaxHealth.
            if (!ghostAlive)
            {
                HpBySlot.Remove(key);
                return 0f;
            }

            // --- Upgrade / rebuild (MaxHealth jumped) ---
            // Activate/upgrade fully heals on the server. Treat as a new gun.
            if (stored.MaxHealthAtApply > 0.01f &&
                math.abs(ghostMax - stored.MaxHealthAtApply) > HealthEpsilon)
            {
                HpBySlot.Remove(key);
                return ghostHealth;
            }

            // --- Regen ---
            // Ghost HP already below max AND rising is out-of-combat heal
            // (PlanetaryDefenseCombatSystem). Ghost still at MaxHealth is the spawn snapshot.
            // Keep the store entry — dropping it would let a later full-HP snapshot paint 100%.
            bool ghostBelowMax = ghostHealth < ghostMax - HealthEpsilon;
            bool ghostRising = ghostHealth > stored.LastSeenGhostHealth + HealthEpsilon;
            if (ghostBelowMax &&
                ghostRising &&
                ghostHealth > stored.Health + HealthEpsilon)
            {
                stored.Health = ghostHealth;
            }

            stored.LastSeenGhostHealth = ghostHealth;
            if (stored.MaxHealthAtApply <= 0.01f && ghostMax > 0.01f)
                stored.MaxHealthAtApply = ghostMax;
            HpBySlot[key] = stored;

            overlayDestroyed = stored.Health <= 0.01f;
            if (now < stored.FlashUntil)
            {
                hitFlash = true;
                flashT = math.saturate(
                    (stored.FlashUntil - now) / math.max(0.01f, HitFlashSeconds));
            }

            // HitRpc remaining HP — never a higher ghost spawn snapshot.
            return math.max(0f, stored.Health);
        }

        /// <summary>Drops every pad (leave session / Play Mode domain reload).</summary>
        public static void Clear()
        {
            HpBySlot.Clear();
        }

        /// <summary>
        /// Drops every pad on one planet (capture / ownership wipe destroys all turrets).
        /// </summary>
        /// <param name="planetId">Stable <see cref="PlanetState.PlanetId"/>.</param>
        public static void ClearPlanet(int planetId)
        {
            if (planetId <= 0 || HpBySlot.Count == 0)
                return;

            KeyScratch.Clear();
            foreach (var kv in HpBySlot)
            {
                if ((int)(kv.Key >> 8) == planetId)
                    KeyScratch.Add(kv.Key);
            }

            for (int i = 0; i < KeyScratch.Count; i++)
                HpBySlot.Remove(KeyScratch[i]);
        }

        /// <summary>
        /// Writes Health onto the client planet buffer for same-frame ECS readers.
        /// Walks <see cref="PlanetClientEntityRegistry"/> — never a planet archetype gather.
        /// </summary>
        static void TryWriteSlotHealth(EntityManager em, int planetId, int slotIndex, float health)
        {
            if (!em.World.IsCreated)
                return;

            PlanetClientEntityRegistry.CopyLive(RegistryScratch);
            for (int i = 0; i < RegistryScratch.Count; i++)
            {
                Entity entity = RegistryScratch[i];
                if (entity == Entity.Null ||
                    !em.Exists(entity) ||
                    !em.HasComponent<PlanetState>(entity) ||
                    !em.HasBuffer<PlanetaryDefenseSlotElement>(entity))
                    continue;

                if (em.GetComponentData<PlanetState>(entity).PlanetId != planetId)
                    continue;

                var buffer = em.GetBuffer<PlanetaryDefenseSlotElement>(entity);
                if (slotIndex < 0 || slotIndex >= buffer.Length)
                    return;

                var slot = buffer[slotIndex];
                slot.Health = health;
                buffer[slotIndex] = slot;
                return;
            }
        }
    }
}
