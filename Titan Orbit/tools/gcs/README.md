# GCS WebGL deploy (Google Cloud Storage)

Batch helpers sync the Unity **WebGL production** build to a bucket using the [Google Cloud CLI](https://cloud.google.com/sdk/docs/install) (`gcloud`).

## Defaults in this repo

The `.bat` files are preconfigured for:

| Setting | Value |
|--------|--------|
| GCP project | `titan-orbit` |
| Bucket | `titan-orbit-webgl` (create this in Cloud Storage if it does not exist; names are globally unique — change `BUCKET=` in both bats if the name is taken) |
| WebGL folder | `C:\Users\jason\Documents\Titan Orbit\Downloads\TitanOrbitWeb1` |

Override the folder by passing it as the first argument, or edit `SOURCE_DIR=` at the top of `upload_webgl_to_gcs.bat` and `set_webgl_gcs_metadata.bat`.

Standard Unity output (menu **TitanOrbit → Build → WebGL Production**) is:

`BuildOutput/WebGL/production/TitanOrbitWebGL`

(relative to the Unity project folder — the directory that contains `Assets`.)

## Prerequisites

1. Install **Google Cloud SDK** and ensure `gcloud` is on your `PATH`.
2. Authenticate and select a project (optional if `PROJECT_ID=` is already set in the bats):
   - `gcloud auth login`
   - `gcloud config set project titan-orbit`
3. Create a **GCS bucket** named **`titan-orbit-webgl`** (or change `BUCKET=` in both `.bat` files). Uniform bucket-level access is recommended.
4. Grant your account (or a deploy service account) permission to create/update/delete objects, e.g. **Storage Object Admin** on the bucket or project.

These scripts **only upload and set object metadata**. Making the site publicly readable, HTTPS, DNS (including **Cloudflare** for `titanorbit.io`), and optional **Cloud CDN / load balancing** are separate steps in Google Cloud Console.

## Scripts

| Script | Purpose |
|--------|---------|
| `upload_webgl_to_gcs.bat` | `gcloud storage rsync` local folder → `gs://BUCKET/` (recursive; deletes remote objects missing locally). |
| `set_webgl_gcs_metadata.bat` | Sets `Content-Type` and `Content-Encoding: br` on `.br` files (and types on uncompressed `.wasm` / `.js` / `.data` if present). Run after upload. |
| `deploy_webgl_gcs.bat` | Runs upload, then metadata. |

### Usage

From Explorer, double-click `deploy_webgl_gcs.bat` (defaults assume this repo layout), or from a shell:

```bat
cd path\to\Titan Orbit\tools\gcs
deploy_webgl_gcs.bat
```

Optional arguments:

```bat
deploy_webgl_gcs.bat "D:\path\to\TitanOrbitWebGL"
deploy_webgl_gcs.bat "D:\path\to\TitanOrbitWebGL" your-gcp-project-id
```

If `PROJECT_ID` is cleared in the scripts and you omit the second argument, the scripts fall back to `gcloud config get-value project`.

### Run order (manual)

1. Build WebGL in Unity.
2. `upload_webgl_to_gcs.bat`
3. `set_webgl_gcs_metadata.bat`

Or use `deploy_webgl_gcs.bat` for steps 2–3.

### If `gcloud storage rsync` fails

On very old SDK versions, try:

```bat
gsutil -m rsync -r -d "C:\path\to\TitanOrbitWebGL" gs://YOUR_BUCKET/
```

Then run `set_webgl_gcs_metadata.bat` so Brotli objects get the right headers.

## Serving the game

After upload, configure public access (e.g. IAM **allUsers** as **Storage Object Viewer** on the bucket for a simple static site), **bucket website** settings or an **HTTPS load balancer** with a managed certificate, and point your **Cloudflare** DNS records at the Google endpoint. Replicate any security headers you need (your Cloudflare Pages `_headers` file is not applied by GCS automatically).

### Root URL (`https://titanorbit.io/` without `index.html`)

GCS behind a load balancer does **not** automatically map `/` to `index.html`. Fix it in **Cloudflare** (easiest):

1. **Rules** → **Redirect Rules** → **Create rule**.
2. Example: **If** *Custom filter expression* → `(http.request.uri.path eq "/")` **Then** *Static* → **301** to `https://titanorbit.io/index.html` (adjust host to match your site).
3. Or use a **Rewrite** (Transform Rules) on paid plans to rewrite the path to `/index.html` without changing the address bar—optional.

After this, opening `https://titanorbit.io` loads the same game as `/index.html`.

## Troubleshooting: site won't load (WASM / data errors)

Chrome errors like:

- `LinkError: WebAssembly.instantiate(): Import #… "JS_SystemInfo_GetLanguage": function import requires a callable`
- `TitanOrbitWebGL.data.unityweb: net::ERR_HTTP2_PROTOCOL_ERROR`
- `[UnityCache] Failed to load … data.unityweb … network error`

usually mean **Build artifacts are mismatched or GCS is serving the wrong `Content-Encoding`** (not a game-code bug).

**Most common causes**

1. **`Content-Encoding: br` on files that are not Brotli-compressed on disk** (or the opposite). The browser then fails to decode `.data.unityweb` / `.framework.js.unityweb`, framework JS never runs, and WASM is missing imports like `JS_SystemInfo_GetLanguage`.
2. **Mixed deploy** — new `loader.js` + old `wasm`/`framework` from cache or a partial upload. Upload the **entire** `TitanOrbitWebGL` folder from one build in a single `deploy_webgl_gcs.bat` run.
3. **Wrong `SOURCE_DIR`** — `upload` uses `rsync --delete`; pointing at an empty or wrong folder can delete remote `Build\` files.
4. **Cloudflare double-compression** — if Cloudflare also compresses `/Build/*`, disable auto compression for those paths or bypass cache after deploy.

**Fix**

1. Rebuild WebGL once in Unity (full production build).
2. Preflight locally:
   ```powershell
   powershell -File "tools\gcs\verify_webgl_build.ps1" "BuildOutput\WebGL\production\TitanOrbitWebGL"
   ```
3. Redeploy (upload + metadata):
   ```bat
   deploy_webgl_gcs.bat "C:\Users\jason\Documents\repo\Titan-Orbit\Titan Orbit\BuildOutput\WebGL\production\TitanOrbitWebGL"
   ```
   `set_webgl_gcs_metadata.bat` now detects Brotli vs plain per file instead of forcing `br` on every `.unityweb`.
4. Purge **Cloudflare** cache for `titanorbit.io` (if used).
5. In Chrome: **Site settings → Clear data** for `titanorbit.io`, then hard reload.

**Verify in DevTools → Network** (reload once):

| File | Should have |
|------|-------------|
| `TitanOrbitWebGL.loader.js` | `Content-Type: application/javascript`, **no** `Content-Encoding` |
| `TitanOrbitWebGL.framework.js.unityweb` | `Content-Encoding: br` if the file is Brotli on disk (see verify script) |
| `TitanOrbitWebGL.wasm.unityweb` | `Content-Encoding: br` + `Content-Type: application/wasm` when Brotli |
| `TitanOrbitWebGL.data.unityweb` | `Content-Encoding: br` when Brotli |

If `data.unityweb` shows `br` but the download size looks like the raw compressed file size and the request fails, metadata is wrong — rerun step 3.

## Troubleshooting: ships/planets invisible after deploy

If the game runs but **ship hulls, planets, moons, and asteroids have no surface** (thrusters, bullets, and UI still look fine), the WebGL build often has **Crunch texture compression** enabled on gameplay albedos. That breaks in many browsers (not magenta—just empty-looking meshes).

**Fix (once per machine / after pulling this repo):**

1. In Unity: **TitanOrbit → Build → Fix WebGL Texture Import (disable Crunch)** (or build via **TitanOrbit → Build → WebGL Production**, which applies the same fix automatically).
2. Rebuild WebGL, then run `deploy_webgl_gcs.bat`.
3. In the browser: clear **site data** for `titanorbit.io` (or hard refresh) so Unity’s cached `.data` file is not mixed with an older build.

Also confirm **Build Settings → Web → Texture Compression** is **DXT** for desktop hosting (ASTC-only data on a desktop GPU shows the same symptom). The preprocess build script forces DXT when using the menu build.

## Related repo files

- WebGL output path: `Assets/Editor/Build/TitanOrbitBuildAutomation.cs`
- WebGL texture import fix: `Assets/Editor/Build/WebGLTextureImportBuildFix.cs`
- VM/server upload scripts (Compute Engine): `tools/gce/`
