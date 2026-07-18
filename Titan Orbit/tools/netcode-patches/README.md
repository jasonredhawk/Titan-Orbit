# NetCode GhostSpawnSystem patch (Titan Orbit)

## Problem

On dedicated Relay late-join with hundreds of map ghosts, the Windows player hard-crashed from Instantiates floods (Burst LocalToWorld) and from map-body `ToEntityArray` during Instantiates / right after Settling OFF.

## Durable location

Embedded package (git-tracked via `file:com.unity.netcode`):

`Packages/com.unity.netcode/Runtime/Snapshot/GhostSpawnSystem.cs`

Canonical copy used by the Editor menu / pre-build guard:

`tools/netcode-patches/GhostSpawnSystem.cs`

## Current patch (`TO_GhostSpawn_v12_joinLoadCounters`)

1. **Safe snapshot copy** — rebuild buffers instead of resize-in-place (`TryCopySnapshotBufferSafe`).
2. **No Burst on `OnUpdate`** — managed Instantiates.
3. **1 Instantiates/frame** from delayed queues.
4. **CreateEntity-all placeholders** each frame (ghost-map safe). Do **not** CreateEntity-cap+requeue — that caused `baseline for a ghost we do not have` → Burst Crash!!!.
5. **Predicted join path** — delayed Instantiates via placeholders.
6. **`TitanOrbitJoinLoadCounters`** — Instantiates/placeholder totals for loading UI without asteroid gathers.
7. **Server companion:** distance `TileSize` ≪ map size (~48); project keeps `TransformSystemGroup` OFF (`TransformQuarantine`) for the in-game session.

## Companion project systems (not in this file)

- Server: `TitanOrbitGhostSendTuneSystem` + `TitanOrbitGhostDistanceImportanceSystem`.
- Client: `TitanOrbitClientJoinTransformGateSystem` + `ClientJoinSettleCache` (Settling + TransformQuarantine).
- Hybrid: `EcsWorldVisualizer` Pending drain; minimap/moon gated on **TransformQuarantine**.

## Re-apply / verify

Unity menu: **Titan Orbit → NetCode → Re-apply GhostSpawnSystem patch**

After Windows client build, `Unity.NetCode.dll` must contain:

`TO_GhostSpawn_v12_joinLoadCounters`
