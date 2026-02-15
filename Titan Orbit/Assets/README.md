# Titan Orbit - Project Structure

## Overview
This is a multiplayer top-down space arcade game built with Unity 6 and Unity Netcode for GameObjects. The game features 3 teams, up to 60 players total, with ship upgrades, planet capture mechanics, asteroid mining, and procedural map generation.

## Project Structure

### Core Systems (`Scripts/Core/`)
- **GameManager.cs** - Main game state manager
- **TeamManager.cs** - Team assignment and management (3 teams, 20 players each)
- **MatchManager.cs** - Match lifecycle management
- **CrossPlatformManager.cs** - Cross-platform configuration

### Networking (`Scripts/Networking/`)
- **NetworkGameManager.cs** - Network game state and player connection handling

### Entities (`Scripts/Entities/`)
- **Planet.cs** - Planet behavior with population and capture mechanics
- **HomePlanet.cs** - Home planet with level system and ship restrictions
- **Asteroid.cs** - Asteroid with gem mining
- **Starship.cs** - Player-controlled ship with movement, combat, and transport
- **Bullet.cs** - Projectile system

### Systems (`Scripts/Systems/`)
- **MiningSystem.cs** - Asteroid mining mechanics
- **CombatSystem.cs** - Combat and bullet spawning
- **TransportSystem.cs** - Population transport between planets
- **CaptureSystem.cs** - Planet capture and win conditions
- **UpgradeSystem.cs** - Ship and planet upgrades
- **AttributeUpgradeSystem.cs** - Ship attribute upgrades
- **VisualEffectsManager.cs** - Particle effects and visual polish

### Generation (`Scripts/Generation/`)
- **MapGenerator.cs** - Procedural map generation with seed-based system
- **ToroidalMap.cs** - Toroidal map wrapping utilities

### Input (`Scripts/Input/`)
- **PlayerInputHandler.cs** - Cross-platform input abstraction
- **MobileInputHandler.cs** - Mobile-specific touch controls

### Camera (`Scripts/Camera/`)
- **CameraController.cs** - Camera follow system with toroidal map support

### UI (`Scripts/UI/`)
- **HUDController.cs** - Main HUD (health, gems, population, ship level)
- **ShipUpgradeUI.cs** - Ship upgrade selection interface
- **MobileControls.cs** - Mobile UI adaptations
- **MainMenu.cs** - Main menu and lobby
- **WinLossScreen.cs** - Win/loss screen

### Data (`Scripts/Data/`)
- **ShipData.cs** - ScriptableObject for ship statistics
- **PlanetData.cs** - ScriptableObject for planet statistics
- **UpgradeTree.cs** - Ship upgrade tree structure (6 levels, 3 choices per level)
- **ShipFocusType.cs** - Enum for ship focus types (Fighter/Miner/Transport)

### Audio (`Scripts/Audio/`)
- **AudioManager.cs** - Audio playback and mixing

## Key Features

### Ship System
- 6 ship levels total
- 2 upgrade choices per level (with overlap between branches)
- Each ship focused on Fighter/Miner/Transport
- Attribute upgrades: Level N ship = N upgrades per attribute
- Ship level upgrade: full gem capacity + home planet level allows next level; can upgrade from anywhere (bottom bar or at home planet)

### Planet System
- Home planets: Start at level 3 (ships can level 1→2→3 without leveling planet). Level up to 4, 5 when team deposits enough gems (increasing thresholds). Max level 5, supports ship levels up to 6
- Neutral planets: Can be captured by transporting population
- Population growth over time
- Capture requires 1 more population than current

### Map Generation
- Seed-based procedural generation
- Toroidal map (wraps around edges)
- Home planets in equilateral triangle formation
- Random neutral planets and asteroids

### Networking
- Unity Netcode for GameObjects
- Dedicated server architecture
- 60 players max (20 per team)

## Setup Instructions

1. **Install Unity Netcode for GameObjects**
   - The package is already added to `Packages/manifest.json`
   - Unity will automatically import it

2. **Create Input Actions**
   - The project uses Unity Input System
   - Create input actions for "Move", "Shoot", and "Look" actions

3. **Create Prefabs**
   - Create prefabs for: Starship, Planet, HomePlanet, Asteroid, Bullet
   - Assign the appropriate scripts to each prefab

4. **Create ScriptableObjects**
   - Create ShipData assets for each ship type
   - Create UpgradeTree asset with ship upgrade paths
   - Create PlanetData assets

5. **Setup Scene**
   - Add NetworkManager to scene
   - Add GameManager, TeamManager, MatchManager
   - Add MapGenerator for procedural generation
   - Setup UI Canvas with HUD and menus

## Next Steps

1. Create visual assets (sprites/models for ships, planets, asteroids)
2. Create particle effects for combat, mining, capture
3. Add audio clips for background music and sound effects
4. Configure Input Actions in Unity Input System
5. Create prefabs and assign scripts
6. Test networking with multiple clients
7. Balance game parameters (gem costs, growth rates, etc.)

## Notes

- All networking uses ServerRpc/ClientRpc pattern
- Server is authoritative for game state
- Client-side prediction for smooth movement
- Mobile optimizations included for performance
