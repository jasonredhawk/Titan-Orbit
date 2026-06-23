using System;
using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;
using TitanOrbit.Generation;

namespace TitanOrbit.Networking
{
    /// <summary>
    /// Shared client render clock for remote entity interpolation.
    ///
    /// The playhead lives in <b>server-time units</b> but is driven by the local frame clock, not by
    /// <c>NetworkManager.ServerTime</c>. NGO continuously speeds up / slows down its client ServerTime
    /// estimate to stay in sync with the host; sampling that directly makes every remote ship visibly
    /// accelerate and decelerate (the "move / slowdown / move" stutter). Instead we:
    ///   1. advance the playhead at real (unscaled) wall-clock rate, and
    ///   2. gently slew it toward <c>(latest received snapshot server-time) - delay</c>.
    /// Snapshots are interpolated on their server timestamps, which the server stamps from its physics clock
    /// (Time.fixedTimeAsDouble) at the instant the pose is sampled, so each position carries a time label
    /// consistent with its true motion and playback speed is constant regardless of network arrival jitter.
    /// </summary>
    [DefaultExecutionOrder(30000)]
    public sealed class ClientRenderTimeline : MonoBehaviour
    {
        public static ClientRenderTimeline Instance { get; private set; }

        // How far in the past remote ships are rendered. Rather than a fixed value, the delay ADAPTS to the
        // measured connection jitter: just enough buffer to stay smooth, and no more. On a clean link it sits
        // near the floor (tight, responsive aiming); on a jittery link it grows automatically so smoothness
        // never breaks. The server stays authoritative for hits, so this only affects where you must aim.
        // Floor must hold ~2-3 packets of the 30 Hz motor stream (~33 ms spacing). 0.05 s (~1.5 packets) left no
        // slack for arrival jitter, so the playhead kept overrunning the newest sample into extrapolation — the
        // micro-stutter that came back. 0.08 s (~2.4 packets) tolerates a late/lost packet and stays smooth while
        // still being tighter than the old fixed 0.10 s.
        [SerializeField, Range(0.03f, 0.12f)] private float minInterpolationDelaySeconds = 0.08f;
        // Cloud play (real internet jitter + loss) needs more headroom than a clean LAN, so the ceiling is
        // generous; the buffer only grows this far when the link actually demands it.
        [SerializeField, Range(0.08f, 0.35f)] private float maxInterpolationDelaySeconds = 0.24f;
        // Effective delay = min + jitterSafetyMultiplier * measuredJitter (clamped to max). The jitter estimate is a
        // peak-tracking envelope (below), so a multiplier near 1.5 already covers the worst recent arrival spread.
        [SerializeField, Range(1f, 4f)] private float jitterSafetyMultiplier = 1.5f;
        // Proportional gain used only to correct slow client/server clock drift, never to chase the
        // per-packet stepping of `target`.
        [SerializeField, Range(0.5f, 8f)] private float clockSlewRate = 3f;
        // Hard cap on how far the playhead may deviate from a true 1:1 real-time rate while correcting drift,
        // expressed as a fraction of real time (0.04 = at most ±4% faster/slower). This is the key to smoothness:
        // it keeps the playhead from accelerating/decelerating with each arriving snapshot. The natural
        // real-time advance already tracks the average data rate, so only a tiny correction budget is needed.
        [SerializeField, Range(0.01f, 0.25f)] private float maxClockCorrectionRate = 0.12f;
        // Drift beyond this (first packet, big hitch, pause, teleport) hard-resyncs the playhead.
        [SerializeField, Range(0.2f, 2f)] private float hardResyncThreshold = 0.4f;

        private double playheadServerTime;
        private double latestSnapshotServerTime;
        private bool hasLatestSnapshot;
        private bool initialized;
        private readonly HashSet<ClientRenderTimelineSource> sources = new HashSet<ClientRenderTimelineSource>(64);

