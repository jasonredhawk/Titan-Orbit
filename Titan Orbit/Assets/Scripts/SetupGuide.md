# Titan Orbit - Setup Guide

## Quick Setup Steps

### 1. Create Basic Prefabs
In Unity Editor, go to: **Titan Orbit > Create Basic Prefabs**
This will create:
- Starship.prefab
- Planet.prefab
- HomePlanet.prefab
- Asteroid.prefab
- Bullet.prefab

### 2. Setup Game Scene
In Unity Editor, go to: **Titan Orbit > Setup Game Scene**
This will create all necessary GameObjects in your scene:
- NetworkManager
- GameManagers (GameManager, TeamManager, MatchManager, CrossPlatformManager)
- Systems (Combat, Mining, Transport, Capture, Upgrade, etc.)
- MapGenerator
- Main Camera
- UI Canvas
- AudioManager

### 3. Configure Prefabs in MapGenerator
1. Select the MapGenerator GameObject in the scene
2. In the Inspector, assign the prefabs:
   - Home Planet Prefab: `Assets/Prefabs/HomePlanet.prefab`
   - Planet Prefab: `Assets/Prefabs/Planet.prefab`
   - Asteroid Prefab: `Assets/Prefabs/Asteroid.prefab`

### 4. Configure CombatSystem
1. Select the Systems GameObject
2. Find CombatSystem component
3. Assign Bullet Prefab: `Assets/Prefabs/Bullet.prefab`

### 5. Setup Input System
1. Create an Input Actions asset: `Assets/InputActions.inputactions`
2. Create three action maps:
   - **Gameplay** map with actions:
     - `Move` (Vector2) - bound to WASD/Arrow keys or left stick
     - `Shoot` (Button) - bound to left mouse button or gamepad button
     - `Look` (Vector2) - bound to mouse position or right stick
3. Assign the Input Actions asset to PlayerInputHandler components

### 6. Create ScriptableObjects
1. Create ShipData assets for each ship type:
   - Right-click in Project > Create > Titan Orbit > Ship Data
   - Configure stats for each ship level and type
2. Create UpgradeTree asset:
   - Right-click in Project > Create > Titan Orbit > Upgrade Tree
   - Configure the 6-level upgrade tree with 3 choices per level
3. Create PlanetData asset:
   - Right-click in Project > Create > Titan Orbit > Planet Data
   - Configure planet stats

### 7. Setup UI Elements
The setup script creates basic UI structure, but you'll need to:
1. Create UI elements in the Canvas:
   - Health bar (Slider)
   - Gem counter (TextMeshPro)
   - Population counter (TextMeshPro)
   - Ship level indicator (TextMeshPro)
   - Team indicator (Image)
2. Assign UI references to HUDController component
3. Create Main Menu UI panels
4. Create Win/Loss screen UI

### 8. Configure NetworkManager
1. Select NetworkManager GameObject
2. In NetworkManager component:
   - Set Transport (Unity Transport is recommended)
   - Configure connection settings
   - Set player prefab to Starship prefab

### 9. Test the Setup
1. Open the scene
2. Press Play
3. Test networking by building and running multiple instances

## Manual Setup (Alternative)

If you prefer to set up manually:

### Create NetworkManager GameObject
- Add NetworkManager component
- Add NetworkGameManager component
- Configure transport and settings

### Create Game Managers GameObject
- Add GameManager
- Add TeamManager
- Add MatchManager
- Add CrossPlatformManager

### Create Systems GameObject
- Add all system components (CombatSystem, MiningSystem, etc.)

### Create MapGenerator GameObject
- Add MapGenerator component
- Assign prefab references

### Setup Camera
- Create Main Camera
- Add CameraController component
- Set tag to "MainCamera"

### Setup UI
- Create Canvas
- Add HUDController, MainMenu, WinLossScreen components
- Create UI elements

## Notes

- All prefabs use basic Unity primitives (Capsule for ships, Sphere for planets/asteroids)
- You can replace these with custom models/sprites later
- The setup script creates a functional foundation - you'll need to configure values in the Inspector
- Make sure to assign all prefab references after running the setup scripts
