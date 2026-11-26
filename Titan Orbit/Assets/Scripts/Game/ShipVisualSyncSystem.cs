using System.Collections.Generic;
using TitanOrbit;
using TitanOrbit.Core;
using TitanOrbit.ECS;
using TitanOrbit.Generation;
using TitanOrbit.Shared;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Publishes NetCode presentation-phase ship poses once per frame for camera, hybrid leftovers,
    /// and parallax. Remote and AI hulls are driven by <see cref="ShipRemoteMotorSystem"/> up to
    /// the estimated server tick. This system copies that motor pose. It does not add a
    /// velocity lead, and it does not coast toward an interpolated snapshot.
    /// <para>
    /// [TITAN-ORBIT] Local ship wraps onto the canonical chart; camera follows that pose.
    /// The published pose is the motor tick (predicted <c>LocalTransform</c> for the owner,
    /// <see cref="ShipRemoteMotorState.DisplayPosition"/> for everyone else). Collision
    /// separation stays inside that tick. This system does not blend, coast, or ease a
    /// contact. A ±map-size jump is the wrapped motor pose in one frame. While dead, display
    /// stays on the wreck — a one-frame LocalTransform hop to the home-ring spawn (asteroid
    /// death) must not drag the camera there and back. <see cref="CameraFollowEcs"/> copies
    /// this pose on a bounce the same way it does in free flight.
    /// Online reconcile steps that pose in
    /// <see cref="ShipLocalTransformPredictionSmoothing"/> before this copy. This
    /// system still does not add a second blend.
    /// </para>
    /// <para>
    /// [TITAN-ORBIT] Asteroid destroy / Join Team eye-blink (2026-07-20 Editor.log): any miss from
    /// <see cref="EcsGameBridge"/> (GhostSpawnBacklog ToEntityArray gate OR team suppress) used to
    /// call <see cref="ToroidalDisplay.ResetSession"/> every frame and wipe ~283 tiles — whole-map
    /// blink. Fix: reuse cached ship when it still exists; never ResetSession during backlog /
    /// team suppress; debounce confirmed despawn (~45 frames) and clear tiles/pose only once.
    /// Join Settling still skips everything. Backlog still skips remote <c>WithEntityAccess</c>.
    /// </para>
    /// People-transport float poses are published afterward by
    /// <see cref="PeopleTransportVisualSyncSystem"/> (not from raw ghost LT).
    /// World: ClientSimulation. Group: PresentationSystemGroup (OrderLast).
    /// </summary>
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation)]
    [UpdateInGroup(typeof(PresentationSystemGroup), OrderLast = true)]
    public partial class ShipVisualSyncSystem : SystemBase
    {
        /// <summary>
        /// Respawn farther than this clears the toroidal display session. The mesh still
        /// copies the motor pose that frame. Otherwise a home-ring respawn keeps the old
        /// tile set and the map blinks.
        /// </summary>
        const float DisplayRespawnSnapDistance = 50f;

        /// <summary>
        /// While <see cref="ShipState.IsDead"/>, ignore sim poses farther than this from the
        /// wreck. Asteroid death can present one frame at the home-ring spawn, then the
        /// ghost returns to the field — following that frame cuts the camera there and back.
        /// </summary>
        const float DeathWreckHoldDistance = 40f;

        /// <summary>
        /// [TITAN-ORBIT] Frames the local ship must stay unresolved before we treat it as a real
        /// despawn. Editor.log 2026-07-20: calling ResetSession every Null frame wiped ~283 tiles
        /// and blinked the whole map (Join Team suppress / brief lookup misses).
        /// </summary>
        const int MissingShipClearDelayFrames = 45;

        // --- Local-owner display state (not sim) ---
        float3 _smoothPos;
        quaternion _smoothRot = quaternion.identity;
        bool _smoothInitialized;
        Entity _smoothShipEntity;

        /// <summary>Consecutive frames where local ship could not be resolved (despawn debounce).</summary>
        int _missingShipFrames;

        /// <summary>True after we already ran one ResetSession/ClearLocalPose for this missing streak.</summary>
        bool _missingShipCleared;

        /// <summary>
        /// Previous presentation frame's local <see cref="ShipState.IsDead"/>. Detects the
        /// death→alive edge so a far respawn clears the toroidal tile session.
        /// </summary>
        bool _localShipWasDead;

        /// <summary>Per remote hull: last frame it was published, so a despawn can leave the cache.</summary>
        readonly Dictionary<Entity, int> _remoteFrame = new Dictionary<Entity, int>(32);

        /// <summary>Reused keys for remotes that were not published this frame.</summary>
        readonly List<Entity> _remotePrune = new List<Entity>(32);

        /// <summary>
        /// Each presentation frame: copy the remote motor pose, then copy the local owner.
        /// Local pose keeps updating during gem Instantiates backlog so the camera does not
        /// freeze then jump.
        /// </summary>
        protected override void OnUpdate()
        {
            // --- Frame stamp for LateUpdate readers ---
            GhostPresentationTransformCache.BeginPublish(UnityEngine.Time.frameCount);

            // --- Join / TeamChoice Instantiates: skip ship archetype gathers ---
            // [TITAN-ORBIT] Settling-only is NOT enough after Join Team (Settling stays OFF).
            // ShouldSkipShipEntityQueries = Settling OR GhostSpawnBacklog OR post–TeamChoice hold
            // OR deferred Confirm. Remotes / BankPivot WithEntityAccess Crash!!! in that window
            // (Player.log 2026-07-19 / 2026-07-28). Local pose still updates from a known entity.
            bool skipShipGathers = ClientJoinSettleCache.ShouldSkipShipEntityQueries;
            if (ClientJoinSettleCache.Settling)
                return;

            // --- Resolve local ship (safe during gem Instantiates / brief lookup misses) ---
            // [TITAN-ORBIT] EcsGameBridge.TryGetLocalShipEntity returns false while
            // ShouldSkipShipEntityQueries OR while team suppress is on. Neither is a
            // despawn. ResetSession every miss wiped ~283 tiles → whole-map blink.
            // Keep using the cached ship entity whenever it still exists (not only during backlog).
            bool teamSuppress = ClientTeamFlowState.ShouldSuppressLocalPlayerControl();
            Entity localShip = Entity.Null;
            bool resolved = !skipShipGathers &&
                            EcsGameBridge.TryGetLocalShipEntityOnWorld(World, out localShip);
            if (!resolved &&
                !teamSuppress &&
                _smoothShipEntity != Entity.Null &&
                EntityManager.Exists(_smoothShipEntity) &&
                EntityManager.HasComponent<ShipTag>(_smoothShipEntity) &&
                LocalShipEntitySeed.EntityMatchesLocalOwner(EntityManager, _smoothShipEntity))
            {
                localShip = _smoothShipEntity;
                resolved = true;
            }

            // --- Instantiates-hook seed (post–Join Team ship while gathers are gated) ---
            // [TITAN-ORBIT] Without a seed, hold leaves hasPose=false then camera snaps on first pose.
            if (!resolved &&
                !teamSuppress &&
                LocalShipEntitySeed.TryGetSeededShip(EntityManager, out var seededShip) &&
                LocalShipEntitySeed.EntityMatchesLocalOwner(EntityManager, seededShip))
            {
                localShip = seededShip;
                resolved = true;
            }

            if (resolved &&
                !LocalShipEntitySeed.EntityMatchesLocalOwner(EntityManager, localShip))
            {
                localShip = Entity.Null;
                resolved = false;
                _smoothShipEntity = Entity.Null;
            }

            // --- Remotes: never WithEntityAccess while ShouldSkipShipEntityQueries ---
            // [TITAN-ORBIT] Player.log 2026-07-19 / 2026-07-28: hand-rolled GhostSpawnBacklog alone
            // missed deferred Confirm / TeamChoice hold folds. Use the helper API.
            if (!skipShipGathers)
            {
                // Motor pose only. A render frame between ticks does not invent a lead.
                int frame = UnityEngine.Time.frameCount;
                foreach (var (lt, entity) in SystemAPI
                             .Query<RefRO<LocalTransform>>()
                             .WithAll<ShipTag>()
                             .WithEntityAccess())
                {
                    if (entity == localShip)
                        continue;
                    if (SystemAPI.HasComponent<GhostOwnerIsLocal>(entity) &&
                        SystemAPI.IsComponentEnabled<GhostOwnerIsLocal>(entity))
                        continue;

                    LocalTransform display = PublishRemotePose(entity, lt.ValueRO);
                    _remoteFrame[entity] = frame;
                    PublishShip(entity, display);
                    if (EntityManager.HasComponent<LocalToWorld>(entity))
                    {
                        EntityManager.SetComponentData(entity, new LocalToWorld
                        {
                            Value = display.ToMatrix(),
                        });
                    }
                }

                PruneRemoteDisplays(frame);
            }
            else if (TitanOrbitDebugFlags.LogAsteroidDestroyPerf)
            {
                // [TITAN-ORBIT] Rate-limit — backlog can last many frames during map Instantiates;
                // logging every frame floods the Console and looks like a hard error storm.
                if (UnityEngine.Time.frameCount % 60 == 0)
                {
                    Debug.Log(
                        $"[AsteroidDestroy] ShipVisualSync skipGathers: localShipCached={resolved} " +
                        $"entityIndex={localShip.Index} frameDtMs={UnityEngine.Time.deltaTime * 1000f:F1}");
                }
            }

            // --- People transports ---
            // [TITAN-ORBIT] Owned by PeopleTransportVisualSyncSystem (UpdateAfter this).

            // --- Local owner: GetComponentData on known entity — no ship gather ---
            PublishLocalShipDisplayPose(localShip, skipBankPivotQuery: skipShipGathers);
        }

        /// <summary>
        /// Copies the local owner's motor pose for the camera and the mesh.
        /// A dead wreck holds across a one-frame spawn hop. A far respawn clears tiles.
        /// </summary>
        /// <param name="localShip">Local player ship entity, or Null when not spawned.</param>
        /// <param name="skipBankPivotQuery">
        /// When true (<see cref="ClientJoinSettleCache.ShouldSkipShipEntityQueries"/>), skip the
        /// BankPivot <c>WithEntityAccess</c> walk — still write ship LocalToWorld + ShipDisplayPose
        /// so the camera keeps tracking.
        /// </param>
        void PublishLocalShipDisplayPose(Entity localShip, bool skipBankPivotQuery = false)
        {
            if (localShip == Entity.Null || !EntityManager.Exists(localShip))
            {
                HandleMissingLocalShip();
                return;
            }

            // --- Ship is back — clear missing-ship debounce ---
            _missingShipFrames = 0;
            _missingShipCleared = false;

            if (!EntityManager.HasComponent<LocalTransform>(localShip))
                return;

            var lt = EntityManager.GetComponentData<LocalTransform>(localShip);
            // --- Canonical wrapped sim pose — camera follows this ---
            float3 targetPos = lt.Position;
            quaternion targetRot = lt.Rotation;

            bool shipChanged = _smoothInitialized && _smoothShipEntity != Entity.Null && localShip != _smoothShipEntity;
            _smoothShipEntity = localShip;

            // --- Death / respawn edge ---
            // [TITAN-ORBIT] Respawn teleports LocalTransform to the home orbit ring. Display
            // stays on the wreck for the countdown. A one-frame hop to that spawn (asteroid
            // death) used to hard-snap the camera home, then the next frame snapped back to
            // the field. Hold the wreck across that spike. The next live pose copies the motor.
            bool isDead = EntityManager.HasComponent<ShipState>(localShip) &&
                          EntityManager.GetComponentData<ShipState>(localShip).IsDead;
            bool justRespawned = _localShipWasDead && !isDead;
            _localShipWasDead = isDead;

            float displayErr = _smoothInitialized ? math.distance(_smoothPos, targetPos) : 0f;
            bool holdWreck = isDead
                             && _smoothInitialized
                             && !shipChanged
                             && displayErr > DeathWreckHoldDistance;
            bool wrapJump = _smoothInitialized
                            && !holdWreck
                            && ToroidalMapEcs.IsWrapJump(_smoothPos, targetPos);
            if (wrapJump)
                MapWrapTransition.NotifyWrap();

            // holdWreck leaves _smoothPos on the death pose (published below).
            if (!holdWreck)
            {
                if (shipChanged)
                    ToroidalDisplay.ResetSession("ShipVisualSync.shipChanged");
                else if (justRespawned && displayErr > DisplayRespawnSnapDistance)
                    ToroidalDisplay.ResetSession("ShipVisualSync.respawn");
                _smoothPos = targetPos;
                _smoothRot = targetRot;
                _smoothInitialized = true;
            }

            // --- Publish pose for camera / hybrid / EG LocalToWorld ---
            float3 published = _smoothPos;
            var displayLt = lt;
            displayLt.Position = published;
            displayLt.Rotation = _smoothRot;
            PublishShip(localShip, displayLt);
            ShipDisplayPose.SetLocalPose((Vector3)published, (Quaternion)_smoothRot);

            // [ECS/DOTS] Entities Graphics reads LocalToWorld — override after transform systems this frame.
            if (!EntityManager.HasComponent<LocalToWorld>(localShip))
                return;

            var shipLtw = new LocalToWorld { Value = displayLt.ToMatrix() };
            EntityManager.SetComponentData(localShip, shipLtw);

            // --- Bank pivots: full query is unsafe during ShouldSkipShipEntityQueries Instantiates ---
            // Camera already has ShipDisplayPose; pivot LTW can wait a frame.
            if (skipBankPivotQuery)
                return;

            foreach (var (pivotTag, pivotLt, pivotEntity) in SystemAPI
                         .Query<RefRO<ShipVisualBankPivotTag>, RefRO<LocalTransform>>()
                         .WithEntityAccess())
            {
                if (pivotTag.ValueRO.ShipEntity != localShip)
                    continue;
                if (!EntityManager.HasComponent<LocalToWorld>(pivotEntity))
                    continue;

                EntityManager.SetComponentData(pivotEntity, new LocalToWorld
                {
                    Value = math.mul(shipLtw.Value, pivotLt.ValueRO.ToMatrix()),
                });
            }
        }

        /// <summary>
        /// Local ship entity is Null / destroyed this frame. Keep pose + tile memory across
        /// GhostSpawnBacklog, team-suppress, and brief lookup misses. Only after a sustained gap
        /// do we clear once (not every frame — that wiped hundreds of tiles and blinked the map).
        /// </summary>
        void HandleMissingLocalShip()
        {
            // --- Gated lookup / Join Team UI — never thrash tiles ---
            // [TITAN-ORBIT] Backlog: gem Instantiates after asteroid destroy. Suppress: map visible
            // before TeamChoiceConfirmed. Both used to call ResetSession every frame (Editor.log).
            // Prefer ShouldSkipShipEntityQueries (includes post–TeamChoice hold + deferred Confirm)
            // over GhostSpawnBacklog alone — a stale backlog=false during the hold used to start the
            // despawn debounce and Clear() the Instantiates-hook seed before Confirm flushed.
            if (ClientJoinSettleCache.ShouldSkipShipEntityQueries ||
                ClientTeamFlowState.ShouldSuppressLocalPlayerControl())
            {
                _missingShipFrames = 0;
                return;
            }

            _missingShipFrames++;

            // --- Still holding last pose for camera / toroidal reference ---
            if (_missingShipFrames < MissingShipClearDelayFrames)
                return;

            // --- Confirmed leave / despawn: clear once per streak ---
            if (_missingShipCleared)
                return;

            _missingShipCleared = true;
            ResetLocalDisplaySmoothing();
            LocalShipEntitySeed.Clear();
            ToroidalDisplay.ResetSession("ShipVisualSync.PublishLocalShip_missingShip_confirmed");
            ShipDisplayPose.ClearLocalPose("ShipVisualSync.PublishLocalShip_missingShip_confirmed");
        }

        /// <summary>
        /// Clears the last published local pose so the next ship copies its motor pose
        /// instead of keeping a disconnect pose for one frame.
        /// </summary>
        void ResetLocalDisplaySmoothing()
        {
            _smoothInitialized = false;
            _smoothShipEntity = Entity.Null;
            _smoothPos = float3.zero;
            _smoothRot = quaternion.identity;
            _localShipWasDead = false;
        }

        /// <summary>
        /// Copies the remote motor pose. The same copy for every hull: no velocity lead,
        /// no collision snap, no per-peer blend. Before the motor is valid, the ghost pose
        /// is the only sample the client has.
        /// </summary>
        LocalTransform PublishRemotePose(Entity entity, in LocalTransform snapshot)
        {
            float3 target = snapshot.Position;
            quaternion targetRot = snapshot.Rotation;
            if (EntityManager.HasComponent<ShipRemoteMotorState>(entity))
            {
                var motor = EntityManager.GetComponentData<ShipRemoteMotorState>(entity);
                if (motor.Valid != 0)
                {
                    target = motor.DisplayPosition;
                    targetRot = motor.DisplayRotation;
                }
            }

            var display = snapshot;
            display.Position = target;
            display.Rotation = targetRot;
            return display;
        }

        /// <summary>Drops presentation cache entries for hulls that were not published this frame.</summary>
        void PruneRemoteDisplays(int frame)
        {
            _remotePrune.Clear();
            foreach (var kv in _remoteFrame)
            {
                if (kv.Value != frame)
                    _remotePrune.Add(kv.Key);
            }

            for (int i = 0; i < _remotePrune.Count; i++)
            {
                _remoteFrame.Remove(_remotePrune[i]);
                GhostPresentationTransformCache.ForgetShip(_remotePrune[i]);
            }
        }

        /// <summary>Writes one ship snapshot into the presentation cache.</summary>
        static void PublishShip(Entity entity, in LocalTransform transform) =>
            GhostPresentationTransformCache.PublishShip(entity, ToSnapshot(transform));

        /// <summary>Maps <see cref="LocalTransform"/> into the hybrid presentation cache format.</summary>
        static GhostPresentationTransformCache.Snapshot ToSnapshot(in LocalTransform transform) =>
            new GhostPresentationTransformCache.Snapshot
            {
                Position = transform.Position,
                Rotation = transform.Rotation,
                Scale = transform.Scale,
            };
    }
}
