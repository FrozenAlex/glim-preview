# Validation Р Р†Р вЂљРІР‚Сњ 2026-11-08

- Glim 0.12.0 plus the independent add-on compiled successfully against the project's Unity references.
- Native preview unit tests: 15 passed. The embedded GPU test was run separately and also exercises OIDN; the standalone noisy-surface OIDN test was not rerun.
- Embedded GPU regression passed with both hardware and software ray tracing: emission enable/disable, lights, transforms, camera changes, resizing, denoising, scene replacement/cache behavior, and shutdown.
- Unity 2022.3.22f1 isolated regression: PASS. Loaded upstream Glim and the add-on as separate assemblies. Verified HDR exposure and diagnostic bands; OIDN with exact primary emission; diagnostics bypassing grading, opacity and denoising; live emission; PPv2 color correction; movement and Undo; manual restart; baked UV preservation; and temporary-copy cleanup.
- Upstream Glim installation hashes were identical before and after installation (71 files).
- The host Unity project imported and compiled the installed add-on successfully (FrozenAlex.GlimPreview.Editor.dll).
- Rendering validation used an isolated scene; the host scene was not saved or rendered by the test.

The isolated validation project retains a Results directory containing the graded, ungraded, false-color, highlights, denoised, and transform screenshots.

Native DLL SHA256: `78ff7f7a4c89862decb08aad4e18a91508b9f3fefbb177488bef68c163cdb431`.

After renaming to com.frozenalex.glim-preview, the isolated Unity regression passed again with the renamed assembly, shader paths, and test entry point. The validation script copies only installable package files, excluding its own working directory.

## Push-constant preparation and shader integer features

The upstream revision remains Glim 0.12.0 (`1c405dffb33ab131d501f5cc45d3ee68dc098ce0`).
This change prepares the add-on payloads; it does not port the later upstream refactor.

- Camera data is 72 bytes, down from 96, preserving all 18 ray-vector components.
  Another 48 bytes of ray-tracing data plus an 8-byte resolution would total 128.
- Lighting remains 40 bytes and denoising capture remains 16 bytes.
- Explicit shader `uint64_t` addresses were replaced with typed pointers. Device
  creation still leaves `shaderInt64` disabled.
- Compiled SPIR-V tests check actual member offsets and sizes, the reserved space,
  and absence of `Int64`, `Int64Atomics`, and 64-bit integer types across all shaders.
- The GPU regression also checks perspective and orthographic camera packing.
- Updated native unit tests: 18 passed. The functional GPU regression passed on
  both hardware and software ray tracing, including live lights/emission, camera
  changes, transforms, resizing, denoising, scene reuse, and shutdown.
- The hardware path also passed with Khronos validation 1.4.368 enabled, zero
  validation errors, and `shaderInt64` still disabled. The layer was loaded only
  for the test through `VK_LAYER_PATH`, without a system-wide installation.
- Validation of the software ray-tracing path exposed a separate missing
  `TopLevelAS` descriptor binding (binding 0). It is outside this layout preparation
  change; the optional validation run reports it as a failure instead of hiding it.
