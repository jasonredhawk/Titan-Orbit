using TitanOrbit.Generation;
using Unity.Mathematics;

namespace TitanOrbit.Simulation
{
    /// <summary>
    /// Magnet-steered people transport motion ported from legacy PeopleTransportProjectile.
    /// Server people-transport systems and client <c>PeopleTransportVisualSyncSystem</c> share these
    /// constants and steering math so cosmetic flight matches delivery timing. Uses toroidal helpers
    /// from <see cref="ToroidalMapEcs"/> for wrap-aware paths.
    /// </summary>
    public static class PeopleTransportMath
    {
        /// <summary>
        /// Base hop time (seconds). Effective travel =
        /// <c>Target × DurationMultiplier / SpeedBonus</c> → 3 × 5 / 2.4 = 6.25s
        /// (the slower ECS feel before the 2.75s “visibility” speedup).
        /// </summary>
        public const float TargetVisualTravelSeconds = 3f;

        /// <summary>Stretches hop time — paired with <see cref="VisualTravelSpeedBonus"/>.</summary>
        public const float VisualTravelDurationMultiplier = 5f;

        /// <summary>Shortens hop time — paired with <see cref="VisualTravelDurationMultiplier"/>.</summary>
        public const float VisualTravelSpeedBonus = 2.4f;

        /// <summary>Outward nudge from planet surface when spawning a load transport (world units).</summary>
        public const float SurfaceSpawnOutwardNudge = 0.45f;

        /// <summary>Load flights cruise a bit faster than unload (toward the moving ship).</summary>
        public const float LoadMagnetSpeedMultiplier = 1.15f;
        public const float MagnetCloseRangeWorld = 5f;
        /// <summary>Mild end-of-hop speed-up (legacy 18/11 was too snappy with the slow cruise).</summary>
        public const float MagnetCloseRangeSpeedRatio = 1.25f;
        public const float ShipLoadCollectPadding = 0.22f;
        public const float ShipLoadCollectMinDistance = 0.4f;
        public const float ShipHullMagnetInset = 0.12f;
        public const float LoadDeliveryMinSeconds = 0.22f;
        public const float LoadDeliveryMinSpawnDistance = 0.35f;
        public const float UnloadDeliveryMinSeconds = 0.18f;
        public const float UnloadDeliveryMinTravelDistance = 0.3f;
        public const float TransportRadius = 0.25f;
        /// <summary>HP per person in sphere (legacy PeopleTransportProjectile.HealthPerShipLevel).</summary>
        public const float HealthPerPeopleAmount = 4f;
        /// <summary>Legacy PeopleTransportProjectile amount → scale curve range.</summary>
        public const float PeopleAmountScaleMin = 1f;
        public const float PeopleAmountScaleMax = 36f;
        public const float VisualScaleMinMultiplier = 0.9f;
        /// <summary>
        /// Max scale at <see cref="PeopleAmountScaleMax"/>. Load batches are
        /// <c>shipLevel × planetLevel</c> (L6×L3 = +18, L6×L6 = +36). Unload
        /// batches are ship level only (L6 = +6).
        /// </summary>
        public const float VisualScaleMaxMultiplier = 2.7f;

        /// <summary>
        /// Runaway ceiling only — not the gameplay pack rule.
        /// Pack size is <see cref="GetTransferChunk"/> (ship × planet); slot count is
        /// <c>ceil(people / chunk)</c>. This stops Instantiates / hit-scan if capacity
        /// is huge (L1×L1 at 30 people → 30 capsules, well under this).
        /// </summary>
        public const int MaxEscortVisualSlots = 64;

        /// <summary>Authored PeopleTransport prefab root scale — used for escort hit radius.</summary>
        public const float EscortPrefabBaseUniform = 0.25f;

        /// <summary>Hull radii behind the ship for the first escort row.</summary>
        public const float EscortBackHullMul = 1.15f;

        /// <summary>Extra back spacing per row after the first.</summary>
        public const float EscortRowSpacingHullMul = 0.55f;

        /// <summary>World-unit pad added to each escort row.</summary>
        public const float EscortRowSpacingPad = 0.35f;

        /// <summary>Lateral offset from centerline as a fraction of hull radius.</summary>
        public const float EscortLateralHullMul = 0.42f;

        /// <summary>World-unit pad on the left/right escort columns.</summary>
        public const float EscortLateralPad = 0.22f;

        /// <summary>
        /// Closest formation radius as a multiple of the hull ellipse at that slot angle.
        /// </summary>
        public const float EscortRingMinRadiusMul = 1.1f;

