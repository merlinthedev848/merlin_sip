param(
    [string] $Configuration = 'Release',
    [string] $RuntimeIdentifier = 'win-x64'
)

$ErrorActionPreference = 'Stop'

$projectDir = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$projectFile = Join-Path $projectDir 'MerlinSIP\MerlinSIP.csproj'
$publishProfile = 'SelfContainedWinX64'
$dotnet = 'C:\Program Files\dotnet\dotnet.exe'
$wix = Join-Path $projectDir '.tools\wix.exe'

if (-not (Test-Path -LiteralPath $dotnet)) {
    $dotnet = 'dotnet'
}

if (-not (Test-Path -LiteralPath $wix)) {
    throw "WiX local tool was not found at '$wix'. Install it with: dotnet tool install wix --tool-path .\.tools --version 5.0.2"
}

$projectXml = [xml](Get-Content -LiteralPath $projectFile)
$version = $projectXml.Project.PropertyGroup.Version | Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($version)) {
    throw "Unable to read product version from '$projectFile'."
}

& $dotnet publish $projectFile -c Release -r win-x64 --self-contained false

$publishDir = Join-Path $projectDir "MerlinSIP\bin\$Configuration\net8.0-windows\$RuntimeIdentifier\publish"
$generatedWix = Join-Path $projectDir 'installer\MerlinSIP.generated.wxs'
$distDir = Join-Path $projectDir 'dist'
$msiPath = Join-Path $distDir "MerlinSIP-$version-x64-faststart.msi"

New-Item -ItemType Directory -Force -Path $distDir | Out-Null

& powershell.exe -ExecutionPolicy Bypass -File (Join-Path $projectDir 'installer\Generate-MerlinSipWix.ps1') `
    -ProjectDir $projectDir `
    -PublishDir $publishDir `
    -OutputPath $generatedWix

& $wix build $generatedWix -arch x64 -ext WixToolset.UI.wixext -ext WixToolset.Util.wixext -o $msiPath

Get-Item -LiteralPath $msiPath
