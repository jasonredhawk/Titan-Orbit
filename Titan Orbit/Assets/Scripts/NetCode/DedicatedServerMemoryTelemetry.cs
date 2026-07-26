using System;
using System.Text;
using TitanOrbit.Diagnostics;
using TitanOrbit.ECS;
using Unity.Entities;
using Unity.NetCode;
using Process = System.Diagnostics.Process;

namespace TitanOrbit.NetCode
{
    /// <summary>
    /// [TITAN-ORBIT] Dedicated-server memory and sim-pressure telemetry.
    /// Logs RSS / private bytes, GC heap, and ECS entity counts so overnight Join Game outages
    /// can be correlated with recreate count vs sudden load spikes.
    /// Also exposes consecutive "STRUGGLING" samples and RSS budget checks used to recycle an
    /// empty process before the guest OS wedges (SSH hang). Never exits an occupied match.
    /// </summary>
    public static class DedicatedServerMemoryTelemetry
    {
        /// <summary>Catch-up % above this counts as a struggling netdiag sample.</summary>
        public const float StrugglingCatchUpPercent = 50f;

        /// <summary>Average Unity frame ms above this counts as struggling.</summary>
        public const float StrugglingAvgFrameMs = 50f;

        static int _consecutiveStrugglingSamples;
        static float _lastCatchUpPercent;
        static float _lastAvgFrameMs;
        static string _lastVerdict = "unknown";
        static int _lastLoggedRssMb = -1;
        static int _lastEmptyRecreateCountWhenLogged = -1;

        /// <summary>
        /// Called from server netdiag every ~10s with the latest window stats.
        /// Updates the consecutive struggling counter used for empty-process recycle.
        /// </summary>
        /// <param name="catchUpPercent">Percent of sim steps that were catch-up ticks.</param>
        /// <param name="avgFrameMs">Average Unity frame ms in the window.</param>
        /// <param name="verdict">Plain-language verdict from netdiag.</param>
        public static void ReportSimHealthSample(float catchUpPercent, float avgFrameMs, string verdict)
        {
            // --- ReportSimHealthSample ---
            _lastCatchUpPercent = catchUpPercent;
            _lastAvgFrameMs = avgFrameMs;
            _lastVerdict = verdict ?? "unknown";

            bool struggling =
                catchUpPercent >= StrugglingCatchUpPercent ||
                avgFrameMs >= StrugglingAvgFrameMs ||
                (!string.IsNullOrEmpty(verdict) &&
                 (verdict.StartsWith("STRUGGLING", StringComparison.Ordinal) ||
                  verdict.StartsWith("SLOW", StringComparison.Ordinal)));

            if (struggling)
                _consecutiveStrugglingSamples++;
            else
                _consecutiveStrugglingSamples = 0;
        }

        /// <summary>Consecutive netdiag windows that looked overloaded (0 when healthy).</summary>
        public static int ConsecutiveStrugglingSamples => _consecutiveStrugglingSamples;

        /// <summary>
        /// True when an empty dedicated process should exit for orchestrator restart due to
        /// sustained sim pressure. Occupied matches must never use this.
        /// </summary>
        /// <param name="requiredConsecutiveSamples">How many struggling windows in a row (e.g. 3 ≈ 30s).</param>
        public static bool ShouldRecycleEmptyDueToStruggling(int requiredConsecutiveSamples)
        {
            if (requiredConsecutiveSamples <= 0)
                return false;
            return _consecutiveStrugglingSamples >= requiredConsecutiveSamples;
        }

        /// <summary>
        /// True when WorkingSet RSS is at/above the MB budget. Occupied matches must never exit on this alone.
        /// </summary>
        /// <param name="rssRecycleMb">Budget in mebibytes; 0 disables.</param>
        public static bool ShouldRecycleEmptyDueToRss(int rssRecycleMb)
        {
            if (rssRecycleMb <= 0)
                return false;
            TryReadProcessMemoryMb(out int workingSetMb, out _);
            return workingSetMb >= rssRecycleMb;
        }