        /// <summary>
        /// Farthest formation radius as a multiple of the hull ellipse at that slot angle.
        /// </summary>
        public const float EscortRingMaxRadiusMul = 2.5f;

        /// <summary>
        /// Aft-arc jitter (radians). Kept small so hash scatter cannot push a seat
        /// past the beam into the forward hemisphere.
        /// </summary>
        public const float EscortAngleJitter = 0.22f;

        /// <summary>Minimum world gap between neighboring escort spheres on the ring.</summary>
        public const float EscortSlotGap = 0.65f;

        /// <summary>
        /// When an escort is this close to its home, latch onto the slot.
        /// Stops idle jitter; they ride with the ship once caught up.
        /// </summary>
        public const float EscortSettleSnap = 0.18f;

        /// <summary>
        /// Un-latch a riding escort only if it is this far from home
        /// (wrap / teleport). Smaller gaps stay glued so they do not jitter.
        /// </summary>
        public const float EscortRideBreak = 5f;

        /// <summary>
        /// Extra reach past the outer formation ring when deciding an escort has
        /// caught the ship (yaw / orbit must not starve the ready call).
        /// </summary>
        public const float EscortGatherSlack = 1.25f;

        /// <summary>Follow cruise (world units/s) for a +36 capsule.</summary>
        public const float EscortFollowCruiseMin = 4f;

        /// <summary>Follow cruise (world units/s) for a +1 capsule.</summary>
        public const float EscortFollowCruiseMax = 6f;

        /// <summary>
        /// Magnet lerp rate toward cruise. Lower than the generic transport <c>4</c>
        /// so escorts ease on instead of snapping up to speed.
        /// </summary>
        public const float EscortAccelRate = 1.45f;

        /// <summary>
        /// How close a ready capsule's center must be to ship center before launch.
        /// Do not scale this by covering-hull radius — that let ring seats skip preload.
        /// </summary>
        public const float EscortReadyCenterSlack = 0.42f;

        /// <summary>Must be this slow at center before the one-way planet launch.</summary>
        public const float EscortReadyStopSpeed = 0.4f;

        /// <summary>Escorts must fly at least this long before surface consume.</summary>
        public const float EscortUnloadMinSeconds = 0.55f;

        /// <summary>Fraction of the launch-to-surface gap that must be covered before consume.</summary>
        public const float EscortUnloadCoverFraction = 0.78f;

        public static float EffectiveVisualTravelSeconds =>
            TargetVisualTravelSeconds * VisualTravelDurationMultiplier / VisualTravelSpeedBonus;

        public static float ComputeCruiseSpeed(float3 fromPos, float3 toPos, bool isLoad, float mapW, float mapH)
        {
            // --- Compute value ---
            float travelDist = ToroidalMapEcs.ToroidalDistance(fromPos, toPos, mapW, mapH);
            float cruiseSpeed = math.max(0.08f, travelDist / EffectiveVisualTravelSeconds);
            if (isLoad)
                cruiseSpeed *= LoadMagnetSpeedMultiplier;
            return cruiseSpeed;
        }

        public static float3 SteerMagnetVelocity(
            float3 myPos,
            float3 targetPos,
            float3 currentVel,
            float dt,
            float cruiseSpeed,
            float mapW,
            float mapH)
        {
            myPos.y = 0f;
            targetPos.y = 0f;
            float3 toTarget = ToroidalMapEcs.ToroidalDirection(myPos, targetPos, mapW, mapH);
            float dist = ToroidalMapEcs.ToroidalDistance(myPos, targetPos, mapW, mapH);
            float closeSpeed = cruiseSpeed * MagnetCloseRangeSpeedRatio;
            float speed = dist <= MagnetCloseRangeWorld ? closeSpeed : cruiseSpeed;
            float3 targetVel = toTarget * speed;
            return math.lerp(currentVel, targetVel, math.saturate(speed * dt * 4f));
        }

