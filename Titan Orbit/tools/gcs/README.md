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

## Related repo files

- WebGL output path: `Assets/Editor/Build/TitanOrbitBuildAutomation.cs`
- VM/server upload scripts (Compute Engine): `tools/gce/`
