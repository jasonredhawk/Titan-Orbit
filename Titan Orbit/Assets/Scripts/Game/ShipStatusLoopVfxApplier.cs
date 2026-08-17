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

        Entity _shipEntity;
        readonly Slot _shock = new Slot();
        readonly Slot _burn = new Slot();

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

            double elapsed = world.Time.ElapsedTime;

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
            // Burn also loops on the hull so a moving ship keeps the fire; Sequence-0 HitRpc
            // still plays per-tick flashes parented to the same proxy.
            SyncSlot(_shock, shockActive, shockBank, shockTeam, ShockLocalY);
            SyncSlot(_burn, burnActive, burnBank, burnTeam, BurnLocalY);
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
        }
    }
}