        /// <summary>
        /// Same magnet as <see cref="SteerMagnetVelocity"/> with mass-scaled accel.
        /// Heavier capsules ease onto the new heading instead of snapping with the pack.
        /// </summary>
        public static float3 SteerEscortVelocity(
            float3 myPos,
            float3 targetPos,
            float3 currentVel,
            float dt,
            float cruiseSpeed,
            float peopleAmount,
            float mapW,
            float mapH)
        {
            float accelMul = EscortMassAccelMul(peopleAmount);
            myPos.y = 0f;
            targetPos.y = 0f;
            float3 toTarget = ToroidalMapEcs.ToroidalDirection(myPos, targetPos, mapW, mapH);
            float dist = ToroidalMapEcs.ToroidalDistance(myPos, targetPos, mapW, mapH);
            float speed = cruiseSpeed;
            if (dist < MagnetCloseRangeWorld)
                speed = math.max(0.4f, cruiseSpeed * math.saturate(dist / MagnetCloseRangeWorld));
            float3 targetVel = toTarget * speed;
            return math.lerp(currentVel, targetVel, math.saturate(cruiseSpeed * dt * EscortAccelRate * accelMul));
        }

        /// <summary>
        /// Ease onto a point: speed scales with remaining distance so arrival
        /// does not floor at a crawl and then teleport.
        /// </summary>
        public static float3 SteerEscortArriveVelocity(
            float3 myPos,
            float3 targetPos,
            float3 currentVel,
            float dt,
            float cruiseSpeed,
            float peopleAmount,
            float mapW,
            float mapH)
        {
            float accelMul = EscortMassAccelMul(peopleAmount);
            myPos.y = 0f;
            targetPos.y = 0f;
            float3 toTarget = ToroidalMapEcs.ToroidalDirection(myPos, targetPos, mapW, mapH);
            float dist = ToroidalMapEcs.ToroidalDistance(myPos, targetPos, mapW, mapH);
            float speed = math.min(cruiseSpeed, dist / 0.42f);
            float3 targetVel = toTarget * speed;
            return math.lerp(currentVel, targetVel, math.saturate(cruiseSpeed * dt * EscortAccelRate * accelMul));
        }

        /// <summary>Ship motion this frame (toroidal) so ready capsules can track a moving center.</summary>
        public static float3 GetEscortShipCarryDelta(
            float3 lastShipPos,
            float3 shipPos,
            float mapW,
            float mapH)
        {
            lastShipPos.y = 0f;
            shipPos.y = 0f;
            if (ToroidalMapEcs.IsValidMapSize(mapW, mapH))
                return ToroidalMapEcs.ShortestOffsetXZ(lastShipPos, shipPos, mapW, mapH);
            return shipPos - lastShipPos;
        }

        /// <summary>
        /// People packed into one load sphere: <c>shipLevel × planetLevel</c>
        /// (L6 ship at L3 planet → 18). Callers may still send a smaller partial when
        /// surplus or cargo space is tight. Unload uses <see cref="GetUnloadChunk"/>.
        /// </summary>
        public static int GetTransferChunk(int shipLevel, int planetLevel)
        {
            return math.max(1, math.max(1, shipLevel) * math.max(1, planetLevel));
        }

        /// <summary>
        /// People packed into one unload sphere: ship level only (L6 → 6).
        /// Planet level does not multiply — unloading a high-level world is not faster
        /// than unloading a low-level one. Callers may still send a smaller partial
        /// when remaining crew or planet room is tight.
        /// </summary>
        public static int GetUnloadChunk(int shipLevel)
        {
            return math.max(1, shipLevel);
        }

        /// <summary>
        /// How many escort spheres for this cargo. Pack size is
        /// <paramref name="chunk"/> (ship × planet). L1 ship at L3 with 30 people
        /// → 10; L1×L1 with 30 → 30. Clamps only at <see cref="MaxEscortVisualSlots"/>.
        /// </summary>
        public static int GetEscortSlotCount(int people, int chunk)
        {
            if (people <= 0)
                return 0;
            chunk = math.max(1, chunk);
            int raw = (people + chunk - 1) / chunk;
            return math.min(raw, MaxEscortVisualSlots);
        }

        /// <summary>
        /// People in one packed escort slot. Full capsules are <paramref name="chunk"/>
        /// (L1×L3 → 3); the last holds the remainder. If the safety ceiling clamps
        /// the count, leftover people spread so the sum still equals
        /// <paramref name="people"/>.
        /// </summary>
        public static int GetEscortSlotAmount(int people, int chunk, int slotIndex)
        {
            chunk = math.max(1, chunk);
            int count = GetEscortSlotCount(people, chunk);
            if (count <= 0 || slotIndex < 0 || slotIndex >= count)
                return 0;

            int full = people / chunk;
            int rem = people % chunk;
            int rawCount = rem > 0 ? full + 1 : full;
            if (count < rawCount)
            {
                int baseAmt = people / count;
                int extra = people - baseAmt * count;
                return baseAmt + (slotIndex < extra ? 1 : 0);
            }

            if (slotIndex < full)
                return chunk;
            if (slotIndex == full && rem > 0)
                return rem;
            return 0;
        }

