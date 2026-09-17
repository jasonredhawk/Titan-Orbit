using System.Collections.Generic;
using System.Reflection;
using TitanOrbit.Core;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using TitanOrbit.Generation;
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
    /// to the mouse. While a lock is burning, this driver also reports <c>DPS × dt</c>
    /// to <see cref="EcsFloatingCountPresenter"/> so laser hull / rock hits show the
    /// same floating damage numbers as <c>BulletHitRpc</c> (lasers never send that RPC).
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
            public TeamId Team;
            public float LastUsed;
            public float LastSeenLive;
            public bool Shown;
            public bool NeedsSilence;
            public bool Thinned;
        }

        /// <summary>
        /// One looping hum for every live cannon beam. Clip is Archanor
        /// <c>loop_laser2.wav</c> (BeamLaserStart PlayOnAwake+Loop at pitch 1.3).
        /// We mute those vendor sources and play this once at a lower pitch.
        /// </summary>
        AudioSource _hum;
        AudioClip _humClip;

        /// <summary>
        /// Local copy of the 10% recharge latch so Shift mouse-aim cannot keep
        /// drawing after the pool hits empty (server lockout can lag one snapshot).
        /// </summary>
        bool _localEnergyLockout;

        /// <summary>Keep a beam drawn across one-tick TargetDistance ghost drops.</summary>
        const float HoldSeconds = 0.22f;

        /// <summary>Vendor BeamLaserStart ships at 1.3 — drop it so the loop is a low hum.</summary>
        const float HumPitch = 0.4f;

        /// <summary>Line + muzzle/impact FX vs the stock LaserStatic width.</summary>
        const float VisualScale = 0.5f;

        /// <summary>
        /// Archanor <c>SciFiArsenalBeamStatic</c> lives in Assembly-CSharp — this
        /// Game asmdef cannot name that type. Public fields are set by reflection.
        /// </summary>
        static FieldInfo s_BeamCollidesField;
        static FieldInfo s_BeamLengthField;
        static FieldInfo s_OriginalWidthField;
        static FieldInfo s_CustomWidthField;
        static FieldInfo s_BeamStartField;
        static FieldInfo s_BeamEndField;

        readonly Dictionary<BeamKey, BeamSlot> _live = new Dictionary<BeamKey, BeamSlot>(16);
        readonly List<BeamKey> _stale = new List<BeamKey>(8);

        CannonLaserVfxSettings _settings;

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
        /// Updates pooled beams from ghosted MEGA gunner locks. Uses hybrid barrel
        /// transforms when present so the line sits on the drawn turret.
        /// </summary>
        void LateUpdate()
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
            MarkAllStale();
            // Interval-gated proxy walk — same cache bullet tracers use to stop on hulls.
            BulletCosmeticHitQuery.TryRefresh();

            var bindings = MegaShipWeaponVisualBinding.Live;
            if (bindings != null)
            {
                for (int b = 0; b < bindings.Count; b++)
                    TickBinding(em, bindings[b], mapW, mapH, now);
            }

            bool anyDrawn = HideStale();
            UpdateHum(anyDrawn);
        }

        void TickBinding(
            EntityManager em,
            MegaShipWeaponVisualBinding binding,
            float mapW,
            float mapH,
            float now)
        {
            if (binding == null || !em.Exists(binding.ShipEntity))
                return;
            if (!em.HasComponent<MegaShipState>(binding.ShipEntity)
                || !em.GetComponentData<MegaShipState>(binding.ShipEntity).IsMega)
                return;
            if (!em.HasComponent<ShipState>(binding.ShipEntity)
                || em.GetComponentData<ShipState>(binding.ShipEntity).IsDead)
                return;
            if (!em.HasBuffer<ShipWeaponMountElement>(binding.ShipEntity)
                || !em.HasBuffer<MegaShipGunnerSlotElement>(binding.ShipEntity))
                return;

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
            bool energyLockout = localOwner
                ? StepLocalEnergyLockout(in shipState, megaState.CannonLaserLockout)
                : megaState.CannonLaserLockout || shipState.CurrentEnergy <= 0.0001f;
            var localInput = localOwner && em.HasComponent<ShipInput>(binding.ShipEntity)
                ? em.GetComponentData<ShipInput>(binding.ShipEntity)
                : default;
            bool localFiring = localOwner && localInput.Fire.IsSet;
            bool localMouseAim = localFiring && localInput.Overdrive && !energyLockout;
            // Local Fire release must cut the beam and hum immediately. Ghosted
            // TargetDistance can stay > 0 for a snapshot after the button is up.
            if (localOwner && !localFiring)
                return;
            if (energyLockout)
                return;

            int count = math.min(mounts.Length, gunners.Length);
            for (int m = 0; m < count; m++)
            {
                if (!ShipWeaponKind.IsCannonLaser(mounts[m], gunners, m))
                    continue;
                if (!localMouseAim && !MegaShipWeaponAim.IsTrackingAim(gunners[m]))
                    continue;

                if (!TryResolveBeamEnds(
                        em, binding, hull, mounts[m], gunners[m], m, team, mapW, mapH,
                        out Vector3 muzzle, out Vector3 end, out Entity beamHit))
                    continue;

                var key = new BeamKey
                {
                    ShipIndex = binding.ShipEntity.Index,
                    MountIndex = m,
                };
                if (!TryGetOrCreate(key, team, out BeamSlot slot) || slot.Root == null)
                    continue;

                slot.LastUsed = now;
                slot.LastSeenLive = now;
                if (SilenceVendorAudio(slot.Root))
                    slot.NeedsSilence = false;
                ApplyThinWidth(slot);

                slot.Root.transform.position = muzzle;
                Vector3 toEnd = end - muzzle;
                toEnd.y = 0f;
                if (toEnd.sqrMagnitude < 1e-6f)
                    continue;
                slot.Root.transform.rotation = Quaternion.LookRotation(toEnd.normalized, Vector3.up);
                ApplyVendorLength(slot.Vendor, toEnd.magnitude);
                SetBeamShown(slot, true);

                // --- Floating damage (same channels as BulletHitRpc) ---
                // [TITAN-ORBIT] Hitscan has no tracer / HitRpc. Asteroids are
                // seed-hydrated (GhostId 0), so resolve the rock from the clipped
                // beam contact — same surface fit bullets use — not TargetGhostId.
                TryNotifyBeamDamageFloat(
                    binding.ShipEntity, team, mounts[m], gunners[m], end, beamHit);
            }
        }

        /// <summary>
        /// Pushes this frame's cannon DPS onto the same floating-count path bullets use.
        /// Lasers never send <c>BulletHitRpc</c>; the live beam is the presentation hook.
        /// Asteroids have no <c>GhostInstance</c> — prefer the clipped contact entity.
        /// </summary>
        static void TryNotifyBeamDamageFloat(
            Entity shooter,
            TeamId ownerTeam,
            in ShipWeaponMountElement mount,
            in MegaShipGunnerSlotElement slot,
            Vector3 impactDisplayPos,
            Entity clippedHit)
        {
            Entity target = clippedHit;
            if (target == Entity.Null && slot.TargetGhostId != 0)
                MegaShipWeaponVisualTargets.TryGetEntity(slot.TargetGhostId, out target);
            if (target == Entity.Null)
                BulletCosmeticHitQuery.TryFindAsteroidAtImpact(impactDisplayPos, out target);
            if (target == Entity.Null)
                return;

            // --- Slice = firePower × fireRate × dt (same DPS the server applies) ---
            float slice = ResolveLaserSlice(in mount) * Time.deltaTime;
            if (slice <= 0.01f)
                return;

            EcsFloatingCountPresenter.TryNotifyLaserBeamSlice(
                shooter, target, slice, ownerTeam, impactDisplayPos);
        }

        /// <summary>
        /// Per-barrel DPS for the float. Mount stats are written when the MEGA chassis
        /// applies (client and server). If prediction cleared FirePower, use the catalog
        /// cannon type-table so the number does not go silent.
        /// </summary>
        static float ResolveLaserSlice(in ShipWeaponMountElement mount)
        {
            float power = mount.FirePower;
            float rate = mount.FireRate;
            if (power > 0.01f && rate > 0.01f)
                return CannonLaserMath.ComputeDps(power, rate);

            var catalog = MegaShipCatalog.Load();
            if (catalog == null)
                return CannonLaserMath.ComputeDps(power, rate);

            MegaShipPartStats stats = catalog.GetStatsForPartType(ShipFamilyPartTypes.WeaponCannon);
            if (power <= 0.01f)
                power = stats.firePower;
            if (rate <= 0.01f)
                rate = stats.fireRate;
            return CannonLaserMath.ComputeDps(power, rate);
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
            if (!TryGetLocalMouseBeamEnd(em, binding, in hull, in mount, muzzle, mapW, mapH, out rawEnd))
            {
                if (ghostId != 0
                    && MegaShipWeaponVisualTargets.TryGetEntity(ghostId, out Entity target)
                    && CannonLaserSurface.TryGetHitPoint(
                        em, target, (float3)muzzle, mapW, mapH, 0.0, out float3 surface))
                {
                    rawEnd = (Vector3)surface;
                }
                else if (ghostId != 0
                    && MegaShipWeaponVisualTargets.TryGetDisplayPos(ghostId, muzzle, out Vector3 lockPos))
                {
                    rawEnd = lockPos;
                }
                else if (!MegaShipWeaponVisualTargets.TryGetTiledPoint(
                             slot.AimWorldX, slot.AimWorldZ, muzzle, out rawEnd))
                {
                    float3 offset = MegaShipWeaponAim.WorldDirFromYawDeg(slot.CurrentYawDeg)
                                    * slot.TargetDistance;
                    rawEnd = muzzle + new Vector3(offset.x, 0f, offset.z);
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
            TryClipBeamToFirstCollider(em, binding, team, muzzle, ref rawEnd, out clippedHit);

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
            out Entity hitEntity)
        {
            hitEntity = Entity.Null;
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
                    out _,
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
        /// Same 10% recharge gate as the server. Shift mouse-aim used to ignore
        /// <see cref="MegaShipGunnerSlotElement.TargetDistance"/> and keep drawing.
        /// </summary>
        bool StepLocalEnergyLockout(in ShipState ship, bool serverLockout)
        {
            float maxEnergy = math.max(1f, ship.MaxEnergy);
            if (serverLockout || ship.CurrentEnergy <= 0.0001f)
                _localEnergyLockout = true;
            if (_localEnergyLockout && !serverLockout
                && ship.CurrentEnergy >= maxEnergy * CannonLaserMath.RechargeRatio)
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
                Team = team,
                LastUsed = Time.unscaledTime,
                Shown = true,
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

        void UpdateHum(bool anyLive)
        {
            if (_hum == null)
            {
                _hum = gameObject.AddComponent<AudioSource>();
                _hum.playOnAwake = false;
                _hum.loop = true;
                _hum.spatialBlend = 0f;
                _hum.volume = 0.35f;
                _hum.pitch = HumPitch;
            }

            if (_hum.clip == null && _humClip != null)
                _hum.clip = _humClip;

            if (anyLive && _hum.clip != null)
            {
                if (!_hum.isPlaying)
                    _hum.Play();
            }
            else if (_hum.isPlaying)
            {
                _hum.Stop();
            }
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
        /// Halves the vendor pulse width and muzzle/impact FX. Must run after
        /// <c>SciFiArsenalBeamStatic.Start</c> so <c>originalWidth</c> is set.
        /// </summary>
        static void ApplyThinWidth(BeamSlot slot)
        {
            if (slot == null || slot.Thinned || slot.Vendor == null)
                return;
            if (s_OriginalWidthField == null)
                CacheVendorFields(slot.Vendor.GetType());
            if (s_OriginalWidthField == null)
                return;

            float original = (float)s_OriginalWidthField.GetValue(slot.Vendor);
            if (original <= 0.0001f)
                return;

            s_OriginalWidthField.SetValue(slot.Vendor, original * VisualScale);
            if (s_CustomWidthField != null)
            {
                float custom = (float)s_CustomWidthField.GetValue(slot.Vendor);
                s_CustomWidthField.SetValue(slot.Vendor, custom * VisualScale);
            }

            ScaleFx((GameObject)s_BeamStartField?.GetValue(slot.Vendor));
            ScaleFx((GameObject)s_BeamEndField?.GetValue(slot.Vendor));
            slot.Thinned = true;
        }

        static void ScaleFx(GameObject fx)
        {
            if (fx == null)
                return;
            fx.transform.localScale *= VisualScale;
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
            foreach (var kv in _live)
            {
                kv.Value.LastUsed = -1f;
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
