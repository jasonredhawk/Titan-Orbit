using UnityEngine;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [TITAN-ORBIT] Single join contract for a late-join into a <b>live</b> match.
    /// Loading UI and Join Team both read <see cref="IsComplete"/> — not a pile of independent flags.
    /// <para>
    /// Paths: asteroids = seed Instantiates + occupancy RPC; planets/ships/gems = NetCode
    /// GhostSpawn Instantiates; people transports = SpawnRpc catch-up (not ghosts).
    /// </para>
    /// Reset on session leave / Play Mode enter.
    /// </summary>
    public static class JoinWorldReadyCache
    {
        /// <summary>Seconds after hydrate to treat missing occupancy RPC as “all alive” (logged).</summary>
        public const float OccupancyTimeoutSeconds = 8f;

        /// <summary>Seconds after InGame to accept GhostCount shortfall (logged, not silent).</summary>
        public const float GhostCatchUpTimeoutSeconds = 20f;

        /// <summary>Ghost Instantiates ratio vs server relevant count before we call catch-up done.</summary>
        public const float GhostInstantiatedReadyRatio = 0.85f;

        /// <summary>Planet ghosts vs server live planet count.</summary>
        public const float PlanetReadyRatio = 0.92f;

        /// <summary>True after occupancy bits were applied (or timed out).</summary>
        public static bool OccupancyApplied { get; private set; }

        /// <summary>True after an occupancy RPC arrived this join.</summary>
        public static bool OccupancyReceived { get; private set; }

        /// <summary>Dead asteroid slots from the last occupancy RPC.</summary>
        public static int OccupancyDeadSlots { get; private set; }

        /// <summary>Blueprint asteroid slot count from occupancy RPC.</summary>
        public static int OccupancySlotCount { get; private set; }

        /// <summary>Server live planet count from recipe meta (homes + neutrals still in play).</summary>
        public static int ExpectedPlanets { get; set; }

        /// <summary>Client planet ghosts Instantiates so far (CalculateEntityCount).</summary>
        public static int ReceivedPlanets { get; private set; }

        /// <summary>Server live ship count from recipe meta.</summary>
        public static int ExpectedShips { get; set; }

        /// <summary>Client ship ghosts Instantiates so far.</summary>
        public static int ReceivedShips { get; private set; }

        /// <summary>NetCode <see cref="GhostCount.GhostCountOnServer"/> (relevancy-filtered).</summary>
        public static int GhostCountOnServer { get; private set; }

        /// <summary>NetCode <see cref="GhostCount.GhostCountReceivedOnClient"/> (Unity late-join test).</summary>
        public static int GhostCountReceived { get; private set; }

        /// <summary>NetCode <see cref="GhostCount.GhostCountInstantiatedOnClient"/>.</summary>
        public static int GhostCountInstantiated { get; private set; }

        /// <summary>Live <c>PlanetGemMoonVisualProxy</c> count (Game registry; presentation moons).</summary>
        public static int ReceivedMoonProxies { get; private set; }

        /// <summary>True when moon visuals meet <see cref="PlanetReadyRatio"/> of expected planets.</summary>
        public static bool MoonsReady { get; private set; }

        /// <summary>True when planet Instantiates meet <see cref="PlanetReadyRatio"/>.</summary>
        public static bool PlanetsReady { get; private set; }

        /// <summary>True when ship Instantiates meet expected N (0 ships is immediately ready).</summary>
        public static bool ShipsReady { get; private set; }

        /// <summary>True when GhostCount Instantiates ratio or timeout is satisfied.</summary>
        public static bool GhostCatchUpReady { get; private set; }

        /// <summary>True after transport catch-up RPCs were sent to this client (or none in flight).</summary>
        public static bool TransportsCatchUpReady { get; set; }

        /// <summary>Realtime when hydrate first completed this generation.</summary>
        public static float HydrateCompleteRealtime { get; private set; } = -1f;

        /// <summary>Realtime when InGame first became true this generation.</summary>
        public static float InGameRealtime { get; private set; } = -1f;

        /// <summary>All join predicates true — loading may dismiss.</summary>
        public static bool IsComplete { get; private set; }

        static string s_LastLog;

        /// <summary>Stashes occupancy RPC until hydrate can apply it.</summary>
        public static void MarkOccupancyReceived(int slotCount, int deadSlots)
        {
            OccupancyReceived = true;
            OccupancySlotCount = mathMax(0, slotCount);
            OccupancyDeadSlots = mathMax(0, deadSlots);
        }

        /// <summary>Occupancy SoftDestroy finished (or timeout treated as all-alive).</summary>
        public static void MarkOccupancyApplied()
        {
            OccupancyApplied = true;
        }

        /// <summary>
        /// Moon GameObject proxies registered this frame (called from <c>EcsGameBridge</c>).
        /// Recomputes <see cref="IsComplete"/> so Join Team does not wait an extra ECS tick.
        /// </summary>
        public static void SetMoonProxyCount(int count)
        {
            ReceivedMoonProxies = mathMax(0, count);
            FinishPredicates(s_LastInGame, s_LastProxyReady);
        }

        /// <summary>
        /// Publishes counts from the client world. Call once per frame from
        /// <c>JoinWorldReadyPublishSystem</c>.
        /// </summary>
        public static void Publish(
            bool inGame,
            int receivedPlanets,
            int receivedShips,
            int ghostOnServer,
            int ghostReceived,
            int ghostInstantiated,
            bool proxyReady)
        {
            if (ClientMapHydrateCache.IsComplete && HydrateCompleteRealtime < 0f)
                HydrateCompleteRealtime = Time.realtimeSinceStartup;

            if (inGame && InGameRealtime < 0f)
                InGameRealtime = Time.realtimeSinceStartup;

            ReceivedPlanets = mathMax(0, receivedPlanets);
            ReceivedShips = mathMax(0, receivedShips);
            GhostCountOnServer = mathMax(0, ghostOnServer);
            GhostCountReceived = mathMax(0, ghostReceived);
            GhostCountInstantiated = mathMax(0, ghostInstantiated);
            s_LastInGame = inGame;
            s_LastProxyReady = proxyReady;

            FinishPredicates(inGame, proxyReady);
        }

        /// <summary>Short loading-bar / stuck-hint line (honest — no fake crawl).</summary>
        public static string GetStatusLabel()
        {
            if (IsComplete)
                return "Ready";
            if (!ClientMapHydrateCache.HasFullRecipe)
                return ClientMapHydrateCache.GetWorldBarStatusLabel();
            if (!ClientMapHydrateCache.IsComplete)
                return ClientMapHydrateCache.GetWorldBarStatusLabel();
            if (!OccupancyApplied && OccupancyReceived)
                return "Applying map occupancy";
            if (!OccupancyApplied)
                return OccupancyTimedOut()
                    ? "Occupancy timeout (assuming live field)"
                    : "Waiting for occupancy";
            if (InGameRealtime < 0f)
                return "Waiting to enter game";
            if (!PlanetsReady)
                return "Planets " + ReceivedPlanets + " / " + Mathf.Max(ExpectedPlanets, MapPlanetFallback());
            if (!MoonsReady)
                return "Moons " + ReceivedMoonProxies + " / " + Mathf.Max(ExpectedPlanets, ReceivedPlanets);
            if (!ShipsReady)
                return "Ships " + ReceivedShips + " / " + ExpectedShips;
            if (!GhostCatchUpReady)
                return "Ghosts " + GhostCountInstantiated + " / " + GhostCountOnServer +
                       " (recv " + GhostCountReceived + ")";
            return "Map visuals";
        }

        /// <summary>Clears session state (disconnect / Play Mode).</summary>
        public static void Clear()
        {
            OccupancyApplied = false;
            OccupancyReceived = false;
            OccupancyDeadSlots = 0;
            OccupancySlotCount = 0;
            ExpectedPlanets = 0;
            ReceivedPlanets = 0;
            ExpectedShips = 0;
            ReceivedShips = 0;
            GhostCountOnServer = 0;
            GhostCountReceived = 0;
            GhostCountInstantiated = 0;
            ReceivedMoonProxies = 0;
            PlanetsReady = false;
            ShipsReady = false;
            MoonsReady = false;
            GhostCatchUpReady = false;
            s_LastInGame = false;
            s_LastProxyReady = false;
            TransportsCatchUpReady = false;
            HydrateCompleteRealtime = -1f;
            InGameRealtime = -1f;
            IsComplete = false;
            s_LastLog = null;
            AsteroidOccupancyPending.Clear();
        }

        static bool OccupancyTimedOut()
        {
            if (OccupancyApplied)
                return false;
            if (HydrateCompleteRealtime < 0f)
                return false;
            return Time.realtimeSinceStartup - HydrateCompleteRealtime >= OccupancyTimeoutSeconds;
        }

        static int MapPlanetFallback()
        {
            // MapSessionMetaCache lives in NetCode — avoid that assembly from ECS.
            return 0;
        }

        static bool s_LastInGame;
        static bool s_LastProxyReady;

        static void FinishPredicates(bool inGame, bool proxyReady)
        {
            int expectPlanets = ExpectedPlanets;
            if (expectPlanets <= 0)
                expectPlanets = MapPlanetFallback();

            PlanetsReady = expectPlanets <= 0 ||
                           ReceivedPlanets >= Mathf.CeilToInt(expectPlanets * PlanetReadyRatio);

            ShipsReady = ExpectedShips <= 0 ||
                         ReceivedShips >= ExpectedShips ||
                         (ExpectedShips > 0 &&
                          ReceivedShips >= Mathf.CeilToInt(ExpectedShips * PlanetReadyRatio));

            bool ghostTimeout = inGame &&
                                InGameRealtime >= 0f &&
                                Time.realtimeSinceStartup - InGameRealtime >= GhostCatchUpTimeoutSeconds;

            MoonsReady = expectPlanets <= 0 ||
                         ReceivedMoonProxies >= Mathf.CeilToInt(expectPlanets * PlanetReadyRatio) ||
                         ghostTimeout;

            float receivedRatio = GhostCountOnServer > 0
                ? (float)GhostCountReceived / GhostCountOnServer
                : 1f;
            float instantiatedRatio = GhostCountOnServer > 0
                ? (float)GhostCountInstantiated / GhostCountOnServer
                : 1f;
            bool ghostRatioReady = GhostCountOnServer <= 0 ||
                                   (receivedRatio >= GhostInstantiatedReadyRatio &&
                                    instantiatedRatio >= GhostInstantiatedReadyRatio);
            GhostCatchUpReady = !inGame
                ? false
                : ghostRatioReady || ghostTimeout ||
                  (PlanetsReady && ShipsReady && MoonsReady && proxyReady);

            if (!OccupancyApplied && OccupancyTimedOut())
                MarkOccupancyApplied();

            bool occupancyReady = OccupancyApplied;

            bool hydrateReady = ClientMapHydrateCache.HasFullRecipe && ClientMapHydrateCache.IsComplete;
            if (ClientMapHydrateCache.HasFullRecipe &&
                ClientMapHydrateCache.ExpectedBodies <= 0)
                hydrateReady = ClientMapHydrateCache.IsComplete;

            IsComplete = hydrateReady &&
                         occupancyReady &&
                         inGame &&
                         PlanetsReady &&
                         MoonsReady &&
                         ShipsReady &&
                         GhostCatchUpReady &&
                         proxyReady;

            LogIfChanged(inGame, occupancyReady, proxyReady, instantiatedRatio, ghostTimeout);
        }

        static void LogIfChanged(
            bool inGame,
            bool occupancyReady,
            bool proxyReady,
            float ghostRatio,
            bool ghostTimeout)
        {
            string line =
                "hydrate=" + ClientMapHydrateCache.IsComplete +
                " occupancy=" + occupancyReady +
                " inGame=" + inGame +
                " planets=" + ReceivedPlanets + "/" + ExpectedPlanets +
                " moons=" + ReceivedMoonProxies +
                " ships=" + ReceivedShips + "/" + ExpectedShips +
                " ghosts=" + GhostCountInstantiated + "/" + GhostCountOnServer +
                " recv=" + GhostCountReceived +
                " ghostRatio=" + ghostRatio.ToString("F2") +
                (ghostTimeout ? " ghostTimeout" : string.Empty) +
                " proxy=" + proxyReady +
                " complete=" + IsComplete;
            if (line == s_LastLog)
                return;
            s_LastLog = line;
            Debug.Log("[JoinWorldReady] " + line);
        }

        static int mathMax(int a, int b) => a > b ? a : b;

#if UNITY_EDITOR
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() => Clear();
#endif
    }
}
