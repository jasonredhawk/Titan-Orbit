# AGENTS.md

## Project overview

**Titan Orbit** is a multiplayer top-down space arcade game built with **Unity 6 (6000.3.6f1)** using the Universal Render Pipeline (URP) and Netcode for GameObjects. The project lives under `/workspace/Titan Orbit/`. It is a single-product repo (not a monorepo).

Key paths:
- C# game scripts: `Titan Orbit/Assets/Scripts/` (147 files across AI, Audio, Camera, Core, Data, Editor, Entities, Generation, Input, Networking, Systems, UI)
- Editor tooling: `Titan Orbit/Assets/Scripts/Editor/`
- Pre-built WebGL production build: `Titan Orbit/BuildOutput/WebGL/production/TitanOrbitWebGL/`
- Documentation: `Titan Orbit/Assets/SETUP_INSTRUCTIONS.md`, `Titan Orbit/Assets/README.md`, `Titan Orbit/tools/LOCAL_MULTIPLAYER_TESTING.md`, `Titan Orbit/Docs/`

## Cursor Cloud specific instructions

### Environment constraints

- **No Unity Editor available.** Unity 6 cannot be installed in the Cloud Agent VM. All Unity Editor operations (building, running in Play Mode, prefab setup, scene editing) require a local dev machine with Unity 6000.3.6f1.
- **.NET SDK 8.0** is installed for C# script analysis and syntax checking. It cannot compile the Unity project (requires Unity's own assemblies and packages), but is useful for quick syntax validation of individual `.cs` files.

### Running the WebGL build locally

A pre-built WebGL production build is committed at `Titan Orbit/BuildOutput/WebGL/production/TitanOrbitWebGL/`. Serve it with any static file server:

```bash
cd "Titan Orbit/BuildOutput/WebGL/production/TitanOrbitWebGL"
python3 -m http.server 8080
```

Open `http://localhost:8080/` in Chrome. The game loads the main menu with a PLAY button.

**Caveat:** Clicking PLAY will fail with "No open latest lobbies found" because it requires live Unity Gaming Services (Authentication, Relay, Lobbies) and a running dedicated server. This is expected in any environment without UGS connectivity. The console (F12) will show `Play failed. Check console and Unity Services.`.

### Lint / build / test

- **Lint:** No standalone linter is configured. C# files follow Unity conventions. For syntax checks on individual files, use `dotnet script` or a C# LSP.
- **Build:** Requires Unity Editor 6000.3.6f1. Use menu commands `TitanOrbit/Build/WebGL Production` or `TitanOrbit/Build/Headless Server (Windows)` from within the Editor.
- **Test:** No automated test suite (no NUnit/Unity Test Runner tests are present). Testing is done via Unity Play Mode or the WebGL build. See `Titan Orbit/tools/LOCAL_MULTIPLAYER_TESTING.md` for multiplayer testing approaches.

### Networking architecture

All networking goes through Unity Gaming Services (UGS): Authentication (anonymous), Relay (WSS for WebGL, UDP for desktop), and Lobbies. The dedicated server (`DedicatedMatchServerBootstrap.cs`) creates Relay allocations and Lobby instances. See `Titan Orbit/Docs/server-hosting-24_7.md` for production hosting details.
