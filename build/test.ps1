# Run tests without publishing or replacing release executables.
param([switch]$CoreOnly, [switch]$Smoke)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$testProject = Join-Path $projectRoot 'tests\CodexUsageNotch.Tests.csproj'

function Invoke-Dotnet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Tests failed with exit code $LASTEXITCODE" }
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

Invoke-Dotnet @('run', '--project', $testProject, '-c', 'Release')
if (-not $CoreOnly) {
    Invoke-Dotnet @('run', '--project', $testProject, '-c', 'Release', '--no-build', '--', '--ui')
}
if ($Smoke) {
    foreach ($name in @('CodexUsageNotch', 'CodexUsageNotch-lite')) {
        $executable = Join-Path $projectRoot ($name + '.exe')
        if (-not (Test-Path -LiteralPath $executable)) { throw "Build the releases before smoke testing: $executable" }
        $report = Join-Path $PSScriptRoot ($name + '-smoke.txt')
        if (Test-Path -LiteralPath $report) { Remove-Item -LiteralPath $report }
        Test-Executable $executable $report
        Remove-Item -LiteralPath $report
    }
}
Write-Output 'All requested tests passed.'
