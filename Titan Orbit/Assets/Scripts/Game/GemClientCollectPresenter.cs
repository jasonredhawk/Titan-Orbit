using System.Collections.Generic;
using TitanOrbit.ECS;
using TitanOrbit.Generation;
using Unity.Entities;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// [HYBRID] Local-player predicted hide when the hull or a wing tip overlaps a gem crystal.
    /// <para>
    /// Pickup is still server-authoritative (<c>GemPickupSystem</c>). RTT means the ghost
    /// lingers after the server already consumed it — or the player overlaps the mesh and
    /// waits a beat before the destroy snapshot arrives. Hiding the crystal on the same
    /// absorb test the server uses makes collection feel instant. If the ghost is still
    /// alive after a short timeout, we show it again (mispredict / self-pickup block).
    /// </para>
    /// Does not invent tractor locks. Does not destroy ECS entities.
    /// </summary>
    public sealed class GemClientCollectPresenter : MonoBehaviour
    {
        static GemClientCollectPresenter _instance;

        /// <summary>How long we wait for the server despawn before un-hiding a still-live gem.</summary>
        const float MispredictShowAgainSeconds = 0.55f;

        struct PendingHide
        {
            public Entity Gem;
            public float ShowAgainAt;
        }

        readonly List<PendingHide> _pending = new List<PendingHide>(16);
        readonly List<GemTractorBeamClientLogic.GemProxySnapshot> _gemScratch =
            new List<GemTractorBeamClientLogic.GemProxySnapshot>(64);

        /// <summary>[UNITY] Ensures one presenter exists after scene load.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void EnsureInstanceExists()
        {
            if (_instance != null)
                return;

            _instance = FindAnyObjectByType<GemClientCollectPresenter>();
            if (_instance != null)
                return;

            var go = GameObject.Find("PlanetConnectionSystems");
            if (go == null)
                go = new GameObject("PlanetConnectionSystems");

            _instance = go.AddComponent<GemClientCollectPresenter>();
        }

        void OnEnable() => _instance = this;

        void OnDisable()
        {
            if (_instance == this)
                _instance = null;
            _pending.Clear();
        }

        /// <summary>
        /// [UNITY] After gem GOs have been posed: hide crystals the local ship is scooping.
        /// </summary>
        void LateUpdate()
        {
            if (!Application.isPlaying)
                return;
            if (ClientJoinSettleCache.ShouldSkipShipEntityQueries)
                return;

            var world = EcsGameBridge.GetVisualizationWorld();
            if (world == null || !world.IsCreated)
                return;

            var em = world.EntityManager;

            // --- Map period (seam scoop) ---
            if (!ToroidalDisplay.ResolveMapSize(em, out float mapW, out float mapH))
                return;

            // --- Local hull (predicted pose) ---
            if (!EcsGameBridge.TryGetLocalShipTransform(out var shipTransform))
                return;

            Entity shipEntity = Entity.Null;
            ShipState shipState = default;
            if (!TryGetLocalShip(em, out shipEntity, out shipState))
                return;
            if (shipState.IsDead || shipState.AwaitingTeamSelection)
                return;

            var wings = em.HasBuffer<ShipWingTractorBeamElement>(shipEntity)
                ? em.GetBuffer<ShipWingTractorBeamElement>(shipEntity)
                : default;

            GemTractorBeamClientLogic.CollectGemProxies(em, _gemScratch);
            var visualizer = EcsWorldVisualizer.Active;

            for (int i = 0; i < _gemScratch.Count; i++)
            {
                var gem = _gemScratch[i];
                if (GemSelfPickupBlock.IsBlockedForShip(
                        gem.State,
                        em.HasComponent<GhostOwner>(shipEntity)
                            ? em.GetComponentData<GhostOwner>(shipEntity).NetworkId
                            : 0,
                        PlanetGemMoonOrbitClock.GetElapsedSecondsOrFallback(em, Time.timeAsDouble)))
                    continue;

                if (!GemTractorBeamClientLogic.IsInsideCargoAbsorbZone(
                        shipTransform, wings, gem.Transform, gem.State, mapW, mapH))
                    continue;

                if (visualizer == null ||
                    !visualizer.TryGetProxy(gem.Entity, out GameObject proxy) ||
                    proxy == null ||
                    !proxy.activeInHierarchy)
                    continue;

                proxy.SetActive(false);
                _pending.Add(new PendingHide
                {
                    Gem = gem.Entity,
                    ShowAgainAt = Time.time + MispredictShowAgainSeconds,
                });
            }

            // --- Mispredict / despawn cleanup ---
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                var pending = _pending[i];
                if (!em.Exists(pending.Gem))
                {
                    _pending.RemoveAt(i);
                    continue;
                }

                if (Time.time < pending.ShowAgainAt)
                    continue;

                // Server did not consume — show the crystal again if the visualizer still owns it.
                if (visualizer != null &&
                    visualizer.TryGetProxy(pending.Gem, out GameObject proxy) &&
                    proxy != null &&
                    !proxy.activeInHierarchy)
                {
                    Transform poolRoot = proxy.transform.parent;
                    if (poolRoot != null && poolRoot.name == "GemVisualPool")
                    {
                        _pending.RemoveAt(i);
                        continue;
                    }

                    proxy.SetActive(true);
                }

                _pending.RemoveAt(i);
            }
        }

        /// <summary>Resolves the local ship entity + state (join-gated by the caller).</summary>
        static bool TryGetLocalShip(EntityManager em, out Entity shipEntity, out ShipState shipState)
        {
            shipEntity = Entity.Null;
            shipState = default;
            using var q = em.CreateEntityQuery(
                ComponentType.ReadOnly<LocalPlayerShipTag>(),
                ComponentType.ReadOnly<ShipTag>(),
                ComponentType.ReadOnly<ShipState>());
            if (q.IsEmptyIgnoreFilter)
                return false;
            shipEntity = q.GetSingletonEntity();
            shipState = em.GetComponentData<ShipState>(shipEntity);
            return true;
        }
    }
}
