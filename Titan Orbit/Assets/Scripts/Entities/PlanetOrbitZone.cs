using UnityEngine;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// Trigger around planets for orbit / people transfer. Planets use a thin orbit ring (not surface-to-outer);
    /// gem moons keep their own wide dock zone via <see cref="PlanetGemMoon"/>.
    /// </summary>
    [RequireComponent(typeof(SphereCollider))]
    public class PlanetOrbitZone : MonoBehaviour
    {
        [SerializeField] private Planet planet;
        private SphereCollider zoneCollider;

        private void Awake()
        {
            if (planet == null)
                planet = GetComponentInParent<Planet>();
            ResolveZoneCollider();
        }

        /// <summary>Planet root may have a solid body sphere plus this trigger; use the trigger collider only.</summary>
        private void ResolveZoneCollider()
        {
            zoneCollider = null;
            foreach (var c in GetComponents<SphereCollider>())
            {
                if (c.isTrigger)
                {
                    zoneCollider = c;
                    break;
                }
            }
        }

        private void OnValidate()
        {
            ResolveZoneCollider();
        }

        public Planet Planet => planet;

        public void SetPlanet(Planet p)
        {
            planet = p;
        }

        private bool IsShipInOrbitRing(Starship ship)
        {
            if (planet == null || ship == null) return false;
            Vector3 t = ship.transform.position;
            Vector3 rbPos = ship.GetComponent<Rigidbody>() != null ? ship.GetComponent<Rigidbody>().position : t;
            return planet.IsWorldPositionInOrbitRing(t) || planet.IsWorldPositionInOrbitRing(rbPos);
        }

        private void OnTriggerEnter(Collider other)
        {
            Starship ship = other.GetComponent<Starship>();
            if (ship != null && IsShipInOrbitRing(ship))
                ship.EnterOrbitZone(planet);
        }

        private void OnTriggerStay(Collider other)
        {
            Starship ship = other.GetComponent<Starship>();
            if (ship == null || planet == null) return;
            if (IsShipInOrbitRing(ship))
                ship.EnterOrbitZone(planet);
            else if (ship.CurrentOrbitPlanet == planet)
                ship.ExitOrbitZone(planet);
        }

        private void OnTriggerExit(Collider other)
        {
            Starship ship = other.GetComponent<Starship>();
            if (ship == null || planet == null) return;
            if (ship.CurrentOrbitPlanet == planet && !IsShipInOrbitRing(ship))
                ship.ExitOrbitZone(planet);
        }
    }
}