        /// <summary>Heavier capsules cruise slower. Used to remap follow into 6 → 4.</summary>
        public static float EscortMassSpeedMul(float peopleAmount)
        {
            return 1f / math.sqrt(1f + math.max(0f, peopleAmount - 1f) * 0.038f);
        }

        /// <summary>Follow cruise in the 4–6 band. +1 → 6, +36 → 4.</summary>
        public static float GetEscortFollowCruise(float peopleAmount)
        {
            float t = math.saturate(
                (math.max(PeopleAmountScaleMin, peopleAmount) - PeopleAmountScaleMin) /
                math.max(0.01f, PeopleAmountScaleMax - PeopleAmountScaleMin));
            return math.lerp(EscortFollowCruiseMax, EscortFollowCruiseMin, t);
        }

        /// <summary>Heavier capsules ease onto heading more slowly. +1 → 1, +36 → ~0.52.</summary>
        public static float EscortMassAccelMul(float peopleAmount)
        {
            return 1f / math.sqrt(1f + math.max(0f, peopleAmount - 1f) * 0.095f);
        }

        /// <summary>Deterministic 0..1 hash so peers share the same escort layout with no extra net.</summary>
        public static float EscortSlotHash01(int shipNetworkId, int salt)
        {
            uint h = (uint)(shipNetworkId * unchecked((int)0x9E3779B9) ^ (salt * unchecked((int)0x85EBCA77)));
            h ^= h >> 16;
            h *= 0x7FEB352D;
            h ^= h >> 15;
            return (h & 0xFFFFu) / 65535f;
        }

        /// <summary>
        /// Hull surface distance along a ship-local XZ direction using the covering
        /// ellipse (extent X = beam, extent Z = length). Not an average of the axes.
        /// </summary>
        public static float GetHullRadiusAlongLocalDir(float extX, float extZ, float localX, float localZ)
        {
            float a = math.max(BodyCollisionMath.MinShipHullRadiusWorld, extX);
            float b = math.max(BodyCollisionMath.MinShipHullRadiusWorld, extZ);
            float2 dir = new float2(localX, localZ);
            float len = math.length(dir);
            if (len < 1e-5f)
                return a;
            dir /= len;
            float d = (dir.x / a) * (dir.x / a) + (dir.y / b) * (dir.y / b);
            return 1f / math.sqrt(math.max(1e-8f, d));
        }

        /// <summary>
        /// Per-slot radius as 1.1–2.5× the hull ellipse along <paramref name="localX"/> /
        /// <paramref name="localZ"/> (ship-local right / forward-back). Outer seat rings
        /// step farther out so 10–30 capsules do not stack on the same hash.
        /// </summary>
        public static float GetEscortSlotRadius(
            float extX,
            float extZ,
            float localX,
            float localZ,
            float peopleAmount,
            int shipNetworkId,
            int slotIndex)
        {
            _ = peopleAmount;
            float hullR = GetHullRadiusAlongLocalDir(extX, extZ, localX, localZ);
            float u = EscortSlotHash01(shipNetworkId, slotIndex * 31 + 7);
            int ring = math.max(0, slotIndex) / 8;
            float ringMul = 1f + ring * 0.18f;
            return hullR * math.lerp(EscortRingMinRadiusMul, EscortRingMaxRadiusMul, u) * ringMul;
        }

        /// <summary>
        /// True when this capsule has caught the ship (inside the outer hover ring).
        /// Uses ship proximity, not the exact hashed seat — orbit yaw moves seats
        /// faster than escorts can chase, which starved the ready-to-center call.
        /// Enroute chases farther than the ring still cannot become ready.
        /// </summary>
        public static bool IsEscortGatheredAtShip(
            float3 escortPos,
            float3 shipPos,
            quaternion shipRot,
            float extX,
            float extZ,
            int slotIndex,
            int slotCount,
            float peopleAmount,
            int shipNetworkId,
            float mapW,
            float mapH)
        {
            _ = shipRot;
            _ = slotIndex;
            _ = slotCount;
            _ = peopleAmount;
            _ = shipNetworkId;
            float outer = math.max(extX, extZ) * EscortRingMaxRadiusMul + EscortGatherSlack;
            return ToroidalMapEcs.ToroidalDistance(escortPos, shipPos, mapW, mapH) <= outer;
        }

