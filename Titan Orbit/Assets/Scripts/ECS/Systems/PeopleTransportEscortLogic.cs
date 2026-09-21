using TitanOrbit.Generation;
using TitanOrbit.Simulation;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;
using Unity.Transforms;

namespace TitanOrbit.ECS
{
    /// <summary>
    /// Server bookkeeping for troop escort drones. Parked seats are a cheap skin on the
    /// ship ghost (<see cref="PeopleEscortVitalElement"/> + closed-form pose). Load/unload
    /// hops stay short server entities; their flight is closed-form like other drones.
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
            float3 shipVelocity,
            double timeSeconds,
            float mapW,
            float mapH,
            float3 formationHeading)
        {
            GetEscortHullExtents(em, ship, transform.Scale, out float extX, out float extZ);
            return PeopleTransportMath.EvaluateEscortSwarmPose(
                transform.Position, transform.Rotation, extX, extZ,
                vital.SeatId, vital.Amount, shipNetworkId, shipVelocity,
                timeSeconds, mapW, mapH, formationHeading);
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
