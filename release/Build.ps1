#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$IsccPath,
    [string]$OutputDirectory,
    [string]$SourceDirectory,
    [switch]$UseExistingModPackages
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
$sourceRoot = if ($SourceDirectory) { [IO.Path]::GetFullPath($SourceDirectory) } else { $root }
$project = Join-Path $sourceRoot 'gui/PhantomBladeModManager.csproj'
$version = ([xml](Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup.Version
$catalog = @(Get-Content -LiteralPath (Join-Path $sourceRoot 'catalog.json') -Raw | ConvertFrom-Json)
$ids = @($catalog.id)
if (@($ids | Select-Object -Unique).Count -ne $ids.Count -or $ids.Count -eq 0) {
    throw 'Release catalog must contain unique mod IDs.'
}
$buildId = '26367565'
$work = Join-Path $root ('.local/release/' + [guid]::NewGuid().ToString('N'))
$stage = Join-Path $work 'app'
$output = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $root "dist/releases/$version" }

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

$selected = @($ids | ForEach-Object {
    $id = $_
    $entry = @($catalog | Where-Object id -EQ $id)
    if ($entry.Count -ne 1 -or $entry[0].backend -notin @('nativeRuntime', 'assetPak') -or
        $entry[0].category -notin @('feature', 'remedy')) { throw "Invalid release mod: $id" }
    $entry[0]
})
ConvertTo-Json -InputObject $selected -Depth 10 | Set-Content -LiteralPath (Join-Path $stage 'catalog.json') -Encoding utf8NoBOM
# Keep the complete binding schema so existing user hotkeys remain compatible.
foreach ($file in @('controls.json', 'locales/en.json', 'locales/zh-CN.json',
    'mods/features/phantom-blade-training/data/high-cycle-template.json')) {
    Copy-ReleaseFile (Join-Path $sourceRoot $file) $file
}

$packages = @()
$shared = @{}
foreach ($mod in $selected) {
    $category = if ($mod.category -eq 'feature') { 'features' } else { 'remedies' }
    $packageKind = if ($mod.backend -eq 'nativeRuntime') { 'runtime' } else { 'package' }
    $relative = "mods/$category/$($mod.project)/dist/$packageKind"
    $source = Resolve-Child $root $relative
    $manifestFile = Join-Path $source 'manifest.json'
    $manifest = Get-Content -LiteralPath $manifestFile -Raw | ConvertFrom-Json
    if ($manifest.id -ne $mod.id -or $manifest.backend -ne $mod.backend -or $manifest.buildId -ne $buildId) {
        throw "Invalid package manifest: $($mod.id)"
    }
    $runtimeVersion = $null
    if ($mod.backend -eq 'nativeRuntime') {
        if ($manifest.schema -ne 1) { throw "Invalid runtime schema: $($mod.id)" }
        $runtimeVersion = $manifest.runtimeVersion
        $runtimeSignature = @($manifest.files | Where-Object path -Like 'Runtime/*' | Sort-Object path |
            ForEach-Object { "$($_.path):$($_.sha256)" }) -join "`n"
        if (!$runtimeSignature -or ($shared.Count -gt 0 -and
            ($shared.version -ne $runtimeVersion -or $shared.signature -ne $runtimeSignature))) {
            throw "Shared runtime mismatch: rebuild all release mods ($($mod.id))"
        }
        $shared = @{ version = $runtimeVersion; signature = $runtimeSignature }
    }
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
    $instructions = Join-Path $sourceRoot "mods/$category/$($mod.project)/INSTALL.txt"
    if ($mod.id -eq 'map') {
        $instructions = Join-Path $sourceRoot "mods/$category/$($mod.project)/PUERTS-INSTALL.txt"
    }
    if (!$seen.Contains('INSTALL.txt') -and (Test-Path -LiteralPath $instructions)) {
        Copy-ReleaseFile $instructions "$relative/INSTALL.txt"
    }
    $packages += [ordered]@{
        id = $mod.id; version = $manifest.version; buildId = $manifest.buildId; backend = $mod.backend
        runtimeVersion = $runtimeVersion; runtimeVerified = $manifest.runtimeVerified
        files = $manifest.files.Count; bytes = ($manifest.files | Measure-Object bytes -Sum).Sum
    }
}

# The integrated quest remedy uses the same installed Desktop Runtime as the manager.
$questRelative = 'mods/remedies/phantom-blade-chaos-reset/dist/PhantomBladeQuestManager-current'
$questStage = Resolve-Child $stage $questRelative
Invoke-Checked 'dotnet' @('publish', (Join-Path $sourceRoot 'mods/remedies/phantom-blade-chaos-reset/manager/PhantomBladeQuestManager.csproj'),
    '-c', 'Release', '-r', 'win-x64', '--self-contained', 'false', '-p:PublishSingleFile=false',
    '-p:PublishTrimmed=false', '-p:EnableCompressionInSingleFile=false', '-o', $questStage)
Copy-ReleaseFile (Join-Path $root 'mods/features/phantom-blade-map/.local-runtime/pak-tools/oo2core_9_win64.dll') "$questRelative/oo2core_9_win64.dll"
Copy-ReleaseFile (Join-Path $sourceRoot 'mods/remedies/phantom-blade-chaos-reset/MANAGER.txt') "$questRelative/INSTALL.txt"

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
    sha256 = $digest
    signed = (Get-AuthenticodeSignature -LiteralPath $installer.FullName).Status.ToString()
    runtimeDownload = $runtime; packages = $packages; files = $inventory
}
$report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $output 'release-report.json') -Encoding utf8NoBOM
Copy-Item -LiteralPath (Join-Path $sourceRoot "release/notes-$version.txt") -Destination (Join-Path $output 'release-notes.txt')
Write-Host "$($installer.FullName) ($($report.installerMiB) MiB)"
Write-Host "Staged application: $stage"
