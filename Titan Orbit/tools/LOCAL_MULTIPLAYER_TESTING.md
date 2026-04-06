# Local multiplayer testing (avoid 15‑minute WebGL builds)

## 1. Unity Multiplayer Play Mode (one Editor, multiple players)

The project already includes `com.unity.multiplayer.playmode`. Use it to run **more than one player** from a **single** Unity Editor without cloning the project.

1. **Window → Multiplayer → Multiplayer Play Mode** (or search **Multiplayer Play Mode** in Help).
2. Add a **Player** (or use the default clone count) so you have at least **2** players.
3. Enter **Play** — Unity runs parallel player instances with isolated data where configured.

Use this to validate **UI**, **join flow**, and **RPCs** quickly. WebGL-specific behavior still needs a browser build later.

---

## 2. LAN: two Editor windows (two project instances)

1. **Player A (host):** Open the project, enter Play, select the **GameManagers** / **NetworkGameManager** in the Hierarchy, use the component **⋮** menu → **Debug → Start LAN Host (no Relay)**.  
   - Listens on **`serverPort`** (default **7777** on `NetworkGameManager`).
2. **Player B (client):** Open a **second** Unity Editor with the **same** project (e.g. duplicate the editor via a second clone, or use **Parrel Sync** / a second checkout), enter Play, select **NetworkGameManager** → **Debug → Start LAN Client → 127.0.0.1**.

Or call from code:

- `NetworkGameManager.Instance.StartLocalHostForLanTest()`
- `NetworkGameManager.Instance.StartLocalClientForLanTest("127.0.0.1")`

**Requirements:** Both must use **direct UDP** (no Relay) for this path. If you just used Relay in the same session, **exit Play** and try again so `UnityTransport` is not left in Relay mode.

---

## 3. Two standalone builds (fastest “real” test)

1. Build **Windows** (or **Mac**) **Development** build twice is unnecessary — build **once**, run **two copies** of the same `.exe`.
2. **Instance 1:** Start **LAN host** (you can add a temporary dev button that calls `StartLocalHostForLanTest()`, or use Relay flow if you prefer).
3. **Instance 2:** Call `StartLocalClientForLanTest("127.0.0.1")` with the **same port** as the host.

Firewall: allow **UDP** on the chosen port on loopback / LAN.

---

## 4. Relay + desktop (no WebGL)

Keep using **Host Online** / **Join** with **Relay join codes** from **two desktop builds** or **Editor + build**. This matches production more than LAN, but still avoids WebGL iteration.

---

## 5. WebGL

Browsers still need **WSS + Relay** (or your deployed server). Use local methods above to fix **gameplay / team logic** first, then verify **WebGL** when those flows are stable.
