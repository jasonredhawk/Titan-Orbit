using UnityEngine;

namespace TitanOrbit.Entities
{
    /// <summary>
    /// Trigger around home planets for orbit / people transfer. Uses the same thin orbit ring as <see cref="PlanetOrbitZone"/>.
    /// </summary>
    [RequireComponent(typeof(SphereCollider))]
    public class HomePlanetOrbitZone : MonoBehaviour
    {
        [SerializeField] private HomePlanet homePlanet;
        private SphereCollider zoneCollider;

        private void Awake()
        {
            if (homePlanet == null)
                homePlanet = GetComponentInParent<HomePlanet>();
            ResolveZoneCollider();
        }

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

        public HomePlanet HomePlanet => homePlanet;

        public void SetHomePlanet(HomePlanet planet)
        {
            homePlanet = planet;
        }

        private bool IsShipInOrbitRing(Starship ship)
        {
            if (homePlanet == null || ship == null) return false;
            Vector3 t = ship.transform.position;
            var body = ship.GetComponent<Rigidbody>();
            Vector3 rbPos = body != null ? body.position : t;
            return homePlanet.IsWorldPositionInOrbitRing(t) || homePlanet.IsWorldPositionInOrbitRing(rbPos);
        }

        private void OnTriggerEnter(Collider other)
        {
            Starship ship = other.GetComponent<Starship>();
            if (ship != null && IsShipInOrbitRing(ship))
                ship.EnterOrbitZone(homePlanet);
        }

        private void OnTriggerStay(Collider other)
        {
            Starship ship = other.GetComponent<Starship>();
            if (ship == null || homePlanet == null) return;
            if (IsShipInOrbitRing(ship))
                ship.EnterOrbitZone(homePlanet);
            else if (ship.CurrentOrbitPlanet == homePlanet)
                ship.ExitOrbitZone(homePlanet);
        }

        private void OnTriggerExit(Collider other)
        {
            Starship ship = other.GetComponent<Starship>();
            if (ship == null || homePlanet == null) return;
            if (ship.CurrentOrbitPlanet == homePlanet && !IsShipInOrbitRing(ship))
                ship.ExitOrbitZone(homePlanet);
        }
    }
}