        // Adaptive-delay state. We estimate network jitter from the spread of (localReceiveTime - serverSendTime)
        // across arriving snapshots. The absolute offset is meaningless (different clock origins) but its
        // VARIANCE is exactly the transit jitter, which is what the buffer must cover.
        private double transitOffsetMean;
        private double transitOffsetJitter;
        private bool transitInitialized;
        private double effectiveInterpolationDelay = 0.12; // safe starting value until jitter is measured
        // Mean tracks slow clock drift. The jitter estimate is a PEAK-TRACKING envelope: it jumps up instantly to a
        // fresh deviation (fast attack, so the buffer can deepen the moment a spike is seen) and decays slowly when
        // the link calms (slow release, so it doesn't tighten back into a new spike). This asymmetry is what keeps a
        // cloud link smooth through bursty jitter instead of stuttering for a couple seconds while a symmetric EMA
        // catches up.
        private const double TransitMeanBlend = 0.05;
        private const double TransitJitterReleasePerSnapshot = 0.02; // ~1.5 s release time-constant at 30 Hz
        // Asymmetric convergence of the effective buffer depth: ~0.4 s to grow, several seconds to shrink.
        private const double DelayAttackRate = 6.0;
        private const double DelayReleaseRate = 0.25;

        public double RenderServerTime => playheadServerTime;
        public float InterpolationDelaySeconds => (float)effectiveInterpolationDelay;

        /// <summary>
        /// Visual delay to apply to a server-driven world event (asteroid impact / destruction) so it lines up
        /// with the ON-SCREEN position of the ship that caused it. Remote ships are rendered
        /// ~<see cref="InterpolationDelaySeconds"/> in the past, so their world events must be held back by the
        /// same amount. Events caused by the local player's own ship (rendered in real time) — and anything
        /// evaluated on the server — are shown immediately (delay 0).
        /// </summary>
        public static float ResolveRemoteEventVisualDelay(ulong causerShipNetworkId)
        {
            NetworkManager nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsClient || nm.IsServer)
                return 0f;

            // Caused by the local player's own ship → it is drawn in real time, so show the event now.
            if (causerShipNetworkId != 0 && nm.SpawnManager != null
                && nm.SpawnManager.SpawnedObjects.TryGetValue(causerShipNetworkId, out NetworkObject causer)
                && causer != null && causer.IsOwner)
                return 0f;

            return Instance != null ? Instance.InterpolationDelaySeconds : 0f;
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }

        public static ClientRenderTimeline EnsureExists()
        {
            if (Instance != null) return Instance;
            var go = new GameObject(nameof(ClientRenderTimeline));
            DontDestroyOnLoad(go);
            return go.AddComponent<ClientRenderTimeline>();
        }

        public void RegisterSource(ClientRenderTimelineSource source)
        {
            if (source != null)
                sources.Add(source);
        }

        public void UnregisterSource(ClientRenderTimelineSource source)
        {
            if (source != null)
                sources.Remove(source);
        }

        /// <summary>Sources call this whenever a fresh snapshot arrives so the clock can track real data.</summary>
        public void NotifySnapshotServerTime(double serverTime)
        {
            if (!hasLatestSnapshot || serverTime > latestSnapshotServerTime)
            {
                latestSnapshotServerTime = serverTime;
                hasLatestSnapshot = true;
            }

            // Update the transit-jitter estimate used to size the interpolation buffer. Only fresh (newer)
            // snapshots are measured so reordered/duplicate packets do not inflate the jitter.
            double transit = Time.unscaledTimeAsDouble - serverTime;
            if (!transitInitialized)
            {
                transitOffsetMean = transit;
                transitOffsetJitter = 0.0;
                transitInitialized = true;
            }
            else
            {
                double deviation = Math.Abs(transit - transitOffsetMean);
                transitOffsetMean += (transit - transitOffsetMean) * TransitMeanBlend;
                // Fast attack, slow release: snap up to a fresh spike immediately; bleed down gently afterwards.
                if (deviation > transitOffsetJitter)
                    transitOffsetJitter = deviation;
                else
                    transitOffsetJitter += (deviation - transitOffsetJitter) * TransitJitterReleasePerSnapshot;
            }
        }

