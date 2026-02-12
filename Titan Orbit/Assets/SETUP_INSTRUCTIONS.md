# Titan Orbit - Complete Setup Instructions

## 🚀 Quick Start (Recommended)

### Option 1: One-Click Setup
1. Open your scene in Unity
2. Go to menu: **Titan Orbit > Quick Setup (All)**
3. This will:
   - Create all basic prefabs (Starship, Planet, HomePlanet, Asteroid, Bullet)
   - Set up all GameObjects in the scene
   - Assign prefab references automatically

### Option 2: Step-by-Step Setup

#### Step 1: Create Prefabs
1. Go to menu: **Titan Orbit > Create Basic Prefabs**
2. This creates 5 prefabs in `Assets/Prefabs/`:
   - **Starship.prefab** - Player ship (Capsule shape, cyan color)
   - **Planet.prefab** - Neutral planet (Sphere, gray)
   - **HomePlanet.prefab** - Home base (Sphere with ring, yellow)
   - **Asteroid.prefab** - Mining target (Oval sphere, brown)
   - **Bullet.prefab** - Projectile (Small sphere, yellow with glow)

#### Step 2: Setup Scene
1. Go to menu: **Titan Orbit > Setup Game Scene**
2. This creates all necessary GameObjects:
   - **NetworkManager** - Handles networking
   - **GameManagers** - Game state management
   - **Systems** - All game systems (combat, mining, etc.)
   - **MapGenerator** - Procedural map generation
   - **Main Camera** - Top-down camera
   - **Canvas** - UI system
   - **AudioManager** - Audio system

#### Step 3: Assign Prefabs (if not auto-assigned)
1. Select **MapGenerator** GameObject
2. In Inspector, assign:
   - Home Planet Prefab → `Assets/Prefabs/HomePlanet.prefab`
   - Planet Prefab → `Assets/Prefabs/Planet.prefab`
   - Asteroid Prefab → `Assets/Prefabs/Asteroid.prefab`

3. Select **Systems** GameObject → **CombatSystem** component
4. Assign:
   - Bullet Prefab → `Assets/Prefabs/Bullet.prefab`

5. Select **NetworkManager** GameObject
6. Assign:
   - Player Prefab → `Assets/Prefabs/Starship.prefab`

## 📋 Detailed Configuration

### Input System Setup

1. **Create Input Actions Asset:**
   - Right-click in Project window → Create → Input Actions
   - Name it `InputActions`

2. **Configure Input Actions:**
   - Open the Input Actions asset
   - Create action map: **Gameplay**
   - Add actions:
     - **Move** (Value, Vector2)
       - Bindings: WASD, Arrow Keys, Left Stick
     - **Shoot** (Button)
       - Bindings: Left Mouse Button, Gamepad Button
     - **Look** (Value, Vector2)
       - Bindings: Mouse Position, Right Stick

3. **Assign to PlayerInputHandler:**
   - Find Starship prefab
   - Select PlayerInputHandler component
   - Assign Input Actions asset

### ScriptableObjects Setup

#### Create ShipData Assets
1. Right-click in Project → Create → Titan Orbit → Ship Data
2. Create one for each ship level/type:
   - `ShipData_Level1_Basic`
   - `ShipData_Level2_Fighter`
   - `ShipData_Level2_Miner`
   - `ShipData_Level2_Transport`
   - ... (continue for all 6 levels)
   - `ShipData_Level6_MegaFirepower`
   - `ShipData_Level6_Sniper`
   - `ShipData_Level6_MegaMiner`
   - `ShipData_Level6_MegaTransport`

3. Configure each ShipData:
   - Ship Level
   - Focus Type (Fighter/Miner/Transport)
   - Base stats (movement speed, fire rate, health, etc.)

#### Create UpgradeTree Asset
1. Right-click → Create → Titan Orbit → Upgrade Tree
2. Configure:
   - Level 2 ships (3 choices)
   - Level 3 ships (3 choices)
   - Level 4 ships (3 choices)
   - Level 5 ships (3 choices)
   - Level 6 ships (4 mega ships)
   - Gem costs per level

#### Create PlanetData Asset
1. Right-click → Create → Titan Orbit → Planet Data
2. Configure:
   - Base max population
   - Base growth rate
   - Upgrade costs

### UI Setup

The setup script creates the UI structure, but you need to create the actual UI elements:

1. **HUD Elements:**
   - Select Canvas → HUD GameObject
   - Add UI elements:
     - Slider for health bar
     - TextMeshPro for gem counter
     - TextMeshPro for population counter
     - TextMeshPro for ship level
     - TextMeshPro for ship type
     - Image for team indicator
   - Assign references to HUDController component

2. **Main Menu:**
   - Create UI panels for main menu
   - Add buttons for Start Server/Host/Client
   - Assign references to MainMenu component

3. **Win/Loss Screen:**
   - Create UI panel for win/loss screen
   - Add text elements
   - Assign references to WinLossScreen component

### Network Configuration

1. **Select NetworkManager GameObject**
2. **Configure Transport:**
   - Add Unity Transport component
   - Or use other transport (Relay, etc.)

3. **Set Connection Settings:**
   - Max connections: 60
   - Player prefab: Starship.prefab

### Testing

1. **Single Player Test:**
   - Press Play in Editor
   - Should see map generate
   - Ship should spawn

2. **Multiplayer Test:**
   - Build the game
   - Run multiple instances
   - One as Host, others as Clients
   - Test networking

## 🎨 Customizing Visuals

### Replace Basic Shapes

All prefabs use Unity primitives. To replace:

1. **Starship:**
   - Open `Assets/Prefabs/Starship.prefab`
   - Replace Capsule with your ship model
   - Keep NetworkObject and scripts

2. **Planets:**
   - Open Planet/HomePlanet prefabs
   - Replace Sphere with planet model
   - Keep scripts

3. **Asteroids:**
   - Open Asteroid prefab
   - Replace with asteroid model
   - Keep scripts

### Materials

- Assign materials to renderers
- Use team colors for planets
- Add particle effects

## 🔧 Troubleshooting

### Prefabs Not Found
- Make sure prefabs are in `Assets/Prefabs/`
- Check prefab references in Inspector

### Network Not Working
- Check NetworkManager transport is configured
- Ensure NetworkObject components are on prefabs
- Check firewall/port settings

### Input Not Working
- Verify Input Actions asset is created
- Check PlayerInputHandler has Input Actions assigned
- Test input bindings

### Map Not Generating
- Check MapGenerator has prefabs assigned
- Verify MapGenerator is in scene
- Check console for errors

## 📝 Next Steps

After setup:
1. Configure game balance values
2. Add visual assets
3. Add audio clips
4. Test gameplay
5. Iterate and polish

## 🎮 Ready to Play!

Once setup is complete, you can:
- Start a server/host
- Join as client
- Mine asteroids
- Capture planets
- Upgrade ships
- Win the game!
