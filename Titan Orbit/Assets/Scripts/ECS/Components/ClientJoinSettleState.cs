using TitanOrbit.Core;
using Unity.Entities;
using Unity.NetCode;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// [TITAN-ORBIT] Client singleton that tracks late-join Instantiates settle.
    /// While <see cref="Settling"/> is non-zero, hybrid/UI code must skip full map-body
    /// <c>ToEntityArray</c> scans. GameObject Instantiates stay rate-limited via Pending drain.
    /// <para>
    /// Player.log proved <see cref="Unity.Transforms.TransformSystemGroup"/> RE-ENABLED after
    /// Instantiates hundreds of asteroids → immediate Burst <c>Crash!!!</c>. So while in-game the
    /// transform group stays off (<see cref="ClientJoinSettleCache.TransformQuarantine"/>) and
    /// ships render as hybrid GameObject proxies instead of Entities Graphics.
    /// </para>
    /// Written by <see cref="TitanOrbitClientJoinTransformGateSystem"/>.
    /// </summary>
    public struct ClientJoinSettleState : IComponentData
    {
        /// <summary>1 while join Instantiates / GhostSpawn backlog is still draining.</summary>
        public byte Settling;

        /// <summary>Consecutive frames with empty GhostSpawnBuffer and zero PendingSpawnPlaceholder.</summary>
        public int IdleClearFrames;

        /// <summary>Frames since NetworkStreamInGame became true this session.</summary>
        public int InGameFrames;

        /// <summary>1 after we observed any spawn-buffer or placeholder activity this join.</summary>
        public byte SawSpawnActivity;

        /// <summary>
        /// 1 after Settling has exited once this session. Prevents re-entering Settling for
        /// post-team ship Instantiates (Player.log Crash!!! after TeamChoice).
        /// </summary>
        public byte JoinSettleCompleted;
    }

    /// <summary>
    /// [HYBRID] Managed mirror of settle / transform quarantine for MonoBehaviours.
    /// </summary>
    public static class ClientJoinSettleCache
    {
        /// <summary>True while GhostSpawn Instantiates backlog is active.</summary>
        public static bool Settling { get; private set; }

        /// <summary>
        /// True while in-game with TransformSystemGroup forced off (Windows late-join safety).
        /// When true, ships must use hybrid GO proxies — Entities Graphics needs Parent/LTW.
        /// </summary>
        public static bool TransformQuarantine { get; private set; }

        /// <summary>Frames in-game this session (diagnostic).</summary>
        public static int InGameFrames { get; private set; }

        /// <summary>
        /// True after the initial join Instantiates settle finished — ship Instantiates after
        /// Join Team must not flip Settling back on.
        /// </summary>
        public static bool JoinSettleCompleted { get; private set; }

        /// <summary>
        /// True while GhostSpawnBuffer or PendingSpawnPlaceholder is non-empty — including the
        /// brief ship Instantiates window after Join Team when Settling stays OFF.
        /// Ship WithEntityAccess / EnsureShipProxies must skip while this is true
        /// (Player.log 2026-07-19 TeamChoiceResult → Crash!!!).
        /// </summary>
        public static bool GhostSpawnBacklog { get; private set; }

        /// <summary>
        /// [TITAN-ORBIT] True when ship <c>ToEntityArray</c> / <c>WithEntityAccess</c> must not run.
        /// Covers Settling, GhostSpawnBacklog (incl. post-ship hold), and a short post–TeamChoice
        /// hold while the ship Instantiates (Settling stays OFF after JoinSettleCompleted).
        /// <para>
        /// Intentional: do <b>not</b> gate forever on <c>TeamChoiceConfirmed &amp;&amp; !HasOwnedShipSeed</c>.
        /// That deadlock stuck the lobby on "Spawning your ship..." when Instantiates-hook seeding
        /// missed once — recovery queries could never run (Editor.log 2026-07-23).
        /// </para>
        /// Prefer this over hand-rolling flags so TeamChoice Crash!!! gates stay one-liners
        /// (see titan-orbit-teamchoice-crash-hardstop.mdc).
        /// </summary>
        public static bool ShouldSkipShipEntityQueries =>
            Settling ||
            GhostSpawnBacklog ||
            s_PostTeamChoiceHoldRemaining > 0 ||
            // [TITAN-ORBIT] Deferred Confirm keeps suppress on; also fold into the helper so
            // hand-rolled GhostSpawnBacklog-only checks are not the only line of defense.
            ClientTeamFlowState.HasDeferredTeamChoiceConfirmPending;

        /// <summary>
        /// [TITAN-ORBIT] True when client code must not gather planets / asteroids / gems / moons
        /// (<c>ToEntityArray</c>, <c>WithEntityAccess</c>, broad <c>foreach</c>).
        /// <para>
        /// <see cref="TransformQuarantine"/> stays true for the whole Windows in-game session.
        /// Gating map gathers on <see cref="Settling"/> alone is forbidden: after Join Team,
        /// Settling is OFF (<see cref="JoinSettleCompleted"/>) but full map gathers still
        /// Crash!!! (Player.log 2026-07-18 Settling OFF; 2026-07-22 TeamChoice toroidal collide).
        /// </para>
        /// Prefer this helper over hand-rolled flags so new systems cannot omit quarantine.
        /// </summary>
        public static bool ShouldSkipMapBodyQueries => TransformQuarantine || Settling;

        /// <summary>
        /// Extra frames to keep <see cref="GhostSpawnBacklog"/> true after a <b>ship</b> Instantiates
        /// even when GhostSpawnBuffer / PendingSpawnPlaceholder are already empty.
        /// TeamChoice ship Instantiates clears the placeholder the same frame — without this hold,
        /// ship systems fail-open immediately (Player.log 2026-07-22 TeamChoiceResult → Crash!!!).
        /// <para>
        /// Intentional: do <b>not</b> re-arm on every map Instantiates. Distance-importance keeps
        /// streaming asteroids at 1/frame after Settling OFF; arming on every Instantiates left
        /// GhostSpawnBacklog true forever → no hybrid ship, HUD stuck on "Spawning your ship...".
        /// </para>
        /// </summary>
        const int PostShipInstantiateHoldFrames = 15;

        /// <summary>
        /// Frames to skip ship gathers after TeamChoice / rejoin confirm while the ship ghost
        /// Instantiates. Expires so a missed Instantiates-hook seed can still recover via a tiny
        /// ship query once Instantiates are idle.
        /// </summary>
        const int PostTeamChoiceHoldFrames = 45;

        /// <summary>Remaining frames of ship Instantiates hold (counts down once per Unity frame).</summary>
        static int s_PostShipInstantiateHoldRemaining;

        /// <summary>Remaining frames of post–TeamChoice Instantiates gap hold.</summary>
        static int s_PostTeamChoiceHoldRemaining;

        /// <summary><see cref="UnityEngine.Time.frameCount"/> of the last hold tick (dedupe dual callers).</summary>
        static int s_PostShipInstantiateHoldTickFrame = -1;

        /// <summary>Updates settle + quarantine flags from the join gate system.</summary>
        public static void Set(
            bool settling,
            bool transformQuarantine,
            int inGameFrames,
            bool joinSettleCompleted,
            bool ghostSpawnBacklog)
        {
            Settling = settling;
            TransformQuarantine = transformQuarantine;
            InGameFrames = inGameFrames;
            JoinSettleCompleted = joinSettleCompleted;
            // [TITAN-ORBIT] Always fold Instantiates hold into the published backlog bit.
            GhostSpawnBacklog = ComputeGhostSpawnBacklog(ghostSpawnBacklog);
            TickPostTeamChoiceHold();
        }

        /// <summary>
        /// Refreshes only <see cref="GhostSpawnBacklog"/> after GhostSpawn runs mid-frame.
        /// The join gate publishes backlog in InitializationSystemGroup — before GhostSpawn —
        /// so MonoBehaviours in LateUpdate would otherwise see a stale false on the arrival frame.
        /// </summary>
        public static void SetGhostSpawnBacklog(bool ghostSpawnBacklog)
        {
            GhostSpawnBacklog = ComputeGhostSpawnBacklog(ghostSpawnBacklog);
            TickPostTeamChoiceHold();
        }

        /// <summary>
        /// Arms the short post–ship Instantiates hold. Call from
        /// <c>LocalShipEntitySeed.NotifyShipInstantiated</c> only — not from map Instantiates.
        /// Immediately publishes <see cref="GhostSpawnBacklog"/> so same-frame LateUpdate
        /// paths that still check the backlog bit (not only <see cref="ShouldSkipShipEntityQueries"/>)
        /// do not fail-open.
        /// </summary>
        public static void ArmPostShipInstantiateHold()
        {
            s_PostShipInstantiateHoldRemaining = PostShipInstantiateHoldFrames;
            // [TITAN-ORBIT] Publish now — do not wait for the next join-gate / GhostSpawn refresh.
            GhostSpawnBacklog = true;
        }

        /// <summary>
        /// Arms the short TeamChoice → ship Instantiates gap hold.
        /// Call from TeamChoice / rejoin handlers <b>before</b> <see cref="ClientTeamFlowState.ConfirmTeamChoice"/>
        /// lifts suppress. Immediately sets <see cref="GhostSpawnBacklog"/> true.
        /// Player.log 2026-07-23: hold was armed but not folded into GhostSpawnBacklog → same-frame
        /// LateUpdate (gem tractor ship <c>ToEntityArray</c>) Crash!!!.
        /// </summary>
        public static void ArmPostTeamChoiceHold()
        {
            s_PostTeamChoiceHoldRemaining = PostTeamChoiceHoldFrames;
            // [TITAN-ORBIT] Publish now — do not wait for join-gate / GhostSpawn refresh.
            GhostSpawnBacklog = true;
        }

        /// <summary>
        /// Queue/placeholder non-empty <b>or</b> recent <b>ship</b> Instantiates hold
        /// <b>or</b> post–TeamChoice Instantiates gap hold.
        /// Call from the join gate and from <c>TitanOrbitGhostSpawnBacklogRefreshSystem</c>.
        /// Hold decrements at most once per Unity frame even when both callers run.
        /// </summary>
        /// <param name="queueOrPlaceholdersNonEmpty">True while GhostSpawn still has work queued.</param>
        /// <returns>Effective backlog flag for ship / Instantiates-sensitive presentation.</returns>
        public static bool ComputeGhostSpawnBacklog(bool queueOrPlaceholdersNonEmpty)
        {
            // --- One hold tick per rendered frame ---
            // [TITAN-ORBIT] Ship hold: ArmPostShipInstantiateHold only (not map Instantiates).
            // TeamChoice hold: ArmPostTeamChoiceHold — MUST be part of GhostSpawnBacklog so every
            // Settling||GhostSpawnBacklog gate covers TeamChoiceResult (2026-07-23 Crash!!!).
            int frame = UnityEngine.Time.frameCount;
            if (s_PostShipInstantiateHoldTickFrame != frame)
            {
                s_PostShipInstantiateHoldTickFrame = frame;
                if (s_PostShipInstantiateHoldRemaining > 0)
                    s_PostShipInstantiateHoldRemaining--;
                if (s_PostTeamChoiceHoldRemaining > 0)
                    s_PostTeamChoiceHoldRemaining--;
            }

            return queueOrPlaceholdersNonEmpty ||
                   s_PostShipInstantiateHoldRemaining > 0 ||
                   s_PostTeamChoiceHoldRemaining > 0;
        }

        /// <summary>
        /// Ensures post–TeamChoice hold still counts down when only <see cref="Set"/> / backlog
        /// refresh run (ComputeGhostSpawnBacklog already ticks both holds).
        /// </summary>
        static void TickPostTeamChoiceHold()
        {
            // ComputeGhostSpawnBacklog already decrements both holds once per frame.
            // Kept as a named step so Set/SetGhostSpawnBacklog call sites stay readable.
        }

        /// <summary>Clears when leaving a session / not in-game.</summary>
        public static void Clear()
        {
            Settling = false;
            TransformQuarantine = false;
            InGameFrames = 0;
            JoinSettleCompleted = false;
            GhostSpawnBacklog = false;
            s_PostShipInstantiateHoldRemaining = 0;
            s_PostTeamChoiceHoldRemaining = 0;
            s_PostShipInstantiateHoldTickFrame = -1;
            // [NETCODE] GhostSpawn join counters — next Relay join starts from zero.
            TitanOrbitJoinLoadCounters.Reset();
        }
    }
}
