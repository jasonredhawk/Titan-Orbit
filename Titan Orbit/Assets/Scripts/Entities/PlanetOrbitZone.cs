using UnityEngine;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// Trigger zone around any planet (orbit/loading zone). When a starship enters and is not
    /// moving with right mouse, it auto-orbits. Home planets get full interaction UI; regular planets get load/unload people.
    /// Orbit is 10% of planet diameter from the surface (ship hugs the planet).
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

        private void OnTriggerEnter(Collider other)
        {
            Starship ship = other.GetComponent<Starship>();
            if (ship != null)
                ship.EnterOrbitZone(planet);
        }

        private void OnTriggerExit(Collider other)
        {
            Starship ship = other.GetComponent<Starship>();
            if (ship != null)
                ship.ExitOrbitZone(planet);
        }
    }
}
