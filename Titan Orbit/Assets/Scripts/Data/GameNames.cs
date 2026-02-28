using System.Collections.Generic;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Cool space-themed names for game rooms and default player names when they leave the field blank.
    /// </summary>
    public static class GameNames
    {
        private static readonly System.Random rng = new System.Random();

        public static readonly IReadOnlyList<string> RoomNames = new[]
        {
            "Nebula Drift", "Void Runner", "Titan's Shadow", "Orbit Protocol", "Stellar Forge",
            "Quantum Drift", "Nova Squadron", "Pulsar Station", "Cosmos Reach", "Eclipse Gate",
            "Astral Horizon", "Zero Point", "Solar Flare", "Dark Matter", "Warp Echo",
            "Crimson Nebula", "Ice Giant", "Phantom Orbit", "Starfall", "Rim Runner",
            "Helios Gate", "Andromeda Dock", "Void Walker", "Singularity", "Event Horizon",
            "Black Hole Bar", "Lunar Drift", "Comet Trail", "Aurora Belt", "Meteor Run",
            "Deep Space Nine", "Orion's Arm", "Cassiopeia", "Nebula Prime", "Drift Sector",
            "Hyperlane", "Wormhole Inn", "Red Giant", "White Dwarf", "Neutron Star",
            "Solar Wind", "Cosmic Dust", "Galaxy's Edge", "Rift Runner", "Void's End",
            "Starbound", "Skyfall", "Starfield", "Celestial", "Infinity Loop",
            "Parallax", "Zenith", "Nadir", "Apogee", "Perigee",
            "The Last Frontier", "Beyond the Belt", "Outer Reach", "Far Side", "The Void"
        };

        public static readonly IReadOnlyList<string> DefaultPlayerNames = new[]
        {
            "Pilot", "Voyager", "Scout", "Ranger", "Nomad", "Drifter", "Striker",
            "Nova", "Orbit", "Comet", "Apex", "Vertex", "Nexus", "Pulse", "Flux",
            "Echo", "Shadow", "Blaze", "Frost", "Storm", "Ember", "Cipher",
            "Raven", "Phoenix", "Viper", "Cobra", "Hawk", "Wolf", "Fox",
            "Ghost", "Wraith", "Phantom", "Spectre", "Reaper", "Sentinel",
            "Vanguard", "Pathfinder", "Wayfinder", "Stargazer", "Skywalker",
            "Rocket", "Booster", "Thruster", "Burner", "Cruiser", "Racer",
            "Ace", "Rogue", "Maverick", "Outlaw", "Renegade", "Vagabond"
        };

        public static string GetRandomRoomName()
        {
            return RoomNames[rng.Next(RoomNames.Count)];
        }

        public static string GetRandomPlayerName()
        {
            return DefaultPlayerNames[rng.Next(DefaultPlayerNames.Count)];
        }

        /// <summary>Stable name for an AI ship (same id always gets the same name). Use for leaderboards and team lists.</summary>
        public static string GetNameForAI(ulong id)
        {
            int count = DefaultPlayerNames.Count;
            return count > 0 ? DefaultPlayerNames[(int)(id % (ulong)count)] : ("AI " + id);
        }
    }
}
