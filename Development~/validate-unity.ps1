param(
    [string]$UnityEditor = 'C:\Program Files\Unity\Hub\Editor\2022.3.22f1\Editor\Unity.exe',
    [string]$AddonPackage = '',
    [string]$UnityProject = '',
    [ValidateSet('PreviewValidation', 'LightmapperSelectionValidation')]
    [string]$Validation = 'PreviewValidation'
)
$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (!$UnityProject) { $UnityProject = Join-Path $repositoryRoot '../..' }
$projectRoot = (Resolve-Path -LiteralPath $UnityProject).Path
if (!(Test-Path -LiteralPath (Join-Path $projectRoot 'ProjectSettings/ProjectVersion.txt'))) {
    throw 'Pass -UnityProject with a Unity project containing Glim 0.12 and the post-processing package.'
}
$work = Join-Path $PSScriptRoot '.work'
New-Item -ItemType Directory -Force -Path $work | Out-Null
$testRoot = Join-Path $work ('GlimAddonValidation-' + [Guid]::NewGuid().ToString('N'))
$addonSource = if ($AddonPackage) { (Resolve-Path -LiteralPath $AddonPackage).Path } else { $repositoryRoot }
$postProcessing = Get-ChildItem (Join-Path $projectRoot 'Library/PackageCache') -Directory -Filter 'com.unity.postprocessing@*' | Select-Object -First 1
if (!$postProcessing) { throw 'The test requires the installed Unity post-processing package.' }
if (!(Test-Path -LiteralPath $UnityEditor)) { throw "Unity editor not found: $UnityEditor" }
foreach ($folder in @('Assets', 'ProjectSettings', 'Packages')) {
    New-Item -ItemType Directory -Path (Join-Path $testRoot $folder) -Force | Out-Null
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'ProjectSettings/ProjectVersion.txt') -Destination (Join-Path $testRoot 'ProjectSettings/ProjectVersion.txt')
$settings = Get-Content (Join-Path $projectRoot 'ProjectSettings/ProjectSettings.asset') -Raw
$colorSpace = [regex]::Match($settings, '(?m)^\s*m_ActiveColorSpace:\s*([01])').Groups[1].Value
if (!$colorSpace) { throw 'Cannot read the project color space.' }
@"
%YAML 1.1
%TAG !u! tag:unity3d.com,2011:
--- !u!129 &1
PlayerSettings:
  m_ObjectHideFlags: 0
  serializedVersion: 26
  m_ActiveColorSpace: $colorSpace
"@ | Set-Content (Join-Path $testRoot 'ProjectSettings/ProjectSettings.asset')
$basePackage = Join-Path $testRoot 'Packages/io.github.z3y.glim'
$testAddon = Join-Path $testRoot 'Packages/com.frozenalex.glim-preview'
Copy-Item -LiteralPath (Join-Path $projectRoot 'Packages/io.github.z3y.glim') -Destination $basePackage -Recurse
# Copy only installable package files. Development~ contains this test project.
New-Item -ItemType Directory -Path $testAddon -Force | Out-Null
foreach ($name in @('Editor', 'Editor.meta', 'package.json', 'package.json.meta', 'README.md', 'README.md.meta', 'LICENSE.md', 'LICENSE.md.meta')) {
    $source = Join-Path $addonSource $name
    if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination $testAddon -Recurse }
}
@{
    name = 'com.z3y.glim.editor'; references = @('com.z3y.glim.runtime')
    includePlatforms = @('Editor'); allowUnsafeCode = $true
} | ConvertTo-Json | Set-Content (Join-Path $basePackage 'Editor/com.z3y.glim.editor.asmdef')
@{
    name = 'FrozenAlex.GlimPreview.Editor'; references = @('com.z3y.glim.runtime', 'com.z3y.glim.editor', 'Unity.Postprocessing.Runtime')
    includePlatforms = @('Editor'); allowUnsafeCode = $true
} | ConvertTo-Json | Set-Content (Join-Path $testAddon 'Editor/FrozenAlex.GlimPreview.Editor.asmdef')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "Tests/$Validation.cs") -Destination (Join-Path $testAddon "Editor/$Validation.cs")
$manifest = Get-Content (Join-Path $projectRoot 'Packages/manifest.json') -Raw | ConvertFrom-Json
$dependencies = @{}
foreach ($property in $manifest.dependencies.PSObject.Properties) {
    if ($property.Name.StartsWith('com.unity.modules.')) { $dependencies[$property.Name] = $property.Value }
}
$dependencies['com.unity.postprocessing'] = 'file:' + $postProcessing.FullName.Replace('\', '/')
@{ dependencies = $dependencies } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $testRoot 'Packages/manifest.json')
$logPath = Join-Path $testRoot 'unity.log'
$arguments = @('-batchmode', '-projectPath', ('"' + $testRoot + '"'), '-executeMethod', "FrozenAlex.GlimPreview.$Validation.Run", '-logFile', ('"' + $logPath + '"'))
$process = Start-Process -FilePath $UnityEditor -ArgumentList $arguments -WorkingDirectory $testRoot -WindowStyle Hidden -PassThru
[pscustomobject]@{ PID = $process.Id; Project = $testRoot; Result = (Join-Path $testRoot 'Results/result.txt'); Log = $logPath }