        /// <summary>
        /// Appends one memory + entity-count line. Call periodically and after idle recreates.
        /// </summary>
        /// <param name="reason">Short tag (periodic, after_idle_recreate, before_recycle).</param>
        /// <param name="emptyInProcessRecreates">Idle recreate counter for correlation.</param>
        /// <param name="playerCount">Live NetCode connections (0 = empty).</param>
        public static void LogSnapshot(string reason, int emptyInProcessRecreates, int playerCount)
        {
            // --- LogSnapshot ---
            try
            {
                TryReadProcessMemoryMb(out int workingSetMb, out int privateMb);
                long gcBytes = GC.GetTotalMemory(false);
                int gcMb = (int)(gcBytes / (1024L * 1024L));

                CountServerEntities(
                    out int ships,
                    out int planets,
                    out int asteroids,
                    out int gems,
                    out int transports,
                    out int connections);

                var sb = new StringBuilder(256);
                sb.Append("reason=").Append(reason);
                sb.Append(" players=").Append(playerCount);
                sb.Append(" emptyRecreates=").Append(emptyInProcessRecreates);
                sb.Append(" rssMb=").Append(workingSetMb);
                sb.Append(" privateMb=").Append(privateMb);
                sb.Append(" gcManagedMb=").Append(gcMb);
                sb.Append(" ships=").Append(ships);
                sb.Append(" planets=").Append(planets);
                sb.Append(" asteroids=").Append(asteroids);
                sb.Append(" gems=").Append(gems);
                sb.Append(" transports=").Append(transports);
                sb.Append(" connections=").Append(connections);
                sb.Append(" catchUp%=").Append(_lastCatchUpPercent.ToString("F0"));
                sb.Append(" avgFrameMs=").Append(_lastAvgFrameMs.ToString("F1"));
                sb.Append(" strugglingStreak=").Append(_consecutiveStrugglingSamples);
                sb.Append(" verdict=").Append(_lastVerdict);

                // [TITAN-ORBIT] Delta helps spot recreate-linked climbs vs sudden spikes.
                if (_lastLoggedRssMb >= 0)
                    sb.Append(" rssDeltaMb=").Append(workingSetMb - _lastLoggedRssMb);
                if (_lastEmptyRecreateCountWhenLogged >= 0)
                    sb.Append(" recreatesSinceLastLog=").Append(emptyInProcessRecreates - _lastEmptyRecreateCountWhenLogged);

                DedicatedServerFileLog.Append("memory", sb.ToString());
                _lastLoggedRssMb = workingSetMb;
                _lastEmptyRecreateCountWhenLogged = emptyInProcessRecreates;
            }
            catch (Exception e)
            {
                DedicatedServerFileLog.Append("memory", "LogSnapshot failed: " + e.Message);
            }
        }

        /// <summary>Reads process WorkingSet and PrivateMemory in mebibytes.</summary>
        public static void TryReadProcessMemoryMb(out int workingSetMb, out int privateMb)
        {
            // --- TryReadProcessMemoryMb ---
            workingSetMb = 0;
            privateMb = 0;
            try
            {
                using Process proc = Process.GetCurrentProcess();
                proc.Refresh();
                workingSetMb = (int)(proc.WorkingSet64 / (1024L * 1024L));
                privateMb = (int)(proc.PrivateMemorySize64 / (1024L * 1024L));
            }
            catch
            {
                // [STANDARD] Best-effort — some hosts restrict process memory queries.
            }
        }

        /// <summary>
        /// Cheap entity counts on ServerWorld for leak correlation. Uses CalculateEntityCount only.
        /// </summary>
        static void CountServerEntities(
            out int ships,
            out int planets,
            out int asteroids,
            out int gems,
            out int transports,
            out int connections)
        {
            ships = planets = asteroids = gems = transports = connections = 0;
            var server = ClientServerBootstrap.ServerWorld;
            if (server == null || !server.IsCreated)
                return;

            var em = server.EntityManager;
            ships = SafeCount(em, typeof(ShipTag));
            planets = SafeCount(em, typeof(PlanetTag));
            asteroids = SafeCount(em, typeof(AsteroidTag));
            gems = SafeCount(em, typeof(GemTag));
            transports = SafeCount(em, typeof(PeopleTransportTag));
            connections = SafeCount(em, typeof(NetworkStreamConnection));
        }

        static int SafeCount(EntityManager em, ComponentType type)
        {
            try
            {
                using var q = em.CreateEntityQuery(type);
                return q.CalculateEntityCount();
            }
            catch
            {
                return -1;
            }
        }
    }
}
