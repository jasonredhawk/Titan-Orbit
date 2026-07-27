using TitanOrbit.Data;
using UnityEngine;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// Shared constants and deterministic helpers for drone swarm presentation (and future combat).
    /// Combat / loot paths from the pre-ECS <c>DroneSwarmController</c> are not restored yet —
    /// this file still owns the plane contract so visuals and sim never fight each other.
    /// <para>
    /// [TITAN-ORBIT] Gameplay is planar on XZ. Drones may float above the ship for readability,
    /// but fire positions, hit spheres, and shield intercepts must stay on <see cref="FixedY"/>.
    /// </para>
    /// </summary>
    public static class DroneSwarmLogic
    {
        /// <summary>
        /// Floor escort ring radius when hull size is tiny (world units).
        /// Matches the legacy default ring so drones do not sit inside a presentation-scaled hull.
        /// </summary>
        public const float DefaultOrbitRadius = 3f;

        /// <summary>How fast drones drift around the ring (degrees per second) when combat orbit resumes.</summary>
        public const float DefaultOrbitSpeedDeg = 55f;

        /// <summary>
        /// Authoritative flight / combat height — Titan Orbit is XZ gameplay.
        /// Server hit tests, muzzle spawn, and shield intercept math must use this (not presentation lift).
        /// </summary>
        public const float FixedY = 0f;

        /// <summary>
        /// Client-only extra height so buzz meshes clear the hull when the camera looks down.
        /// [HYBRID] Never feed this into server combat, bullet spawn, or shield sphere centers.
        /// </summary>
        public const float PresentationLiftY = 0.45f;

        /// <summary>
        /// Deterministic buzz/orbit phase from ship network id + slot so peers would match
        /// if combat visuals are restored later.
        /// </summary>
        /// <param name="shipNetworkId">Ghost / network id of the owning ship.</param>
        /// <param name="slotIndex">Equipment buffer slot that owns this drone.</param>
        /// <param name="droneType">Fighter, Shield, or Mining — mixed into the hash.</param>
        /// <returns>Phase in radians in roughly [0, 2π).</returns>
        public static float DeterministicBasePhaseRad(int shipNetworkId, int slotIndex, StoreItemType droneType)
        {
            // --- Mix ship + slot + type into a stable 32-bit hash ---
            // [STANDARD] Same inputs → same phase on every client (no Random).
            uint hash = (uint)(shipNetworkId ^ (slotIndex * unchecked((int)0x9E3779B9)) ^ ((int)droneType * unchecked((int)0x85EBCA6B)));
            hash ^= hash >> 16;
            hash *= 0x7FEB352D;
            hash ^= hash >> 15;

            // Map into ~[0, 6.283) without floating-point noise across machines.
            return (hash % 6283) / 1000f;
        }

        /// <summary>
        /// Builds the world Y used for drawing a drone mesh.
        /// Sim / combat callers should use <see cref="FixedY"/> directly instead.
        /// </summary>
        /// <param name="optionalBuzzY">Extra vertical wobble from presentation buzz (usually small).</param>
        /// <returns>World Y for the cosmetic proxy only.</returns>
        public static float PresentationWorldY(float optionalBuzzY = 0f)
        {
            return FixedY + PresentationLiftY + optionalBuzzY;
        }
    }
}
