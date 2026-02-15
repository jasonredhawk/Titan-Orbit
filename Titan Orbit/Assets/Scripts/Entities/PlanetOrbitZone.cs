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
            zoneCollider = GetComponent<SphereCollider>();
            if (zoneCollider != null)
                zoneCollider.isTrigger = true;
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
