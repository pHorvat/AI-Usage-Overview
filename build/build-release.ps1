# Default: publish, promote to the project root, then remove staging.
# StageOnly lets the current indicator keep running until promotion.
param([switch]$StageOnly, [switch]$PromoteOnly)
$ErrorActionPreference = 'Stop'
if ($StageOnly -and $PromoteOnly) { throw 'Choose either -StageOnly or -PromoteOnly.' }
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectFile = Join-Path $projectRoot 'CodexUsageNotch.csproj'
$projectVersion = ([xml](Get-Content -LiteralPath $projectFile -Raw)).Project.PropertyGroup.Version
$stagingRoot = Join-Path $PSScriptRoot 'staging'
$manifestPath = Join-Path $stagingRoot 'release.json'
$editions = @(
    @{ Name = 'CodexUsageNotch'; Folder = 'portable'; SelfContained = 'true' },
    @{ Name = 'CodexUsageNotch-lite'; Folder = 'lite'; SelfContained = 'false' }
)

function Invoke-Dotnet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE" }
}

if (-not $PromoteOnly) {
    New-Item -ItemType Directory -Path $stagingRoot -Force | Out-Null
    # Never retain a prior successful manifest after a failed build.
    if (Test-Path -LiteralPath $manifestPath) { Remove-Item -LiteralPath $manifestPath }
    $artifacts = @()
    foreach ($edition in $editions) {
        $folder = Join-Path $stagingRoot $edition.Folder
        Invoke-Dotnet @('publish', $projectFile, '-c', 'Release', '-r', 'win-x64', '--self-contained', $edition.SelfContained,
            '-p:PublishSingleFile=true', ('-p:AssemblyName=' + $edition.Name), '-p:RootNamespace=CodexUsageNotch', '-o', $folder)
        $executable = Join-Path $folder ($edition.Name + '.exe')
        $artifacts += @{ Name = $edition.Name + '.exe'; Source = $executable; SHA256 = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash }
    }
    @{ Version = $projectVersion; BuiltUtc = [DateTimeOffset]::UtcNow.ToString('O'); Artifacts = $artifacts } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
    if ($StageOnly) { Write-Output "Built releases are in $stagingRoot"; exit 0 }
}

if (-not (Test-Path -LiteralPath $manifestPath)) { throw 'Build staging before promotion.' }
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.Version -ne $projectVersion) { throw 'Staged release version differs from the project. Stage again before promotion.' }
if ($manifest.Artifacts.Count -ne 2 -or ($manifest.Artifacts.Name | Select-Object -Unique).Count -ne 2) { throw 'Expected exactly two built editions.' }
foreach ($edition in $editions) {
    if (Get-Process -Name $edition.Name -ErrorAction SilentlyContinue) {
        throw 'Exit Codex Usage Notch from its tray menu, then run build-release.ps1 -PromoteOnly. The staged releases are already built.'
    }
}
foreach ($artifact in $manifest.Artifacts) {
    $source = [IO.Path]::GetFullPath($artifact.Source)
    if (-not $source.StartsWith($stagingRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Artifact is outside staging.' }
    if ($artifact.Name -notin @('CodexUsageNotch.exe', 'CodexUsageNotch-lite.exe')) { throw 'Unexpected artifact name.' }
    if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $artifact.SHA256) { throw 'Staged artifact changed after building.' }
}
foreach ($artifact in $manifest.Artifacts) {
    $destination = Join-Path $projectRoot $artifact.Name
    Copy-Item -LiteralPath $artifact.Source -Destination ($destination + '.new') -Force
    Move-Item -LiteralPath ($destination + '.new') -Destination $destination -Force
}
foreach ($artifact in $manifest.Artifacts) {
    if ((Get-FileHash -LiteralPath (Join-Path $projectRoot $artifact.Name) -Algorithm SHA256).Hash -ne $artifact.SHA256) {
        throw 'Promoted artifact verification failed. Staging has been preserved.'
    }
}
& (Join-Path $PSScriptRoot 'clean.ps1') -StagingOnly
Write-Output "Portable and lite releases are in $projectRoot. Temporary staging files removed."
