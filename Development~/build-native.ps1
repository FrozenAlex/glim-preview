param(
    [string]$OutputDirectory = '',
    [switch]$GpuTests
)
$ErrorActionPreference = 'Stop'
$windowsHost = [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT
if (!$windowsHost -and !$IsLinux) { throw 'Supported build hosts are Windows x64 and Linux x64 (PowerShell 7).' }
if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne [System.Runtime.InteropServices.Architecture]::X64) {
    throw 'The native preview requires an x64 build host.'
}
$target = if ($windowsHost) { 'x86_64-pc-windows-msvc' } else { 'x86_64-unknown-linux-gnu' }
$library = if ($windowsHost) { 'glim.dll' } else { 'libglim.so' }
$filename = if ($windowsHost) { 'glim_preview_addon_012_v1.dll' } else { 'glim_preview_addon_012_v1.so' }
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$work = Join-Path $PSScriptRoot '.work'
New-Item -ItemType Directory -Force -Path $work | Out-Null
$buildId = [Guid]::NewGuid().ToString('N')
$source = Join-Path $work ('GlimAddonBuild-' + $buildId)
$revision = '5f7a25f414f20c3a90f2a147c69cc3a64dd6c262'
if (!$OutputDirectory) { $OutputDirectory = Join-Path $root 'dist' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
foreach ($tool in @('git', 'cargo', 'slangc')) { Get-Command $tool -ErrorAction Stop | Out-Null }
& git clone --depth 1 --branch v0.12.2 https://github.com/z3y/glim.git $source
if ($LASTEXITCODE) { throw 'Could not clone upstream Glim.' }
if ((& git -C $source rev-parse HEAD).Trim() -ne $revision) { throw 'Upstream tag does not match the reviewed revision.' }
& git -C $source apply (Join-Path $PSScriptRoot 'native-preview-0.12.patch')
if ($LASTEXITCODE) { throw 'Native preview patch could not be applied.' }
Push-Location $source
try {
    $targetDirectory = if ($env:CARGO_TARGET_DIR) {
        [IO.Path]::GetFullPath($env:CARGO_TARGET_DIR)
    } elseif ($windowsHost) {
        # Keep MSBuild's generated file-tracking paths below its path limit.
        Join-Path ([IO.Path]::GetTempPath()) ('glim-' + $buildId.Substring(0, 12))
    } else {
        Join-Path $source 'target'
    }
    & cargo test --locked --lib preview_ --release --target $target --target-dir $targetDirectory
    if ($LASTEXITCODE) { throw 'Native preview tests failed.' }
    if ($GpuTests) {
        & cargo test --locked --lib embedded_gpu_emission_camera_and_resize --release --target $target --target-dir $targetDirectory -- --ignored
        if ($LASTEXITCODE) { throw 'GPU preview tests failed.' }
    }
    & cargo build --locked --release --lib --target $target --target-dir $targetDirectory
    if ($LASTEXITCODE) { throw 'Native preview build failed.' }
    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
    $output = Join-Path $OutputDirectory $filename
    Copy-Item -LiteralPath (Join-Path $targetDirectory "$target/release/$library") -Destination $output
    if (!$windowsHost) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot "$filename.meta") -Destination "$output.meta"
    }
    Get-FileHash -LiteralPath $output -Algorithm SHA256
}
finally { Pop-Location }
# Install the output only while that add-on library is not loaded in Unity.
