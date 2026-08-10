using System.Collections.Generic;

namespace TitanOrbit.Data
{
    /// <summary>
    /// Static name pools for lobby room titles and default player display names when the user
    /// leaves the field blank. Used by <see cref="NetCode.TitanOrbitSessionManager"/> and main-menu
    /// join flow. Client-side only — names are sent to the server after pick; AI bots get stable
    /// names from <see cref="GetNameForAI"/> for leaderboards. No ScriptableObject; hard-coded lists.
    /// </summary>
    public static class GameNames
    {
        // [STANDARD] Shared Random for name picks — not cryptographically secure; fine for display names.
        private static readonly System.Random rng = new System.Random();

        /// <summary>Cool space-themed names for lobby room titles shown in browser UI.</summary>
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

        /// <summary>Default display names when the player leaves the name field blank on connect.</summary>
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

        /// <summary>
        /// Picks a random room name for host-created sessions. Called once when the host opens a lobby.
        /// </summary>
        /// <returns>One entry from <see cref="RoomNames"/>.</returns>
        public static string GetRandomRoomName()
        {
            // --- Uniform pick from room title pool ---
            return RoomNames[rng.Next(RoomNames.Count)];
        }

        /// <summary>
        /// Picks a random default player name for new human connections without a custom name.
        /// </summary>
        /// <returns>One entry from <see cref="DefaultPlayerNames"/>.</returns>
        public static string GetRandomPlayerName()
        {
            // --- Uniform pick from default player name pool ---
            return DefaultPlayerNames[rng.Next(DefaultPlayerNames.Count)];
        }

        /// <summary>
        /// Stable name for an AI ship — same network id always maps to the same display name.
        /// Used for leaderboards and team lists so bots feel consistent across rounds.
        /// </summary>
        /// <param name="id">AI entity or connection id used as hash input.</param>
        /// <returns>Deterministic name from <see cref="DefaultPlayerNames"/> or fallback "AI {id}".</returns>
        public static string GetNameForAI(ulong id)
        {
            // --- Deterministic index from network id ---
            int count = DefaultPlayerNames.Count;
            // [STANDARD] Modulo maps any ulong into list index; empty list falls back to generic label.
            return count > 0 ? DefaultPlayerNames[(int)(id % (ulong)count)] : ("AI " + id);
        }
    }
}
