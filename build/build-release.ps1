param([switch]$StageOnly, [switch]$PromoteOnly)
$ErrorActionPreference = 'Stop'
if ($StageOnly -and $PromoteOnly) { throw 'Choose either -StageOnly or -PromoteOnly.' }
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectFile = Join-Path $projectRoot 'CodexUsageNotch.csproj'
$projectVersion = ([xml](Get-Content -LiteralPath $projectFile -Raw)).Project.PropertyGroup.Version
$stagingRoot = Join-Path $PSScriptRoot 'staging'
$manifestPath = Join-Path $stagingRoot 'validated.json'
$editions = @(
    @{ Name = 'CodexUsageNotch'; Folder = 'portable'; SelfContained = 'true' },
    @{ Name = 'CodexUsageNotch-lite'; Folder = 'lite'; SelfContained = 'false' }
)

function Invoke-Dotnet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE" }
}

function Test-Executable([string]$Executable, [string]$Report) {
    $process = Start-Process -FilePath $Executable -ArgumentList @('--smoke-test', ('"' + $Report + '"')) -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(15000)) {
        Stop-Process -Id $process.Id -ErrorAction SilentlyContinue
        throw "Smoke test timed out: $Executable"
    }
    $process.Refresh()
    if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $Report) -or
        -not (Select-String -LiteralPath $Report -Pattern '^trayRegistered=True$' -Quiet) -or
        -not (Select-String -LiteralPath $Report -Pattern '^stripAtTopCenter=True$' -Quiet) -or
        -not (Select-String -LiteralPath $Report -Pattern '^previewInteractive=False$' -Quiet)) {
        throw "Smoke test failed: $Executable"
    }
}

if (-not $PromoteOnly) {
    New-Item -ItemType Directory -Path $stagingRoot -Force | Out-Null
    # Never retain a prior successful validation after a failed build.
    if (Test-Path -LiteralPath $manifestPath) { Remove-Item -LiteralPath $manifestPath }
    Invoke-Dotnet @('run', '--project', (Join-Path $projectRoot 'tests\CodexUsageNotch.Tests.csproj'), '-c', 'Release')
    Invoke-Dotnet @('run', '--project', (Join-Path $projectRoot 'tests\CodexUsageNotch.Tests.csproj'), '-c', 'Release', '--no-build', '--', '--ui')
    $artifacts = @()
    foreach ($edition in $editions) {
        $folder = Join-Path $stagingRoot $edition.Folder
        Invoke-Dotnet @('publish', $projectFile, '-c', 'Release', '-r', 'win-x64', '--self-contained', $edition.SelfContained,
            '-p:PublishSingleFile=true', ('-p:AssemblyName=' + $edition.Name), '-p:RootNamespace=CodexUsageNotch', '-o', $folder)
        $executable = Join-Path $folder ($edition.Name + '.exe')
        $report = Join-Path $stagingRoot ($edition.Folder + '-smoke.txt')
        if (Test-Path -LiteralPath $report) { Remove-Item -LiteralPath $report }
        Test-Executable $executable $report
        $artifacts += @{ Name = $edition.Name + '.exe'; Source = $executable; SHA256 = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash }
    }
    @{ Version = $projectVersion; ValidatedUtc = [DateTimeOffset]::UtcNow.ToString('O'); Artifacts = $artifacts } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
    if ($StageOnly) { Write-Output "Validated releases are in $stagingRoot"; exit 0 }
}

if (-not (Test-Path -LiteralPath $manifestPath)) { throw 'Build and validate staging before promotion.' }
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.Version -ne $projectVersion) { throw 'Staged release version differs from the project. Stage again before promotion.' }
if ($manifest.Artifacts.Count -ne 2 -or ($manifest.Artifacts.Name | Select-Object -Unique).Count -ne 2) { throw 'Expected exactly two validated editions.' }
foreach ($edition in $editions) {
    if (Get-Process -Name $edition.Name -ErrorAction SilentlyContinue) {
        throw 'Exit Codex Usage Notch from its tray menu, then run build-release.ps1 -PromoteOnly. The staged releases are already validated.'
    }
}
foreach ($artifact in $manifest.Artifacts) {
    $source = [IO.Path]::GetFullPath($artifact.Source)
    if (-not $source.StartsWith($stagingRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Artifact is outside staging.' }
    if ($artifact.Name -notin @('CodexUsageNotch.exe', 'CodexUsageNotch-lite.exe')) { throw 'Unexpected artifact name.' }
    if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $artifact.SHA256) { throw 'Staged artifact changed after validation.' }
}
$dist = Join-Path $projectRoot 'dist'
New-Item -ItemType Directory -Path $dist -Force | Out-Null
foreach ($artifact in $manifest.Artifacts) {
    foreach ($folder in @($dist, $projectRoot)) {
        $destination = Join-Path $folder $artifact.Name
        if (-not ([IO.Path]::GetFullPath($destination)).StartsWith($projectRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Destination is outside the project.' }
        # Keep the preceding executable for a straightforward portable rollback.
        if (Test-Path -LiteralPath $destination) { Copy-Item -LiteralPath $destination -Destination ($destination + '.previous') -Force }
        Copy-Item -LiteralPath $artifact.Source -Destination ($destination + '.new') -Force
        Move-Item -LiteralPath ($destination + '.new') -Destination $destination -Force
    }
}
@{ Version = $manifest.Version; ValidatedUtc = $manifest.ValidatedUtc; Artifacts = @($manifest.Artifacts | Select-Object Name,SHA256) } |
    ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $dist 'release-manifest.json') -Encoding UTF8
Write-Output "Portable and lite releases promoted to $dist and the project root."
