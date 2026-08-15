using System.Collections.Generic;
using TitanOrbit.ECS;
using Unity.Entities;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Observes local-player overlap with gem crystals. Does <b>not</b> hide meshes —
    /// crystals disappear only when the server destroys the ghost (actual consume).
    /// Predicted-hide made fly-over look like a scoop, then the gem popped back.
    /// </summary>
    public sealed class GemClientCollectPresenter : MonoBehaviour
    {
        static GemClientCollectPresenter _instance;

        /// <summary>How long we wait for the server despawn before un-hiding a still-live gem.</summary>
        const float MispredictShowAgainSeconds = 0.55f;

        struct PendingHide
        {
            public Entity Gem;
            public int BindSerial;
            public float ShowAgainAt;
        }

        readonly List<PendingHide> _pending = new List<PendingHide>(16);

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

            // --- Local hull ---
            if (!EcsGameBridge.TryGetLocalShipTransform(out _))
                return;

            Entity shipEntity = Entity.Null;
            ShipState shipState = default;
            if (!TryGetLocalShip(em, out shipEntity, out shipState))
                return;
            if (shipState.IsDead || shipState.AwaitingTeamSelection)
                return;

            var visualizer = EcsWorldVisualizer.Active;
            RevealPendingHides(em, visualizer);
        }

        /// <summary>
        /// Immediately un-hides crystals that were predicted-hidden. Used when the hold is
        /// full so leftovers stay visible on the wing until capacity opens.
        /// </summary>
        void RevealPendingHides(EntityManager em, EcsWorldVisualizer visualizer)
        {
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                var pending = _pending[i];
                if (!em.Exists(pending.Gem) || !em.HasComponent<GemTag>(pending.Gem))
                {
                    _pending.RemoveAt(i);
                    continue;
                }

                if (visualizer != null &&
                    visualizer.TryGetProxy(pending.Gem, out GameObject proxy) &&
                    proxy != null)
                {
                    Transform poolRoot = proxy.transform.parent;
                    if (poolRoot != null && poolRoot.name == "GemVisualPool")
                    {
                        _pending.RemoveAt(i);
                        continue;
                    }

                    var motion = proxy.GetComponent<GemClientMotionApplier>();
                    if (motion != null && motion.BindSerial != pending.BindSerial)
                    {
                        _pending.RemoveAt(i);
                        continue;
                    }

                    if (!proxy.activeInHierarchy)
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