        /// <summary>True when a ready capsule has reached the ship center and may launch.</summary>
        public static bool IsEscortReadyAtShipCenter(
            float3 escortPos,
            float3 shipPos,
            float hullRadius,
            float mapW,
            float mapH)
        {
            _ = hullRadius;
            return ToroidalMapEcs.ToroidalDistance(escortPos, shipPos, mapW, mapH) <= EscortReadyCenterSlack;
        }

        /// <summary>At ship center and nearly stopped — may launch toward the planet.</summary>
        public static bool IsEscortParkedAtShipCenter(
            float3 escortPos,
            float3 escortVel,
            float3 shipPos,
            float hullRadius,
            float mapW,
            float mapH)
        {
            if (!IsEscortReadyAtShipCenter(escortPos, shipPos, hullRadius, mapW, mapH))
                return false;
            escortVel.y = 0f;
            return math.lengthsq(escortVel) <= EscortReadyStopSpeed * EscortReadyStopSpeed;
        }

        /// <summary>
        /// First free seat in 0..<see cref="MaxEscortVisualSlots"/>. Bit <c>s</c> of
        /// <paramref name="usedMask"/> means that seat is taken (up to 64 seats).
        /// </summary>
        public static int AllocateEscortSeatId(ulong usedMask)
        {
            for (int s = 0; s < MaxEscortVisualSlots; s++)
            {
                if ((usedMask & (1UL << s)) == 0)
                    return s;
            }

            return 0;
        }

        /// <summary>
        /// Home pose in the <b>rear hemisphere</b> for a stable <paramref name="seatId"/>.
        /// Angle is hashed from the seat only — live count must not move other capsules.
        /// Client visuals and server follow / hit-scan must share this.
        /// </summary>
        public static float3 EvaluateEscortSlotPose(
            float3 shipPos,
            quaternion shipRot,
            float extX,
            float extZ,
            int seatId,
            float peopleAmount,
            int shipNetworkId,
            float mapW,
            float mapH)
        {
            GetEscortShipBasis(shipPos, shipRot, out shipPos, out float3 forward, out float3 right);
            seatId = math.max(0, seatId);

            // Golden-ratio wrap so 10 or 30 seats stay unique in the aft 180°.
            float wrapped = math.frac(seatId * 0.6180339887f + 0.5f);
            float uAng = EscortSlotHash01(shipNetworkId, seatId * 17 + 11);
            float aftAng = (wrapped - 0.5f) * math.PI + (uAng - 0.5f) * EscortAngleJitter;
            aftAng = math.clamp(aftAng, -0.5f * math.PI, 0.5f * math.PI);

            // aftAng 0 = dead astern (−forward). ±90° = beam, still not in front.
            float3 dir = math.cos(aftAng) * (-forward) + math.sin(aftAng) * right;
            float localX = math.sin(aftAng);
            float localZ = -math.cos(aftAng);
            float3 pos = shipPos + dir * GetEscortSlotRadius(
                extX, extZ, localX, localZ, peopleAmount, shipNetworkId, seatId);
            pos.y = 0f;
            if (ToroidalMapEcs.IsValidMapSize(mapW, mapH))
                pos = ToroidalMapEcs.Wrap(pos, mapW, mapH);
            return pos;
        }

        /// <summary>Cruise after size scale. Bigger troop packs move slower.</summary>
        public static float GetEscortCruise(float peopleAmount, float baseCruise)
        {
            return math.max(0.12f, baseCruise * EscortMassSpeedMul(peopleAmount));
        }

        /// <summary>
        /// Snap onto <paramref name="target"/> when close so idle escorts do not jitter.
        /// </summary>
        public static bool TrySettleEscort(
            ref float3 pos,
            ref float3 vel,
            float3 target,
            float mapW,
            float mapH)
        {
            pos.y = 0f;
            target.y = 0f;
            if (ToroidalMapEcs.ToroidalDistance(pos, target, mapW, mapH) > EscortSettleSnap)
                return false;
            pos = target;
            pos.y = 0f;
            vel = float3.zero;
            return true;
        }

