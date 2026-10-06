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
    /// higher ghost value (that is a stale spawn snapshot, not a heal). Out-of-combat regen is
    /// drawn on the client with the same delay and HP/s as
    /// <c>PlanetaryDefenseCombatSystem</c> — planet ghosts are static and only send Health a
    /// few times a second, so waiting on that field left the bar frozen until the next shot.
    /// A ghost sample that is still below max and rising ahead of that prediction replaces it.
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
        /// MaxHealth jump that means upgrade / rebuild (not regen noise).
        /// GhostField Quantization = 100 is 0.01; turret max HP steps are much larger.
        /// </summary>
        const float HealthEpsilon = 0.75f;

        /// <summary>
        /// Smallest ghost Health rise that counts as a real snapshot step.
        /// Above quantization (0.01) and below one frame of regen (~0.05 at 3 HP/s).
        /// </summary>
        const float GhostRiseEpsilon = 0.02f;

        /// <summary>
        /// Hits that land in this window keep the lowest remaining HP so a reordered
        /// RPC cannot paint a heal. A later shot, after regen, replaces HP outright.
        /// </summary>
        const float RapidFireReorderSeconds = 0.35f;

        /// <summary>Cap on one presentation regen step so a hitch cannot fill the bar.</summary>
        const float MaxRegenStepSeconds = 0.1f;

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
            /// Ghost Health last time presentation sampled it — a rise while below max
            /// means the server snapshot is ahead of local regen.
            /// </summary>
            public float LastSeenGhostHealth;

            /// <summary>Unity <c>Time.time</c> of the last HitRpc (reorder window).</summary>
            public float LastRpcClientTime;

            /// <summary>
            /// Unity <c>Time.time</c> of the last HitRpc that reduced HP.
            /// Presentation regen waits <c>healthRegenDelayAfterDamage</c> from here.
            /// </summary>
            public float LastDamageClientTime;
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

            // --- Lowest remaining HP wins inside one burst ---
            // [TITAN-ORBIT] Same idea as asteroid ApplyAuthoritativeHealth: a reordered RPC
            // must not heal. After the burst, remaining HP is the new sample — regen may
            // have raised the bar, and this shot's healthAfter is the server value.
            bool had = HpBySlot.TryGetValue(key, out var existing);
            float previous = had ? existing.Health : rpcHp;
            if (had && (now - existing.LastRpcClientTime) <= RapidFireReorderSeconds)
                rpcHp = math.min(rpcHp, existing.Health);

            float lastDamage = had ? existing.LastDamageClientTime : now;
            if (!had || rpcHp < previous - GhostRiseEpsilon)
                lastDamage = now;

            HpBySlot[key] = new SlotHp
            {
                Health = rpcHp,
                MaxHealthAtApply = existing.MaxHealthAtApply,
                FlashUntil = now + HitFlashSeconds,
                LastSeenGhostHealth = existing.LastSeenGhostHealth,
                LastRpcClientTime = now,
                LastDamageClientTime = lastDamage,
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
        /// Ghost Health seeds a pad before any shot, and a rising below-max sample can pull
        /// the bar forward. Between shots, <paramref name="regenPerSecond"/> fills the bar
        /// after <paramref name="regenDelaySeconds"/> — same contract as server combat.
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
        /// <param name="regenPerSecond">HP per second after the delay. 0 disables presentation regen.</param>
        /// <param name="regenDelaySeconds">Seconds after the last damaging hit before the bar climbs.</param>
        /// <param name="deltaTime">Frame dt used for one regen step.</param>
        /// <returns>Health to draw (0..MaxHealth).</returns>
        public static float ResolveDisplayHealth(
            int planetId,
            int slotIndex,
            in PlanetaryDefenseSlotElement slot,
            float now,
            out bool hitFlash,
            out float flashT,
            out bool overlayDestroyed,
            float regenPerSecond,
            float regenDelaySeconds,
            float deltaTime)
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
            // [TITAN-ORBIT] Same pause as PlanetaryDefenseCombatSystem: the bar stays on the
            // hit until healthRegenDelayAfterDamage, then climbs. Ghost still at MaxHealth is
            // the spawn snapshot — do not copy it. A below-max rise ahead of the bar is a
            // fresher server sample; otherwise tick locally. Planet ghosts are Static /
            // importance 40 / 15 Hz, and the old 0.75 rise test never saw one interpolated
            // frame of ~3 HP/s, so the strip stayed on the last hit until the next shot.
            // Keep the store entry — dropping it would let a later full-HP snapshot paint 100%.
            float delay = math.max(0f, regenDelaySeconds);
            bool regenUnlocked = stored.LastDamageClientTime > 0f &&
                                 now >= stored.LastDamageClientTime + delay;
            bool followGhost = false;
            if (regenUnlocked && stored.LastSeenGhostHealth > GhostRiseEpsilon)
            {
                bool ghostBelowMax = ghostHealth < ghostMax - GhostRiseEpsilon;
                bool ghostRising = ghostHealth > stored.LastSeenGhostHealth + GhostRiseEpsilon;
                if (ghostBelowMax &&
                    ghostRising &&
                    ghostHealth > stored.Health + GhostRiseEpsilon)
                {
                    stored.Health = ghostHealth;
                    followGhost = true;
                }
            }

            if (!followGhost &&
                regenUnlocked &&
                regenPerSecond > 0f &&
                stored.Health > 0.01f &&
                stored.Health < ghostMax - GhostRiseEpsilon)
            {
                float step = math.clamp(deltaTime, 0f, MaxRegenStepSeconds);
                stored.Health = math.min(
                    ghostMax,
                    stored.Health + regenPerSecond * step);
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
