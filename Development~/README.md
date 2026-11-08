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

## Build

With Rust/MSVC and `slangc` on PATH, run:

```powershell
./Development~/build-native.ps1 -GpuTests
```

The script creates a new source checkout, applies the patch, runs the unit and
optional GPU tests, and writes `glim_preview_addon_012_v1.dll` to its `dist`
directory at the repository root. Copy the result into `Editor/` with Unity closed
if replacing the same loaded DLL. Preserve its `.meta` file. The upstream `glim.dll`
must remain unchanged.

For GPU tests with Khronos validation installed, set `GLIM_PREVIEW_VALIDATE=1`
before running the build script with `-GpuTests`. `GLIM_PREVIEW_GPU_MODE=hardware`
or `software` selects one ray-tracing path; leaving it unset tests both. Validation
errors fail the test. The current software path has a separate `TopLevelAS`
descriptor-layout validation issue, recorded in `VALIDATION.md`.

The DLL is not tracked in Git; its `.meta` file is tracked. For a new source
checkout, close Unity and use `-OutputDirectory ./Editor` to install the build
directly. Release ZIPs already include the DLL built by GitHub Actions.

## Verify Scene View behavior

```powershell
./Development~/validate-unity.ps1
```

This creates an isolated project containing both packages and PPv2, then runs the
actual Scene View regression. `-UnityProject` selects the host Unity project when this checkout is outside its Packages folder. `-AddonPackage` can select a staged add-on.
The command returns the test process, project, log, and result paths. The harness
exits Unity when complete. It covers live emission, HDR, scene desaturation,
False Color, Highlights, denoising, transforms/Undo, restart behavior, and baked UV
and temporary-renderer preservation. It does not open or save the host scene.

The package README documents the supported workflow and remaining live-update
limits. This add-on replaces the earlier in-place Glim 0.11 preview extension.

## Release workflow

`.github/workflows/release.yml` uses Windows 2022, Rust 1.97.1, and Slang 2026.14
(SHA-256 verified). It applies the native patch to the checked upstream revision,
runs the native unit tests, and builds a fresh DLL. GPU and Unity visual tests
still require the local commands above; GitHub's hosted runner has no suitable GPU.

`package_release.py` packages only the installable files plus third-party notices.
The ZIP has `package.json` at its root and contains the freshly built DLL, preserving
the checked-in Unity metadata. The release's external `package.json` and `SHA256SUMS`
contain the ZIP hash; the manifest inside the ZIP omits the hash.

To check packaging locally after building:

```powershell
python -m unittest discover -s Development~ -p test_package_release.py -v
python Development~/package_release.py --native-dll dist/glim_preview_addon_012_v1.dll --output dist/package --tag v0.1.0
```

For a new release, change the manifest version and push that commit. Run **Build
Release** with **publish** enabled, or push `v<version>`; a tag that disagrees with
the manifest is rejected. Manual runs default to build-only. Published versions
are never overwritten. If uploading fails and leaves a draft, delete that draft
before retrying. The publisher creates a draft, uploads every asset, and only then
makes it public.

After packaging, `actions/attest` generates SLSA build provenance for the ZIP,
external manifest, checksum file, and freshly built DLL. The action is pinned to
a commit and uses GitHub OIDC; no signing key secret is required. Only the build
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
