using System.Collections.Generic;
using TitanOrbit.Audio;
using TitanOrbit.Data;
using TitanOrbit.ECS;
using TitanOrbit.Generation;
using TitanOrbit.NetCode;
using TitanOrbit.Shared;
using TitanOrbit.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace TitanOrbit.Game
{
    /// <summary>
    /// Client hull-collision and grind one-shots from classified Unity Physics contacts.
    /// <para>
    /// Ram / wall hits play on contact enter. Asteroid grind plays on the same 4 Hz
    /// metronome as server <c>ShipRammingCollisionDamageSystem</c> while the local hull
    /// thrusts into the rock. Pitch uses the bullet fire-power piano
    /// (<see cref="AudioManager.ResolveFirePowerPitch"/>) keyed off impact or grind-pulse
    /// damage so a heavier RAM chip / slam sits lower, like a heavier bolt.
    /// </para>
    /// Local predicted contacts own the local hull. Nearby remotes arrive via Sequence-0
    /// <see cref="NotifyRemoteRamPulse"/>. Presentation only — no RPC or ghost fields.
    /// </summary>
    [DefaultExecutionOrder(67010)]
    public sealed class ShipCollisionSfxDriver : MonoBehaviour
    {
        /// <summary>
        /// Same floor as <c>ShipRammingCollisionDamageSystem.ImpactMinClosingSpeed</c> —
        /// soft scrapes stay quiet. Grind does not use this gate.
        /// </summary>
        const float ImpactMinClosingSpeed = 0.35f;

        /// <summary>Absorb a one-tick PhysX flicker so wrap / solve gaps do not double-fire.</summary>
        const float ReplayCooldownSeconds = 0.22f;

        /// <summary>How far a remote Sequence-0 pulse can be and still play (toroidal XZ).</summary>
        const float HearRange = 48f;

        /// <summary>Treat a HitRpc as local when the flash is this close (world XZ).</summary>
        const float LocalMergeRadius = 18f;

        /// <summary>Grind chips sit under the ram slam.</summary>
        const float GrindVolumeScale = 0.62f;

        struct ContactKey : System.IEquatable<ContactKey>
        {
            public int AIndex;
            public int AVersion;
            public int BIndex;
            public int BVersion;
            public byte Kind;

            public static ContactKey From(in ShipPhysicsContactElement pair)
            {
                int i0 = pair.Ship.Index;
                int v0 = pair.Ship.Version;
                int i1 = pair.Other.Index;
                int v1 = pair.Other.Version;
                if (pair.Kind == ShipPhysicsContactKind.Ship &&
                    (i1 < i0 || (i1 == i0 && v1 < v0)))
                {
                    int ti = i0;
                    int tv = v0;
                    i0 = i1;
                    v0 = v1;
                    i1 = ti;
                    v1 = tv;
                }

                return new ContactKey
                {
                    AIndex = i0,
                    AVersion = v0,
                    BIndex = i1,
                    BVersion = v1,
                    Kind = pair.Kind,
                };
            }

            public bool Equals(ContactKey other) =>
                AIndex == other.AIndex && AVersion == other.AVersion &&
                BIndex == other.BIndex && BVersion == other.BVersion &&
                Kind == other.Kind;

            public override bool Equals(object obj) => obj is ContactKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int h = AIndex;
                    h = (h * 397) ^ AVersion;
                    h = (h * 397) ^ BIndex;
                    h = (h * 397) ^ BVersion;
                    h = (h * 397) ^ Kind;
                    return h;
                }
            }
        }

        static ShipCollisionSfxDriver s_instance;

        HashSet<ContactKey> _liveLast = new HashSet<ContactKey>(16);
        HashSet<ContactKey> _liveNow = new HashSet<ContactKey>(16);
        readonly Dictionary<ContactKey, float> _nextSfx = new Dictionary<ContactKey, float>(16);
        readonly List<ContactKey> _staleScratch = new List<ContactKey>(8);
        readonly Dictionary<int, float> _remoteNextSfx = new Dictionary<int, float>(8);
        readonly List<int> _staleRemoteScratch = new List<int>(8);

        EntityQuery _queueQuery;
        World _cachedQueryWorld;
        bool _queriesCreated;
        int _lastTickFrame = -1;
        float _nextLocalGrindAt;
        bool _playedLocalAsteroidSfx;

        /// <summary>
        /// Sequence-0 ram / grind pulse from <see cref="BulletVfxDriver"/>. Skips the local
        /// hull (predicted contacts already played) and kill booms (explosion SFX owns those).
        /// </summary>
        /// <param name="displayPos">Observer display-space contact (Y flattened).</param>
        /// <param name="damage">Impact or grind-pulse HP — same piano key as bullets.</param>
        /// <param name="isKill">True when the rock died this pulse.</param>
        public static void NotifyRemoteRamPulse(float3 displayPos, float damage, bool isKill)
        {
            if (s_instance == null)
                return;
            s_instance.ApplyRemotePulse(displayPos, damage, isKill);
        }

        /// <summary>[UNITY] Attach next to other client VFX drivers so Play Mode has SFX without scene wiring.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
