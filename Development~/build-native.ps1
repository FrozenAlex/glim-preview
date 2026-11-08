param(
    [string]$OutputDirectory = '',
    [switch]$GpuTests
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$work = Join-Path $PSScriptRoot '.work'
New-Item -ItemType Directory -Force -Path $work | Out-Null
$source = Join-Path $work ('GlimAddonBuild-' + [Guid]::NewGuid().ToString('N'))
$revision = '1c405dffb33ab131d501f5cc45d3ee68dc098ce0'
if (!$OutputDirectory) { $OutputDirectory = Join-Path $root 'dist' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
foreach ($tool in @('git', 'cargo', 'slangc')) { Get-Command $tool -ErrorAction Stop | Out-Null }
& git clone --depth 1 --branch v0.12.0 https://github.com/z3y/glim.git $source
if ($LASTEXITCODE) { throw 'Could not clone upstream Glim.' }
if ((& git -C $source rev-parse HEAD).Trim() -ne $revision) { throw 'Upstream tag does not match the reviewed revision.' }
& git -C $source apply (Join-Path $PSScriptRoot 'native-preview-0.12.patch')
if ($LASTEXITCODE) { throw 'Native preview patch could not be applied.' }
Push-Location $source
try {
    & cargo test --locked --lib preview_ --release
    if ($LASTEXITCODE) { throw 'Native preview tests failed.' }
    if ($GpuTests) {
        & cargo test --locked --lib embedded_gpu_emission_camera_and_resize --release -- --ignored
        if ($LASTEXITCODE) { throw 'GPU preview tests failed.' }
    }
    & cargo build --locked --release --lib
    if ($LASTEXITCODE) { throw 'Native preview build failed.' }
    New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
    $output = Join-Path $OutputDirectory 'glim_preview_addon_012_v1.dll'
    Copy-Item -LiteralPath (Join-Path $source 'target/release/glim.dll') -Destination $output
    Get-FileHash -LiteralPath $output -Algorithm SHA256
}
finally { Pop-Location }
# Install the output only while that add-on DLL is not loaded in Unity.
