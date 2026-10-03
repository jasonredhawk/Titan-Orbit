using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using TitanOrbit.Entities;
using Unity.Entities;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Client-only: loops the bullet-bank impact ("end") particle on a ship proxy for the
    /// remaining burn DoT or electric-shock stun. Reads ghosted
    /// <see cref="ShipBurnOverTimeState"/> / <see cref="ShipElectricShockState"/>.
    /// Cosmetic — no sim change. Attached by <see cref="EcsWorldVisualizer"/>.
    /// </summary>
    [DefaultExecutionOrder(108)]
    public class ShipStatusLoopVfxApplier : MonoBehaviour
    {
        const float ShockLocalY = 0.55f;
        const float BurnLocalY = 0.2f;

        /// <summary>
        /// How often the burn slot re-emits the impact burst. Matches the default DoT tick
        /// so the hull keeps burning instead of waiting out the prefab's 2s one-shot duration.
        /// </summary>
        const float BurnReplayInterval = 0.25f;

        Entity _shipEntity;
        readonly Slot _shock = new Slot();
        readonly Slot _burn = new Slot();

        /// <summary>Frame of the cached ServerTick sample. One query pair per presentation frame.</summary>
        static int s_SharedClockFrame = -1;

        static double s_SharedClockElapsed;
        static bool s_SharedClockValid;

        /// <summary>Links this proxy to the ship ghost that owns burn / shock state.</summary>
        public void Bind(Entity shipEntity)
        {
            ReleaseAll();
            _shipEntity = shipEntity;
        }

        void OnDestroy() => ReleaseAll();

        void LateUpdate()
        {
            if (_shipEntity == Entity.Null)
                return;

            if (ClientJoinSettleCache.ShouldSkipShipEntityQueries)
                return;

            if (TitanOrbitDebugFlags.IsolateDisableImpactVfx)
            {
                ReleaseAll();
                return;
            }

            var world = EcsGameBridge.GetVisualizationWorld();
            if (world == null || !world.IsCreated)
            {
                ReleaseAll();
                return;
            }

            var em = world.EntityManager;
            if (!em.Exists(_shipEntity) || !em.HasComponent<ShipState>(_shipEntity))
            {
                ReleaseAll();
                return;
            }

            if (em.GetComponentData<ShipState>(_shipEntity).IsDead)
            {
                ReleaseAll();
                return;
            }

            // ExpiresAt is server sim elapsed. ClientWorld.Time starts at join, so a
            // late joiner's world clock stays behind that timestamp and the lightning
            // impact (loop forced on for the stun) never reaches ReleaseSlot.
            // ServerTick seconds are the same timeline on server and client.
            if (!TryGetSharedStatusElapsed(em, out double elapsed))
            {
                ReleaseAll();
                return;
            }

            bool shockActive = false;
            int shockBank = 0;
            byte shockTeam = 0;
            if (em.HasComponent<ShipElectricShockState>(_shipEntity))
            {
                var shock = em.GetComponentData<ShipElectricShockState>(_shipEntity);
                shockActive = shock.IsActive(elapsed);
                shockBank = shock.VfxBankIndex;
                shockTeam = shock.VfxTeam;
            }

            bool burnActive = false;
            int burnBank = 0;
            byte burnTeam = 0;
            if (em.HasComponent<ShipBurnOverTimeState>(_shipEntity))
            {
                var burn = em.GetComponentData<ShipBurnOverTimeState>(_shipEntity);
                burnActive = burn.IsActive(elapsed);
                burnBank = burn.VfxBankIndex;
                burnTeam = burn.VfxTeam;
            }

            // Shock has no damage ticks — keep the impact looping for the stun window.
            // Burn re-emits that same impact on the hull for the whole DoT. Sequence-0 burn
            // ticks are not ram sparks; asteroids get the same burst from BurnImpactLoop.
            SyncSlot(_shock, shockActive, shockBank, shockTeam, ShockLocalY);
            SyncSlot(_burn, burnActive, burnBank, burnTeam, BurnLocalY);
            if (burnActive)
                ReplayBurnIfDue(_burn);
        }

        /// <summary>
        /// Impact prefabs burst once per multi-second duration. While the burn ghost is active,
        /// emit that burst again so the ship shows fire for every damage tick.
        /// </summary>
        void ReplayBurnIfDue(Slot slot)
        {
            if (slot.Instance == null)
                return;
            if (Time.time < slot.NextReplay)
                return;

            slot.NextReplay = Time.time + BurnReplayInterval;
            VfxUrpCompat.ReplayParticleBursts(slot.Instance);
        }

        void SyncSlot(Slot slot, bool active, int bankIndex, byte team, float localY)
        {
            if (!active)
            {
                ReleaseSlot(slot);
                return;
            }

            if (slot.Instance != null &&
                (slot.BankIndex != bankIndex || slot.Team != team))
                ReleaseSlot(slot);

            if (slot.Instance == null)
                TryStartSlot(slot, bankIndex, team, localY);
        }

        void TryStartSlot(Slot slot, int bankIndex, byte team, float localY)
        {
            BulletVfxBank bank = BulletVfxBank.LoadDefault();
            if (bank == null)
                return;

            GameObject prefab = bank.GetImpactPrefab(bankIndex, (TeamId)team);
            if (prefab == null)
                return;

            if (!BulletOneShotVfxPool.TryRent(prefab, out GameObject go) || go == null)
                return;

            go.name = prefab.name + "_StatusLoop";
            go.transform.SetParent(transform, false);
            go.transform.localPosition = new Vector3(0f, localY, 0f);
            go.transform.localRotation = Quaternion.identity;

            float parentLossy = transform.lossyScale.x;
            if (parentLossy < 0.0001f)
                parentLossy = 0.0001f;
            float worldScale = BulletVisualFactory.GetImpactScale(bank, 1f, bankIndex);
            VfxUrpCompat.ApplyImpactVisualScale(go, worldScale / parentLossy);

            MuteAudio(go);
            VfxUrpCompat.SetParticleSystemsLooping(go, true);

            slot.Instance = go;
            slot.BankIndex = bankIndex;
            slot.Team = team;
            // Start already emitted the burst. The next replay is one DoT step later.
            slot.NextReplay = Time.time + BurnReplayInterval;
        }

        void ReleaseAll()
        {
            ReleaseSlot(_shock);
            ReleaseSlot(_burn);
        }

        static void ReleaseSlot(Slot slot)
        {
            if (slot.Instance == null)
            {
                slot.BankIndex = -1;
                slot.Team = 0;
                return;
            }

            GameObject go = slot.Instance;
            slot.Instance = null;
            slot.BankIndex = -1;
            slot.Team = 0;
            slot.NextReplay = 0f;

            VfxUrpCompat.SetParticleSystemsLooping(go, false);
            RestoreAudio(go);
            BulletOneShotVfxPool.ReturnNow(go);
        }

        /// <summary>
        /// ServerTick seconds for this presentation frame. Cached so each ship proxy does not
        /// allocate a NetworkTime query. No World.Time fallback — that clock is join-local.
        /// </summary>
        static bool TryGetSharedStatusElapsed(EntityManager em, out double elapsed)
        {
            int frame = Time.frameCount;
            if (frame != s_SharedClockFrame)
            {
                s_SharedClockFrame = frame;
                s_SharedClockValid = PlanetGemMoonOrbitClock.TryGetElapsedSeconds(
                    em, out float tickElapsed, includeTickFraction: true);
                s_SharedClockElapsed = tickElapsed;
            }

            elapsed = s_SharedClockElapsed;
            return s_SharedClockValid;
        }

        static void MuteAudio(GameObject root)
        {
            AudioSource[] sources = root.GetComponentsInChildren<AudioSource>(true);
            for (int i = 0; i < sources.Length; i++)
            {
                if (sources[i] != null)
                    sources[i].enabled = false;
            }
        }

        static void RestoreAudio(GameObject root)
        {
            AudioSource[] sources = root.GetComponentsInChildren<AudioSource>(true);
            for (int i = 0; i < sources.Length; i++)
            {
                if (sources[i] != null)
                    sources[i].enabled = true;
            }
        }

        sealed class Slot
        {
            public GameObject Instance;
            public int BankIndex = -1;
            public byte Team;
            public float NextReplay;
        }
    }
}
