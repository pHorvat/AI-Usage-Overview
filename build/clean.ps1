[CmdletBinding(SupportsShouldProcess = $true)]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$rootPrefix = $projectRoot + [IO.Path]::DirectorySeparatorChar
$relativePaths = @(
    'bin', 'obj', 'tests\bin', 'tests\obj',
    'build\output', 'build\previews', 'build\protocol-review'
)
$targets = @($relativePaths | ForEach-Object { Join-Path $projectRoot $_ })
$targets += @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*smoke*.txt' -File | Select-Object -ExpandProperty FullName)

foreach ($target in $targets) {
    $fullPath = [IO.Path]::GetFullPath($target)
    if (-not $fullPath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Cleanup target is outside the project: $fullPath"
    }
    if (-not (Test-Path -LiteralPath $fullPath)) { continue }
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
