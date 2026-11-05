#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$IsccPath,
    [switch]$UseExistingModPackages,
    [ValidateRange(1, 1024)][int]$MaxInstallerMiB = 40
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'gui/PhantomBladeModManager.csproj'
$version = ([xml](Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup.Version
$ids = @('sprint', 'map', 'talisman', 'quickActions')
$buildId = '26367565'
$work = Join-Path $root ('.local/release/' + [guid]::NewGuid().ToString('N'))
$stage = Join-Path $work 'app'
$output = Join-Path $root "dist/releases/$version"

function Invoke-Checked([string]$Executable, [string[]]$Arguments) {
    & $Executable @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "$Executable failed with exit code $LASTEXITCODE" }
}

function Resolve-Child([string]$Parent, [string]$Relative) {
    $path = [IO.Path]::GetFullPath((Join-Path $Parent $Relative))
    if (!$path.StartsWith($Parent.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Package path escapes its directory: $Relative"
    }
    return $path
}

function Copy-ReleaseFile([string]$Source, [string]$Relative) {
    $target = Resolve-Child $stage $Relative
    New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
    Copy-Item -LiteralPath $Source -Destination $target
}

if (!$IsccPath) {
    $candidates = @(
        (Join-Path $root '.local/tools/inno/ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs/Inno Setup 7/ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 7/ISCC.exe')
    )
    $IsccPath = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if (!$IsccPath -or !(Test-Path -LiteralPath $IsccPath)) {
    throw 'Install Inno Setup 7.1 or later, or pass -IsccPath.'
}

New-Item -ItemType Directory -Path $stage, $output -Force | Out-Null
Invoke-Checked 'dotnet' @('publish', $project, '-p:PublishProfile=Installer', '-o', $stage)
$manager = Join-Path $stage 'PhantomBladeModManager.exe'
if (!$UseExistingModPackages) {
    foreach ($id in $ids) { Invoke-Checked $manager @($root, 'build', $id, '--json') }
}

$catalog = @(Get-Content -LiteralPath (Join-Path $root 'catalog.json') -Raw | ConvertFrom-Json)
$selected = @($ids | ForEach-Object {
    $id = $_
    $entry = @($catalog | Where-Object id -EQ $id)
    if ($entry.Count -ne 1 -or $entry[0].backend -ne 'nativeRuntime') { throw "Invalid release mod: $id" }
    $entry[0]
})
ConvertTo-Json -InputObject $selected -Depth 10 | Set-Content -LiteralPath (Join-Path $stage 'catalog.json') -Encoding utf8NoBOM
# Keep the complete binding schema so existing user hotkeys remain compatible.
foreach ($file in @('controls.json', 'locales/en.json', 'locales/zh-CN.json')) {
    Copy-ReleaseFile (Join-Path $root $file) $file
}

$packages = @()
$shared = @{}
foreach ($mod in $selected) {
    $relative = "mods/features/$($mod.project)/dist/runtime"
    $source = Resolve-Child $root $relative
    $manifestFile = Join-Path $source 'manifest.json'
    $manifest = Get-Content -LiteralPath $manifestFile -Raw | ConvertFrom-Json
    if ($manifest.schema -ne 1 -or $manifest.id -ne $mod.id -or $manifest.backend -ne $mod.backend -or $manifest.buildId -ne $buildId) {
        throw "Invalid package manifest: $($mod.id)"
    }
    $runtimeSignature = @($manifest.files | Where-Object path -Like 'Runtime/*' | Sort-Object path |
        ForEach-Object { "$($_.path):$($_.sha256)" }) -join "`n"
    if ($shared.Count -gt 0 -and ($shared.version -ne $manifest.runtimeVersion -or $shared.signature -ne $runtimeSignature)) {
        throw "Shared runtime mismatch: rebuild all release mods ($($mod.id))"
    }
    $shared = @{ version = $manifest.runtimeVersion; signature = $runtimeSignature }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $manifest.files) {
        if (!$seen.Add($entry.path)) { throw "Duplicate package path: $($entry.path)" }
        $file = Resolve-Child $source $entry.path
        $info = Get-Item -LiteralPath $file
        if ($info.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Linked package file: $file" }
        if ($info.Length -ne $entry.bytes -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $entry.sha256) {
            throw "Package checksum mismatch: $file"
        }
        Copy-ReleaseFile $file "$relative/$($entry.path)"
    }
    Copy-ReleaseFile $manifestFile "$relative/manifest.json"
    $packages += [ordered]@{
        id = $mod.id; version = $manifest.version; buildId = $manifest.buildId
        runtimeVersion = $manifest.runtimeVersion; runtimeVerified = $manifest.runtimeVerified
        files = $manifest.files.Count; bytes = ($manifest.files | Measure-Object bytes -Sum).Sum
    }
}

$runtime = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'dotnet-runtime.json') -Raw | ConvertFrom-Json
$runtimeConfig = Get-Content -LiteralPath (Join-Path $stage 'PhantomBladeModManager.runtimeconfig.json') -Raw | ConvertFrom-Json
if ($runtimeConfig.runtimeOptions.frameworks.Count -ne 2 -or
    @($runtimeConfig.runtimeOptions.frameworks | Where-Object version -NE '10.0.0').Count -ne 0) {
    throw 'Update installer runtime detection to match the published framework requirements'
}
if (([uri]$runtime.url).Scheme -ne 'https' -or ([uri]$runtime.url).Host -ne 'builds.dotnet.microsoft.com' -or
    $runtime.sha256 -notmatch '^[0-9a-f]{64}$' -or $runtime.version -notmatch '^10\.0\.\d+$') {
    throw 'Invalid pinned .NET runtime download'
}

$files = @(Get-ChildItem -LiteralPath $stage -Recurse -File)
$forbidden = $files | Where-Object {
    $relative = [IO.Path]::GetRelativePath($stage, $_.FullName)
    $_.Name -match '\.(pdb|cs|cpp|obj|lib|exp|log)$|^settings\.local\.json$|^key\.local$|^(repak|retoc)\.exe$' -or
    $relative -match '(^|[\\/])(\.git|\.local|\.local-runtime|trash|bin|obj|design|preview)([\\/]|$)'
}
if ($forbidden) { throw "Unexpected release files: $($forbidden.Name -join ', ')" }
$inventory = @($files | Sort-Object FullName | ForEach-Object {
    [ordered]@{ path = [IO.Path]::GetRelativePath($stage, $_.FullName).Replace('\', '/'); bytes = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant() }
})
Invoke-Checked $IsccPath @('/Qp', "/DStageDir=$stage", "/DReleaseVersion=$version", "/DReleaseDir=$output",
    "/DRuntimeUrl=$($runtime.url)", "/DRuntimeFile=$($runtime.fileName)", "/DRuntimeSha256=$($runtime.sha256)",
    (Join-Path $PSScriptRoot 'installer.iss'))
$installer = Get-Item -LiteralPath (Join-Path $output "PhantomBladeModManager-$version-Setup.exe")
$digest = (Get-FileHash -LiteralPath $installer.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
"$digest  $($installer.Name)" | Set-Content -LiteralPath ($installer.FullName + '.sha256') -Encoding ascii
$report = [ordered]@{
    version = $version; gameBuildId = $buildId; installer = $installer.Name
    installerBytes = $installer.Length; installerMiB = [math]::Round($installer.Length / 1MB, 2)
    installedBytes = ($files | Measure-Object Length -Sum).Sum
    maxInstallerMiB = $MaxInstallerMiB; sha256 = $digest
    signed = (Get-AuthenticodeSignature -LiteralPath $installer.FullName).Status.ToString()
    runtimeDownload = $runtime; packages = $packages; files = $inventory
}
$report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $output 'release-report.json') -Encoding utf8NoBOM
if ($installer.Length -gt $MaxInstallerMiB * 1MB) { throw "Installer exceeds $MaxInstallerMiB MiB: $($report.installerMiB) MiB" }
Write-Host "$($installer.FullName) ($($report.installerMiB) MiB)"
Write-Host "Staged application: $stage"
