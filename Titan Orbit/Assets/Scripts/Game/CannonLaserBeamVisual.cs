using System.Collections.Generic;
using System.Reflection;
using TitanOrbit.Audio;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using TitanOrbit.Generation;
using TitanOrbit.NetCode;
using TitanOrbit.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Client-only pooled Archanor <c>LaserStatic</c> beams for MEGA cannons.
    /// Stretch each live beam from the hybrid barrel to the ghosted lock
    /// (<see cref="MegaShipGunnerSlotElement"/>). Cosmetic — damage is server hitscan.
    /// Shift mouse-aim follows the cursor, then clips to the first collider along
    /// that segment via <see cref="BulletCosmeticHitQuery"/> (same spheres / MEGA
    /// parts as tracers) so the line stops on the hull instead of tunneling through
    /// to the mouse. While a lock is burning, this driver reports each replicated
    /// <see cref="MegaShipGunnerSlotElement.CannonLaserBurnSequence"/> step to
    /// <see cref="EcsFloatingCountPresenter"/> — not a local <c>Time.deltaTime</c>
    /// guess, which reached 0 HP before the asteroid pop. Lasers stay hidden while the
    /// 10% recharge lockout is latched.
    /// Beam width tracks the 50%→300% DPS ramp so the line starts thin and fattens.
    /// Beams stay on while a live lock (or Shift mouse-aim) is burning.
    /// Barrels the arsenal HUD muted (<see cref="ShipWeaponArmState"/>) stay dark
    /// even while Fire is held — local auto-lock used to draw every cannon.
    /// <para>
    /// The Archanor line is world-space. Pose the root and snap the
    /// <see cref="LineRenderer"/> after hybrid hulls move — otherwise the beam sits
    /// on last physics tick while the titan interpolates (jitter while moving).
    /// </para>
    /// </summary>
    [DefaultExecutionOrder(67040)]
    public sealed class CannonLaserBeamVisual : MonoBehaviour
    {
        static CannonLaserBeamVisual _instance;

        struct BeamKey : System.IEquatable<BeamKey>
        {
            public int ShipIndex;
            public int MountIndex;

            public bool Equals(BeamKey other) =>
                ShipIndex == other.ShipIndex && MountIndex == other.MountIndex;

            public override bool Equals(object obj) => obj is BeamKey other && Equals(other);

            public override int GetHashCode() => unchecked(ShipIndex * 397) ^ MountIndex;
        }

        sealed class BeamSlot
        {
            public GameObject Root;
            public MonoBehaviour Vendor;
            public LineRenderer Line;
            public TeamId Team;
            public float LastUsed;
            public float LastSeenLive;
            public bool Shown;
            public bool NeedsSilence;
            public bool WidthReady;
            public float VendorBaseWidth;
            public Vector3 StartFxBaseScale;
            public Vector3 EndFxBaseScale;
            public int StickyGhostId;
            public float StickyAimX;
            public float StickyAimZ;
            public float LocalRampSeconds;
            public Entity RampTarget;
            public int RampGhostId;
            /// <summary>
            /// Last burn subject on <see cref="RampTarget"/>. Pad slot + 1, or
            /// <see cref="CannonLaserHitApply.FocusMoon"/>. <see cref="int.MinValue"/>
            /// until the first resolved subject so a late aim does not wipe charge.
            /// </summary>
            public int RampFocus = int.MinValue;
            /// <summary>False until this pooled beam has seen one burn-sequence snapshot.</summary>
            public bool BurnSequenceReady;
            /// <summary>Last <see cref="MegaShipGunnerSlotElement.CannonLaserBurnSequence"/> already shown.</summary>
            public uint LastBurnSequence;
        }

        /// <summary>
        /// First snapshot of a pooled beam. A short lead-in is the start of this
        /// burn; a large value is a barrel we just started watching mid-match.
        /// </summary>
        const uint MaxBurnLeadInTicks = 8;

        /// <summary>
        /// In-progress catch-up. A hitch can deliver about a second of server
        /// ticks at once — those still belong on the streak. A full uint wrap
        /// (sequence reset) is larger than this and is dropped.
        /// </summary>
        const uint MaxBurnCatchUpTicks = 90;

        /// <summary>
        /// One looping hum for every live cannon beam. Clip is Archanor
        /// <c>loop_laser2.wav</c> (BeamLaserStart PlayOnAwake+Loop at pitch 1.3).
        /// We mute those vendor sources and play this once; pitch starts low
        /// at 50% DPS and rises (still in hum range) as the hottest beam charges.
        /// </summary>
        AudioSource _hum;
        AudioClip _humClip;

        /// <summary>
        /// Local copy of the 10% recharge latch so Shift mouse-aim cannot keep
        /// drawing after the pool hits empty (server lockout can lag one snapshot).
        /// </summary>
        bool _localEnergyLockout;

        /// <summary>Keep a beam drawn across one-tick TargetDistance ghost drops.</summary>
        const float HoldSeconds = 0.45f;

        /// <summary>
        /// Local Fire + a recent lock keeps the hum even when one snapshot
        /// dropped <c>TargetDistance</c>. Fire release still cuts immediately.
        /// </summary>
        bool _humFireHeld;

        /// <summary>Hum pitch at 50% DPS (thin beam, first tick of a lock).</summary>
        const float HumPitchAtMinDps = 0.25f;

        /// <summary>
        /// Hum pitch at 300% DPS. Kept in hum range so full charge does not
        /// jump to the frantic 1.0 from the first pass.
        /// </summary>
        const float HumPitchAtMaxDps = 0.52f;

        /// <summary>Hottest live-beam ramp this frame (drives hum pitch).</summary>
        float _humRampSeconds;

        /// <summary>
        /// Line + muzzle/impact FX vs the stock LaserStatic width at 100% authored
        /// DPS. Live width is this × the 50%–300% ramp multiplier.
        /// </summary>
        const float VisualScale = 0.5f;

        /// <summary>
        /// Archanor <c>SciFiArsenalBeamStatic</c> lives in Assembly-CSharp — this
        /// Game asmdef cannot name that type. Public fields are set by reflection.
        /// </summary>
        static FieldInfo s_BeamCollidesField;
        static FieldInfo s_BeamLengthField;
        static FieldInfo s_OriginalWidthField;
        static FieldInfo s_CustomWidthField;
        static FieldInfo s_WidthMultiplierField;
        static FieldInfo s_BeamStartField;
        static FieldInfo s_BeamEndField;

        readonly Dictionary<BeamKey, BeamSlot> _live = new Dictionary<BeamKey, BeamSlot>(16);
        readonly List<BeamKey> _stale = new List<BeamKey>(8);

        CannonLaserVfxSettings _settings;

        /// <summary>Frame when <see cref="SyncAfterHullProxies"/> already posed beams.</summary>
        int _syncedAfterHullsFrame = -1;

        /// <summary>[UNITY] Ensures a scene driver exists after load.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void EnsureInstanceExists()
        {
            if (_instance != null)
                return;

            _instance = FindAnyObjectByType<CannonLaserBeamVisual>();
            if (_instance != null)
                return;

            var go = GameObject.Find("PlanetConnectionSystems");
            if (go == null)
                go = new GameObject("PlanetConnectionSystems");

            _instance = go.GetComponent<CannonLaserBeamVisual>();
            if (_instance == null)
                _instance = go.AddComponent<CannonLaserBeamVisual>();
        }

        void OnEnable()
        {
            _instance = this;
            _settings = CannonLaserVfxSettings.LoadDefault();
        }

        void OnDisable()
        {
            if (_instance == this)
                _instance = null;
            HideAll();
        }

        void OnDestroy()
        {
            DestroyAll();
        }

        /// <summary>
        /// Pins live beams to barrels the visualizer just posed (LateUpdate or
        /// <c>onBeforeRender</c>). Same-frame second calls no-op.
        /// </summary>
        public static void SyncAfterHullProxies()
        {
            if (_instance == null)
                return;
            if (_instance._syncedAfterHullsFrame == Time.frameCount)
                return;
            _instance.TickBeams();
            _instance._syncedAfterHullsFrame = Time.frameCount;
        }

        /// <summary>
        /// Fallback when the hybrid visualizer did not sync this frame (batch /
        /// headless). Skip when <see cref="SyncAfterHullProxies"/> already ran.
        /// </summary>
        void LateUpdate()
        {
            if (!Application.isPlaying)
                return;
            if (_syncedAfterHullsFrame == Time.frameCount)
                return;
            TickBeams();
        }

        /// <summary>
        /// Updates pooled beams from ghosted MEGA gunner locks. Uses hybrid barrel
        /// transforms when present so the line sits on the drawn turret.
        /// </summary>
        void TickBeams()
        {
            if (!Application.isPlaying)
                return;
            if (ClientJoinSettleCache.ShouldSkipShipEntityQueries)
            {
                HideAll();
                return;
            }

            var world = EcsGameBridge.GetVisualizationWorld();
            if (world == null || !world.IsCreated)
            {
                HideAll();
                return;
            }

            var em = world.EntityManager;
            if (!ToroidalDisplay.ResolveMapSize(em, out float mapW, out float mapH))
            {
                HideAll();
                return;
            }

            if (_settings == null)
                _settings = CannonLaserVfxSettings.LoadDefault();

            float now = Time.unscaledTime;
            _humFireHeld = false;
            float drawnHumRamp = 0f;
            bool hadDrawnRamp = false;
            float humHear = 0f;
            MarkAllStale();
            // Interval-gated proxy walk — same cache bullet tracers use to stop on hulls.
            BulletCosmeticHitQuery.TryRefresh();

            var bindings = MegaShipWeaponVisualBinding.Live;
            if (bindings != null)
            {
                for (int b = 0; b < bindings.Count; b++)
                    TickBinding(em, bindings[b], mapW, mapH, now, ref drawnHumRamp, ref hadDrawnRamp, ref humHear);
            }

            if (hadDrawnRamp)
                _humRampSeconds = drawnHumRamp;
            // Local Fire is the player's own ship — keep the hum if one snapshot
            // dropped the beam pose. Remote beams only contribute their distance.
            if (_humFireHeld)
                humHear = math.max(humHear, 1f);
            bool anyDrawn = HideStale();
            UpdateHum(anyDrawn || (_humFireHeld && AnyRecentLive(now)), _humRampSeconds, humHear);
        }

        void TickBinding(
            EntityManager em,
            MegaShipWeaponVisualBinding binding,
            float mapW,
            float mapH,
            float now,
            ref float drawnHumRamp,
            ref bool hadDrawnRamp,
            ref float humHear)
        {
            if (binding == null || !em.Exists(binding.ShipEntity))
                return;
            if (!em.HasComponent<MegaShipState>(binding.ShipEntity)
                || !em.GetComponentData<MegaShipState>(binding.ShipEntity).IsMega)
                return;
            if (!em.HasComponent<ShipState>(binding.ShipEntity)
                || em.GetComponentData<ShipState>(binding.ShipEntity).IsDead)
            {
                ResetRampsForShip(binding.ShipEntity.Index);
                return;
            }
            if (!em.HasBuffer<ShipWeaponMountElement>(binding.ShipEntity)
                || !em.HasBuffer<MegaShipGunnerSlotElement>(binding.ShipEntity))
                return;
            // Landed hulls cannot fire. Cut the beam the same frame Fire is still held.
            if (ShipMoonDockState.IsFullyLandedOnMoon(em, binding.ShipEntity))
            {
                ResetRampsForShip(binding.ShipEntity.Index);
                ClearStickyForShip(binding.ShipEntity.Index);
                return;
            }

            var mounts = em.GetBuffer<ShipWeaponMountElement>(binding.ShipEntity);
            var gunners = em.GetBuffer<MegaShipGunnerSlotElement>(binding.ShipEntity);
            ShipWeaponKind.RestoreMountKindsFromGhostedSlots(mounts, gunners);
            var shipState = em.GetComponentData<ShipState>(binding.ShipEntity);
            var megaState = em.GetComponentData<MegaShipState>(binding.ShipEntity);
            var team = shipState.Team;
            var hull = em.HasComponent<LocalTransform>(binding.ShipEntity)
                ? em.GetComponentData<LocalTransform>(binding.ShipEntity)
                : default;

            bool localOwner = em.HasComponent<LocalPlayerShipTag>(binding.ShipEntity);
            bool pulseOn = megaState.CannonLaserPulseOn;
            bool energyLockout = localOwner
                ? StepLocalEnergyLockout(in shipState, megaState.CannonLaserLockout, pulseOn)
                : !pulseOn
                  && (megaState.CannonLaserLockout
                      || CannonLaserMath.IsLaserPoolEmpty(shipState.CurrentEnergy));
            var localInput = localOwner && em.HasComponent<ShipInput>(binding.ShipEntity)
                ? em.GetComponentData<ShipInput>(binding.ShipEntity)
                : default;
            bool localFiring = localOwner && localInput.Fire.IsSet;
            bool localMouseAim = localFiring && localInput.Overdrive && !energyLockout;
            // Local Fire release / energy lockout must cut the beam and hum
            // immediately. Ghosted TargetDistance can stay > 0 for a snapshot.
            if (localOwner && !localFiring)
            {
                ClearStickyForShip(binding.ShipEntity.Index);
                return;
            }
            if (energyLockout)
            {
                ClearStickyForShip(binding.ShipEntity.Index);
                return;
            }
            if (localFiring)
                _humFireHeld = true;

            // [TITAN-ORBIT] Same mask the arsenal HUD paints. Overlay the click so a
            // mute hides the beam this frame, before the predicted ghost write lands.
            var arm = ShipWeaponArmState.Resolve(em, binding.ShipEntity);
            if (localOwner)
                arm = WeaponArmSelection.Overlay(in arm, mounts);

            int count = math.min(mounts.Length, gunners.Length);
            for (int m = 0; m < count; m++)
            {
                if (!ShipWeaponKind.IsCannonLaser(mounts[m], gunners, m))
                    continue;
                var key = new BeamKey
                {
                    ShipIndex = binding.ShipEntity.Index,
                    MountIndex = m,
                };
                // Silenced in the arsenal. Local Fire used to auto-lock every cannon
                // and draw a beam (plus a cosmetic damage float) anyway.
                if (!ShipWeaponArmState.IsArmed(in arm, m))
                {
                    HideMountImmediate(key);
                    continue;
                }
                bool tracking = MegaShipWeaponAim.IsTrackingAim(gunners[m])
                    && MegaShipWeaponVisualTargets.IsLiveLock(
                        em,
                        gunners[m].TargetGhostId,
                        gunners[m].AimWorldX,
                        gunners[m].AimWorldZ);
                bool sticky = !localMouseAim
                    && !tracking
                    && HasStickyLock(key);
                // Local Fire must try to pose this frame — waiting on ghosted
                // tracking hid the first auto-lock until a Shift mouse-aim.
                if (!localMouseAim && !tracking && !sticky && !localFiring)
                {
                    // Kill snapshot clears the lock in the same tick it counts the
                    // last slice. Report that slice before the beam (and its sticky
                    // aim) is dropped, or the streak freezes a snapshot early.
                    ReportReplicatedBurn(
                        em, binding.ShipEntity, team, mounts[m], gunners[m], key, Entity.Null, default,
                        mapW, mapH);
                    HideMountImmediate(key);
                    continue;
                }

                if (!TryResolveBeamEnds(
                        em, binding, hull, mounts[m], gunners[m], m, team, mapW, mapH,
                        localFiring && !localMouseAim, out Vector3 muzzle, out Vector3 end,
                        out Entity beamHit))
                {
                    ReportReplicatedBurn(
                        em, binding.ShipEntity, team, mounts[m], gunners[m], key, Entity.Null, default,
                        mapW, mapH);
                    HideMountImmediate(key);
                    continue;
                }

                if (!TryGetOrCreate(key, team, out BeamSlot slot) || slot.Root == null)
                    continue;

                RememberStickyLock(em, slot, gunners[m], beamHit, end);
                slot.LastUsed = now;
                slot.LastSeenLive = now;
                if (SilenceVendorAudio(slot.Root))
                    slot.NeedsSilence = false;
                // Local owner: the float uses this ramp, so a ship retarget prints
                // the thin number in the same frame the beam thins. Remotes use the
                // ghost seconds (Clamp, so a server reset to 0 snaps).
                float rampSeconds = ResolveBeamRampSeconds(
                    em, slot, localOwner, gunners[m], beamHit, localFiring && !energyLockout,
                    team, mapW, mapH);
                ReportReplicatedBurn(
                    em, binding.ShipEntity, team, mounts[m], gunners[m], key, beamHit, end, mapW, mapH,
                    localOwner ? rampSeconds : -1f);
                ApplyRampWidth(slot, rampSeconds);
                drawnHumRamp = math.max(drawnHumRamp, rampSeconds);
                hadDrawnRamp = true;
                humHear = math.max(humHear, GameplaySfxProximity.VolumeAt(muzzle));

                slot.Root.transform.position = muzzle;
                Vector3 toEnd = end - muzzle;
                toEnd.y = 0f;
                if (toEnd.sqrMagnitude < 1e-6f)
                    continue;
                slot.Root.transform.rotation = Quaternion.LookRotation(toEnd.normalized, Vector3.up);
                ApplyVendorLength(slot.Vendor, toEnd.magnitude);
                SetBeamShown(slot, true);
                ApplyVendorLinePose(slot, muzzle, end);
            }
        }

        /// <summary>
        /// Applies new server burn ticks to the floating damage streak.
        /// <paramref name="clippedHit"/> wins; otherwise the beam's last sticky
        /// aim point is used so a lock that died this snapshot still gets its slice.
        /// </summary>
        void ReportReplicatedBurn(
            EntityManager em,
            Entity shooter,
            TeamId ownerTeam,
            in ShipWeaponMountElement mount,
            in MegaShipGunnerSlotElement gunner,
            BeamKey key,
            Entity clippedHit,
            Vector3 impactDisplayPos,
            float mapW,
            float mapH,
            float rampOverride = -1f)
        {
            if (!_live.TryGetValue(key, out BeamSlot slot) || slot == null)
                return;
            if (!TryConsumeReplicatedBurnTicks(slot, gunner.CannonLaserBurnSequence, out uint burnTicks))
                return;

            if (clippedHit == Entity.Null
                && impactDisplayPos.sqrMagnitude < 1e-4f
                && (math.abs(slot.StickyAimX) > 0.01f || math.abs(slot.StickyAimZ) > 0.01f))
            {
                impactDisplayPos = new Vector3(slot.StickyAimX, 0f, slot.StickyAimZ);
            }

            TryNotifyBeamDamageFloat(
                em, shooter, ownerTeam, in mount, in gunner, impactDisplayPos, clippedHit, burnTicks,
                mapW, mapH, rampOverride);
        }

        /// <summary>
        /// Turns new server burn ticks into one floating-count slice.
        /// Lasers never send <c>BulletHitRpc</c>; the replicated sequence is the hook.
        /// Asteroids have no <c>GhostInstance</c> — prefer the clipped contact entity.
        /// </summary>
        static void TryNotifyBeamDamageFloat(
            EntityManager em,
            Entity shooter,
            TeamId ownerTeam,
            in ShipWeaponMountElement mount,
            in MegaShipGunnerSlotElement slot,
            Vector3 impactDisplayPos,
            Entity clippedHit,
            uint burnTicks,
            float mapW,
            float mapH,
            float rampOverride)
        {
            Entity target = clippedHit;
            if (target == Entity.Null && slot.TargetGhostId != 0)
                MegaShipWeaponVisualTargets.TryGetEntity(slot.TargetGhostId, out target);
            if (target == Entity.Null)
                BulletCosmeticHitQuery.TryFindAsteroidAtImpact(impactDisplayPos, out target);
            if (target == Entity.Null)
                return;

            // One server tick of ramped DPS per new sequence step. Top-killer +5%
            // matches CannonLaserCombatSystem so the total is not short of the pop.
            float tickDt = 1f / TitanOrbitServerTickRateSystem.SimulationHz;
            float rampSeconds = rampOverride >= 0f ? rampOverride : slot.CannonLaserRampSeconds;
            float slice = ResolveLaserSlice(in mount, rampSeconds) * tickDt * burnTicks;
            if (em.Exists(shooter) && em.HasComponent<GhostOwner>(shooter))
            {
                int net = em.GetComponentData<GhostOwner>(shooter).NetworkId;
                slice = TeamCommandRoleRules.ScaleFirePower(
                    slice, ShipTopOfTeamRoles.IsKiller(ownerTeam, net));
            }

            // Same target row the server applies in CannonLaserHitApply. Without this
            // the streak prints raw fire power while the rock or hull loses the bonus.
            slice = ScaleLaserSliceForTarget(
                em, in mount, in slot, target, ownerTeam, mapW, mapH, slice);

            if (slice <= 0.01f)
                return;

            EcsFloatingCountPresenter.TryNotifyLaserBeamSlice(
                shooter, target, slice, ownerTeam, impactDisplayPos);
        }

        /// <summary>
        /// New server slices since the last beam frame. The first snapshot of a
        /// fresh barrel counts a short lead-in; a large jump is a resync.
        /// </summary>
        static bool TryConsumeReplicatedBurnTicks(BeamSlot slot, uint sequence, out uint ticks)
        {
            ticks = 0;
            if (slot == null)
                return false;

            if (!slot.BurnSequenceReady)
            {
                slot.BurnSequenceReady = true;
                if (sequence == 0 || sequence > MaxBurnLeadInTicks)
                {
                    slot.LastBurnSequence = sequence;
                    return false;
                }

                slot.LastBurnSequence = sequence;
                ticks = sequence;
                return true;
            }

            uint delta = sequence - slot.LastBurnSequence;
            slot.LastBurnSequence = sequence;
            if (delta == 0 || delta > MaxBurnCatchUpTicks)
                return false;

            ticks = delta;
            return true;
        }

        /// <summary>
        /// Multiplies one laser float by this barrel's vs-asteroid / vs-ship / vs-moon
        /// row. Ships and drones share the ship row. A planet lock uses
        /// <see cref="CannonLaserHitApply.FocusMoon"/> for the moon row and the ship
        /// row for a turret pad. Mega lasers stamp 0 extra levels, matching the server.
        /// </summary>
        static float ScaleLaserSliceForTarget(
            EntityManager em,
            in ShipWeaponMountElement mount,
            in MegaShipGunnerSlotElement gunner,
            Entity target,
            TeamId ownerTeam,
            float mapW,
            float mapH,
            float slice)
        {
            if (slice <= 0.01f || target == Entity.Null || !em.Exists(target))
                return slice;

            // Asteroids and ships are the body itself. A planet lock is a moon
            // only when the aim is on the shield; a pad on that same planet is a ship.
            BulletBankDamageTarget kind = BulletBankDamageTarget.ShipOrDrone;
            if (em.HasComponent<AsteroidState>(target))
                kind = BulletBankDamageTarget.Asteroid;
            else if (em.HasComponent<ShipState>(target))
                kind = BulletBankDamageTarget.ShipOrDrone;
            else if (TryResolveRampFocus(
                         em, target, ownerTeam, gunner.AimWorldX, gunner.AimWorldZ, mapW, mapH, out int focus)
                     && focus == CannonLaserHitApply.FocusMoon)
                kind = BulletBankDamageTarget.GemMoon;

            int bank = BulletBankFireResolve.ResolveMegaMountFireBank(in mount, 0);
            if (!BulletBankCombatLogic.TryGetProfile(bank, out BulletBankProfile profile) || profile == null)
                return slice;
            return BulletBankCombatLogic.ResolveHitDamage(profile, kind, slice, 0);
        }

        /// <summary>
        /// Per-barrel DPS for the float. Mount stats are written when the MEGA chassis
        /// applies (client and server). If prediction cleared FirePower, use the catalog
        /// cannon type-table so the number does not go silent.
        /// </summary>
        static float ResolveLaserSlice(in ShipWeaponMountElement mount, float rampSeconds)
        {
            float power = mount.FirePower;
            float rate = mount.FireRate;
            if (power > 0.01f && rate > 0.01f)
                return CannonLaserMath.ComputeRampedDps(power, rate, rampSeconds);

            var catalog = MegaShipCatalog.Load();
            if (catalog == null)
                return CannonLaserMath.ComputeRampedDps(power, rate, rampSeconds);

            MegaShipPartStats stats = catalog.GetStatsForPartType(ShipFamilyPartTypes.WeaponCannon);
            if (power <= 0.01f)
                power = stats.firePower;
            if (rate <= 0.01f)
                rate = stats.fireRate;
            return CannonLaserMath.ComputeRampedDps(power, rate, rampSeconds);
        }

        static bool TryResolveBeamEnds(
            EntityManager em,
            MegaShipWeaponVisualBinding binding,
            in LocalTransform hull,
            in ShipWeaponMountElement mount,
            in MegaShipGunnerSlotElement slot,
            int mountIndex,
            TeamId team,
            float mapW,
            float mapH,
            bool allowLocalAutoLock,
            out Vector3 muzzle,
            out Vector3 end,
            out Entity clippedHit)
        {
            muzzle = Vector3.zero;
            end = Vector3.zero;
            clippedHit = Entity.Null;

            if (binding.Barrels != null
                && mountIndex >= 0
                && mountIndex < binding.Barrels.Length
                && binding.Barrels[mountIndex] != null)
            {
                muzzle = binding.Barrels[mountIndex].position;
            }
            else if (ShipWeaponPose.TryResolve(hull, mount, out float3 ecsMuzzle, out _))
            {
                muzzle = (Vector3)ecsMuzzle;
            }
            else
            {
                return false;
            }

            Vector3 rawEnd = muzzle;
            int ghostId = slot.TargetGhostId;
            float aimX = slot.AimWorldX;
            float aimZ = slot.AimWorldZ;
            bool haveLiveAim = MegaShipWeaponVisualTargets.IsLiveLock(em, ghostId, aimX, aimZ);
            // Sticky is only for one-tick ghost drops. Interpolated AimWorld after
            // a kill is not a lock — if the last point is dead, search again or hide.
            if (!haveLiveAim
                && TryGetStickyFallback(binding, mountIndex, out int stickyGhost, out float stickyX, out float stickyZ)
                && MegaShipWeaponVisualTargets.IsLiveLock(em, stickyGhost, stickyX, stickyZ))
            {
                ghostId = stickyGhost;
                aimX = stickyX;
                aimZ = stickyZ;
                haveLiveAim = true;
            }
            else if (!haveLiveAim)
            {
                ClearStickyMount(binding, mountIndex);
            }

            bool committedPlanet = false;
            Entity committedPlanetEntity = Entity.Null;
            if (!TryGetLocalMouseBeamEnd(em, binding, in hull, in mount, muzzle, mapW, mapH, out rawEnd))
            {
                double beamMoonElapsed = 0d;
                PlanetGemMoonOrbitClock.TryGetElapsedSeconds(
                    out beamMoonElapsed, includeTickFraction: true);
                if (TryCommittedPlanetEnd(
                        em, binding, mountIndex, team, muzzle, ghostId, aimX, aimZ, mapW, mapH,
                        out rawEnd, out committedPlanetEntity))
                {
                    committedPlanet = true;
                }
                else if (ghostId != 0
                    && MegaShipWeaponVisualTargets.TryGetEntity(ghostId, out Entity target)
                    && MegaShipWeaponVisualTargets.IsLiveVisualTarget(em, target)
                    && CannonLaserSurface.TryGetHitPoint(
                        em, target, (float3)muzzle, mapW, mapH, beamMoonElapsed, out float3 surface))
                {
                    rawEnd = (Vector3)surface;
                }
                else if (ghostId != 0
                    && MegaShipWeaponVisualTargets.TryGetDisplayPos(ghostId, muzzle, out Vector3 lockPos)
                    && (!MegaShipWeaponVisualTargets.TryGetEntity(ghostId, out Entity ghostEnt)
                        || MegaShipWeaponVisualTargets.IsLiveVisualTarget(em, ghostEnt)))
                {
                    rawEnd = lockPos;
                }
                else if (haveLiveAim
                    && MegaShipWeaponVisualTargets.TryGetTiledPoint(
                        aimX, aimZ, muzzle, out rawEnd))
                {
                    // Live AimWorld (asteroid / pad) still occupied this frame.
                }
                else if (allowLocalAutoLock
                    && TryFindLocalAutoLock(
                        em, binding, team, in mount, muzzle, mapW, mapH, out rawEnd))
                {
                    // First auto-fire frame, or retarget after the last rock popped.
                }
                else
                {
                    return false;
                }
            }

            rawEnd.y = muzzle.y;
            if (ToroidalMapEcs.IsValidMapSize(mapW, mapH))
            {
                float3 tiled = ToroidalMapEcs.GetDisplayPosition(
                    (float3)rawEnd, (float3)muzzle, mapW, mapH);
                rawEnd = new Vector3(tiled.x, muzzle.y, tiled.z);
            }

            // Mouse aim used to keep the cursor length even when the ray already
            // punched a hull / rock / pad. Clip to the first contact on this segment.
            Vector3 intendedEnd = rawEnd;
            TryClipBeamToFirstCollider(
                em, binding, team, muzzle, ref rawEnd, out clippedHit, out var clipKind);
            // A pad lock must not end on the moon, and a moon lock must not end on a pad.
            if (committedPlanet
                && clippedHit == committedPlanetEntity
                && (clipKind == BulletCosmeticHitQuery.ObstacleKind.Moon
                    || clipKind == BulletCosmeticHitQuery.ObstacleKind.PlanetaryDefense
                    || clipKind == BulletCosmeticHitQuery.ObstacleKind.Planet))
            {
                rawEnd = intendedEnd;
                clippedHit = committedPlanetEntity;
            }
            // Parked Titans clip the new lock through the last rock's leftover
            // collider (DestroyRpc / optimistic-0 lag). Keep the intended end so
            // the beam can leave the corpse without the hull moving.
            else if (clippedHit != Entity.Null
                && !MegaShipWeaponVisualTargets.IsLiveVisualTarget(em, clippedHit))
            {
                rawEnd = intendedEnd;
                clippedHit = Entity.Null;
            }

            end = rawEnd;
            return Vector3.Distance(muzzle, end) > 0.05f;
        }

        /// <summary>
        /// Shortens <paramref name="end"/> to the nearest cosmetic collider on
        /// muzzle → end. Skips the shooter's own hull. Heal banks still stop on allies.
        /// Returns the hybrid-proxy entity when the clip landed (asteroid / ship).
        /// </summary>
        static bool TryClipBeamToFirstCollider(
            EntityManager em,
            MegaShipWeaponVisualBinding binding,
            TeamId team,
            Vector3 muzzle,
            ref Vector3 end,
            out Entity hitEntity,
            out BulletCosmeticHitQuery.ObstacleKind hitKind)
        {
            hitEntity = Entity.Null;
            hitKind = BulletCosmeticHitQuery.ObstacleKind.Asteroid;
            if (binding == null || !em.Exists(binding.ShipEntity))
                return false;

            int ownerNet = 0;
            if (em.HasComponent<GhostOwner>(binding.ShipEntity))
                ownerNet = em.GetComponentData<GhostOwner>(binding.ShipEntity).NetworkId;

            int bankIndex = 0;
            if (em.HasComponent<ShipLoadoutState>(binding.ShipEntity))
            {
                bankIndex = BulletBankFireResolve.ResolveFireBankIndex(
                    em.GetComponentData<ShipLoadoutState>(binding.ShipEntity));
            }

            if (!BulletCosmeticHitQuery.TryHitSegment(
                    (float3)muzzle,
                    (float3)end,
                    (byte)team,
                    ownerNet,
                    isDisplaySpace: true,
                    out float3 hit,
                    out hitKind,
                    out hitEntity,
                    out _,
                    out _,
                    damageFilter: 0,
                    scaleMultiplier: 1f,
                    bankIndex: bankIndex))
                return false;

            end = new Vector3(hit.x, muzzle.y, hit.z);
            return hitEntity != Entity.Null;
        }

        /// <summary>
        /// Hide at empty before the lockout ghost arrives. The server already
        /// waits for &gt;10% before clearing <see cref="MegaShipState.CannonLaserLockout"/>;
        /// do not also require that ratio here — after unlock the ghost pool
        /// is already draining and would never look ready again.
        /// </summary>
        bool StepLocalEnergyLockout(in ShipState ship, bool serverLockout, bool pulseOn)
        {
            if (pulseOn)
            {
                _localEnergyLockout = false;
                return false;
            }

            if (serverLockout || CannonLaserMath.IsLaserPoolEmpty(ship.CurrentEnergy))
                _localEnergyLockout = true;
            else
                _localEnergyLockout = false;
            return _localEnergyLockout;
        }

        /// <summary>
        /// Local Shift mouse-aim: beam follows the cursor (clamped to cannon range)
        /// so it matches the projectile guns without waiting on a ghost snapshot.
        /// </summary>
        static bool TryGetLocalMouseBeamEnd(
            EntityManager em,
            MegaShipWeaponVisualBinding binding,
            in LocalTransform hull,
            in ShipWeaponMountElement mount,
            Vector3 muzzle,
            float mapW,
            float mapH,
            out Vector3 end)
        {
            end = muzzle;
            if (binding == null
                || !em.HasComponent<LocalPlayerShipTag>(binding.ShipEntity)
                || !em.HasComponent<ShipInput>(binding.ShipEntity))
                return false;

            var input = em.GetComponentData<ShipInput>(binding.ShipEntity);
            if (!input.Overdrive || !input.Fire.IsSet)
                return false;
            if (!MegaShipWeaponAim.TryGetOwnerMouseAimPoint(in hull, in input, out float3 mouse))
                return false;
            if (!MegaShipWeaponVisualTargets.TryGetTiledPoint(mouse.x, mouse.z, muzzle, out end))
                return false;

            Vector3 toEnd = end - muzzle;
            toEnd.y = 0f;
            float dist = toEnd.magnitude;
            if (dist < 0.05f)
                return false;

            float range = mount.BulletRange > 0.5f
                ? mount.BulletRange
                : MegaShipCatalog.DefaultCannonAcquireRange;
            if (dist > range)
                end = muzzle + toEnd / dist * range;
            return true;
        }

        void ClearStickyForShip(int shipIndex)
        {
            foreach (var kv in _live)
            {
                if (kv.Key.ShipIndex != shipIndex)
                    continue;
                kv.Value.StickyGhostId = 0;
                kv.Value.StickyAimX = 0f;
                kv.Value.StickyAimZ = 0f;
                kv.Value.LastSeenLive = 0f;
            }
        }

        bool HasStickyLock(BeamKey key)
        {
            if (!_live.TryGetValue(key, out BeamSlot slot) || slot == null)
                return false;
            if (slot.LastSeenLive <= 0f || Time.unscaledTime - slot.LastSeenLive > HoldSeconds)
                return false;
            return slot.StickyGhostId != 0
                   || math.abs(slot.StickyAimX) > 0.01f
                   || math.abs(slot.StickyAimZ) > 0.01f;
        }

        static void RememberStickyLock(
            EntityManager em,
            BeamSlot slot,
            in MegaShipGunnerSlotElement gunner,
            Entity beamHit,
            Vector3 end)
        {
            if (slot == null)
                return;

            if (MegaShipWeaponVisualTargets.IsLiveLock(
                    em, gunner.TargetGhostId, gunner.AimWorldX, gunner.AimWorldZ))
            {
                slot.StickyGhostId = gunner.TargetGhostId;
                slot.StickyAimX = gunner.AimWorldX;
                slot.StickyAimZ = gunner.AimWorldZ;
                return;
            }

            if (beamHit != Entity.Null
                && MegaShipWeaponVisualTargets.IsLiveVisualTarget(em, beamHit))
            {
                slot.StickyGhostId = 0;
                slot.StickyAimX = end.x;
                slot.StickyAimZ = end.z;
                return;
            }

            slot.StickyGhostId = 0;
            slot.StickyAimX = 0f;
            slot.StickyAimZ = 0f;
        }

        static void ClearStickyMount(MegaShipWeaponVisualBinding binding, int mountIndex)
        {
            if (_instance == null || binding == null)
                return;
            var key = new BeamKey
            {
                ShipIndex = binding.ShipEntity.Index,
                MountIndex = mountIndex,
            };
            _instance.HideMountImmediate(key);
        }

        void HideMountImmediate(BeamKey key)
        {
            if (!_live.TryGetValue(key, out BeamSlot slot) || slot == null)
                return;
            slot.StickyGhostId = 0;
            slot.StickyAimX = 0f;
            slot.StickyAimZ = 0f;
            slot.LastSeenLive = 0f;
            slot.LastUsed = -1f;
            SetBeamShown(slot, false);
        }

        /// <summary>
        /// Local owner predicts per-barrel charge. Restarts at 50% when the burn
        /// subject changes: a new lock entity, another pad / moon on the same
        /// planet, or the next body after the lock dies. A clipped rock in front
        /// of a still-live lock is not a new target. Remotes read the ghosted
        /// slot seconds (Clamp, so a server reset to 0 snaps).
        /// </summary>
        static float ResolveBeamRampSeconds(
            EntityManager em,
            BeamSlot slot,
            bool localOwner,
            in MegaShipGunnerSlotElement gunner,
            Entity beamHit,
            bool charging,
            TeamId team,
            float mapW,
            float mapH)
        {
            if (!localOwner)
                return math.max(0f, gunner.CannonLaserRampSeconds);

            bool gunnerLive = MegaShipWeaponVisualTargets.IsLiveLock(
                em, gunner.TargetGhostId, gunner.AimWorldX, gunner.AimWorldZ);
            bool resolved = MegaShipWeaponVisualTargets.TryGetLockEntity(
                    em, gunner.TargetGhostId, gunner.AimWorldX, gunner.AimWorldZ, out Entity lockEnt)
                && lockEnt != Entity.Null
                && MegaShipWeaponVisualTargets.IsLiveVisualTarget(em, lockEnt);

            // Committed subject. Prefer the live gunner lock. Once that lock is
            // gone, the body the beam is actually on is the new target — waiting
            // on a stale AimWorld left a maxed ramp stuck after every kill.
            Entity subject = Entity.Null;
            int focus = CannonLaserHitApply.FocusWholeEntity;
            bool haveFocus = false;
            if (resolved && gunnerLive)
                subject = lockEnt;
            else if (!gunnerLive
                     && beamHit != Entity.Null
                     && MegaShipWeaponVisualTargets.IsLiveVisualTarget(em, beamHit))
                subject = beamHit;

            if (subject != Entity.Null)
                haveFocus = TryResolveRampFocus(
                    em, subject, team, gunner.AimWorldX, gunner.AimWorldZ, mapW, mapH, out focus);

            int previousGhost = slot.RampGhostId;
            Entity previousTarget = slot.RampTarget;
            bool switchedShip = previousGhost != 0
                                && gunner.TargetGhostId != 0
                                && gunner.TargetGhostId != previousGhost;
            // Moon and pad locks often ghost id 0. Clearing the stored ship id
            // here is what lets the next ship start thin instead of staying fat.
            bool leftNumberedLock = previousGhost != 0
                                    && gunner.TargetGhostId == 0
                                    && (subject == Entity.Null || subject != previousTarget);
            bool reset = switchedShip || leftNumberedLock;
            if (subject != Entity.Null)
            {
                bool newEntity = previousTarget != Entity.Null && previousTarget != subject;
                bool newFocus = haveFocus
                    && slot.RampTarget == subject
                    && slot.RampFocus != int.MinValue
                    && slot.RampFocus != focus;
                // Lookup never named a body for the whole climb. The first body
                // we can name at the cap is a new target, not a late aim on the
                // one we already charged (that resolve lands before the cap).
                bool cappedWithoutSubject = previousTarget == Entity.Null
                    && slot.LocalRampSeconds >= CannonLaserMath.RampDurationSeconds - 0.001f;
                if (newEntity || newFocus || cappedWithoutSubject)
                    reset = true;
                slot.RampTarget = subject;
                if (haveFocus)
                    slot.RampFocus = focus;
                else if (newEntity || cappedWithoutSubject || reset)
                    slot.RampFocus = int.MinValue;
            }
            else if (reset)
            {
                slot.RampTarget = Entity.Null;
                slot.RampFocus = int.MinValue;
            }

            slot.RampGhostId = gunner.TargetGhostId;

            bool haveLock = subject != Entity.Null
                            || (gunnerLive && lockEnt != Entity.Null)
                            || beamHit != Entity.Null
                            || MegaShipWeaponAim.IsTrackingAim(in gunner);
            float shown = reset ? 0f : slot.LocalRampSeconds;
            slot.LocalRampSeconds = CannonLaserMath.StepRampSeconds(
                shown,
                Time.deltaTime,
                reset: reset,
                charging: charging && subject != Entity.Null && !reset);
            return shown;
        }

        /// <summary>
        /// Beam end for a planet lock. Moon stops on the shield along the barrel
        /// ray. A pad stops on that pad. A closer neighbor does not steal the end.
        /// </summary>
        static bool TryCommittedPlanetEnd(
            EntityManager em,
            MegaShipWeaponVisualBinding binding,
            int mountIndex,
            TeamId team,
            Vector3 muzzle,
            int ghostId,
            float aimX,
            float aimZ,
            float mapW,
            float mapH,
            out Vector3 end,
            out Entity planet)
        {
            end = muzzle;
            planet = Entity.Null;
            if (!MegaShipWeaponVisualTargets.TryGetLockEntity(em, ghostId, aimX, aimZ, out Entity lockEnt)
                || lockEnt == Entity.Null
                || !em.Exists(lockEnt)
                || !em.HasComponent<PlanetState>(lockEnt))
                return false;

            planet = lockEnt;
            double moonElapsed = 0d;
            PlanetGemMoonOrbitClock.TryGetElapsedSeconds(out moonElapsed, includeTickFraction: true);
            float3 from = (float3)muzzle;
            float3 hint = new float3(aimX, 0f, aimZ);
            int focus = CannonLaserHitApply.ResolvePlanetFocus(
                em, planet, team, hint, mapW, mapH, moonElapsed);
            CannonLaserHitApply.PlanetSubjectAim subject;
            if (focus == CannonLaserHitApply.FocusWholeEntity)
            {
                if (!CannonLaserHitApply.TryPickClosestSubject(
                        em, planet, team, from, float.MaxValue, mapW, mapH, moonElapsed, out subject))
                    return false;
                focus = subject.Focus;
            }
            else if (!CannonLaserHitApply.TryGetSubjectAim(
                         em, planet, team, from, focus, mapW, mapH, moonElapsed, out subject))
            {
                return false;
            }

            float3 point = subject.Aim;
            if (focus == CannonLaserHitApply.FocusMoon
                && subject.Radius > 0.05f
                && binding != null
                && binding.Barrels != null
                && mountIndex >= 0
                && mountIndex < binding.Barrels.Length
                && binding.Barrels[mountIndex] != null)
            {
                float3 barrelFwd = (float3)binding.Barrels[mountIndex].forward;
                if (CannonLaserMath.TryRaySphereSurface(
                        from, barrelFwd, subject.Center, subject.Radius, mapW, mapH, out float3 rayHit))
                {
                    point = rayHit;
                }
                else
                {
                    barrelFwd.y = 0f;
                    if (math.lengthsq(barrelFwd) > 0.0001f)
                    {
                        barrelFwd = math.normalize(barrelFwd);
                        point = from + barrelFwd * math.max(0.25f, subject.Distance);
                    }
                }
            }

            end = (Vector3)point;
            return true;
        }

        /// <summary>
        /// Pad slot + 1 or moon focus for a planet lock. Ships and asteroids are
        /// the whole entity (<see cref="CannonLaserHitApply.FocusWholeEntity"/>).
        /// </summary>
        static bool TryResolveRampFocus(
            EntityManager em,
            Entity subject,
            TeamId team,
            float aimX,
            float aimZ,
            float mapW,
            float mapH,
            out int focus)
        {
            focus = CannonLaserHitApply.FocusWholeEntity;
            if (subject == Entity.Null || !em.HasComponent<PlanetState>(subject))
                return true;
            if (math.abs(aimX) + math.abs(aimZ) <= 0.05f)
                return false;

            double moonElapsed = 0d;
            PlanetGemMoonOrbitClock.TryGetElapsedSeconds(out moonElapsed, includeTickFraction: true);
            focus = CannonLaserHitApply.ResolvePlanetFocus(
                em, subject, team, new float3(aimX, 0f, aimZ), mapW, mapH, moonElapsed);
            return focus != CannonLaserHitApply.FocusWholeEntity;
        }

        void ResetRampsForShip(int shipIndex)
        {
            foreach (var kv in _live)
            {
                if (kv.Key.ShipIndex != shipIndex)
                    continue;
                ResetSlotRamp(kv.Value);
            }
        }

        static void ResetSlotRamp(BeamSlot slot)
        {
            if (slot == null)
                return;
            slot.LocalRampSeconds = 0f;
            slot.RampTarget = Entity.Null;
            slot.RampGhostId = 0;
            slot.RampFocus = int.MinValue;
            slot.BurnSequenceReady = false;
            slot.LastBurnSequence = 0;
        }

        static bool TryFindLocalAutoLock(
            EntityManager em,
            MegaShipWeaponVisualBinding binding,
            TeamId team,
            in ShipWeaponMountElement mount,
            Vector3 muzzle,
            float mapW,
            float mapH,
            out Vector3 end)
        {
            end = muzzle;
            if (binding == null || !em.Exists(binding.ShipEntity))
                return false;

            bool heal = em.HasComponent<ShipLoadoutState>(binding.ShipEntity)
                        && em.GetComponentData<ShipLoadoutState>(binding.ShipEntity)
                            .HealingBulletsActive;
            float range = mount.BulletRange > 0.5f
                ? mount.BulletRange
                : MegaShipCatalog.DefaultCannonAcquireRange;
            var obstacles = BulletCosmeticHitQuery.CurrentObstacles;
            if (obstacles == null || obstacles.Count == 0)
                return false;

            float best = range;
            bool found = false;
            Vector3 bestEnd = muzzle;
            for (int i = 0; i < obstacles.Count; i++)
            {
                var o = obstacles[i];
                if (o.SourceEntity == binding.ShipEntity)
                    continue;
                if (!ObstacleMatchesAutoLock(em, o, team, heal))
                    continue;

                float3 logical = o.LogicalCenter;
                if (o.Kind == BulletCosmeticHitQuery.ObstacleKind.Moon)
                {
                    double moonElapsed = 0d;
                    PlanetGemMoonOrbitClock.TryGetElapsedSeconds(
                        out moonElapsed, includeTickFraction: true);
                    if (!PlanetGemMoonCombatLogic.TryGetNonFriendlyMoonAim(
                            (TeamId)o.TeamOrOwnership,
                            team,
                            o.LogicalCenter,
                            o.Scale,
                            o.PlanetLevel,
                            o.PlanetId,
                            o.IsHomePlanet,
                            o.CurrentShield,
                            (float3)muzzle,
                            mapW,
                            mapH,
                            moonElapsed,
                            out logical,
                            out _))
                        continue;
                }

                float d = ToroidalMapEcs.ToroidalDistance((float3)muzzle, logical, mapW, mapH);
                if (d >= best)
                    continue;
                if (!MegaShipWeaponVisualTargets.TryGetTiledPoint(logical.x, logical.z, muzzle, out Vector3 tiled))
                    continue;
                best = d;
                bestEnd = tiled;
                found = true;
            }

            if (!found)
                return false;
            end = bestEnd;
            return true;
        }

        static bool ObstacleMatchesAutoLock(
            EntityManager em,
            in BulletCosmeticHitQuery.Obstacle o,
            TeamId team,
            bool heal)
        {
            switch (o.Kind)
            {
                case BulletCosmeticHitQuery.ObstacleKind.Ship:
                    if (o.SourceEntity != Entity.Null && em.Exists(o.SourceEntity)
                        && em.HasComponent<ShipState>(o.SourceEntity)
                        && em.GetComponentData<ShipState>(o.SourceEntity).IsDead)
                        return false;
                    return heal
                        ? (TeamId)o.TeamOrOwnership == team
                        : (TeamId)o.TeamOrOwnership != team
                          && (TeamId)o.TeamOrOwnership != TeamId.None;
                case BulletCosmeticHitQuery.ObstacleKind.Asteroid:
                    if (heal)
                        return false;
                    return o.SourceEntity != Entity.Null
                           && MegaShipWeaponVisualTargets.IsLiveVisualTarget(em, o.SourceEntity);
                case BulletCosmeticHitQuery.ObstacleKind.PlanetaryDefense:
                    if (heal)
                        return false;
                    return (TeamId)o.TeamOrOwnership != team
                           && (TeamId)o.TeamOrOwnership != TeamId.None;
                case BulletCosmeticHitQuery.ObstacleKind.Moon:
                {
                    if (heal)
                        return false;
                    var moonOwner = (TeamId)o.TeamOrOwnership;
                    return moonOwner != TeamId.None
                           && !PlanetGemMoonCombatLogic.IsTeamFriendlyToMoon(moonOwner, team);
                }
                default:
                    return false;
            }
        }

        static bool TryGetStickyFallback(
            MegaShipWeaponVisualBinding binding,
            int mountIndex,
            out int ghostId,
            out float aimX,
            out float aimZ)
        {
            ghostId = 0;
            aimX = 0f;
            aimZ = 0f;
            if (_instance == null || binding == null)
                return false;

            var key = new BeamKey
            {
                ShipIndex = binding.ShipEntity.Index,
                MountIndex = mountIndex,
            };
            if (!_instance._live.TryGetValue(key, out BeamSlot slot) || slot == null)
                return false;
            if (slot.StickyGhostId == 0
                && math.abs(slot.StickyAimX) <= 0.01f
                && math.abs(slot.StickyAimZ) <= 0.01f)
                return false;

            ghostId = slot.StickyGhostId;
            aimX = slot.StickyAimX;
            aimZ = slot.StickyAimZ;
            return true;
        }

        bool AnyRecentLive(float now)
        {
            foreach (var kv in _live)
            {
                if (kv.Value.LastSeenLive > 0f && now - kv.Value.LastSeenLive <= HoldSeconds)
                    return true;
            }

            return false;
        }

        bool TryGetOrCreate(BeamKey key, TeamId team, out BeamSlot slot)
        {
            if (_live.TryGetValue(key, out slot) && slot.Root != null)
            {
                if (slot.Team != team)
                {
                    Destroy(slot.Root);
                    _live.Remove(key);
                    slot = null;
                }
                else
                    return true;
            }

            if (_settings == null)
            {
                slot = null;
                return false;
            }

            GameObject prefab = _settings.GetPrefabForTeam(team);
            if (prefab == null)
            {
                slot = null;
                return false;
            }

            var root = Instantiate(prefab, transform);
            root.name = "CannonLaser_" + key.ShipIndex + "_" + key.MountIndex;
            var vendor = FindVendorBeam(root);
            ApplyVendorLength(vendor, 1f);
            SilenceVendorAudio(root);

            slot = new BeamSlot
            {
                Root = root,
                Vendor = vendor,
                Line = root.GetComponentInChildren<LineRenderer>(true),
                Team = team,
                LastUsed = Time.unscaledTime,
                Shown = false,
                NeedsSilence = true,
            };
            _live[key] = slot;
            return true;
        }

        static MonoBehaviour FindVendorBeam(GameObject root)
        {
            if (root == null)
                return null;

            var behaviours = root.GetComponents<MonoBehaviour>();
            for (int i = 0; i < behaviours.Length; i++)
            {
                MonoBehaviour behaviour = behaviours[i];
                if (behaviour == null)
                    continue;
                if (behaviour.GetType().Name != "SciFiArsenalBeamStatic")
                    continue;
                CacheVendorFields(behaviour.GetType());
                return behaviour;
            }

            return null;
        }

        static void CacheVendorFields(System.Type vendorType)
        {
            if (s_BeamLengthField != null)
                return;
            const BindingFlags bind = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            s_BeamCollidesField = vendorType.GetField("beamCollides", bind);
            s_BeamLengthField = vendorType.GetField("beamLength", bind);
            s_OriginalWidthField = vendorType.GetField("originalWidth", bind);
            s_CustomWidthField = vendorType.GetField("customWidth", bind);
            s_WidthMultiplierField = vendorType.GetField("widthMultiplier", bind);
            s_BeamStartField = vendorType.GetField("beamStart", bind);
            s_BeamEndField = vendorType.GetField("beamEnd", bind);
        }

        /// <summary>
        /// BeamLaserStart is spawned in vendor Start() after our first Instantiate,
        /// with PlayOnAwake+Loop. Mute those sources once they exist. True when
        /// at least one source was found (so we can stop retrying).
        /// </summary>
        bool SilenceVendorAudio(GameObject root)
        {
            if (root == null)
                return false;

            var sources = root.GetComponentsInChildren<AudioSource>(true);
            bool found = false;
            for (int i = 0; i < sources.Length; i++)
            {
                AudioSource source = sources[i];
                if (source == null)
                    continue;
                found = true;
                if (_humClip == null && source.clip != null)
                    _humClip = source.clip;
                source.playOnAwake = false;
                source.loop = false;
                source.Stop();
                source.enabled = false;
            }

            return found;
        }

        void UpdateHum(bool anyLive, float rampSeconds = 0f, float hear = 0f)
        {
            if (_hum == null)
            {
                _hum = gameObject.AddComponent<AudioSource>();
                _hum.playOnAwake = false;
                _hum.loop = true;
                _hum.spatialBlend = 0f;
                _hum.volume = 0.35f;
            }

            if (_hum.clip == null && _humClip != null)
                _hum.clip = _humClip;

            _hum.pitch = ComputeHumPitch(rampSeconds);
            _hum.volume = 0.35f * math.saturate(hear);

            // One shared hum for every live beam. Only beams near the local ship
            // contribute; a MEGA firing across the map does not fill the mix.
            if (anyLive && hear > 0.001f && _hum.clip != null)
            {
                if (!_hum.isPlaying)
                    _hum.Play();
            }
            else if (_hum.isPlaying)
            {
                _hum.Stop();
            }
        }

        /// <summary>
        /// Maps the 50%→300% DPS ramp onto hum pitch (low → higher, still a hum).
        /// </summary>
        static float ComputeHumPitch(float rampSeconds)
        {
            float mul = CannonLaserMath.ComputeRampMultiplier(rampSeconds);
            float span = CannonLaserMath.RampDamageMax - CannonLaserMath.RampDamageMin;
            float t = span > 0.0001f
                ? (mul - CannonLaserMath.RampDamageMin) / span
                : 0f;
            return math.lerp(HumPitchAtMinDps, HumPitchAtMaxDps, math.saturate(t));
        }

        static void ApplyVendorLength(MonoBehaviour vendor, float length)
        {
            if (vendor == null)
                return;
            if (s_BeamLengthField == null)
                CacheVendorFields(vendor.GetType());
            s_BeamCollidesField?.SetValue(vendor, false);
            s_BeamLengthField?.SetValue(vendor, math.max(0.05f, length));
        }

        /// <summary>
        /// Writes the world-space line and muzzle/impact FX for this frame.
        /// Vendor <c>LateUpdate</c> also does this; we snap here so
        /// <see cref="SyncAfterHullProxies"/> (including <c>onBeforeRender</c>)
        /// is not left one physics tick behind the hull.
        /// </summary>
        static void ApplyVendorLinePose(BeamSlot slot, Vector3 muzzle, Vector3 end)
        {
            if (slot == null)
                return;

            if (slot.Line == null && slot.Root != null)
                slot.Line = slot.Root.GetComponentInChildren<LineRenderer>(true);

            LineRenderer line = slot.Line;
            if (line != null)
            {
                line.useWorldSpace = true;
                if (line.positionCount < 2)
                    line.positionCount = 2;
                line.SetPosition(0, muzzle);
                line.SetPosition(1, end);
            }

            if (slot.Vendor == null)
                return;
            if (s_BeamStartField == null)
                CacheVendorFields(slot.Vendor.GetType());

            var startGo = s_BeamStartField != null
                ? s_BeamStartField.GetValue(slot.Vendor) as GameObject
                : null;
            var endGo = s_BeamEndField != null
                ? s_BeamEndField.GetValue(slot.Vendor) as GameObject
                : null;
            if (startGo != null)
            {
                startGo.transform.position = muzzle;
                Vector3 look = end - muzzle;
                look.y = 0f;
                if (look.sqrMagnitude > 1e-8f)
                    startGo.transform.rotation = Quaternion.LookRotation(look, Vector3.up);
            }

            if (endGo != null)
            {
                endGo.transform.position = end;
                Vector3 look = muzzle - end;
                look.y = 0f;
                if (look.sqrMagnitude > 1e-8f)
                    endGo.transform.rotation = Quaternion.LookRotation(look, Vector3.up);
            }
        }

        /// <summary>
        /// Scales the vendor pulse width and muzzle/impact FX with the DPS ramp.
        /// Must run after <c>SciFiArsenalBeamStatic.Start</c> so <c>originalWidth</c>
        /// is set. Captures the stock width once, then writes every frame.
        /// </summary>
        static void ApplyRampWidth(BeamSlot slot, float rampSeconds)
        {
            if (slot == null || slot.Vendor == null)
                return;
            if (s_OriginalWidthField == null)
                CacheVendorFields(slot.Vendor.GetType());
            if (s_OriginalWidthField == null)
                return;

            if (!slot.WidthReady)
            {
                float original = (float)s_OriginalWidthField.GetValue(slot.Vendor);
                if (original <= 0.0001f)
                    return;

                slot.VendorBaseWidth = original;
                var startGo = s_BeamStartField != null
                    ? s_BeamStartField.GetValue(slot.Vendor) as GameObject
                    : null;
                var endGo = s_BeamEndField != null
                    ? s_BeamEndField.GetValue(slot.Vendor) as GameObject
                    : null;
                slot.StartFxBaseScale = startGo != null ? startGo.transform.localScale : Vector3.one;
                slot.EndFxBaseScale = endGo != null ? endGo.transform.localScale : Vector3.one;
                slot.WidthReady = true;
            }

            float widthScale = VisualScale * CannonLaserMath.ComputeRampMultiplier(rampSeconds);
            float width = slot.VendorBaseWidth * widthScale;
            s_OriginalWidthField.SetValue(slot.Vendor, width);
            if (s_CustomWidthField != null)
            {
                float pulseMul = 1.5f;
                if (s_WidthMultiplierField != null)
                    pulseMul = (float)s_WidthMultiplierField.GetValue(slot.Vendor);
                s_CustomWidthField.SetValue(slot.Vendor, width * math.max(1f, pulseMul));
            }

            var startFx = s_BeamStartField != null
                ? s_BeamStartField.GetValue(slot.Vendor) as GameObject
                : null;
            var endFx = s_BeamEndField != null
                ? s_BeamEndField.GetValue(slot.Vendor) as GameObject
                : null;
            if (startFx != null)
                startFx.transform.localScale = slot.StartFxBaseScale * widthScale;
            if (endFx != null)
                endFx.transform.localScale = slot.EndFxBaseScale * widthScale;
        }

        void MarkAllStale()
        {
            foreach (var kv in _live)
                kv.Value.LastUsed = -1f;
        }

        /// <summary>
        /// Hides unused pooled beams without SetActive (vendor PlayOnAwake retriggers).
        /// Returns true only when a beam was actually drawn this frame — the hold
        /// window keeps the line, not the hum.
        /// </summary>
        bool HideStale()
        {
            bool anyDrawn = false;
            float now = Time.unscaledTime;
            _stale.Clear();
            foreach (var kv in _live)
            {
                BeamSlot slot = kv.Value;
                bool drawnThisFrame = slot.LastUsed >= 0f;
                bool holdVisual = !drawnThisFrame
                    && slot.LastSeenLive > 0f
                    && now - slot.LastSeenLive <= HoldSeconds;
                if (drawnThisFrame || holdVisual)
                {
                    if (drawnThisFrame)
                        anyDrawn = true;
                    if (slot.NeedsSilence && SilenceVendorAudio(slot.Root))
                        slot.NeedsSilence = false;
                    continue;
                }

                slot.StickyGhostId = 0;
                slot.StickyAimX = 0f;
                slot.StickyAimZ = 0f;
                slot.LastSeenLive = 0f;
                SetBeamShown(slot, false);
                _stale.Add(kv.Key);
            }

            return anyDrawn;
        }

        /// <summary>
        /// Show or hide the vendor line and particles. The LaserStatic root stays
        /// active so <c>BeamLaserStart</c> does not PlayOnAwake again.
        /// </summary>
        static void SetBeamShown(BeamSlot slot, bool shown)
        {
            if (slot == null || slot.Root == null || slot.Shown == shown)
                return;

            slot.Shown = shown;
            if (!shown)
            {
                var sources = slot.Root.GetComponentsInChildren<AudioSource>(true);
                for (int i = 0; i < sources.Length; i++)
                {
                    AudioSource source = sources[i];
                    if (source == null)
                        continue;
                    source.Stop();
                    source.enabled = false;
                }
            }

            var renderers = slot.Root.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] != null)
                    renderers[i].enabled = shown;
            }

            var lines = slot.Root.GetComponentsInChildren<LineRenderer>(true);
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i] != null)
                    lines[i].enabled = shown;
            }

            var particles = slot.Root.GetComponentsInChildren<ParticleSystem>(true);
            for (int i = 0; i < particles.Length; i++)
            {
                ParticleSystem ps = particles[i];
                if (ps == null)
                    continue;
                if (shown)
                    ps.Play(true);
                else
                    ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            }
        }

        void HideAll()
        {
            _humRampSeconds = 0f;
            foreach (var kv in _live)
            {
                kv.Value.LastUsed = -1f;
                ResetSlotRamp(kv.Value);
                SetBeamShown(kv.Value, false);
            }

            UpdateHum(false);
        }

        void DestroyAll()
        {
            foreach (var kv in _live)
            {
                if (kv.Value.Root != null)
                    Destroy(kv.Value.Root);
            }

            _live.Clear();
            if (_hum != null && _hum.isPlaying)
                _hum.Stop();
        }
    }
}
