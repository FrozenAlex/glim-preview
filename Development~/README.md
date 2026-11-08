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
