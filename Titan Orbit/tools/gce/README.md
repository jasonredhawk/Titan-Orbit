# GCE dedicated server deploy (Google Compute Engine)

Batch helpers upload the Unity **Linux headless** build to your VM and optionally restart the **systemd** service, using the [Google Cloud CLI](https://cloud.google.com/sdk/docs/install) (`gcloud`).

This mirrors the idea of `tools/gcs/deploy_webgl_gcs.bat`: one main script for day-to-day publishes.

**Build the binary in Unity:** **TitanOrbit → Build → Headless Server (Linux — Google Cloud)** (requires **Linux Dedicated Server** Hub module).

### Fast path: Unity stays open (recommended day-to-day)

Closing Unity and using batchmode is **slower** (full project reopen). When the Editor is already open:

**TitanOrbit → Build → Headless Server (Linux — Google Cloud) + Deploy**

That builds in the open Editor, copies output to `BuildOutput/Server/deploy-staging/TitanOrbitLinux1`, then launches the existing **`deploy_server_gce.bat freeDisk useGcs`** in a console window. Unity stays open.

Build-only (no upload): **TitanOrbit → Build → Headless Server (Linux — Google Cloud)**.

### One-shot from PowerShell (Editor must be closed)

Use **`build_and_deploy_server_gce.bat`** only when Unity is **closed** (CI, or you are not in the Editor). Batchmode cannot open a project the GUI already holds.

From `tools\gce`:

```bat
build_and_deploy_server_gce.bat
```

That runs Unity headless (`BuildHeadlessServerLinuxBatchMode`) → `BuildOutput/Server/TitanOrbitLinux1` → then **`deploy_server_gce.bat`** defaults (**`freeDisk useGcs`**).

| Command | Meaning |
|---------|---------|
| `build_and_deploy_server_gce.bat` | Build + deploy (`freeDisk useGcs`) |
| `build_and_deploy_server_gce.bat freeDisk useGcs useIap` | Build + deploy with IAP / VM reset |
| `build_and_deploy_server_gce.bat buildOnly` | Unity build only |
| `build_and_deploy_server_gce.bat deployOnly freeDisk useGcs` | Skip build; deploy existing folder |

Optional: set **`TITANORBIT_UNITY_EDITOR`** to a full `Unity.exe` path if Hub auto-detect fails. Build logs land in **`BuildOutput/Logs/`**.

**Stable Windows pipeline (recommended):** Install **OpenSSH Client** (Windows optional feature). **`upload_linux_build_to_gce.bat`** and **`deploy_server_gce.bat`** call **`upload_linux_build_to_gce_openssh.ps1`**, which uses **`ssh.exe` / `scp.exe` + `gcloud compute start-iap-tunnel`** — **not** `gcloud compute ssh` / `scp` (those use PuTTY **plink** and are flaky). **`restart_titanorbit_server_on_gce.bat`** uses **`restart_server_remote.ps1`**, which prefers **OpenSSH** (direct to external IP when present, then IAP tunnel + ssh.exe; plink only as an explicit last resort). If Windows SSH is still unusable, use the **GCS + Cloud Shell** path below (no PC SSH).

## Recommended: dedicated server without Windows SSH (GCS + Cloud Shell)

**Goal:** One **Linux VM** runs the authoritative game; **players never host** — they join your server.

If **`gcloud compute ssh`**, **`gcloud compute scp`**, or PuTTY on your PC is unreliable, **stop using SSH from Windows for uploads**. Use storage + Google’s browser terminal instead.

| Step | What to do |
|------|------------|
| 1 | **Unity:** **TitanOrbit → Build → Headless Server (Linux — Google Cloud)** → output folder `BuildOutput/Server/TitanOrbitLinux1`. |
| 2 | **Console:** **Cloud Storage → Buckets → Create** a bucket (default name in script: **`titan-orbit-dedicated-server`**, or pass your own name as the 3rd argument to the upload `.bat`). Same region as the VM is fine (e.g. `us-central1`). |
| 3 | **Bucket permissions:** **Permissions → Grant access** → principal **Compute Engine default service account** (`PROJECT_NUMBER-compute@developer.gserviceaccount.com`) → role **Storage Object Viewer** on that bucket. |
| 4 | **Windows:** From `tools\gce` run **`upload_linux_build_to_gcs.bat`** (optional args: build folder, project id, bucket name). Uses **`gcloud storage cp` only** — no SSH. |
| 5 | **Console:** Open **Cloud Shell** (top bar **>_**). Paste the script in **[Simple deploy: GCS + Cloud Shell](#simple-deploy-gcs--cloud-shell)**. That copies the tarball from GCS onto the VM and extracts it under **`/home/jason/titanorbit-server/`**. |
| 6 | **Run the server:** Use **Console → Compute Engine → VM → SSH** (browser) and start the binary, or install **`titanorbit-server.service`** once if you want **`systemctl`**. |

**Why not Cloud Run?** Cloud Run is aimed at request/response and scaling containers; a typical Unity dedicated process is a **long-lived** process with **persistent networking**. **Compute Engine (a VM)** matches that model.

### Simple deploy: GCS + Cloud Shell

Paste into **Cloud Shell** (Console top-right terminal icon) after **`upload_linux_build_to_gcs.bat`** has finished. Defaults match this repo; change the `export` lines if you use other project / zone / user / bucket names.

```bash
export PROJECT_ID=titan-orbit
export ZONE=us-central1-f
export VM=jason@titanorbitcp
export BUCKET=titan-orbit-dedicated-server
export OBJECT=titanorbit-linux-build/TitanOrbitLinux1-latest.tar.gz

gcloud config set project "$PROJECT_ID"
gcloud storage cp "gs://${BUCKET}/${OBJECT}" /tmp/titan-latest.tgz
gcloud compute scp --project="$PROJECT_ID" --zone="$ZONE" /tmp/titan-latest.tgz "${VM}:/tmp/titan-latest.tgz"
gcloud compute ssh "$VM" --project="$PROJECT_ID" --zone="$ZONE" --command 'bash -lc "set -e; mkdir -p /home/jason/titanorbit-server; rm -rf /home/jason/titanorbit-server/TitanOrbitLinux1; tar -xzf /tmp/titan-latest.tgz -C /home/jason/titanorbit-server; rm -f /tmp/titan-latest.tgz; chmod -R a+rX /home/jason/titanorbit-server/TitanOrbitLinux1 2>/dev/null || true; chmod 755 /home/jason/titanorbit-server/TitanOrbitLinux1/TitanOrbitServer /home/jason/titanorbit-server/TitanOrbitLinux1/TitanOrbitServer.x86_64 2>/dev/null || true; chmod a+r /home/jason/titanorbit-server/TitanOrbitLinux1/GameAssembly.so /home/jason/titanorbit-server/TitanOrbitLinux1/UnityPlayer.so 2>/dev/null || true; test $(stat -c%s /home/jason/titanorbit-server/TitanOrbitLinux1/TitanOrbitServer_Data/il2cpp_data/Metadata/global-metadata.dat 2>/dev/null || echo 0) -ge 1000000 || { echo FATAL: global-metadata.dat missing or too small after extract. Repack on Windows with Unity closed, or use upload_linux_build_to_gce. >&2; exit 1; }; ls -la /home/jason/titanorbit-server/TitanOrbitLinux1"'
```

**IL2CPP Linux dedicated builds** often ship **`TitanOrbitServer`** (small ELF) with **no** **`TitanOrbitServer.x86_64`**. Only chmod’ing the `.x86_64` name leaves the real binary non-executable → **`systemd` fails with `status=203/EXEC` / `Permission denied`**. The line above sets **755** on both names and opens read/traverse on the tree (same idea as `upload_linux_build_to_gce_openssh.ps1`).

If **`gcloud compute scp`** / **`ssh`** from Cloud Shell times out (VM has no public IP, or firewall), add **`--tunnel-through-iap`** to both **`gcloud compute scp`** and **`gcloud compute ssh`** lines (same flag on each). Ensure IAP TCP forwarding to port 22 is allowed (firewall + **`allow-iap-ssh`** tag) as described elsewhere in this README.

### Troubleshooting: Serial Console shows `203/EXEC` or `Permission denied` on `TitanOrbitServer`

That means **systemd cannot execute** `ExecStart` — almost always **missing execute bit** (or wrong owner) on the Unity player after **`tar`** from Windows, **not** a Unity code bug.

**On the VM** (browser **SSH**, **Serial Console** login as `jason`, or Cloud Shell `gcloud compute ssh`):

```bash
sudo chmod 755 /home/jason/titanorbit-server/TitanOrbitLinux1/TitanOrbitServer \
  /home/jason/titanorbit-server/TitanOrbitLinux1/TitanOrbitServer.x86_64 2>/dev/null || true
sudo chmod -R a+rX /home/jason/titanorbit-server/TitanOrbitLinux1 2>/dev/null || true
sudo chmod a+r /home/jason/titanorbit-server/TitanOrbitLinux1/GameAssembly.so \
  /home/jason/titanorbit-server/TitanOrbitLinux1/UnityPlayer.so 2>/dev/null || true
ls -la /home/jason/titanorbit-server/TitanOrbitLinux1/TitanOrbitServer*
sudo systemctl restart titanorbit-server
sudo systemctl status titanorbit-server --no-pager -l | head -n 25
```

You should see **`-rwxr-xr-x`** on the server binary. If the unit points at the wrong filename (`.x86_64` vs none), run **`bash cloudshell_restart_titanorbit_server.sh`** from this folder in **Cloud Shell** (it fixes permissions and syncs **`ExecStart`** to whichever binary exists), or redeploy with **`restart_titanorbit_server_on_gce.bat`** / **`restart_server_remote.ps1`**, which run the same chmod block before **`systemctl restart`**.

**`reset_gce_vm.bat` / hard reboot** only restarts the guest — it **does not** repair **`chmod`**. If Serial Console shows **`203/EXEC`** / **`Permission denied`** on **`TitanOrbitServer`**, run the **`chmod`** block above, **or** reinstall the unit from this repo: **`tools/gce/titanorbit-server.service`** now includes **`PermissionsStartOnly=true`** and an **`ExecStartPre`** that **`chmod 755`** both **`TitanOrbitServer`** and **`TitanOrbitServer.x86_64`** as **root** on every start (so a bad **`tar`** extract is usually fixed by **`sudo systemctl daemon-reload`** after copying the file, then **`sudo systemctl restart titanorbit-server`**). Reinstall with **`install_unit_remote.ps1`** or **`bash cloudshell_install_titanorbit_unit.sh`** (see **`install_enable_server_service_on_gce.bat`**).

Rarely, the game directory is on a filesystem mounted **`noexec`**; then move the install to **`/home`** or **`/opt`** (normal GCE disks are fine).

### Serial console flooded on boot / “can’t reboot properly”

The VM **is** booting. **`titanorbit-server`** is enabled, crashes in ~0.5s, and **systemd restarts it every few seconds**, so Serial Console looks like a reboot loop.

**At the `titanorbitcp login:` prompt**, log in (browser SSH works too), then run:

```bash
sudo systemctl stop titanorbit-server
sudo systemctl disable titanorbit-server
sudo systemctl mask titanorbit-server
```

Or run **`tools/gce/emergency_stop_titanorbit_server.sh`** on the VM. After that, the serial log stays quiet. Fix **`Player.log`** / redeploy, then **`sudo systemctl unmask titanorbit-server && sudo systemctl enable --now titanorbit-server`**.

### Troubleshooting: Serial / journal shows `status=1/FAILURE` and `[UnityMemory]` in a tight restart loop

**What it means:** **`systemd` is starting the player**, Unity prints the usual **`[UnityMemory] … boot.config`** lines, then the process **exits with code 1** within a fraction of a second. That is **not** the same as **`203/EXEC`** (wrong permissions / bad path). The next evidence is almost always in **`Player.log`** next to the binary, or in **`TitanOrbitDedicatedServer.log`** if managed code got far enough to append a line.

**Intermittent “sometimes a game is created, sometimes not” on the same VM:** If serial shows **`Found startup-script in metadata`** around the same time as **`titanorbit-server`** restarts, a **metadata `startup-script` that reinstalls or chmods the build** can race the running player (partial extract → **`Failed to initialize IL2CPP`** or instant exit **1**). Check and remove one-shot scripts after they run:

```bash
gcloud compute instances describe titanorbitcp --project=titan-orbit --zone=us-central1-f \
  --format="yaml(metadata.items)"
```

Remove **`startup-script`** from instance metadata when you no longer need it. Reinstall **`titanorbit-server.service`** from this repo so the unit **`After=google-startup-scripts.service`** (server starts only after metadata startup finishes).

**Stop the log flood first** (optional but makes serial usable):

```bash
sudo systemctl stop titanorbit-server
```

After you **reinstall the unit** from this repo (`install_unit_remote.ps1` or redeploy with `upload_linux_build_to_gce_openssh.ps1`), Unity logs go to **journald** (`-logFile /dev/stdout`), so the **Serial Console** and `journalctl -u titanorbit-server -n 100` should show the real error line (e.g. **`Failed to initialize IL2CPP`**) instead of only `[UnityMemory]` lines. **ExecStartPre** also refuses to start if **`GameAssembly.so`**, **`UnityPlayer.so`**, or **`global-metadata.dat`** are too small.

**Collect runtime evidence on the VM** (paths match **`titanorbit-server.service`** in this repo):

```bash
cd /home/jason/titanorbit-server/TitanOrbitLinux1
ls -la
ls -la TitanOrbitServer_Data 2>/dev/null || echo "MISSING TitanOrbitServer_Data (incomplete extract or wrong folder)"
tail -n 200 Player.log
tail -n 100 TitanOrbitDedicatedServer.log 2>/dev/null || true
command -v ldd >/dev/null && ldd ./TitanOrbitServer 2>/dev/null | head -n 40
command -v ldd >/dev/null && ldd ./TitanOrbitServer.x86_64 2>/dev/null | head -n 40
```

### Dedicated match availability (UGS lobbies)

The headless server publishes **Unity Gaming Services (UGS) lobbies** so clients can browse and join. **`TitanOrbitDedicatedServerHost`** keeps matches available:

| Trigger | When | Action |
|---------|------|--------|
| Boot | systemd starts | Relay + lobby (`IsLatest=1`, `IsOpen=1`) + heartbeat every 15s |
| Empty recreate | 0 players for `--emptyMatchRecreateSeconds` (default 1800s; countdown starts when last player leaves) | In-process new Relay + lobby — **never while players are connected** |
| Empty process recycle | After `--maxInProcessEmptyRecreates` (default **6**) successful empty in-process recreates | **Exit process** (still 0 players) so systemd `Restart=always` boots fresh — stops overnight Unity wedges |
| Main-thread hang | Unity Update stamps stale for `--mainThreadHangQuitSeconds` (default **90**) | Background watchdog `Environment.Exit(1)` → systemd restart |
| Stale lobby | Our lobby closed or heartbeat-stale while empty (`--staleLobbyRecreateSeconds=120`) | In-process recreate as latest |
| Self-heal | No joinable `IsLatest` lobby in UGS while this server is empty | Immediate in-process recreate (or process recycle if empty-recreate cap hit) |
| Age rotation | Match age ≥ 30 min, players present, not full | **`SpawnNextMatch`** + demote `IsLatest` but **keep `IsOpen=1`** (occupied maps stay joinable) |
| Full rotation | Lobby at max players | Close listing + **`SpawnNextMatch`** (sibling OS process; not managed by systemd) |
| Match request | Client publishes wake lobby when browse is empty | Idle server recreates immediately |

**Rotation spawns sibling processes** (`SpawnNextMatch`) using `--serverExecutablePath` from **`titanorbit-server.service`**. Those children are **not** restarted by systemd; if a successor dies after handoff, the **self-heal** and **heartbeat-failure** paths recreate a joinable lobby from the main process when it is empty.

**If Join Game shows no matches:** check `journalctl -u titanorbit-server -f` for `Dedicated host loops started`, `Heartbeat failed`, `Self-heal`, or `SpawnNextMatch`. Stale ghost lobbies in UGS are filtered client-side after **45s** without heartbeat.

**Run the same command as `ExecStart` in the foreground** (after install, **`systemctl cat titanorbit-server`** should show **`run_titanorbit_server.sh`** as the entry point). You should see the same exit code and often a clearer last line on the terminal.

**Interpretation:** If **`Player.log`** ends with a **managed** exception or **`DedicatedMatchServerBootstrap`** / **`Application.Quit`**, fix that path in the game or configuration (UGS keys, scene list, prefabs). If the log stops right after engine lines or mentions **missing `.so`**, **`SIGSEGV`**, or **Burst / native plugin** load errors, treat it as a **Linux build / deploy / `glibc`** issue (`ldd`, full tarball redeploy, VM image compatibility).

#### `Player.log` ends with **`Failed to initialize IL2CPP`** (confirmed root cause class)

That line is emitted **before** managed game code runs. In practice it is almost always one of:

1. **Corrupt or truncated IL2CPP payload on the VM** — e.g. **`GameAssembly.so`**, **`UnityPlayer.so`**, or **`TitanOrbitServer_Data/il2cpp_data/Metadata/global-metadata.dat`** is **missing, empty, or far too small** (common when **`tar`** was built on Windows while Unity or antivirus still had files open). A typical pattern is **large `GameAssembly.so` + `UnityPlayer.so` but `global-metadata.dat` is 0 bytes** → **`Failed to initialize IL2CPP`**. The **OpenSSH upload** path (`upload_linux_build_to_gce_openssh.ps1`) checks archive and VM file sizes. **`upload_linux_build_to_gcs.bat`** now refuses to upload if **`global-metadata.dat`** (or **`GameAssembly.so`**) is missing or too small before **`tar`**. The **Cloud Shell** one-liner below **aborts the extract** if **`global-metadata.dat`** on the VM is **smaller than 1 MB** so you do not silently run a broken install.

2. **Missing system libraries** for the Unity player — run **`ldd ./GameAssembly.so`** (from the install folder) and fix any **`not found`** lines (on Debian 12, installing **`libc6`**, **`libstdc++6`**, and **`libgcc-s1`** covers most stock players).

**On the VM, verify sizes** (adjust the path if your install user differs from **`jason`**):

```bash
cd /home/jason/titanorbit-server/TitanOrbitLinux1
stat -c '%n %s bytes' GameAssembly.so UnityPlayer.so TitanOrbitServer_Data/il2cpp_data/Metadata/global-metadata.dat 2>/dev/null
```

Healthy IL2CPP builds are typically on the order of **tens of MB** for **`GameAssembly.so`**, **several MB+** for **`UnityPlayer.so`**, and **~1MB+** for **`global-metadata.dat`**. If any size is **0** or suspiciously tiny, **rebuild the Linux server in Unity**, **quit the Editor**, then re-create the tarball and redeploy (or use **`upload_linux_build_to_gce.bat`** / **`upload_linux_build_to_gce_openssh.ps1`**, which refuse bad archives).

**Check dynamic deps:**

```bash
ldd ./GameAssembly.so 2>&1 | grep -F 'not found' || echo "No missing libs reported by ldd."
```

### Browser SSH and IAP error 4003

The **SSH** button in **Compute Engine → VM instances** usually connects through **Identity-Aware Proxy (IAP)**. A popup like **Connection via IAP failed**, **Code: 4003**, **failed to connect to backend** means IAP could not open **TCP port 22** on your VM (firewall + tags), or **`sshd`** is not listening on 22.

**Tag ≠ firewall rule:** **`allow-iap-ssh` on the VM only marks which instances a rule applies to.** You still need a **VPC firewall rule** on the **same network as the VM** (e.g. `default`) that allows **`35.235.240.0/20` → `tcp:22`** to **target tags** `allow-iap-ssh`. If the rule is missing, on the wrong VPC, or denied by an org/folder policy, you get **4003** even though the tag is set.

**Linux username vs repo defaults:** New VMs often get **metadata / OS Login** keys for your Google identity, e.g. Linux user **`jason_redhawk`**, while these scripts default to **`jason`**. That mismatch does **not** cause 4003 (that is before SSH auth), but it **will** cause **Permission denied** once TCP works. Set environment variables before deploy: **`TITANORBIT_GCE_SSH_USER`** (e.g. `jason_redhawk`) for upload, and **`TITANORBIT_GCE_INSTANCE_TARGET`** (e.g. `jason_redhawk@titanorbitcp`) for restart/install PowerShell scripts, **or** create a Linux user **`jason`** and add your `google_compute_engine` public key for that user.

**Convenience (PowerShell, from `tools\gce`):** **`deploy_identity_google_user.ps1`** sets those env vars for **`jason_redhawk@titanorbitcp`** (override with `-LinuxUser` / `-InstanceName`) and runs **`deploy_server_gce_iap.bat`**. **`deploy_identity_repo_default.ps1`** clears the overrides and deploys as **`jason`** (create that user on the VM first if needed).

**Fix A — Google Cloud Console (no scripts)**

1. Open **VPC network → Firewall** (or search “Firewall” in the top search box).
2. Click **CREATE FIREWALL RULE**.
   - **Name:** e.g. `allow-iap-ssh-ingress` (any unique name).
   - **Network:** choose the **same VPC** your VM uses (often **`default`** — check the VM’s **VPC network** column on the instances list).
   - **Targets:** **Specified target tags** → tag: **`allow-iap-ssh`** (exact spelling).
   - **Source IPv4 ranges:** **`35.235.240.0/20`** (Google’s IAP TCP forwarding range — do not change this).
   - **Protocols and ports:** **Specified protocols and ports** → **`tcp:22`**.
3. Click **CREATE**.
4. Go back to **Compute Engine → VM instances** → select your VM → **EDIT** (pencil) → **Networking** section → **Network tags** → add **`allow-iap-ssh`** → **SAVE**.
5. Wait **1–2 minutes**, then open **SSH** again (try a **new** browser tab if the old one is stuck).

**Fix B — From your PC or Cloud Shell**

From `tools\gce` run **`add_iap_ssh_firewall_and_tag.bat`** (it creates **`iap-allow-ssh-<your-vpc-name>`** and adds the tag), or run the `gcloud compute firewall-rules create …` block under [IAP tunnel error 4003](#iap-tunnel-error-4003--failed-to-connect-to-port-22) (adjust **`--network=`** to match your VM’s VPC).

**From Cloud Shell only:** upload **`add_iap_ssh_firewall_and_tag_cloudshell.sh`** from the same folder, then **`bash add_iap_ssh_firewall_and_tag_cloudshell.sh`** (optional args: `PROJECT_ID ZONE INSTANCE`). It mirrors the Windows script: reads the VM’s **real VPC**, creates **`iap-allow-ssh-<vpc>`**, adds **`allow-iap-ssh`**. Wait ~60 seconds, then rerun **`cloudshell_install_titanorbit_unit.sh`** or use **SSH** in the Console.

**Fix C — Bypass IAP (only if the VM has a public IP and allows SSH from the internet)**

In the SSH window, use **any “connect without IAP” / “different connection”** option if the UI offers it, **or** use **External IP** SSH from your own machine once **`default-allow-ssh`** (or similar) allows **`0.0.0.0/0` → tcp:22** to that VM. If the VM has **no external IP**, IAP is the normal path — use Fix A or B.

**If it still fails after the firewall + tag:** confirm the image is a normal Linux VM with **`sshd`** on port **22** (default **Debian/Ubuntu** GCE images do). Custom or container-optimized images may need SSH installed or a different port. On the VM, **`sshd`** should listen on **`0.0.0.0:22`** (not only `127.0.0.1`). Grant your Google account **`roles/iap.tunnelResourceAccessor`** on the project if Console SSH still says IAP failed. For **`gcloud compute start-iap-tunnel`** from your PC, this repo’s **`install_unit_remote.ps1`** passes **`--iap-tunnel-disable-connection-check`** so a flaky pre-flight probe does not block the tunnel after the firewall is correct.

**If Windows `install_enable_server_service_on_gce.bat useIap` reaches “Running ssh.exe” but fails with `kex_exchange_identification` / connection reset:** the IAP tunnel is usually fine; **`ssh.exe` → localhost → IAP** on your PC is the weak link (OS Login, keys, or endpoint software). Run **`write_cloudshell_install_unit_script.bat`**, open **Cloud Shell**, upload **`cloudshell_install_titanorbit_unit.sh`**, then **`bash cloudshell_install_titanorbit_unit.sh`** — that runs the same install using **Google’s Linux `gcloud` + OpenSSH**.

**If Cloud Shell then reports IAP `4003` / failed to connect to port 22:** that is **not** a Windows problem — the VM still does not allow **IAP’s range** to **tcp:22** with your instance’s **network tag**. Run **`add_iap_ssh_firewall_and_tag_cloudshell.sh`** in Cloud Shell (or **`add_iap_ssh_firewall_and_tag.bat`** on your PC), wait a minute, and try again. Until browser **SSH** works, Cloud Shell **`gcloud compute ssh --tunnel-through-iap`** will keep failing the same way.

## Web client vs dedicated server — do you need two VMs?

**Usually no.** In this repo’s intended layout:

| Piece | Where it runs |
|--------|----------------|
| **WebGL game (browser clients)** | **Google Cloud Storage** (and DNS / Cloudflare), via **`tools/gcs/`** — not on the same VM as the headless server. |
| **Dedicated Linux game server** | **Compute Engine**, e.g. **`titanorbitcp`**, via **`tools/gce/`**. |

So **`titanorbitcp`** is for the **authoritative headless server**, not for hosting the static WebGL build (unless you deliberately put nginx on that VM too — that would be your own choice, not what these scripts assume).

You add a **second** VM only if you want a separate test server, more capacity, or isolation — not because the browser game “needs its own VM” by default.

## Defaults in this repo

| Setting | Value |
|--------|--------|
| GCP project | **`titan-orbit`** (hard-coded default in the `.bat` files; pass an override as the project argument if needed) |
| Instance | `titanorbitcp` |
| Zone | `us-central1-f` |
| Remote user | `jason` |
| Upload target | `/home/jason/titanorbit-server/` (archive extracts to a folder named like your local build folder) |
| Linux build folder (upload default) | `<repo>\BuildOutput\Server\TitanOrbitLinux1` (from Unity menu **TitanOrbit → Build → Headless Server (Linux — Google Cloud)**) |

Override the folder by passing it as the first argument to each script, or edit the `for %%I` / `SOURCE_DIR=` block at the top of `upload_linux_build_to_gce.bat`.

## Prerequisites

1. Install **Google Cloud SDK** and ensure `gcloud` is on your `PATH`.
2. `gcloud auth login` and `gcloud config set project YOUR_PROJECT` (or pass project id as the 2nd argument).
3. **First time on the VM:** either use **browser SSH** in the Console to install **`titanorbit-server.service`**, or run `install_enable_server_service_on_gce.bat` (or **`..._iap.bat`** / `useIap`) **if** Windows SSH works. If Windows SSH is broken, use **[Recommended: GCS + Cloud Shell](#recommended-dedicated-server-without-windows-ssh-gcs--cloud-shell)** to put the build on the VM, then manage **systemd** from **browser SSH** only.

## Windows first-time setup (plain steps)

The **`install_enable_server_service_on_gce.bat useIap`** path (and `install_unit_remote.ps1` with **IAP**) needs two things on your PC: **OpenSSH Client**, and a **small key file** Google creates the first time you SSH successfully. You only do this once per machine (unless you reinstall Windows).

### Part A — Install “OpenSSH Client” (the `ssh` program)

1. Press the **Windows** key, type **optional features**, and open **“Optional features”** (or **“Add an optional feature”**, depending on your Windows version).
2. Click **“View features”** (or **“Add a feature”**).
3. Search for **OpenSSH Client**.
4. Select **OpenSSH Client** and install it.
5. **Close** any open PowerShell or Command Prompt windows and **open a new** PowerShell.
6. Type **`ssh`** and press **Enter**.  
   - **Good:** you see help text starting with `usage: ssh ...`.  
   - **Bad:** `ssh` is not recognized → reboot once, or confirm the feature finished installing, then try again.

### Part B — Get the SSH key file the install script needs

**What this is:** The install script looks for a private key file on your PC named **`google_compute_engine`**, next to a **`.pub`** file. Same idea as a house key and a spare tag — the **`.pub`** line is what you may paste into Google Cloud so the VM trusts your PC.

**Where it lives:** `C:\Users\<YOUR_WINDOWS_NAME>\.ssh\google_compute_engine`  
To find `<YOUR_WINDOWS_NAME>`: open PowerShell, run **`echo $env:USERNAME`**, use that name under `C:\Users\`. Turn on **hidden files** in File Explorer if you do not see the **`.ssh`** folder.

#### If **`ssh.exe`** says **UNPROTECTED PRIVATE KEY** / **OWNER RIGHTS (S-1-3-4)** / **Permissions are too open**

Windows often adds inherited ACLs OpenSSH rejects. From **`tools\gce`** run **`fix_google_compute_engine_key_acl.bat`** (or **`create_local_gce_ssh_key.bat`** again — it runs the same ACL step at the end). Then retry **`restart_titanorbit_server_on_gce_iap.bat`** or **`install_enable_server_service_on_gce_iap.bat`**.

---

#### If you already tried `gcloud compute ssh ...` and saw **“Remote side unexpectedly closed network connection”**

That message is almost always **Windows Google Cloud Tools using PuTTY (`plink`)**, not “you did something wrong.” **Browser SSH in the Console can still work** while this popup happens on your PC.

Do this **in order**:

1. **Check whether the key file was created anyway.**  
   Open **`C:\Users\<YOUR_WINDOWS_NAME>\.ssh`**.  
   - If you see **`google_compute_engine`** (no extension): **skip to the end of Part B** and run **`install_enable_server_service_on_gce_iap.bat`**. The install path uses **`ssh.exe` + an IAP tunnel**, not `plink`, so you often do **not** need a working `gcloud compute ssh`.
   - If **`google_compute_engine` is missing**, continue with step 2.

2. **Create the key on your PC and tell Google to trust it (no `gcloud compute ssh` required).**  
   - In File Explorer, open your repo folder, then **`Titan Orbit\tools\gce`**.  
   - Double‑click **`create_local_gce_ssh_key.bat`** (or in PowerShell:  
     `powershell -NoProfile -File "...\tools\gce\create_local_gce_ssh_key.ps1"`).  
   - It creates **`google_compute_engine`** if needed and copies **one line** to your clipboard (starts with **`jason:`** if you use the default Linux user from this repo).

3. **Paste that line into Google Cloud (web):**  
   - Open [Compute Engine → VM instances](https://console.cloud.google.com/compute/instances).  
   - **Either** click **Metadata** in the left menu → **SSH keys** tab → **EDIT** → **Add item** → paste → **Save**.  
   - **Or** open **your VM** → **EDIT** → **SSH Keys** → **Add item** → paste → **Save**.  
   Use **one** place (project or VM), not both twice with the same key, unless you know you need both.

4. Wait **~30 seconds**, then run **`install_enable_server_service_on_gce_iap.bat`** from **`tools\gce`**.

If your Linux username on the VM is **not** `jason`, from **`tools\gce`** run:

```bat
create_local_gce_ssh_key.bat -LinuxUser YOUR_LINUX_NAME
```

---

#### Optional — try `gcloud compute ssh` only if you want a normal shell (may still pop up on Windows)

1. If you have never logged this PC into Google Cloud, run once: **`gcloud auth login`** and finish the browser sign-in.
2. Run (defaults for this repo):

   ```text
   gcloud compute ssh jason@titanorbitcp --project=titan-orbit --zone=us-central1-f --tunnel-through-iap
   ```

3. If you get a **Linux prompt**, type **`exit`**. If you only get the **“Remote side unexpectedly closed”** popup, **ignore it for our scripts** and use the **Part B** steps above (check for **`google_compute_engine`**, or run **`create_local_gce_ssh_key.bat`**).

---

After Part A and Part B, run **`install_enable_server_service_on_gce_iap.bat`** (or **`install_enable_server_service_on_gce.bat useIap`**) from the **`tools\gce`** folder.

## Scripts

| Script | Purpose |
|--------|--------|
| `upload_linux_build_to_gcs.bat` | Tar local folder → **`gcloud storage cp`** to **`gs://…/titanorbit-linux-build/…-latest.tar.gz`** (no SSH). Pair with **Cloud Shell** commands in README. |
| `upload_linux_build_to_gce.bat` | **OpenSSH only:** `upload_linux_build_to_gce_openssh.ps1` — `tar` → `scp`/`ssh` + optional **`gcloud start-iap-tunnel`** (no `gcloud compute ssh` / PuTTY plink). Requires **ssh.exe**, **scp.exe**, **gcloud**. |
| `upload_linux_build_to_gce_iap.bat` | Same as upload with **`-UseIap`** (IAP tunnel for VMs without a public IP or when direct SSH is blocked). |
| `restart_titanorbit_server_on_gce.bat` / `_iap.bat` | **`restart_server_remote.ps1`**: non-IAP uses **base64-in-`gcloud compute ssh --command`**; **`useIap`** / **`_iap.bat`** use **`start-iap-tunnel` + `ssh.exe`**, then **auto-fallback** to plain `gcloud compute ssh` if IAP fails. On Windows, if **`cmd`** shows **Terminate batch job** or **plink** hangs, use **`restart_titanorbit_server.ps1`** from **PowerShell** instead of the `.bat`. |
| `restart_titanorbit_server.ps1` | Windows **PowerShell** entry point for **`restart_server_remote.ps1`** (same args; avoids **`cmd.exe`** + PuTTY quirks). Example: **`.\restart_titanorbit_server.ps1`** or **`.\restart_titanorbit_server.ps1 -UseIap`**. |
| `cloudshell_restart_titanorbit_server.sh` | Run **in Cloud Shell**: restarts **`titanorbit-server` on your GCE VM** via **`gcloud compute ssh … --command`**. Do **not** run bare **`sudo systemctl …`** in Cloud Shell — that is **not** your VM (you will see *“not been booted with systemd as init system”*). |
| `deploy_server_gce.bat` | Runs **upload**, then **restart** (main one-step publish). |
| `deploy_server_gce_iap.bat` | Same as deploy, always uses IAP (default paths / project only). |
| `prepare_and_start_server_on_gce.bat` | Manual chmod + foreground run (debug). |
| `install_enable_server_service_on_gce.bat` | Uses **`install_unit_remote.ps1`**: **`gcloud compute ssh --command`** runs a VM-side **`bash -lc`** that **base64-decodes** an embedded install script then runs **`bash -s`**, so the script is **not** on stdin (plink/gcloud otherwise read stdin for **Y/n** and remote bash can error with **`y: command not found`**). **No `gcloud compute scp` / pscp**. **PuTTY `plink`** may prompt to cache the **host key** (type **y** once, or use **`install_enable_server_service_on_gce_iap.bat`**). **Fresh VM:** creates **`/home/jason/titanorbit-server/TitanOrbitLinux1`** and installs the unit **before** the Linux build exists (**`systemctl start`** after you upload). |
| `add_iap_ssh_firewall_and_tag.bat` | Calls **`add_iap_ssh_firewall_and_tag.ps1`**: reads the VM’s **actual VPC**, creates **`iap-allow-ssh-<vpc>`** (TCP 22 from **`35.235.240.0/20`**) + adds **`allow-iap-ssh`** (fixes IAP **4003** when a rule on **`default`** did not match the VM’s network). |
| `add_iap_ssh_firewall_and_tag_cloudshell.sh` | Same logic for **Google Cloud Shell** (bash + `gcloud`): upload and run **`bash add_iap_ssh_firewall_and_tag_cloudshell.sh`** when **`cloudshell_install_titanorbit_unit.sh`** or Console SSH returns **4003**. Optional: **`TITANORBIT_IAP_SSH_ALL_VMS=1`** (IAP→22 for all VMs on the VPC). Optional: **`TITANORBIT_IAP_SSH_PRIORITY0=1`** (same as tagged or all‑VMs path, but **`--priority=0`** so it wins over lower‑precedence DENY rules). Source is always **`35.235.240.0/20`**. |
| `guest_network_recovery_startup.sh` | **Optional** one-time **metadata `startup-script`** when serial is unusable (log flood) and the guest cannot reach **`169.254.169.254`**. Copy to your PC, then: **`gcloud compute instances add-metadata INSTANCE --zone=ZONE --metadata-from-file=startup-script=guest_network_recovery_startup.sh`** and **`gcloud compute instances reset`**. Edit **`GW=`** inside the script if your internal subnet is not **`10.128.0.0/20`**. Remove the metadata key after recovery. |
| `reset_gce_vm.bat` | **Hard reboot** via **`gcloud compute instances reset`** (defaults **`titanorbitcp`**, **`titan-orbit`**, **`us-central1-f`**). Optional args: **instance**, **project**, **zone**. Use when SSH/IAP fail from guest-side issues; pair with **`guest_network_recovery_startup.sh`** if metadata recovery is needed first. |
| `diagnose_iap_ssh_cloudshell.sh` | In Cloud Shell: prints **v3** marker, **effective firewalls**, **metadata**, **network firewall policies**, and VPC rules. Use when **4003** remains. After upload, confirm **`head -6 diagnose_iap_ssh_cloudshell.sh`** includes **`TITANORBIT_IAP_DIAG_FILE=v3`**; if the run jumps from Tags straight to VPC rules, you are not executing this file (wrong path or stale copy). |
| `install_unit_remote.ps1` | With **IAP**, uses **`gcloud compute start-iap-tunnel`** + **`ssh.exe`** + remote **`bash -lc`** that **base64-decodes** the install script (same pattern as non-IAP; avoids **stdin pipe truncation** to `bash -s` on Windows). Non-IAP uses **`gcloud compute ssh`** with the **base64-in-`--command`** path above (not stdin). |
| `write_cloudshell_install_unit_script.bat` / `.ps1` | Writes **`cloudshell_install_titanorbit_unit.sh`**: run **that script in Cloud Shell** to install the unit via **`gcloud compute ssh … --tunnel-through-iap`** on **Linux** (use when Windows **`ssh.exe`** fails with **kex / connection reset** through IAP). |
| `install_enable_server_service_on_gce_iap.bat` | Same, always **`--tunnel-through-iap`** (use if plain install times out on Windows). |
| `titanorbit-server.service` | **Source** unit file next to the install script (edit `User=` / paths / `ExecStart=` if your VM layout differs). |
| `open_gce_shell.bat` | Open SSH shell. |
| `create_local_gce_ssh_key.bat` / `create_local_gce_ssh_key.ps1` | Creates **`%USERPROFILE%\.ssh\google_compute_engine`** and copies a **`username:ssh-ed25519 …`** line for Cloud Console when **`gcloud compute ssh`** fails with the PuTTY “remote closed” popup. Runs **`fix_google_compute_engine_key_acl.ps1`** at the end so **`ssh.exe`** accepts the key. |
| `fix_google_compute_engine_key_acl.bat` / `.ps1` | **`icacls`** on **`google_compute_engine`**: drop inheritance, grant only your user **Read**, remove **OWNER RIGHTS** when present. Use when **`ssh.exe`** reports **bad permissions** / **UNPROTECTED PRIVATE KEY**. |

## Cloud Shell: restart `systemd` **on the VM**, not inside Cloud Shell

Cloud Shell is its **own** Linux environment. Running **`sudo systemctl restart titanorbit-server`** there only talks to **that** container (often: *“System has not been booted with systemd as init system”*).

To restart the service **on your Compute Engine instance**, SSH in with **`gcloud`** and run the command **remotely**:

1. Upload **`cloudshell_restart_titanorbit_server.sh`** from this folder (or paste its contents), then in Cloud Shell:

   ```bash
   bash cloudshell_restart_titanorbit_server.sh
   ```

2. Or a one-liner (adjust project / zone / user@instance if yours differ):

   ```bash
   gcloud compute ssh jason@titanorbitcp --project=titan-orbit --zone=us-central1-f --tunnel-through-iap --command='sudo systemctl restart titanorbit-server && sudo systemctl is-active titanorbit-server'
   ```

Same idea for **status** / **logs**: use **`gcloud compute ssh … --command='…'`**, or **Console → Compute Engine → your VM → SSH** in the browser.

### Usage

From a shell:

```bat
cd path\to\Titan Orbit\tools\gce
deploy_server_gce.bat
```

With overrides:

```bat
deploy_server_gce.bat "D:\Builds\TitanOrbitLinux1" my-gcp-project-id
deploy_server_gce.bat "D:\Builds\TitanOrbitLinux1" my-gcp-project-id useIap
```

## SSH from Windows — timeouts, plink, IAP (“Remote side closed”)

**IAP is not required** to run a dedicated server. It is only an optional way to reach SSH when **direct** SSH from your PC is flaky.

What you are seeing is usually **two separate layers**:

1. **Upload script (`upload_linux_build_to_gce.bat`):** uses **OpenSSH** (`ssh.exe`/`scp.exe`), not plink. If upload still fails, use **`gcloud compute ssh … --troubleshoot`** for IAP/firewall diagnosis, or the **GCS + Cloud Shell** path above.

2. **With IAP** (`--tunnel-through-iap`): traffic goes through Google’s tunnel. A **“Remote side unexpectedly closed”** popup usually means the tunnel got further but the **SSH session** then failed (wrong Linux username, `sshd` / OS Login / host key, or IAP IAM not set up). That is **not** the same bug as (1); fixing it means checking **VM login user** and **IAP + IAM**, not “turn off IAP to fix the server.”

### Get the dedicated server working without fighting IAP first

Do these in order:

1. **Confirm the VM is running** in [Compute Engine → VM instances](https://console.cloud.google.com/compute/instances) (project **titan-orbit**).

2. **SSH from the browser:** open the VM row → **SSH**. That path usually uses **IAP**. If you see **4003** / **Connection via IAP failed**, fix **IAP → port 22** (firewall **`35.235.240.0/20`** + tag **`allow-iap-ssh`**) as in **[Browser SSH and IAP error 4003](#browser-ssh-and-iap-error-4003)** above — it is **not** the same as “local plink on Windows.”  
   - If browser SSH **works** after that, use it (or **Cloud Shell**) to manage the VM even when Windows `gcloud` SSH is flaky.  
   - If it **still** fails, check **VPC / `sshd` / OS Login** and that the VM’s **VPC** matches the firewall rule’s **network**. The `.bat` files use **`REMOTE_USER=jason`** — change scripts if your Linux user differs.

3. **VPC firewall:** allow **tcp:22** to the instance (e.g. default `default-allow-ssh` or equivalent) if you rely on **public IP** SSH.

4. **Optional — IAP later:** only after (2) works or you explicitly need it, enable [IAP for Compute Engine](https://cloud.google.com/iap/docs/enabling-compute-howto) and grant **`roles/iap.tunnelResourceAccessor`** (and VM login roles). Then **`upload_linux_build_to_gce_iap.bat`** / **`useIap`** can help from restrictive networks.

### IAP tunnel error **4003** / **“Failed to connect to port 22”**

That message means **Google’s IAP tunnel reached your project, but nothing accepted SSH on port 22 through the path IAP uses**. It is usually a **VPC firewall** gap, not your Windows key.

**Easiest:** from **`tools\gce`** run **`add_iap_ssh_firewall_and_tag.bat`** (optional: `... YOUR_PROJECT_ID` then `YOUR_VPC_NAME` to override auto-detect). The script **detects which VPC the VM is on** and creates **`iap-allow-ssh-<vpc>`** so the rule is not silently created on the wrong network. Wait about a minute, then **`install_enable_server_service_on_gce.bat useIap`**.

**Manual:** do this once per VPC (replace **`default`** if your VM uses another network; replace **`titan-orbit`** if your project id differs):

```bat
gcloud compute firewall-rules create allow-ssh-ingress-from-iap --project=titan-orbit --network=default --direction=INGRESS --action=ALLOW --rules=tcp:22 --source-ranges=35.235.240.0/20 --target-tags=allow-iap-ssh
```

If the rule already exists, `gcloud` will say so; that is fine.

Then in [Compute Engine → VM instances](https://console.cloud.google.com/compute/instances): open your VM → **EDIT** → **Network tags** → add **`allow-iap-ssh`** → **Save**. Wait about a minute, then run **`install_enable_server_service_on_gce_iap.bat`** again.

**4003 still after `add_iap_ssh_firewall_and_tag_cloudshell.sh`?**

1. In Cloud Shell run **`bash diagnose_iap_ssh_cloudshell.sh`** (upload from `tools/gce/`). Confirm the VM is **RUNNING**, **`allow-iap-ssh`** appears under tags, and some rule shows **SRC** including **`35.235.240.0/20`** and **ALLOW** including **tcp:22** for that VPC. If the script warns about **Shared VPC** (subnetwork in another project), create the same IAP firewall rule in the **host** project on the shared network — rules only in the service project will not fix 4003.
2. Run **`gcloud compute ssh titanorbitcp --project=titan-orbit --zone=us-central1-f --troubleshoot --tunnel-through-iap`** and read the printed checks (firewall path, IAP API).
3. Broaden allow (still IAP-only): **`TITANORBIT_IAP_SSH_ALL_VMS=1 bash add_iap_ssh_firewall_and_tag_cloudshell.sh`** — adds **`iap-allow-ssh-allvms-<vpc>`** with **no target tags** so tag mismatch cannot block IAP. Source range is unchanged (**Google IAP only**).
4. **DENY wins over ALLOW** when it has **better (numerically lower) priority**. If **`diagnose_iap_ssh_cloudshell.sh`** shows an ingress **DENY** for port 22 before your IAP **ALLOW**, add an IAP allow at **priority 0**: **`TITANORBIT_IAP_SSH_PRIORITY0=1 bash add_iap_ssh_firewall_and_tag_cloudshell.sh`** (optionally combine with **`TITANORBIT_IAP_SSH_ALL_VMS=1`**). Wait ~60s, retry SSH.
5. On the VM (**serial console** if needed: enable in VM **EDIT**, then connect): confirm **`sshd`** listens on **`0.0.0.0:22`** (`sudo ss -tlnp | grep :22`). If the OS image has no SSH server, install and enable it.
6. **Organization / folder firewall policies** can block port 22 even when project VPC rules look correct — your org admin must adjust those policies.

**`--troubleshoot` reports REACHABLE but plain `gcloud compute ssh --tunnel-through-iap` still returns 4003:** The Network Intelligence probe can still show **REACHABLE** when routing and VPC firewall allow the path, while IAP’s **4003** means the **actual TCP connect to port 22 on the VM** failed (nothing listening, **RST**, guest firewall **DROP**, or similar). Treat that as a **guest OS / sshd** problem, not a missing GCP firewall rule.

**Serial / guest logs show `169.254.169.254: network is unreachable`:** On Compute Engine, **`169.254.169.254`** is the **metadata server** (instance config, DNS via the guest path, guest agent, OS Config). If the VM cannot reach it, **guest networking is broken** (missing **default route**, wrong interface, or **iptables/nftables** blocking). That often matches **SSH timeouts**, **IAP 4003**, and **`google-guest-agent-manager` / `OSConfigAgent` crash loops**. Fix from **serial**: **`ip route`**, **`ip addr`**, **`ping 169.254.169.254`**, restore default via **`10.128.0.1`** (or your subnet’s gateway) on the primary NIC, and clear OUTPUT drops. Wrong fixes on VPC firewall alone will not help.

**When `firewall-rules list` already shows IAP → tcp:22 + tag, but `gcloud compute ssh --tunnel-through-iap` still returns 4003:** GCP is still not getting a successful TCP connection to **port 22 on the guest**. Pull the latest **`diagnose_iap_ssh_cloudshell.sh`** and run it again — it prints **`get-effective-firewalls`** (org/folder DENY), **metadata** (OS Login / block SSH keys / startup scripts), and **network firewall policies**. Then use **serial console** (enable **connect to serial ports** on the VM, then **`gcloud compute connect-to-serial-port`**) to run **`sudo ss -tlnp | grep :22`** and **`sudo systemctl status ssh sshd`**. Repair or reinstall **`openssh-server`**, clear **ufw/iptables** blocks on 22, or **recreate the VM** from a stock Debian/Ubuntu image if the disk was customized.

Official reference: [IAP TCP forwarding — create firewall rule](https://cloud.google.com/iap/docs/using-tcp-forwarding#create-firewall-rule).

### **`'base64' is not recognized`** when running install **without** `useIap`

That was Windows accidentally trying to run **`base64`** locally. The non-IAP install path puts **`base64 -d | bash -s`** inside the remote **`bash -lc '...'`** from **`--command`**, so decoding runs **on the Linux VM**, not on Windows.

### **“Unknown option -s”** (PuTTY popup) when running install **without** `useIap`

Windows **`gcloud compute ssh`** uses **PuTTY `plink`**. Passing **`bash -s` after `--`** made plink treat **`-s`** as its own flag. The script uses **`--command=bash -s`** instead so **`-s` is part of the remote command**, not a plink option.

**Other local fixes:** exclude **`plink.exe`** (under your Cloud SDK) from antivirus; run the same **`gcloud`** from **WSL**; try another network.

## Related

- **WebGL** static hosting: `tools/gcs/README.md`
- Headless server boot: `Assets/Scripts/Networking/DedicatedMatchServerBootstrap.cs`
