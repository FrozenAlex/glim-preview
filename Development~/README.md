# Glim Live Preview add-on development

This repository root is the Unity package. Install it as `Packages/com.frozenalex.glim-preview`.
Supported upstream: Glim **0.12.0**, revision
`1c405dffb33ab131d501f5cc45d3ee68dc098ce0`.

The managed source lives in `Editor/`, under its own namespace and
assembly. It reuses Glim's runtime settings and public editor utilities. Its
preview lifecycle and native bridge are independent of the upstream bake path.

`native-preview-0.12.patch` applies to the pinned upstream revision and contains
the native live-update, embedded HDR preview, diagnostics support, and OIDN work.
The port uses Glim 0.12's vertex layout, normalized UVs, visibility-buffer format,
zero-radius lights, and group-before-mesh export order.

The camera push payload packs six vectors as 18 scalar floats (72 bytes). This
reserves 48 bytes for upstream ray-tracing addresses and 8 bytes for resolution,
fitting the portable 128-byte limit when that later port happens. The current
upstream revision remains pinned above. Preview lighting uses 40 bytes and the
denoising capture uses 16 bytes. Shader addresses use typed pointers, without a
`shaderInt64` requirement; Rust still stores Vulkan device addresses as 64-bit
host values. Unit tests inspect compiled SPIR-V sizes, offsets, and capabilities
so padding changes or accidental `Int64` requirements fail CI.

Hardware and software ray tracing use separate compiled shader variants. The
software variant omits `TopLevelAS` and ray-query code entirely, matching its
descriptor layout and avoiding hardware-ray-tracing shader requirements. CI
checks this in the generated SPIR-V for every software shader.

## Build

With Rust and `slangc` on PATH, run the same script on Windows x64 (MSVC)
or Linux x64 (GCC, CMake and PowerShell 7). On Ubuntu, install the GLFW build
dependencies first:

```bash
sudo apt-get install cmake build-essential libwayland-dev libxi-dev libxinerama-dev libxkbcommon-dev libxrandr-dev libxcursor-dev
```

```powershell
./Development~/build-native.ps1 -GpuTests
```

The script creates a new source checkout, applies the patch, runs the unit and
optional GPU tests, and writes `glim_preview_addon_012_v1.dll` (Windows) or
`glim_preview_addon_012_v1.so` (Linux) to `dist` at the repository root. Copy the
result into `Editor/` with Unity closed. Preserve its platform-specific `.meta`
file. The upstream native library must remain unchanged.

For GPU tests with Khronos validation installed, set `GLIM_PREVIEW_VALIDATE=1`
before running the build script with `-GpuTests`. `GLIM_PREVIEW_GPU_MODE=hardware`
or `software` selects one ray-tracing path; leaving it unset tests both. Validation
errors fail the test. Both paths pass with validation enabled; see `VALIDATION.md`.

Native libraries are not tracked in Git; their `.meta` files are tracked. For a new source
checkout, close Unity and use `-OutputDirectory ./Editor` to install the build
directly. Release ZIPs include the native libraries built by GitHub Actions.

## Verify Scene View behavior

```powershell
./Development~/validate-unity.ps1
```

This creates an isolated project containing both packages and PPv2, then runs the
actual Scene View regression. `-UnityProject` selects the host Unity project when this checkout is outside its Packages folder. `-AddonPackage` can select a staged add-on.
The command returns the test process, project, log, and result paths. The harness
exits Unity when complete. It covers live albedo and emission, HDR, scene desaturation,
False Color, Highlights, denoising, transforms/Undo, restart behavior, and baked UV
and temporary-renderer preservation. It does not open or save the host scene.

To check Lightmapper selection across Glim's temporary bake-scene replacement:

```powershell
./Development~/validate-unity.ps1 -Validation LightmapperSelectionValidation
```

This closes and reopens an isolated source scene three times, verifies the exact
selected component is restored even with multiple/inactive Lightmappers, rejects
a competing component in the temporary scene, and preserves explicit clearing.

The package README documents the supported workflow and remaining live-update
limits. This add-on replaces the earlier in-place Glim 0.11 preview extension.

## Release workflow

`.github/workflows/release.yml` builds Windows x64 on Windows 2022 and Linux x64
on Ubuntu 22.04, matching the platforms shipped by upstream. Both use Rust 1.97.1
and Slang 2026.14 (SHA-256 verified), apply the same patch to the pinned upstream
revision, run native unit tests, and load the resulting library to check every
Unity bridge export. No GPU is initialized by the export check. GPU and Unity visual tests
still require the local commands above; GitHub's hosted runner has no suitable GPU.