        /// <summary>
        /// Follow at own 4–6 cruise, then latch onto the hashed seat so orbit yaw
        /// cannot leave capsules chasing a moving home (stepped ring motion).
        /// Ready capsules ride the moving ship center and ease to a stop.
        /// </summary>
        public static void IntegrateEscortFollow(
            ref float3 pos,
            ref float3 vel,
            ref bool riding,
            float3 target,
            float3 carryDelta,
            float peopleAmount,
            float dt,
            float mapW,
            float mapH,
            bool readyToCenter)
        {
            bool wasRiding = riding;
            bool parkedAtCenter = wasRiding && readyToCenter;
            riding = false;
            pos.y = 0f;
            target.y = 0f;
            carryDelta.y = 0f;
            if (readyToCenter)
            {
                pos += carryDelta;
                if (ToroidalMapEcs.IsValidMapSize(mapW, mapH))
                    pos = ToroidalMapEcs.Wrap(pos, mapW, mapH);

                float dist = ToroidalMapEcs.ToroidalDistance(pos, target, mapW, mapH);
                if (parkedAtCenter)
                {
                    if (dist > 0.03f)
                    {
                        float3 off = ToroidalMapEcs.ShortestOffsetXZ(pos, target, mapW, mapH);
                        pos += off * math.saturate(dt * 6f);
                        if (ToroidalMapEcs.IsValidMapSize(mapW, mapH))
                            pos = ToroidalMapEcs.Wrap(pos, mapW, mapH);
                    }

                    vel = float3.zero;
                    riding = true;
                    return;
                }

                float cruise = GetEscortFollowCruise(peopleAmount);
                vel = SteerEscortArriveVelocity(
                    pos, target, vel, dt, cruise, peopleAmount, mapW, mapH);
                pos += vel * dt;
                pos.y = 0f;
                if (ToroidalMapEcs.IsValidMapSize(mapW, mapH))
                    pos = ToroidalMapEcs.Wrap(pos, mapW, mapH);

                dist = ToroidalMapEcs.ToroidalDistance(pos, target, mapW, mapH);
                if (dist <= EscortReadyCenterSlack &&
                    math.lengthsq(vel) <= EscortReadyStopSpeed * EscortReadyStopSpeed)
                {
                    vel = float3.zero;
                    riding = true;
                }

                return;
            }

            // Parked formation: once caught, snap to the current seat. Orbit yaw
            // moves that home every tick — chasing it at 4–6 u/s reads as steps.
            if (wasRiding)
            {
                float rideDist = ToroidalMapEcs.ToroidalDistance(pos, target, mapW, mapH);
                if (rideDist <= EscortRideBreak)
                {
                    pos = target;
                    pos.y = 0f;
                    vel = float3.zero;
                    riding = true;
                    return;
                }
            }

            float followCruise = GetEscortFollowCruise(peopleAmount);
            vel = SteerEscortVelocity(
                pos, target, vel, dt, followCruise, peopleAmount, mapW, mapH);
            pos += vel * dt;
            pos.y = 0f;
            if (ToroidalMapEcs.IsValidMapSize(mapW, mapH))
                pos = ToroidalMapEcs.Wrap(pos, mapW, mapH);

            if (TrySettleEscort(ref pos, ref vel, target, mapW, mapH))
                riding = true;
        }

        static void GetEscortShipBasis(
            float3 shipPos,
            quaternion shipRot,
            out float3 planarPos,
            out float3 forward,
            out float3 right)
        {
            planarPos = shipPos;
            planarPos.y = 0f;
            forward = math.mul(shipRot, new float3(0f, 0f, 1f));
            forward.y = 0f;
            if (math.lengthsq(forward) < 1e-4f)
                forward = new float3(0f, 0f, 1f);
            else
                forward = math.normalize(forward);
            right = new float3(-forward.z, 0f, forward.x);
            if (math.lengthsq(right) < 1e-4f)
                right = new float3(1f, 0f, 0f);
            else
                right = math.normalize(right);
        }

        /// <summary>World hit-sphere radius for a packed escort amount (matches visual scale).</summary>
        public static float GetEscortHitRadius(float peopleAmount)
        {
            float scale = EscortPrefabBaseUniform * GetVisualScaleMultiplier(peopleAmount);
            return GetBulletHitRadius(scale);
        }

        /// <summary>
        /// Multiplier on the prefab's authored localScale from carried people amount.
        /// Dispatch packs each batch into one sphere. Load Amount =
        /// <c>shipLevel × planetLevel</c>; unload Amount = ship level. Higher Amount
        /// → larger visual (e.g. +1 ≈ 0.9×, +18 ≈ 1.7×, +36 ≈ 2.7× on the prefab's
        /// 0.25 base scale).
        /// </summary>
        public static float GetVisualScaleMultiplier(float peopleAmount)
        {
            float clamped = math.clamp(math.max(0.001f, peopleAmount), PeopleAmountScaleMin, PeopleAmountScaleMax);
            float normalized = math.unlerp(PeopleAmountScaleMin, PeopleAmountScaleMax, clamped);
            return math.lerp(VisualScaleMinMultiplier, VisualScaleMaxMultiplier, normalized);
        }

