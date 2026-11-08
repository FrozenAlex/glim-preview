# Glim Live Preview

Independent editor add-on for **Glim 0.12.x**, Unity 2022.3, Windows x64 and Linux x64.
These are the two platforms built by upstream Glim. Linux builds target Ubuntu
22.04 or newer (glibc 2.35+), with a Vulkan-capable graphics driver.
Optional Linux denoising needs `libOpenImageDenoise.so.2` on the library search path.
The add-on uses its own editor assembly and native library. It leaves Glim's
installed package, inspector, bake renderer, and scene components in place.

## Install with VCC

Add [FrozenAlex's package listing](https://frozenalex.com/vpm/) and
[z3y's listing](https://z3y.github.io/vpm-package-listing/) to Creator Companion.
Then add **Glim Live Preview** to your Unity 2022.3 project. The add-on requires
Glim 0.12.x from z3y's listing. The direct listing URL is
`https://frozenalex.com/vpm/index.json`.

## Open the preview

1. Open **Tools в†’ Glim в†’ Live Preview**, or use **Live Preview** in a Glim
   Lightmapper component's context menu.
2. Select the active scene's Glim Lightmapper. Its group, preview resolution,
   samples, bounce count, and ray-tracing settings are used. Save the scene first.
3. Click **Preview in Scene View**. Navigate the Scene camera normally.
4. Leave **Scene color correction / effects** enabled to use the Scene View's
   existing post-processing. The HDR preview is composited before image effects,
   including PPv2 color grading. The add-on restores the original Scene effects
   switches when stopped.

**Open Separate Preview Window** opens the native camera preview. Scene color
correction, display modes and the overlay apply to Scene View only.

## Display controls

The **Glim Preview** Scene View overlay and the add-on window expose:

- **Normal**: HDR rendering with optional scene effects.
- **False Color**: luminance bands relative to an adjustable reference gray.
- **Highlights**: flags pixels exceeding an adjustable HDR highlight threshold.
- **Exposure (EV)** and optional **Surface denoising (OIDN)**.
- The overlay also offers **Show preview**, **Opacity**, and **Stop Preview**.

Diagnostics bypass grading, opacity blending, and denoising so their colors stay
legible. Denoising uses the installed Open Image Denoise runtime, preserves primary
emission and material color, and reports unavailability in the overlay.

## Live updates and limits

Baked/Mixed lights, their creation/deletion and settings, material emission,
existing object transforms, and the indirect multiplier update live. Material
changes recapture the affected emission atlas through asynchronous CPU readback
and upload it to the add-on's Vulkan renderer. This is still a full-atlas transfer.

Stop and reopen after adding, deleting, disabling, or replacing geometry, changing
UV layouts or groups, changing albedo/alpha, changing the skybox, or introducing
new cookie textures. Runtime MaterialPropertyBlock animation and terrain emission
are not monitored. Play mode entry and assembly reload stop the preview.

The color-correction integration targets the built-in render pipeline used by this
project; URP/HDRP compositing is not implemented. Preview UVs are packed on hidden
copies, preserving the scene's baked lightmap UVs.

## Updating and rebuilding

This package is separate from `io.github.z3y.glim`, so updating Glim does not replace
the add-on files. Compatibility is checked against Glim 0.12.x; a later upstream
version needs a matching add-on port.

The repository's `Development~` directory contains the native patch,
pinned upstream revision, build script, and isolated Unity visual regression.
The native libraries have distinct names and cannot replace Glim's baking libraries.

The exporter and renderer are derived from Glim, copyright 2026 z3y, under the
included MIT license. Native third-party notices accompany the source patch.

## Repository development

This package directory is the Git repository root. Unity loads `Editor/` directly
when the repository is installed under a project's `Packages` directory.
`Development~` contains build tools and tests; Unity ignores folders ending in `~`.

Native libraries are distributed in release packages and are not tracked in Git.
For a source checkout, close Unity and build the library into `Editor/` before
using the preview. Run the build script with PowerShell 7 on Linux. Its `.meta`
file remains tracked to preserve the importer settings. The Unity validation
script currently targets Windows.

```powershell
./Development~/build-native.ps1 -OutputDirectory ./Editor -GpuTests
./Development~/validate-unity.ps1
```

For a checkout outside a Unity project, pass `-UnityProject 'path/to/project'`
to the validation script. That project must have Glim 0.12 and Unity's
post-processing package installed. See [development notes](Development~/README.md).

## Publishing

Update `package.json`'s version, commit and push, then run **Actions → Build Release**
with **publish** enabled. Alternatively, push a matching version tag such as
`v0.1.1`. Leaving **publish** disabled builds downloadable workflow artifacts only.
The workflow builds the patched Rust renderer on Windows and Ubuntu, packages
both native libraries in one VPM ZIP, and publishes the ZIP, manifest,
checksum, and signed build provenance bundle. The VPM listing checks for releases hourly; its **Build Repo Listing**
workflow can also be run manually for an immediate refresh.

## Verify a release

New workflow builds attest the ZIP, manifest, checksum file, and Windows DLL and Linux SO using
[GitHub artifact attestations](https://github.com/actions/attest). With GitHub CLI
installed, replace `VERSION` with the downloaded version and run:

```powershell
gh attestation verify ./com.frozenalex.glim-preview-VERSION.zip --repo FrozenAlex/glim-preview --signer-workflow FrozenAlex/glim-preview/.github/workflows/release.yml --deny-self-hosted-runners
```

You can verify the extracted native library with the same command using its file path.
The release also includes `provenance.sigstore.json`; pass
`--bundle ./provenance.sigstore.json` to use that bundle instead of fetching it
from GitHub. The original `0.1.0` release predates attestations.
