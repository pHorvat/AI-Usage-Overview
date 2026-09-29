param([string]$ProjectFile = (Join-Path (Join-Path $PSScriptRoot '..') 'CodexUsageNotch.csproj'))
$ErrorActionPreference = 'Stop'

$path = [IO.Path]::GetFullPath($ProjectFile)
$content = [IO.File]::ReadAllText($path)
$pattern = '<Version>(\d+)\.(\d+)\.(\d+)(?:\.(\d+))?</Version>'
$match = [regex]::Match($content, $pattern)
if (-not $match.Success -or [regex]::Matches($content, '<Version>').Count -ne 1) {
    throw 'Expected one numeric Version element in the project file.'
}
$revision = if ($match.Groups[4].Success) { [int]$match.Groups[4].Value } else { 0 }
if ($revision -ge 65534) { throw 'The fourth version component has reached its limit. Increase the base version.' }
$next = '{0}.{1}.{2}.{3}' -f $match.Groups[1].Value, $match.Groups[2].Value, $match.Groups[3].Value, ($revision + 1)
$updated = $content.Remove($match.Index, $match.Length).Insert($match.Index, "<Version>$next</Version>")
[IO.File]::WriteAllText($path, $updated, [Text.UTF8Encoding]::new($false))
Write-Output $next