`package_release.py` packages only the installable files plus third-party notices.
The ZIP has `package.json` at its root and contains both freshly built libraries, preserving
the checked-in Unity metadata. The release's external `package.json` and `SHA256SUMS`
contain the ZIP hash; the manifest inside the ZIP omits the hash.

To check packaging locally after building:

```powershell
python -m unittest discover -s Development~ -p test_package_release.py -v
python Development~/package_release.py --native-dll dist/glim_preview_addon_012_v1.dll --native-so dist/glim_preview_addon_012_v1.so --output dist/package --tag v0.1.4
```

For a new release, change the manifest version and push that commit. Run **Build
Release** with **publish** enabled, or push `v<version>`; a tag that disagrees with
the manifest is rejected. Manual runs default to build-only. Published versions
are never overwritten. If uploading fails and leaves a draft, delete that draft
before retrying. The publisher creates a draft, uploads every asset, and only then
makes it public.

After packaging, `actions/attest` generates SLSA build provenance for the ZIP,
external manifest, checksum file, and both freshly built native libraries. The action is pinned to
a commit and uses GitHub OIDC; no signing key secret is required. Only the packaging
job receives `id-token: write` and `attestations: write`. The Sigstore bundle is
included as `provenance.sigstore.json` in the workflow artifact and release.
Both the build and publisher verify the signatures, repository, signer workflow,
source commit, and GitHub-hosted runner identity before proceeding. Build-only
runs generate attestations too; existing releases are not retroactively signed.

`FrozenAlex/vpm` tracks `FrozenAlex/glim-preview` through its `source.json`.
Its listing workflow refreshes hourly, on source changes, or manually. Scheduled
runs can be delayed by GitHub, and inactive public repositories can have schedules
disabled after 60 days. No cross-repository token or extra repository secret is
required. After publishing, run **Build Repo Listing** there for an immediate update.

## Live albedo updates

`PreviewMaterials` caches each shader's base-color/base-texture property IDs and
compares their values separately from emission's material CRC. Material dirty
counts and emission flags do not invalidate albedo. Non-emissive materials are
excluded from the emission signature. One shared 0.35-second scanner schedules
at most one atlas readback at a time, coalescing rapid edits without increasing
the existing transfer concurrency. Geometry validity is checked only for dirty
captures, rather than on every idle scan.

`app_update_preview_albedo` validates the group and buffer size, copies the latest
snapshot into a bounded mailbox, and ignores identical pixel fingerprints. The
renderer updates the existing albedo image using the same staging/upload barrier
path as emission, resets accumulation, and invalidates denoised output. Initial
atlas fingerprints avoid redundant uploads without retaining full CPU textures.
The new native export requires rebuilding the add-on library with this patch.

The Unity regression covers live color, Undo, base-texture edits, and zero atlas
captures while idle; it also asserts emission edits do not recapture albedo and
non-emissive color edits do not recapture emission. The GPU regression verifies
reflected color and denoising on both ray-tracing paths without changing the camera.


## Live skybox updates

`PreviewSkybox` separately tracks the scene skybox material/texture inputs and
`RenderSettings.ambientIntensity`. The initial export is unchanged. Subsequent
cubemap captures render only the sky, read all six faces asynchronously, and queue
one latest snapshot for the render thread. Capture resources are disposed on stop;
non-finite HDR captures are discarded so a later corrected edit can be retried.
The built-in procedural sky also observes its explicit or automatic directional sun.

The renderer waits for in-flight work before replacing the cubemap, updates all
cached preview descriptors, and resets accumulation/denoising. Intensity is an
existing specialization constant: it recreates only the compute pipelines while
reusing modules, layouts, and descriptor sets, avoiding exhaustion of upstream's
64-set pool. Shader layouts, push-constant sizes, and Vulkan feature requirements
remain unchanged. The skybox transfer barrier covers compute sampling as well as
fragment sampling.

Run the focused Unity regression with:

```powershell
./Development~/validate-unity.ps1 -Validation SkyboxValidation
```

It compares all six asynchronous faces against the initial export, then checks
material/exposure/Undo/texture/resolution/removal/sun edits, idle and intensity-only
capture exclusion, geometry preservation, and stopping with a pending readback.
Native GPU tests cover lighting, background, OIDN, cubemap resizing, and 24 repeated
intensity updates on both ray-tracing paths.
