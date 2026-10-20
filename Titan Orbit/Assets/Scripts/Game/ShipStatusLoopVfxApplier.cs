using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using TitanOrbit.Entities;
using Unity.Entities;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Client-only: plays the bullet-bank impact on a ship proxy for burn DoT or
    /// electric-shock stun. Reads ghosted <see cref="ShipBurnOverTimeState"/> /
    /// <see cref="ShipElectricShockState"/>. Burn sits on the ghosted collider contact
    /// (<see cref="ShipBurnOverTimeState.HitLocalX"/>) and replays that impact each time
    /// <see cref="ShipBurnOverTimeState.TickSequence"/> advances. Shock stays on the hull
    /// center for the stun window. Cosmetic — no sim change. Attached by <see cref="EcsWorldVisualizer"/>.
    /// </summary>
    [DefaultExecutionOrder(108)]
    public class ShipStatusLoopVfxApplier : MonoBehaviour
    {
        const float ShockLocalY = 0.55f;
        const float BurnLocalY = 0.2f;

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
            // New proxy. The next sample snapshots TickSequence so a hull that is
            // already burning shows fire, without treating 0 as a fresh tick.
            _burn.AwaitingBaseline = true;
            _burn.LastTickSequence = 0;
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

            bool haveBurn = em.HasComponent<ShipBurnOverTimeState>(_shipEntity);
            bool burnActive = false;
            int burnBank = 0;
            byte burnTeam = 0;
            uint burnSeq = 0;
            Vector2 burnLocalXZ = Vector2.zero;
            if (haveBurn)
            {
                var burn = em.GetComponentData<ShipBurnOverTimeState>(_shipEntity);
                burnActive = burn.IsActive(elapsed);
                burnBank = burn.VfxBankIndex;
                burnTeam = burn.VfxTeam;
                burnSeq = burn.TickSequence;
                burnLocalXZ = new Vector2(burn.HitLocalX, burn.HitLocalZ);
            }

            // Shock has no damage ticks — keep the impact looping on the hull center.
            // Burn replays the same impact on each ghosted damage tick.
            SyncSlot(_shock, shockActive, shockBank, shockTeam, ShockLocalY, Vector2.zero);
            SyncBurn(haveBurn, burnActive, burnSeq, burnBank, burnTeam, burnLocalXZ);
        }

        /// <summary>
        /// One impact play per burn iteration. The bullet's own hit already played the
        /// first explosion; <paramref name="seq"/> advances only when a DoT tick lands.
        /// </summary>
        void SyncBurn(bool haveBurn, bool active, uint seq, int bankIndex, byte team, Vector2 hitLocalXZ)
        {
            if (!haveBurn || !active)
            {
                ReleaseSlot(_burn);
                // Remember the counter while idle so the next bullet's impact is not
                // replayed again — only a later TickSequence step starts the burn flash.
                if (haveBurn)
                {
                    _burn.AwaitingBaseline = false;
                    _burn.LastTickSequence = seq;
                }

                return;
            }

            if (_burn.AwaitingBaseline)
            {
                _burn.AwaitingBaseline = false;
                _burn.LastTickSequence = seq;
                // Proxy appeared while this hull was already on fire (late join).
                TryStartBurn(bankIndex, team, hitLocalXZ);
                return;
            }

            if (_burn.Instance != null)
                PlaceOnHull(_burn.Instance.transform, BurnLocalY, hitLocalXZ);

            if (seq == _burn.LastTickSequence)
                return;

            _burn.LastTickSequence = seq;
            if (_burn.Instance != null &&
                (_burn.BankIndex != bankIndex || _burn.Team != team))
                ReleaseSlot(_burn);

            if (_burn.Instance == null)
                TryStartBurn(bankIndex, team, hitLocalXZ);
            else
                VfxUrpCompat.ReplayImpactAnimation(_burn.Instance);
        }

        void SyncSlot(Slot slot, bool active, int bankIndex, byte team, float localY, Vector2 hitLocalXZ)
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
                TryStartSlot(slot, bankIndex, team, localY, hitLocalXZ);
            else
                PlaceOnHull(slot.Instance.transform, localY, hitLocalXZ);
        }

        void TryStartSlot(Slot slot, int bankIndex, byte team, float localY, Vector2 hitLocalXZ)
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
            PlaceOnHull(go.transform, localY, hitLocalXZ);

            float parentLossy = transform.lossyScale.x;
            if (parentLossy < 0.0001f)
                parentLossy = 0.0001f;
            float worldScale = BulletVisualFactory.GetImpactScale(bank, 1f, bankIndex);
            VfxUrpCompat.ApplyImpactVisualScale(go, worldScale / parentLossy);

            MuteAudio(go);
            if (!go.activeSelf)
                go.SetActive(true);
            VfxUrpCompat.SetParticleSystemsLooping(go, true);

            slot.Instance = go;
            slot.BankIndex = bankIndex;
            slot.Team = team;
        }

        /// <summary>
        /// Rents the bank impact and plays it once. Later ticks call
        /// <see cref="VfxUrpCompat.ReplayImpactAnimation"/> on this same shell.
        /// </summary>
        void TryStartBurn(int bankIndex, byte team, Vector2 hitLocalXZ)
        {
            BulletVfxBank bank = BulletVfxBank.LoadDefault();
            if (bank == null)
                return;

            GameObject prefab = bank.GetImpactPrefab(bankIndex, (TeamId)team);
            if (prefab == null)
                return;

            if (!BulletOneShotVfxPool.TryRent(prefab, out GameObject go) || go == null)
                return;

            go.name = prefab.name + "_BurnTick";
            go.transform.SetParent(transform, false);
            PlaceOnHull(go.transform, BurnLocalY, hitLocalXZ);

            float parentLossy = transform.lossyScale.x;
            if (parentLossy < 0.0001f)
                parentLossy = 0.0001f;
            float worldScale = BulletVisualFactory.GetImpactScale(bank, 1f, bankIndex);
            VfxUrpCompat.ApplyImpactVisualScale(go, worldScale / parentLossy);

            MuteAudio(go);
            // Inactive rent: Prepare enables the shell and plays the time-0 burst.
            VfxUrpCompat.PrepareVfxInstance(go);

            _burn.Instance = go;
            _burn.BankIndex = bankIndex;
            _burn.Team = team;
        }

        /// <summary>
        /// <paramref name="hitLocalXZ"/> is world units along the ship axes. The proxy root is
        /// scaled, so divide to land on the same world point as the collider contact.
        /// </summary>
        void PlaceOnHull(Transform vfx, float localY, Vector2 hitLocalXZ)
        {
            float scale = transform.lossyScale.x;
            if (scale < 0.0001f)
                scale = 1f;
            vfx.localPosition = new Vector3(hitLocalXZ.x / scale, localY, hitLocalXZ.y / scale);
            vfx.localRotation = Quaternion.identity;
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
            public uint LastTickSequence;
            /// <summary>True until this proxy has sampled <see cref="ShipBurnOverTimeState.TickSequence"/> once.</summary>
            public bool AwaitingBaseline = true;
        }
    }
}
