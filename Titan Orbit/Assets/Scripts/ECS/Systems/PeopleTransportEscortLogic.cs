using TitanOrbit.Entities;
using TitanOrbit.Generation;
using TitanOrbit.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;
using UnityEngine;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server bookkeeping for troop escort drones. Parked seats are a cheap skin on the
    /// ship ghost (<see cref="PeopleEscortVitalElement"/> + the same slot pose drones use).
    /// Load hops join that slot when the batch opens a new escort. A batch that only
    /// tops up cargo homes on the parked capsule that grows. Unload hops leave a slot
    /// toward the planet with drone formation accel, and a capture coasts them back.
    /// </summary>
    public static class PeopleTransportEscortLogic
    {
        /// <summary>
        /// NetworkTime seconds when valid, else world elapsed. Hop pose and buzz share this
        /// clock with <c>DroneSwarmSimTime</c> so client meshes match server hit spheres.
        /// </summary>
        public static float ReadHopClockSeconds(float elapsedTime, in NetworkTime networkTime, int simulationHz)
        {
            if (networkTime.ServerTick.IsValid)
            {
                int hz = simulationHz > 0 ? simulationHz : PlanetGemMoonOrbitClock.FallbackSimulationHz;
                return (float)PlanetGemMoonOrbitClock.ToElapsedSeconds(networkTime, hz, includeTickFraction: false);
            }

            return elapsedTime;
        }

        /// <summary>Pack size for escort orbs: last load combine, else ship level.</summary>
        public static int ResolveChunk(int shipLevel, int lastLoadCombineMax)
        {
            if (lastLoadCombineMax > 0)
                return lastLoadCombineMax;
            return math.max(1, shipLevel);
        }

        /// <summary>
        /// Rebuild parked seats so they pack <paramref name="cargoPeople"/>. In-flight
        /// reservations (load hops still flying) are kept. Health ratio is preserved
        /// when a seat's amount changes.
        /// </summary>
        public static void SyncParkedEscorts(
            EntityManager em,
            Entity ship,
            int cargoPeople,
            int chunk,
            int shipNetworkId)
        {
            if (!em.HasBuffer<PeopleEscortVitalElement>(ship))
                return;

            var buf = em.GetBuffer<PeopleEscortVitalElement>(ship);
            cargoPeople = math.max(0, cargoPeople);
            chunk = math.max(1, chunk);

            ulong usedMask = 0;
            int inFlightCount = 0;
            int parkedCount = 0;
            for (int i = 0; i < buf.Length; i++)
            {
                var v = buf[i];
                usedMask |= 1UL << (v.SeatId & 15);
                if (v.InFlight != 0)
                    inFlightCount++;
                else
                    parkedCount++;
            }

            int want = PeopleTransportMath.GetEscortSlotCount(cargoPeople, chunk);
            while (parkedCount > want)
            {
                for (int i = buf.Length - 1; i >= 0; i--)
                {
                    if (buf[i].InFlight != 0)
                        continue;
                    usedMask &= ~(1UL << (buf[i].SeatId & 15));
                    buf.RemoveAt(i);
                    parkedCount--;
                    break;
                }
            }

            for (int slot = 0; slot < want; slot++)
            {
                int amount = PeopleTransportMath.GetEscortSlotAmount(cargoPeople, chunk, slot);
                int parkedIndex = FindNthParked(buf, slot);
                if (parkedIndex >= 0)
                {
                    var v = buf[parkedIndex];
                    float oldAmt = math.max(1f, v.Amount);
                    float ratio = v.Health / math.max(1f, PeopleTransportMath.ComputeMaxHealth(oldAmt));
                    v.Amount = (byte)math.clamp(amount, 1, 255);
                    float newMax = PeopleTransportMath.ComputeMaxHealth(v.Amount);
                    v.Health = (byte)math.clamp((int)math.round(newMax * math.saturate(ratio)), 1, 255);
                    buf[parkedIndex] = v;
                    continue;
                }

                byte seat = (byte)PeopleTransportMath.AllocateEscortSeatId(usedMask);
                usedMask |= 1UL << (seat & 15);
                float hp = PeopleTransportMath.ComputeMaxHealth(amount);
                buf.Add(new PeopleEscortVitalElement
                {
                    SeatId = seat,
                    Amount = (byte)math.clamp(amount, 1, 255),
                    Health = (byte)math.clamp((int)math.round(hp), 1, 255),
                    InFlight = 0,
                });
                parkedCount++;
            }

            _ = inFlightCount;
            _ = shipNetworkId;
        }

        /// <summary>
        /// Home for a planet→ship hop. <paramref name="peopleBefore"/> is crew
        /// already committed ahead of this sphere (aboard plus earlier inbound).
        /// The sphere flies to the parked capsule whose packed count grows when
        /// <paramref name="incomingAmount"/> is added on top of that. A new seat
        /// is reserved only when no parked capsule gains troops, so the next
        /// sphere does not follow one that is about to fill.
        /// </summary>
        public static void ResolveLoadHomeSeat(
            EntityManager em,
            Entity ship,
            int peopleBefore,
            int incomingAmount,
            int chunk,
            out byte seatId,
            out bool ownsSeat)
        {
            seatId = 0;
            ownsSeat = false;
            incomingAmount = math.max(0, incomingAmount);
            chunk = math.max(1, chunk);
            int before = math.max(0, peopleBefore);
            int after = before + incomingAmount;

            if (TryPickGrowingSeat(em, ship, before, after, chunk, out seatId))
                return;

            if (TryReserveLoadSeat(em, ship, math.max(1, incomingAmount), out seatId))
            {
                ownsSeat = true;
                return;
            }

            // Visual cap is full and packing did not flag a gain. Still join a
            // parked capsule instead of an empty seat hash.
            if (TryNthParkedSeat(em, ship, 0, out seatId))
                return;

            seatId = 0;
        }

        /// <summary>
        /// Parked capsule whose packed troop count grows the most from
        /// <paramref name="peopleBefore"/> to <paramref name="peopleAfter"/>.
        /// False when every parked amount stays the same (this batch opens a new slot).
        /// Ties keep the earliest seat, matching how leftover people are spread.
        /// </summary>
        static bool TryPickGrowingSeat(
            EntityManager em,
            Entity ship,
            int peopleBefore,
            int peopleAfter,
            int chunk,
            out byte seatId)
        {
            seatId = 0;
            if (!em.HasBuffer<PeopleEscortVitalElement>(ship))
                return false;

            var buf = em.GetBuffer<PeopleEscortVitalElement>(ship);
            int parked = 0;
            for (int i = 0; i < buf.Length; i++)
            {
                if (buf[i].InFlight == 0)
                    parked++;
            }

            if (parked <= 0)
                return false;

            chunk = math.max(1, chunk);
            int bestSlot = -1;
            int bestGain = 0;
            int limit = math.min(parked, PeopleTransportMath.MaxEscortVisualSlots);
            for (int slot = 0; slot < limit; slot++)
            {
                int beforeAmt = PeopleTransportMath.GetEscortSlotAmount(peopleBefore, chunk, slot);
                int afterAmt = PeopleTransportMath.GetEscortSlotAmount(peopleAfter, chunk, slot);
                int gain = afterAmt - beforeAmt;
                if (gain > bestGain)
                {
                    bestGain = gain;
                    bestSlot = slot;
                }
            }

            if (bestSlot < 0)
                return false;

            return TryNthParkedSeat(em, ship, bestSlot, out seatId);
        }

        /// <summary>
        /// Extra distance a new seat must beat the current one by before every
        /// in-flight load switches. Buzz on the parked orbs is smaller than this,
        /// so a yaw that actually brings another seat closer still switches.
        /// </summary>
        const float ClosestSeatSwitchSlack = 0.45f;

        /// <summary>
        /// True when cargo already needs more than 12 escort slots, so leftovers
        /// are spread across the existing seats instead of opening another one.
        /// </summary>
        public static bool IsEscortOverflow(int currentPeople, int chunk)
        {
            chunk = math.max(1, chunk);
            if (currentPeople <= 0)
                return false;
            int raw = (currentPeople + chunk - 1) / chunk;
            return raw > PeopleTransportMath.MaxEscortVisualSlots;
        }

        /// <summary>
        /// Keep a load on the seat chosen for <paramref name="peopleBefore"/>
        /// (aboard plus earlier inbound). Later spheres already carry a higher
        /// total, so a capsule filling does not pull the whole flight onto one
        /// new seat. A hop moves when that claim now grows a different parked
        /// capsule, or when it still needs a seat of its own. Past 12 seats,
        /// leftovers rotate, so the hop homes on the parked capsule closest to
        /// <paramref name="approachPos"/> and follows it as the ship rotates.
        /// <paramref name="ownsNewSeat"/> is set when this call reserved the
        /// returned seat.
        /// </summary>
        public static bool TryRetargetLoadSeat(
            EntityManager em,
            Entity ship,
            int peopleBefore,
            int incomingAmount,
            int chunk,
            byte currentSeat,
            bool ownsSeat,
            float3 approachPos,
            in LocalTransform shipTransform,
            int shipNetworkId,
            double timeSeconds,
            float mapW,
            float mapH,
            out byte newSeat,
            out bool ownsNewSeat)
        {
            newSeat = currentSeat;
            ownsNewSeat = ownsSeat;
            if (incomingAmount <= 0 || ship == Entity.Null || !em.Exists(ship))
                return false;

            // Past 12 seats the packed "+1" index rotates. Aim at the seated
            // transport nearest the planet instead, and move only when rotation
            // makes a different one clearly closer.
            if (IsEscortOverflow(peopleBefore, chunk) &&
                TryPickClosestParkedSeat(
                    em, ship, shipTransform, shipNetworkId, approachPos, currentSeat,
                    timeSeconds, mapW, mapH, out byte closest))
            {
                if (closest == currentSeat)
                    return false;
                newSeat = closest;
                ownsNewSeat = false;
                return true;
            }

            if (!em.HasBuffer<PeopleEscortVitalElement>(ship))
                return false;

            int before = math.max(0, peopleBefore);
            int after = before + incomingAmount;
            if (TryPickGrowingSeat(em, ship, before, after, chunk, out byte growing))
            {
                if (growing == currentSeat)
                    return false;
                newSeat = growing;
                ownsNewSeat = false;
                return true;
            }

            // This claim opens a slot. The hop that already reserved it stays.
            if (ownsSeat)
                return false;

            if (!TryReserveLoadSeat(em, ship, math.max(1, incomingAmount), out byte reserved))
                return false;

            newSeat = reserved;
            ownsNewSeat = true;
            return true;
        }

        /// <summary>
        /// Parked escort whose world pose is nearest <paramref name="approachPos"/>.
        /// Keeps <paramref name="currentSeat"/> when it is still within
        /// <see cref="ClosestSeatSwitchSlack"/> of that nearest pose.
        /// </summary>
        static bool TryPickClosestParkedSeat(
            EntityManager em,
            Entity ship,
            in LocalTransform shipTransform,
            int shipNetworkId,
            float3 approachPos,
            byte currentSeat,
            double timeSeconds,
            float mapW,
            float mapH,
            out byte seatId)
        {
            seatId = currentSeat;
            if (!em.HasBuffer<PeopleEscortVitalElement>(ship))
                return false;

            var buf = em.GetBuffer<PeopleEscortVitalElement>(ship);
            GetEscortHullExtents(em, ship, shipTransform.Scale, out float extX, out float extZ);
            float3 heading = float3.zero;
            if (em.HasComponent<ShipKinematics>(ship))
                heading = em.GetComponentData<ShipKinematics>(ship).FormationHeading;

            approachPos.y = 0f;
            float bestDist = float.MaxValue;
            byte bestSeat = 0;
            bool found = false;
            float currentDist = float.MaxValue;
            bool haveCurrent = false;

            for (int i = 0; i < buf.Length; i++)
            {
                var vital = buf[i];
                if (vital.InFlight != 0 || vital.Amount <= 0)
                    continue;

                float3 pose = PeopleTransportMath.EvaluateEscortSwarmPose(
                    shipTransform.Position, shipTransform.Rotation, extX, extZ,
                    vital.SeatId, vital.Amount, shipNetworkId,
                    timeSeconds, mapW, mapH, heading);
                float dist = ToroidalMapEcs.ToroidalDistance(pose, approachPos, mapW, mapH);
                if (vital.SeatId == currentSeat)
                {
                    currentDist = dist;
                    haveCurrent = true;
                }

                if (dist >= bestDist)
                    continue;
                bestDist = dist;
                bestSeat = vital.SeatId;
                found = true;
            }

            if (!found)
                return false;
            if (haveCurrent && currentDist <= bestDist + ClosestSeatSwitchSlack)
            {
                seatId = currentSeat;
                return true;
            }

            seatId = bestSeat;
            return true;
        }

        /// <summary>Seat id of the nth parked escort (in-flight reservations skipped).</summary>
        static bool TryNthParkedSeat(EntityManager em, Entity ship, int slot, out byte seatId)
        {
            seatId = 0;
            if (!em.HasBuffer<PeopleEscortVitalElement>(ship))
                return false;

            var buf = em.GetBuffer<PeopleEscortVitalElement>(ship);
            int index = FindNthParked(buf, slot);
            if (index < 0)
                return false;

            seatId = buf[index].SeatId;
            return true;
        }

        /// <summary>Reserve a seat for a planet→ship hop. False when the 12-seat cap is full.</summary>
        public static bool TryReserveLoadSeat(EntityManager em, Entity ship, int amount, out byte seatId)
        {
            seatId = 0;
            if (!em.HasBuffer<PeopleEscortVitalElement>(ship) || amount <= 0)
                return false;

            var buf = em.GetBuffer<PeopleEscortVitalElement>(ship);
            if (buf.Length >= PeopleTransportMath.MaxEscortVisualSlots)
                return false;
            ulong used = 0;
            for (int i = 0; i < buf.Length; i++)
                used |= 1UL << (buf[i].SeatId & 15);

            seatId = (byte)PeopleTransportMath.AllocateEscortSeatId(used);
            float hp = PeopleTransportMath.ComputeMaxHealth(amount);
            buf.Add(new PeopleEscortVitalElement
            {
                SeatId = seatId,
                Amount = (byte)math.clamp(amount, 1, 255),
                Health = (byte)math.clamp((int)math.round(hp), 1, 255),
                InFlight = 1,
            });
            return true;
        }

        /// <summary>Load hop arrived — the reserved seat parks around the hull.</summary>
        public static void MarkSeatParked(EntityManager em, Entity ship, byte seatId)
        {
            if (!em.HasBuffer<PeopleEscortVitalElement>(ship))
                return;

            var buf = em.GetBuffer<PeopleEscortVitalElement>(ship);
            for (int i = 0; i < buf.Length; i++)
            {
                var v = buf[i];
                if (v.SeatId != seatId)
                    continue;
                v.InFlight = 0;
                buf[i] = v;
                return;
            }
        }

        /// <summary>Remove a reserved in-flight seat (shot down or refunded).</summary>
        public static void RemoveSeat(EntityManager em, Entity ship, byte seatId)
        {
            if (!em.HasBuffer<PeopleEscortVitalElement>(ship))
                return;

            var buf = em.GetBuffer<PeopleEscortVitalElement>(ship);
            for (int i = 0; i < buf.Length; i++)
            {
                if (buf[i].SeatId != seatId)
                    continue;
                buf.RemoveAt(i);
                return;
            }
        }

        /// <summary>
        /// Peel one parked seat for a ship→planet hop. Returns false when no parked orb is ready.
        /// </summary>
        public static bool TryPeelParkedSeat(
            EntityManager em,
            Entity ship,
            out byte seatId,
            out int amount,
            out float health)
        {
            seatId = 0;
            amount = 0;
            health = 0f;
            if (!em.HasBuffer<PeopleEscortVitalElement>(ship))
                return false;

            var buf = em.GetBuffer<PeopleEscortVitalElement>(ship);
            int best = -1;
            for (int i = 0; i < buf.Length; i++)
            {
                if (buf[i].InFlight != 0 || buf[i].Amount <= 0)
                    continue;
                best = i;
                break;
            }

            if (best < 0)
                return false;

            var v = buf[best];
            seatId = v.SeatId;
            amount = v.Amount;
            health = v.Health;
            buf.RemoveAt(best);
            return amount > 0;
        }

        /// <summary>
        /// Damage a parked escort. Returns remaining HP (0 = seat destroyed and cargo lost).
        /// </summary>
        public static float ApplyDamage(EntityManager em, Entity ship, byte seatId, float damage)
        {
            if (!em.HasBuffer<PeopleEscortVitalElement>(ship) || !em.HasComponent<ShipState>(ship))
                return -1f;

            var buf = em.GetBuffer<PeopleEscortVitalElement>(ship);
            for (int i = 0; i < buf.Length; i++)
            {
                var v = buf[i];
                if (v.SeatId != seatId || v.InFlight != 0)
                    continue;

                float hp = math.max(0f, v.Health - math.max(0f, damage));
                if (hp <= 0.01f)
                {
                    int lost = v.Amount;
                    buf.RemoveAt(i);
                    var shipState = em.GetComponentData<ShipState>(ship);
                    shipState.CurrentPeople = math.max(0, shipState.CurrentPeople - lost);
                    em.SetComponentData(ship, shipState);
                    return 0f;
                }

                v.Health = (byte)math.clamp((int)math.round(hp), 1, 255);
                buf[i] = v;
                return v.Health;
            }

            return -1f;
        }

        /// <summary>Drop every escort orb (death / respawn).</summary>
        public static void ClearAll(EntityManager em, Entity ship)
        {
            if (!em.HasBuffer<PeopleEscortVitalElement>(ship))
                return;
            em.GetBuffer<PeopleEscortVitalElement>(ship).Clear();
        }

        /// <summary>
        /// Covering ellipse in world units, falling back to hull radius on both axes.
        /// </summary>
        public static void GetEscortHullExtents(
            EntityManager em,
            Entity ship,
            float transformScale,
            out float extX,
            out float extZ)
        {
            float hull = PeopleTransportMath.GetShipHullRadius(transformScale);
            extX = hull;
            extZ = hull;
            if (!em.HasComponent<ShipHullColliderState>(ship))
                return;

            var covering = em.GetComponentData<ShipHullColliderState>(ship);
            float scale = math.max(0.25f, transformScale);
            float worldX = covering.AppliedCoveringExtentX * scale;
            float worldZ = covering.AppliedCoveringExtentZ * scale;
            if (worldX >= 0.05f && worldZ >= 0.05f)
            {
                extX = worldX;
                extZ = worldZ;
            }
        }

        /// <summary>Parked-seat world pose (same function client visuals use).</summary>
        public static float3 EvaluateParkedPose(
            EntityManager em,
            Entity ship,
            in LocalTransform transform,
            in PeopleEscortVitalElement vital,
            int shipNetworkId,
            double timeSeconds,
            float mapW,
            float mapH,
            float3 formationHeading)
        {
            GetEscortHullExtents(em, ship, transform.Scale, out float extX, out float extZ);
            return PeopleTransportMath.EvaluateEscortSwarmPose(
                transform.Position, transform.Rotation, extX, extZ,
                vital.SeatId, vital.Amount, shipNetworkId,
                timeSeconds, mapW, mapH, formationHeading);
        }

        /// <summary>Formation home is the seeded escort slot on the ship.</summary>
        public const byte TroopAnchorSlot = 1;

        /// <summary>Formation home is a planet surface point (load refund).</summary>
        public const byte TroopAnchorPlanet = 2;

        /// <summary>
        /// Where a troop sphere should fly this tick. Same struct on the server
        /// entity and the client VFX so both use drone formation motion.
        /// </summary>
        public struct TroopMoveIntent
        {
            public float3 Home;
            public float3 DesiredOffset;
            public byte Anchor;
            public bool HoldWorld;
            public bool HasHome;
        }

        /// <summary>
        /// Seeded rear slot for this seat. Buzz stays on the parked presenter —
        /// the flight home is that same hull-locked slot drones orbit in.
        /// </summary>
        public static bool TryEvaluateTroopSlot(
            EntityManager em,
            Entity ship,
            in LocalTransform transform,
            int shipNetworkId,
            int seatId,
            float amount,
            double timeSeconds,
            float mapW,
            float mapH,
            out float3 slot)
        {
            slot = transform.Position;
            slot.y = 0f;
            if (ship == Entity.Null || !em.Exists(ship))
                return false;

            GetEscortHullExtents(em, ship, transform.Scale, out float extX, out float extZ);
            float3 heading = float3.zero;
            if (em.HasComponent<ShipKinematics>(ship))
                heading = em.GetComponentData<ShipKinematics>(ship).FormationHeading;

            // Same pose the parked presenter draws, so joining and rejoining
            // land on the orb the player already sees.
            slot = PeopleTransportMath.EvaluateEscortSwarmPose(
                transform.Position, transform.Rotation, extX, extZ,
                seatId, amount, shipNetworkId, timeSeconds, mapW, mapH, heading);
            return true;
        }

        /// <summary>
        /// Load joins the seeded slot. Unload advances from that slot toward the
        /// planet the way a mining drone leaves its idle pose. Capture clears the
        /// offset so the sphere coasts back onto the slot.
        /// </summary>
        public static bool TryBuildTroopMoveIntent(
            bool isLoad,
            bool loadEligible,
            bool recallUnload,
            bool hasSlot,
            float3 slot,
            bool hasPlanet,
            float3 planetCenter,
            float planetSize,
            float3 world,
            float mapW,
            float mapH,
            out TroopMoveIntent intent)
        {
            intent = default;
            world.y = 0f;
            slot.y = 0f;
            planetCenter.y = 0f;

            if (isLoad && loadEligible && hasSlot)
            {
                intent.Home = slot;
                intent.DesiredOffset = float3.zero;
                intent.Anchor = TroopAnchorSlot;
                intent.HoldWorld = ToroidalMapEcs.ToroidalDistance(world, slot, mapW, mapH) > 1.5f;
                intent.HasHome = true;
                return true;
            }

            // Ship is still the target but the slot pose is not ready this tick.
            // Hold — do not treat that as a refund back to the planet.
            if (isLoad && loadEligible)
                return false;

            if (isLoad && hasPlanet)
            {
                intent.Home = PeopleTransportMath.GetPlanetSurfaceToward(
                    planetCenter, planetSize, world, mapW, mapH);
                intent.DesiredOffset = float3.zero;
                intent.Anchor = TroopAnchorPlanet;
                intent.HoldWorld = true;
                intent.HasHome = true;
                return true;
            }

            if (!isLoad && recallUnload && hasSlot)
            {
                intent.Home = slot;
                intent.DesiredOffset = float3.zero;
                intent.Anchor = TroopAnchorSlot;
                // No target: offset stays ship-relative so the sphere swings back
                // onto the slot with the hull, matching fighter / mining return.
                intent.HoldWorld = false;
                intent.HasHome = true;
                return true;
            }

            if (!isLoad && hasPlanet && hasSlot)
            {
                float3 surface = PeopleTransportMath.GetPlanetSurfaceToward(
                    planetCenter, planetSize, slot, mapW, mapH);
                intent.Home = slot;
                intent.DesiredOffset = TroopOffsetToward(slot, surface, mapW, mapH);
                intent.Anchor = TroopAnchorSlot;
                intent.HoldWorld = true;
                intent.HasHome = true;
                return true;
            }

            if (!isLoad && hasPlanet)
            {
                intent.Home = PeopleTransportMath.GetPlanetSurfaceToward(
                    planetCenter, planetSize, world, mapW, mapH);
                intent.DesiredOffset = float3.zero;
                intent.Anchor = TroopAnchorPlanet;
                intent.HoldWorld = true;
                intent.HasHome = true;
                return true;
            }

            if (!isLoad && recallUnload)
            {
                return false;
            }

            return false;
        }

        /// <summary>
        /// Full advance from a drone idle pose to a point. Troop sorties are not
        /// leash-limited — they have to reach the planet surface.
        /// </summary>
        public static float3 TroopOffsetToward(float3 home, float3 point, float mapW, float mapH)
        {
            Vector3 desired = DroneSwarmLogic.ComputeDesiredFormationOffset(
                new Vector3(home.x, 0f, home.z),
                new Vector3(point.x, 0f, point.z),
                true,
                100000f,
                0f,
                mapW,
                mapH);
            return new float3(desired.x, 0f, desired.z);
        }

        /// <summary>
        /// One drone formation step. A new home rebases the offset so the sphere
        /// stays where it is and curves onto that home. Speed is kept — clearing
        /// it on a seat change braked every inbound load to a stop.
        /// </summary>
        public static void StepTroopDrone(
            ref float3 offset,
            ref float3 offsetVelocity,
            ref float3 previousIdle,
            ref byte ready,
            ref byte anchor,
            float3 world,
            in TroopMoveIntent intent,
            float dt,
            float mapW,
            float mapH,
            out float3 newWorld,
            out float3 worldVelocity)
        {
            world.y = 0f;
            newWorld = world;
            worldVelocity = float3.zero;
            if (!intent.HasHome)
                return;

            float3 home = intent.Home;
            home.y = 0f;
            bool first = ready == 0;
            if (first || anchor != intent.Anchor)
            {
                offset = ToroidalMapEcs.ShortestOffsetXZ(home, world, mapW, mapH);
                offset.y = 0f;
                previousIdle = home;
                ready = 1;
                anchor = intent.Anchor;
            }

            var state = new DroneSwarmLogic.FormationAnchorState
            {
                Offset = new Vector3(offset.x, 0f, offset.z),
                Velocity = new Vector3(offsetVelocity.x, 0f, offsetVelocity.z),
                PreviousIdle = new Vector3(previousIdle.x, 0f, previousIdle.z),
                HasPreviousIdle = true,
            };
            state = DroneSwarmLogic.StepFormationOffset(
                state,
                new Vector3(intent.DesiredOffset.x, 0f, intent.DesiredOffset.z),
                new Vector3(home.x, 0f, home.z),
                dt,
                mapW,
                mapH,
                intent.HoldWorld);
            offset = new float3(state.Offset.x, 0f, state.Offset.z);
            offsetVelocity = new float3(state.Velocity.x, 0f, state.Velocity.z);
            previousIdle = new float3(state.PreviousIdle.x, 0f, state.PreviousIdle.z);

            newWorld = home + offset;
            newWorld.y = 0f;
            if (ToroidalMapEcs.IsValidMapSize(mapW, mapH))
                newWorld = ToroidalMapEcs.Wrap(newWorld, mapW, mapH);

            if (dt > 0.0001f)
            {
                float3 delta = ToroidalMapEcs.ShortestOffsetXZ(world, newWorld, mapW, mapH);
                worldVelocity = delta / dt;
                worldVelocity.y = 0f;
            }
        }

        /// <summary>
        /// Put a peeled seat back on the hull at the same id so a recalled sortie
        /// reappears in the slot it just flew home to.
        /// </summary>
        public static void RestoreParkedSeat(
            EntityManager em,
            Entity ship,
            byte seatId,
            int amount,
            float health)
        {
            if (!em.HasBuffer<PeopleEscortVitalElement>(ship) || amount <= 0)
                return;

            var buf = em.GetBuffer<PeopleEscortVitalElement>(ship);
            byte amt = (byte)math.clamp(amount, 1, 255);
            byte hp = (byte)math.clamp((int)math.round(math.max(1f, health)), 1, 255);
            for (int i = 0; i < buf.Length; i++)
            {
                if (buf[i].SeatId != seatId)
                    continue;
                var v = buf[i];
                v.Amount = amt;
                v.Health = hp;
                v.InFlight = 0;
                buf[i] = v;
                return;
            }

            buf.Add(new PeopleEscortVitalElement
            {
                SeatId = seatId,
                Amount = amt,
                Health = hp,
                InFlight = 0,
            });
        }

        static int FindNthParked(DynamicBuffer<PeopleEscortVitalElement> buf, int n)
        {
            int seen = 0;
            for (int i = 0; i < buf.Length; i++)
            {
                if (buf[i].InFlight != 0)
                    continue;
                if (seen == n)
                    return i;
                seen++;
            }

            return -1;
        }
    }
}
