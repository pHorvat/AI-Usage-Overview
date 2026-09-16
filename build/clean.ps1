[CmdletBinding(SupportsShouldProcess = $true)]
param([switch]$StagingOnly)

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$rootPrefix = $projectRoot + [IO.Path]::DirectorySeparatorChar
$relativePaths = if ($StagingOnly) { @('build\staging') } else { @(
    'bin', 'obj', 'tests\bin', 'tests\obj',
    'build\output', 'build\previews', 'build\protocol-review', 'build\staging', 'dist', 'TestResults'
) }
$targets = @($relativePaths | ForEach-Object { Join-Path $projectRoot $_ })
if (-not $StagingOnly) {
    $targets += @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*smoke*.txt' -File | Select-Object -ExpandProperty FullName)
    foreach ($name in @('CodexUsageNotch', 'CodexUsageNotch-lite')) {
        $targets += Join-Path $projectRoot ($name + '.exe.previous')
        $targets += Join-Path $projectRoot ($name + '.exe.new')
    }
}

# Do not clean build folders while an executable inside them is running.
$runningPaths = @(Get-Process | ForEach-Object { try { $_.Path } catch { } } | Where-Object { $_ })

foreach ($target in $targets) {
    $fullPath = [IO.Path]::GetFullPath($target)
    if (-not $fullPath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Cleanup target is outside the project: $fullPath"
    }
    if (-not (Test-Path -LiteralPath $fullPath)) { continue }
    foreach ($runningPath in $runningPaths) {
        if ($runningPath.Equals($fullPath, [StringComparison]::OrdinalIgnoreCase) -or
            $runningPath.StartsWith($fullPath + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Exit the app running from $fullPath before cleanup."
        }
    }
    # Refuse links/junctions, including ancestors, to keep cleanup inside this checkout.
    $item = Get-Item -LiteralPath $fullPath -Force
    while ($null -ne $item -and $item.FullName -ne $projectRoot) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Cleanup target traverses a link: $($item.FullName)"
        }
        $item = Get-Item -LiteralPath (Split-Path -Parent $item.FullName) -Force
    }
    $links = @(Get-ChildItem -LiteralPath $fullPath -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint })
    if ($links.Count -gt 0) { throw "Cleanup target contains links: $fullPath" }
    if ($PSCmdlet.ShouldProcess($fullPath, 'Remove generated output')) {
        Remove-Item -LiteralPath $fullPath -Recurse -Force
    }
}
