using System.Collections.Generic;
using TitanOrbit.Core;
using UnityEngine;

namespace TitanOrbit.Entities
{
    /// <summary>ECS-backed planet view for OrbitStationUI (legacy Planet API).</summary>
    public class Planet : MonoBehaviour
    {
        public static readonly List<Planet> AllPlanets = new List<Planet>();

        public int PlanetId { get; set; }
        public int PlanetLevel { get; set; } = 1;
        public TeamManager.Team TeamOwnership { get; set; } = TeamManager.Team.None;

        void OnEnable()
        {
            if (!AllPlanets.Contains(this))
                AllPlanets.Add(this);
        }

        void OnDisable()
        {
            AllPlanets.Remove(this);
        }
    }

    /// <summary>ECS-backed home planet view for OrbitStationUI (legacy HomePlanet API).</summary>
    public class HomePlanet : Planet
    {
        public static readonly List<HomePlanet> AllHomePlanets = new List<HomePlanet>();

        public TeamManager.Team AssignedTeam
        {
            get => TeamOwnership;
            set => TeamOwnership = value;
        }

        public int HomePlanetLevel
        {
            get => PlanetLevel;
            set => PlanetLevel = value;
        }

        void OnEnable()
        {
            if (!AllHomePlanets.Contains(this))
                AllHomePlanets.Add(this);
        }

        void OnDisable()
        {
            AllHomePlanets.Remove(this);
        }
    }
}