        public static float ComputeMaxHealth(float peopleAmount)
        {
            float amount = math.max(0.001f, peopleAmount);
            return math.max(HealthPerPeopleAmount, amount * HealthPerPeopleAmount);
        }

        public static float GetBulletHitRadius(float transformScale)
        {
            return math.max(TransportRadius, math.max(0.001f, transformScale));
        }

        public static float3 GetPlanetSurfaceToward(float3 planetCenter, float planetSize, float3 fromWorldPos, float mapW, float mapH)
        {
            // --- Compute value ---
            float3 fromPos = fromWorldPos;
            fromPos.y = 0f;
            float3 toCore = ToroidalMapEcs.ToroidalDirection(fromPos, planetCenter, mapW, mapH);
            float surfaceWorld = math.max(0.25f, planetSize) * 0.5f;
            float3 surface = planetCenter - toCore * surfaceWorld;
            surface.y = 0f;
            return surface;
        }

        public static float3 GetPlanetSurfaceSpawnToward(float3 planetCenter, float planetSize, float3 towardWorldPos, float mapW, float mapH)
        {
            // --- Compute value ---
            float3 surface = GetPlanetSurfaceToward(planetCenter, planetSize, towardWorldPos, mapW, mapH);
            float3 outward = ToroidalMapEcs.ToroidalDirection(planetCenter, surface, mapW, mapH);
            float nudge = math.max(SurfaceSpawnOutwardNudge, planetSize * 0.045f);
            surface += outward * nudge;
            surface.y = 0f;
            return surface;
        }

        public static float3 GetShipMagnetTarget(float3 shipCenter, float shipRadius, float3 fromWorldPos, float mapW, float mapH)
        {
            // --- Compute value ---
            float3 fromPos = fromWorldPos;
            fromPos.y = 0f;
            float3 toCenter = ToroidalMapEcs.ToroidalDirection(fromPos, shipCenter, mapW, mapH);
            float hullRadius = math.max(0.2f, shipRadius);
            float inset = math.clamp(hullRadius * ShipHullMagnetInset, 0.05f, 0.45f);
            float3 hullPoint = shipCenter - toCenter * math.max(0.2f, hullRadius - inset);
            hullPoint.y = 0f;
            return hullPoint;
        }

        /// <summary>
        /// World hull radius from ECS <c>LocalTransform.Scale</c> — matches ship presentation size.
        /// [TITAN-ORBIT] Do not use Scale raw as radius (that spawned unloads ~1 unit out and looked
        /// like they left from the nose when the ship faced the planet).
        /// </summary>
        public static float GetShipHullRadius(float shipTransformScale) =>
            BodyCollisionMath.GetShipHullRadiusWorld(shipTransformScale);

        /// <summary>
        /// Unload spawn on the planet-facing flank of the ship (toroidal), independent of ship yaw.
        /// </summary>
        /// <param name="shipCenter">Ship logical position.</param>
        /// <param name="shipRadius">World hull radius from <see cref="GetShipHullRadius"/>.</param>
        /// <param name="planetCenter">Planet center — direction ship→planet defines the flank.</param>
        public static float3 GetShipUnloadSpawnToward(
            float3 shipCenter,
            float shipRadius,
            float3 planetCenter,
            float mapW,
            float mapH)
        {
            // --- Planet-facing flank (ignore ship rotation / nose) ---
            // [TITAN-ORBIT] ToroidalDirection(ship, planet) is always the side closest to the planet.
            float3 towardPlanet = ToroidalMapEcs.ToroidalDirection(shipCenter, planetCenter, mapW, mapH);
            float hullRadius = math.max(BodyCollisionMath.MinShipHullRadiusWorld, shipRadius);
            // Clear the visual hull so the float reads as leaving the planetward side, not the cockpit.
            float nudge = math.max(0.2f, hullRadius * 0.55f);
            float3 spawn = shipCenter + towardPlanet * (hullRadius + nudge);
            spawn.y = 0f;
            return spawn;
        }

        public static bool CanDeliverLoadToShip(float3 projectilePos, float3 shipCenter, float shipRadius, float mapW, float mapH)
        {
            // --- CanDeliverLoadToShip ---
            float3 hullPoint = GetShipMagnetTarget(shipCenter, shipRadius, projectilePos, mapW, mapH);
            float collectDist = math.max(ShipLoadCollectMinDistance, TransportRadius + ShipLoadCollectPadding);
            return ToroidalMapEcs.ToroidalDistance(projectilePos, hullPoint, mapW, mapH) <= collectDist;
        }

