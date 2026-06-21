using System;
using System.Collections.Generic;
using UnityEngine;
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
    /// Snapshots are interpolated on their server timestamps, which the server stamps at an even tick
    /// cadence, so playback speed is constant regardless of network arrival jitter.
    /// </summary>
    [DefaultExecutionOrder(30000)]
    public sealed class ClientRenderTimeline : MonoBehaviour
    {
        public static ClientRenderTimeline Instance { get; private set; }

        // Render remote ships ~100 ms in the past (≈3 snapshots at the 30 Hz unreliable motion stream rate).
        // Large enough to absorb send jitter and ride out single dropped packets while staying in the
        // interpolation regime instead of the extrapolation tail. The server stays authoritative for hits,
        // so this "render others slightly in the past" trade-off does not affect aiming.
        [SerializeField, Range(0.05f, 0.25f)] private float interpolationDelaySeconds = 0.10f;
        // Proportional gain used only to correct slow client/server clock drift, never to chase the
        // per-packet stepping of `target`.
        [SerializeField, Range(0.5f, 8f)] private float clockSlewRate = 3f;
        // Hard cap on how far the playhead may deviate from a true 1:1 real-time rate while correcting drift,
        // expressed as a fraction of real time (0.04 = at most ±4% faster/slower). This is the key to smoothness:
        // it keeps the playhead from accelerating/decelerating with each arriving snapshot. The natural
        // real-time advance already tracks the average data rate, so only a tiny correction budget is needed.
        [SerializeField, Range(0.01f, 0.25f)] private float maxClockCorrectionRate = 0.04f;
        // Drift beyond this (first packet, big hitch, pause, teleport) hard-resyncs the playhead.
        [SerializeField, Range(0.2f, 2f)] private float hardResyncThreshold = 0.4f;

        private double playheadServerTime;
        private double latestSnapshotServerTime;
        private bool hasLatestSnapshot;
        private bool initialized;
        private readonly HashSet<ClientRenderTimelineSource> sources = new HashSet<ClientRenderTimelineSource>(64);

        public double RenderServerTime => playheadServerTime;
        public float InterpolationDelaySeconds => interpolationDelaySeconds;

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
        }

        private void LateUpdate()
        {
            if (!hasLatestSnapshot)
                return;

            double target = latestSnapshotServerTime - interpolationDelaySeconds;

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

            double trimBefore = playheadServerTime - interpolationDelaySeconds * 3.0;
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

        public void PushSnapshot(double serverTime, Vector3 position, Quaternion rotation, Vector3 velocity, uint lastProcessedInputSeq)
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

        public bool TrySampleAt(double renderServerTime, out Vector3 position, out Quaternion rotation, out Vector3 velocity)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            velocity = Vector3.zero;
            if (samples.Count == 0) return false;
            if (samples.Count == 1)
            {
                position = samples[0].Position;
                rotation = samples[0].Rotation;
                velocity = samples[0].Velocity;
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
                velocity = Vector3.Lerp(a.Velocity, b.Velocity, t);
                return true;
            }

            // Buffer underrun: playhead is past the newest snapshot. Dead-reckon along last known velocity.
            TimelineSample tail = samples[samples.Count - 1];
            double ahead = renderServerTime - tail.ServerTime;
            if (ahead < 0.0) ahead = 0.0;
            if (ahead > MaxExtrapolationSeconds) ahead = MaxExtrapolationSeconds;
            position = tail.Position + tail.Velocity * (float)ahead;
            rotation = tail.Rotation;
            velocity = tail.Velocity;
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