        private double ComputeTargetInterpolationDelay()
        {
            double target = minInterpolationDelaySeconds + jitterSafetyMultiplier * transitOffsetJitter;
            if (target < minInterpolationDelaySeconds) target = minInterpolationDelaySeconds;
            if (target > maxInterpolationDelaySeconds) target = maxInterpolationDelaySeconds;
            return target;
        }

        private void LateUpdate()
        {
            if (!hasLatestSnapshot)
                return;

            double dtForDelay = Time.unscaledDeltaTime;
            // Grow the buffer FAST toward a higher target (a jitter spike must be covered before it underruns) but
            // shrink SLOWLY back toward a lower target (don't tighten straight into the next spike). The render-side
            // projective smoothing absorbs the small playback-rate change a growing buffer implies, so a quick grow
            // no longer pops the ship.
            double delayTarget = ComputeTargetInterpolationDelay();
            double delayRate = delayTarget > effectiveInterpolationDelay ? DelayAttackRate : DelayReleaseRate;
            effectiveInterpolationDelay += (delayTarget - effectiveInterpolationDelay)
                * (1.0 - Math.Exp(-delayRate * dtForDelay));

            double target = latestSnapshotServerTime - effectiveInterpolationDelay;

            if (!initialized)
            {
                playheadServerTime = target;
                initialized = true;
            }
            else
            {
                double dt = Time.unscaledDeltaTime;

                // 1:1 real-time advance. Because the server streams at a fixed cadence, this alone tracks the
                // average data rate, so the playhead never has to speed up or slow down to keep pace.
                playheadServerTime += dt;

                double error = target - playheadServerTime;
                if (Math.Abs(error) > hardResyncThreshold)
                {
                    // First packet, large hitch, alt-tab, or teleport: jump rather than crawl.
                    playheadServerTime = target;
                }
                else
                {
                    // Correct only slow drift, and clamp the correction to a tiny fraction of real time so the
                    // ~33 ms stepping of `target` (one step per arriving snapshot) is ignored. Chasing that step
                    // is exactly what made remote ships speed up/slow down with every packet.
                    double desired = error * clockSlewRate * dt;
                    double maxStep = maxClockCorrectionRate * dt;
                    if (desired > maxStep) desired = maxStep;
                    else if (desired < -maxStep) desired = -maxStep;
                    playheadServerTime += desired;
                }
            }

            double trimBefore = playheadServerTime - maxInterpolationDelaySeconds * 3.0;
            foreach (ClientRenderTimelineSource source in sources)
                source.TrimSamplesOlderThan(trimBefore);
        }
    }

    /// <summary>Per-entity snapshot ring buffer sampled on the shared server-time playhead.</summary>
    public abstract class ClientRenderTimelineSource : MonoBehaviour
    {
        protected struct TimelineSample
        {
            public double ServerTime;
            public Vector3 Position;
            public Quaternion Rotation;
            public Vector3 Velocity;
            public uint LastProcessedInputSeq;
            public bool Thrust;
        }

        private readonly List<TimelineSample> samples = new List<TimelineSample>(32);
        private const int MaxSamples = 32;

        protected virtual void OnEnable()
        {
            ClientRenderTimeline.EnsureExists()?.RegisterSource(this);
        }

        protected virtual void OnDisable()
        {
            if (ClientRenderTimeline.Instance != null)
                ClientRenderTimeline.Instance.UnregisterSource(this);
        }

        public void PushSnapshot(double serverTime, Vector3 position, Quaternion rotation, Vector3 velocity, uint lastProcessedInputSeq, bool thrust)
        {
            position.y = 0f;
            velocity.y = 0f;

            if (samples.Count > 0)
            {
                Vector3 jumpOffset = ToroidalMap.ShortestWorldOffsetXZ(samples[samples.Count - 1].Position, position);
                if (jumpOffset.sqrMagnitude > 10000f)
                    ClearSnapshots();
            }

            if (samples.Count > 0)
            {
                // Unreliable transport can reorder or duplicate packets. Keep the buffer strictly increasing in
                // server time by dropping any sample that is not newer than the newest one already stored; a stale
                // sample must never overwrite fresher data or advance the render clock.
                TimelineSample last = samples[samples.Count - 1];
                if (serverTime <= last.ServerTime + 0.0001)
                    return;
            }

            samples.Add(new TimelineSample
            {
                ServerTime = serverTime,
                Position = position,
                Rotation = rotation,
                Velocity = velocity,
                LastProcessedInputSeq = lastProcessedInputSeq,
                Thrust = thrust,
            });

            while (samples.Count > MaxSamples)
                samples.RemoveAt(0);

            NotifyTimeline(serverTime);
        }

        private static void NotifyTimeline(double serverTime)
        {
            var timeline = ClientRenderTimeline.Instance;
            if (timeline != null)
                timeline.NotifySnapshotServerTime(serverTime);
        }

        // Continue along last known velocity for at most this long past the newest snapshot. Keeps remote
        // ships gliding through a single dropped packet (~33 ms at the 30 Hz stream) instead of freezing, but
        // bounded so a dropped sender does not drift forever and a recovered packet never snaps backward.
        private const double MaxExtrapolationSeconds = 0.10;

        public bool TrySampleAt(double renderServerTime, out Vector3 position, out Quaternion rotation, out Vector3 velocity, out bool thrust)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            velocity = Vector3.zero;
            thrust = false;
            if (samples.Count == 0) return false;
            if (samples.Count == 1)
            {
                position = samples[0].Position;
                rotation = samples[0].Rotation;
                velocity = samples[0].Velocity;
                thrust = samples[0].Thrust;
                return true;
            }

            for (int i = 1; i < samples.Count; i++)
            {
                TimelineSample a = samples[i - 1];
                TimelineSample b = samples[i];
                if (renderServerTime > b.ServerTime) continue;

                double span = b.ServerTime - a.ServerTime;
                float t = span > 0.0001 ? (float)((renderServerTime - a.ServerTime) / span) : 1f;
                t = Mathf.Clamp01(t);
                Vector3 segmentOffset = ToroidalMap.ShortestWorldOffsetXZ(a.Position, b.Position);
                position = a.Position + segmentOffset * t;
                rotation = Quaternion.Slerp(a.Rotation, b.Rotation, t);
                // Segment-derived velocity matches the position path (avoids Hermite-style overshoot when
                // snapshot velocity disagrees with position deltas on client-reported poses).
                if (span > 0.0001)
                    velocity = segmentOffset / (float)span;
                else
                    velocity = Vector3.Lerp(a.Velocity, b.Velocity, t);
                velocity.y = 0f;
                // Thrust is a discrete intent: hold the flame lit across the whole segment if either end is
                // thrusting so it doesn't flicker at packet boundaries.
                thrust = a.Thrust || b.Thrust;
                return true;
            }

            // Buffer underrun: playhead is past the newest snapshot. Dead-reckon along the velocity implied by
            // the last two real samples (not the reported snapshot velocity, which can disagree with the actual
            // path — e.g. a parked ship still reporting a residual speed would otherwise drift forward).
            TimelineSample tail = samples[samples.Count - 1];
            Vector3 tailVelocity = tail.Velocity;
            if (samples.Count >= 2)
            {
                TimelineSample prev = samples[samples.Count - 2];
                double sp = tail.ServerTime - prev.ServerTime;
                if (sp > 0.0001)
                    tailVelocity = ToroidalMap.ShortestWorldOffsetXZ(prev.Position, tail.Position) / (float)sp;
            }
            tailVelocity.y = 0f;
            double ahead = renderServerTime - tail.ServerTime;
            if (ahead < 0.0) ahead = 0.0;
            if (ahead > MaxExtrapolationSeconds) ahead = MaxExtrapolationSeconds;
            position = tail.Position + tailVelocity * (float)ahead;
            rotation = tail.Rotation;
            velocity = tailVelocity;
            thrust = tail.Thrust;
            return true;
        }

        public void ClearSnapshots() => samples.Clear();

        internal void TrimSamplesOlderThan(double cutoffTime)
        {
            while (samples.Count > 2 && samples[0].ServerTime < cutoffTime)
                samples.RemoveAt(0);
        }
    }
}