        public static bool HasBriefTravelBeforeLoad(float3 projectilePos, float3 spawnPosition, float elapsed, float mapW, float mapH)
        {
            // --- HasBriefTravelBeforeLoad ---
            if (elapsed < LoadDeliveryMinSeconds)
                return false;
            return ToroidalMapEcs.ToroidalDistance(projectilePos, spawnPosition, mapW, mapH) >= LoadDeliveryMinSpawnDistance;
        }

        public static bool CanCompleteUnloadDelivery(float3 projectilePos, float3 spawnPosition, float3 planetCenter, float planetSize, float elapsed, float mapW, float mapH)
        {
            // --- CanCompleteUnloadDelivery ---
            // [TITAN-ORBIT] Brief min-time + min-travel so a brand-new spawn on the surface is not
            // consumed on the same tick; once those clear, surface reach finishes the hop.
            if (elapsed < UnloadDeliveryMinSeconds)
                return false;
            if (ToroidalMapEcs.ToroidalDistance(projectilePos, spawnPosition, mapW, mapH) < UnloadDeliveryMinTravelDistance)
                return false;
            float surfaceReach = math.max(0.85f, planetSize * 0.12f);
            float3 surface = GetPlanetSurfaceToward(planetCenter, planetSize, projectilePos, mapW, mapH);
            return ToroidalMapEcs.ToroidalDistance(projectilePos, surface, mapW, mapH) <= surfaceReach;
        }

        /// <summary>
        /// Escort unload consume. Capsules launch from beside the ship in the orbit ring —
        /// the generic surface-reach gate would fire while they are still next to the hull.
        /// They must cover most of the launch-to-surface gap and actually reach the planet.
        /// </summary>
        public static bool CanCompleteEscortUnload(
            float3 projectilePos,
            float3 spawnPosition,
            float3 planetCenter,
            float planetSize,
            float elapsed,
            float mapW,
            float mapH)
        {
            if (elapsed < EscortUnloadMinSeconds)
                return false;

            float3 surface = GetPlanetSurfaceToward(planetCenter, planetSize, projectilePos, mapW, mapH);
            float distNow = ToroidalMapEcs.ToroidalDistance(projectilePos, surface, mapW, mapH);
            float distAtSpawn = ToroidalMapEcs.ToroidalDistance(spawnPosition, surface, mapW, mapH);
            if (distAtSpawn > 1.25f && distNow > distAtSpawn * (1f - EscortUnloadCoverFraction))
                return false;

            float surfaceReach = math.max(0.4f, planetSize * 0.05f);
            return distNow <= surfaceReach;
        }

        /// <summary>
        /// Whether a load transport that turned around (ship left orbit / became ineligible) has
        /// reached the source planet surface and should refund population.
        /// <para>
        /// [TITAN-ORBIT] Uses surface reach + a short min elapsed only. Do <b>not</b> wait the full
        /// <see cref="EffectiveVisualTravelSeconds"/> hop (~6.25s), and do <b>not</b> require a large
        /// distance-from-spawn. Load spheres spawn on the surface; when they return along that same
        /// radial, spawn distance shrinks again while they are on the surface — a 0.75 world-unit
        /// spawn gate fought surface consume and left spheres bouncing near the planet for a long
        /// time (or until an intermittent geometry sweet spot).
        /// </para>
        /// </summary>
        public static bool CanCompleteReturnToSourcePlanet(
            float3 projectilePos,
            float3 spawnPosition,
            float3 planetCenter,
            float planetSize,
            float elapsed,
            float mapW,
            float mapH)
        {
            // --- Return-to-planet consume (ship left ring mid-load) ---
            // spawnPosition is unused for distance gating (spawn is on the surface — see summary).
            _ = spawnPosition;

            // Short min-time only: avoids same-tick refund if the ship leaves on the spawn frame.
            // Unload's min-travel-from-spawn does not apply here — that gate fights surface arrival.
            if (elapsed < UnloadDeliveryMinSeconds)
                return false;

            float surfaceReach = math.max(0.85f, planetSize * 0.12f);
            float3 surface = GetPlanetSurfaceToward(planetCenter, planetSize, projectilePos, mapW, mapH);
            return ToroidalMapEcs.ToroidalDistance(projectilePos, surface, mapW, mapH) <= surfaceReach;
        }
    }
}
