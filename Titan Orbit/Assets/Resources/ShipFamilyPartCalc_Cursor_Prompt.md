# Titan Orbit — classify ship component names

You are helping author `ShipFamilyPartCalcProfileSet` mappings for Unity.
Discovered from `Assets/Prefabs/Ships` (12 family folders, 241 prefabs).

## Task
Classify each `discoveredName` (prefab asset suffix). Return **ONLY** a JSON array (no markdown fences) of objects:

```
{
  "discoveredName": "Thrusters_Big",
  "partType": "Thruster",
  "contributesAbilityStats": true,
  "enablePropulsionVfx": true,
  "propulsionVfxScale": 1.5,
  "confidence": 0.9,
  "rationale": "Plural big thrusters — jets on"
}
```

## Mental model
- `partType` = **broad group** (shared Part Profile stats + attribute mesh-scale bucket).
- `Cockpit` and `Cockpit_Base` both → `Cockpit` so Troop Cap scales together.
- Covers / plates / holders stay in the parent group (e.g. Thruster Cover → `Thruster`)
  but `contributesAbilityStats: false` and `enablePropulsionVfx: false` so they grow with
  thrusters visually without adding move stats or jet particles.

## Rules
- partType: Engine, Thruster, Wing, Cockpit, Weapon, Fin, Tail, Arm, Ignore, …
- Gun / Missile / Machinegun → Weapon; contributesAbilityStats true; VFX off
- Exhaust / Thrusters / Thrusters_Big / Tiny_Thrusters → Thruster; stats true; VFX on
- Thrusters_Big → VFX scale ≈ **1.5**; Tiny_Thrusters → ≈ **0.45**
- Thruster_Place / *Cover* / *Plate* / *Holder* → same group as parent keyword;
  **contributesAbilityStats false**; **VFX off**
- Default propulsionVfxScale = 1 when VFX is on and size is normal

## Output
Save your JSON array to `Assets/Resources/ShipFamilyPartCalc_Cursor_Suggestions.json` (create/overwrite), or paste it back in Unity via **Import Cursor Suggestions JSON**.

## Names to classify
- `Acc`
- `Body`
- `Body1`
- `Body2`
- `Body_01`
- `Body_02`
- `Body_03`
- `Core`
- `Cover`
- `Cylinder`
- `Detail`
- `Flap`
- `Hatch`
- `Hood`
- `Hull`
- `Joint`
- `Knob`
- `MainBody1`
- `MainBody2`
- `MainBody3`
- `MainBody4`
- `Part_1`
- `Part_2`
- `Ring`
- `Sensor`
- `Shell`
- `Solar`
- `Something`
- `Spike`
- `Support`
- `Tracks`
- `Vents`