#if UNITY_SERVER && !UNITY_EDITOR
            return;
#else
            if (!TitanOrbitDedicatedServerAutoBoot.ShouldRunClientPresentation())
                return;
            if (s_instance != null)
                return;

            var go = new GameObject(nameof(ShipCollisionSfxDriver));
            DontDestroyOnLoad(go);
            s_instance = go.AddComponent<ShipCollisionSfxDriver>();
#endif
        }

        void Awake()
        {
            if (s_instance != null && s_instance != this)
            {
                Destroy(gameObject);
                return;
            }

            s_instance = this;
        }

        void OnDestroy()
        {
            DisposeQueries();
            if (s_instance == this)
                s_instance = null;
        }

        void LateUpdate()
        {
            if (_lastTickFrame == Time.frameCount)
                return;
            _lastTickFrame = Time.frameCount;

            if (!TitanOrbitDedicatedServerAutoBoot.ShouldRunClientPresentation())
                return;
            if (ClientJoinSettleCache.ShouldSkipShipEntityQueries)
            {
                ResetLive();
                return;
            }

            TickContacts();
        }

        /// <summary>
        /// Walks this frame's classified contact buffer. Local hull only — remotes use
        /// <see cref="NotifyRemoteRamPulse"/>.
        /// </summary>
        void TickContacts()
        {
            var world = EcsGameBridge.GetLocalPlayerShipWorld();
            if (world == null || !world.IsCreated)
            {
                ResetLive();
                return;
            }

            EnsureQuery(world);
            if (!_queriesCreated)
            {
                ResetLive();
                return;
            }

            var em = world.EntityManager;
            float now = Time.unscaledTime;
            float pulse = ShipComponentRammingSuggestions.GrindPulseIntervalSeconds;
            var audio = AudioManager.Instance;
            _playedLocalAsteroidSfx = false;

            if (!EcsGameBridge.TryGetLocalShipEntityOnWorld(world, out Entity localShip) ||
                localShip == Entity.Null)
            {
                EndFrame();
                return;
            }

            DynamicBuffer<ShipPhysicsContactElement> pairs = default;
            bool hasPairs = !_queueQuery.IsEmptyIgnoreFilter;
            if (hasPairs)
            {
                Entity queueEntity = _queueQuery.GetSingletonEntity();
                hasPairs = em.HasBuffer<ShipPhysicsContactElement>(queueEntity);
                if (hasPairs)
                    pairs = em.GetBuffer<ShipPhysicsContactElement>(queueEntity);
            }

            if (hasPairs)
            {
                for (int i = 0; i < pairs.Length; i++)
                {
                    ShipPhysicsContactElement pair = pairs[i];
                    if (pair.Kind != ShipPhysicsContactKind.Asteroid &&
                        pair.Kind != ShipPhysicsContactKind.Ship &&
                        pair.Kind != ShipPhysicsContactKind.Planet &&
                        pair.Kind != ShipPhysicsContactKind.Moon &&
                        pair.Kind != ShipPhysicsContactKind.Shield)
                        continue;
                    if (pair.Ship != localShip && pair.Other != localShip)
                        continue;

                    ContactKey key = ContactKey.From(in pair);
                    if (!_liveNow.Add(key))
                        continue;

                    bool isNew = !_liveLast.Contains(key);
                    bool isAsteroid = pair.Kind == ShipPhysicsContactKind.Asteroid;
                    bool played = false;

                    if (isNew &&
                        pair.ClosingSpeed >= ImpactMinClosingSpeed &&
                        CanPlay(key, now) &&
                        TryResolveEventDamage(
                            em, pair.Ship, pair.ClosingSpeed, grindPulse: false,
                            out float impactDamage))
                    {
                        if (audio != null)
                            PlayForKind(audio, pair.Kind, impactDamage, grind: false);
                        ScheduleNext(key, now, isAsteroid ? pulse : ReplayCooldownSeconds);
                        if (isAsteroid)
                        {
                            _playedLocalAsteroidSfx = true;
                            _nextLocalGrindAt = now + pulse;
                        }

                        played = true;
                    }

                    if (!played &&
                        isAsteroid &&
                        CanPlay(key, now) &&
                        TryIsLocalGrind(em, pair.Ship, in pair) &&
                        TryResolveEventDamage(
                            em, pair.Ship, pair.ClosingSpeed, grindPulse: true,
                            out float grindDamage))
                    {
                        if (audio != null)
                            PlayForKind(audio, pair.Kind, grindDamage, grind: true);
                        ScheduleNext(key, now, pulse);
                        _playedLocalAsteroidSfx = true;
                        _nextLocalGrindAt = now + pulse;
                    }
                }
            }

            TickLocalGrindFallback(em, localShip, now, pulse, audio);
            EndFrame();
            PruneSchedule(now);
        }

        /// <summary>
        /// Same <see cref="ShipAsteroidContactState"/> the spark stream uses. Covers a
        /// one-tick contact-buffer miss so grind SFX stay on the 4 Hz metronome.
        /// </summary>
        void TickLocalGrindFallback(
            EntityManager em,
            Entity localShip,
            float now,
            float pulse,
            AudioManager audio)
        {
            if (_playedLocalAsteroidSfx || now < _nextLocalGrindAt)
                return;
            if (!em.Exists(localShip) || !em.HasComponent<ShipAsteroidContactState>(localShip))
                return;
            if (em.GetComponentData<ShipAsteroidContactState>(localShip).InContact == 0)
                return;
            if (em.HasComponent<MegaShipState>(localShip) &&
                em.GetComponentData<MegaShipState>(localShip).IsMega)
                return;
            if (!em.HasComponent<ShipInput>(localShip) ||
                !em.GetComponentData<ShipInput>(localShip).Thrust)
                return;
            if (audio == null)
                return;
            if (!TryResolveEventDamage(em, localShip, 0f, grindPulse: true, out float grindDamage))
                return;

            PlayForKind(audio, ShipPhysicsContactKind.Asteroid, grindDamage, grind: true);
            _nextLocalGrindAt = now + pulse;
        }

        void ApplyRemotePulse(float3 displayPos, float damage, bool isKill)
        {
            if (isKill || damage <= 0.0001f)
                return;
            if (AudioManager.Instance == null)
                return;
            if (IsNearLocalShip(displayPos))
                return;

            displayPos.y = 0f;
            if (!IsWithinHearRange(displayPos))
                return;

            int key = NeighborhoodKey(displayPos);
            float now = Time.unscaledTime;
            if (_remoteNextSfx.TryGetValue(key, out float nextAt) && now < nextAt)
                return;

            float pulse = ShipComponentRammingSuggestions.GrindPulseIntervalSeconds;
            PlayForKind(AudioManager.Instance, ShipPhysicsContactKind.Asteroid, damage, grind: false);
            _remoteNextSfx[key] = now + pulse;
        }

        static bool IsNearLocalShip(float3 displayPos)
        {
            if (!ShipDisplayPose.HasLocalPose)
                return false;
            Vector3 p = ShipDisplayPose.LocalPosition;
            float dx = p.x - displayPos.x;
            float dz = p.z - displayPos.z;
            return dx * dx + dz * dz <= LocalMergeRadius * LocalMergeRadius;
        }

        static bool IsWithinHearRange(float3 displayPos)
        {
            float3 listener = displayPos;
            bool hasListener = false;
            if (ShipDisplayPose.HasLocalPose)
            {
                Vector3 p = ShipDisplayPose.LocalPosition;
                listener = new float3(p.x, 0f, p.z);
                hasListener = true;
            }
            else if (EcsGameBridge.TryGetLocalShipPresentationPosition(out Vector3 ecsPos))
            {
                listener = new float3(ecsPos.x, 0f, ecsPos.z);
                hasListener = true;
            }

            if (!hasListener)
                return false;
            if (!ToroidalMapEcs.TryGetMapSize(out float mapW, out float mapH))
                return false;
            return ToroidalMapEcs.ToroidalDistance(listener, displayPos, mapW, mapH) <= HearRange;
        }

        static int NeighborhoodKey(float3 displayPos)
        {
            int x = (int)math.floor(displayPos.x / 8f);
            int z = (int)math.floor(displayPos.z / 8f);
            return (x * 73856093) ^ (z * 19349663);
        }

        bool CanPlay(in ContactKey key, float now) =>
            !_nextSfx.TryGetValue(key, out float nextAt) || now >= nextAt;

        void ScheduleNext(in ContactKey key, float now, float cooldown) =>
            _nextSfx[key] = now + math.max(0.05f, cooldown);

        /// <summary>
        /// Same grind gate as the server: thrust held, not a MEGA plow, push into the rock.
        /// </summary>
        static bool TryIsLocalGrind(
            EntityManager em,
            Entity ship,
            in ShipPhysicsContactElement pair)
        {
            if (!em.Exists(ship))
                return false;
            if (em.HasComponent<MegaShipState>(ship) && em.GetComponentData<MegaShipState>(ship).IsMega)
                return false;
            if (!em.HasComponent<ShipInput>(ship) || !em.GetComponentData<ShipInput>(ship).Thrust)
                return false;
            if (!TryResolveRamPower(em, ship, out _, out _, out float taxedAccel))
                return false;
            if (!em.HasComponent<LocalTransform>(ship))
                return false;

            float3 forward = math.mul(
                em.GetComponentData<LocalTransform>(ship).Rotation,
                new float3(0f, 0f, 1f));
            forward.y = 0f;
            float3 driveForce = float3.zero;
            if (math.lengthsq(forward) > 1e-6f)
                driveForce = math.normalize(forward) * math.max(0f, taxedAccel);

            float pushN = ShipComponentRammingSuggestions.ComputeNormalPushNewtons(
                new Vector3(pair.NormalShipFromOther.x, 0f, pair.NormalShipFromOther.z),
                new Vector3(driveForce.x, 0f, driveForce.z));
            return pushN >= ShipComponentRammingSuggestions.GrindMinPushNewtons;
        }

        /// <summary>
        /// Impact burst or one grind pulse — same products as
        /// <c>ShipRammingCollisionDamageSystem</c>.
        /// </summary>
        static bool TryResolveEventDamage(
            EntityManager em,
            Entity ship,
            float closingSpeed,
            bool grindPulse,
            out float damage)
        {
            damage = 0f;
            if (!TryResolveRamPower(em, ship, out float ramRating, out float totalMass, out _))
                return false;
            // MEGA skip-tax reports totalMass 0 — still need a piano key (use the reference hull).
            if (totalMass <= 0.01f)
                totalMass = ShipComponentRammingSuggestions.MassReference;

            if (grindPulse)
            {
                damage = ShipComponentRammingSuggestions.ComputeGrindDamagePerPulse(
                    ramRating, totalMass, ShipComponentRammingSuggestions.GrindPulseIntervalSeconds);
            }
            else
            {
                damage = ShipComponentRammingSuggestions.ComputeImpactDamage(
                    ramRating, totalMass, closingSpeed);
            }

            return damage > 0.0001f;
        }

        /// <summary>
        /// Family rammingPower × bank mul × Global, plus mobility totalMass / taxed accel.
        /// Mirrors server <c>ResolveMobilityRamInputs</c> + bank mul.
        /// </summary>
        static bool TryResolveRamPower(
            EntityManager em,
            Entity ship,
            out float ramRating,
            out float totalMass,
            out float taxedAccel)
        {
            ramRating = 0f;
            totalMass = 0f;
            taxedAccel = 0f;
            if (!em.Exists(ship) ||
                !em.HasComponent<ShipState>(ship) ||
                !em.HasComponent<ShipMotorConfig>(ship))
                return false;

            var shipState = em.GetComponentData<ShipState>(ship);
            var motor = em.GetComponentData<ShipMotorConfig>(ship);

            float baseMass = motor.Mass > 0f ? motor.Mass : ShipMassLogic.DefaultBaseMass;
            float componentSize = motor.HullMassReference > 0f
                ? motor.HullMassReference
                : math.max(ShipMassLogic.MinMass, baseMass * ShipMassLogic.HullMassScale);

            ShipMobilityResolution.TaxedMotorStats taxed = ShipMobilityResolution.ResolveLiveMotorStats(
                motor.MaxSpeed,
                motor.EngineThrust,
                motor.RotationSpeed,
                shipState.CurrentGems,
                shipState.CurrentPeople,
                componentSize,
                skipMassTax: motor.SkipMassTax != 0);
            totalMass = taxed.TotalMass;
            taxedAccel = taxed.EngineThrust;

            float familyRam = motor.RammingPower > 0.001f
                ? motor.RammingPower
                : ShipFamilyDefaultFallbackStats.CreateBaseline().rammingPower;
            int ramBankIndex = 0;
            if (em.HasComponent<ShipLoadoutState>(ship))
            {
                ramBankIndex = BulletBankFireResolve.ResolveFireBankIndex(
                    em.GetComponentData<ShipLoadoutState>(ship));
            }

            familyRam *= BulletBankCombatLogic.GetRammingPowerMultiplier(ramBankIndex);
            ramRating = ShipComponentRammingSuggestions.ComputeDamageRatingFromFamilyPower(familyRam);
            return ramRating > 0.0001f;
        }

        static void PlayForKind(AudioManager audio, byte kind, float damage, bool grind)
        {
            float pitch = audio.ResolveFirePowerPitch(damage);
            float volume = VolumeForDamage(damage, grind);

            if (kind == ShipPhysicsContactKind.Ship)
            {
                audio.PlayShipCollisionSound(pitch, volume);
                return;
            }

            if (kind == ShipPhysicsContactKind.Asteroid)
            {
                audio.PlayAsteroidCollisionSound(pitch, volume);
                return;
            }

            audio.PlayWorldCollisionSound(pitch, volume);
        }

        /// <summary>Soft chips quieter; hard slams full. Grind is a bit under ram at the same HP.</summary>
        static float VolumeForDamage(float damage, bool grind)
        {
            float t = math.saturate((damage - 0.5f) / 20f);
            float volume = math.lerp(0.45f, 1f, t);
            if (grind)
                volume *= GrindVolumeScale;
            return volume;
        }

        void EndFrame()
        {
            var tmp = _liveLast;
            _liveLast = _liveNow;
            _liveNow = tmp;
            _liveNow.Clear();
        }

        void ResetLive()
        {
            _liveLast.Clear();
            _liveNow.Clear();
        }

        void PruneSchedule(float now)
        {
            if (_nextSfx.Count >= 32)
            {
                _staleScratch.Clear();
                foreach (var kv in _nextSfx)
                {
                    if (now - kv.Value > 2f)
                        _staleScratch.Add(kv.Key);
                }

                for (int i = 0; i < _staleScratch.Count; i++)
                    _nextSfx.Remove(_staleScratch[i]);
            }

            if (_remoteNextSfx.Count < 24)
                return;

            _staleRemoteScratch.Clear();
            foreach (var kv in _remoteNextSfx)
            {
                if (now - kv.Value > 2f)
                    _staleRemoteScratch.Add(kv.Key);
            }

            for (int i = 0; i < _staleRemoteScratch.Count; i++)
                _remoteNextSfx.Remove(_staleRemoteScratch[i]);
        }

        void EnsureQuery(World world)
        {
            if (_queriesCreated && _cachedQueryWorld == world)
                return;

            DisposeQueries();
            _queueQuery = world.EntityManager.CreateEntityQuery(
                ComponentType.ReadOnly<ShipPhysicsContactQueueTag>());
            _cachedQueryWorld = world;
            _queriesCreated = true;
        }

        /// <summary>
        /// Releases the cached query when the world is still alive.
        /// After world teardown the query is already gone — <c>Dispose()</c> NREs.
        /// </summary>
        void DisposeQueries()
        {
            if (_queriesCreated && _queueQuery != default
                && _cachedQueryWorld != null && _cachedQueryWorld.IsCreated)
            {
                _queueQuery.Dispose();
            }

            _queueQuery = default;
            _queriesCreated = false;
            _cachedQueryWorld = null;
        }
    }
}
