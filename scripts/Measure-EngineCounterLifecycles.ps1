param([string]$EngineRoot = 'C:/Program Files/Epic Games/UE_5.8/Engine')
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$output = Join-Path $repoRoot ('artifacts/counter-lifecycles/' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $output -Force | Out-Null
$pattern = 'TRACE_COUNTER_(?<Operation>INCREMENT|DECREMENT|ADD|SUBTRACT|SET|SET_IF_DIFFERENT|ADD_IF_NOT_ZERO|SUBTRACT_IF_NOT_ZERO)\s*\(\s*(?<Identifier>[A-Za-z_]\w*)'
$roots = @((Join-Path $EngineRoot 'Source'), (Join-Path $EngineRoot 'Plugins'))
$lines = & rg --json --multiline -g '*.cpp' -g '*.h' $pattern @roots
if ($LASTEXITCODE -gt 1) { throw 'Engine search failed.' }
$sites = @(foreach ($line in $lines) {
    $entry = $line | ConvertFrom-Json
    if ($entry.type -ne 'match') { continue }
    $text = $entry.data.lines.text
    if ($text.TrimStart() -match '^(//|/\*|\*|#\s*define)') { continue }
    foreach ($match in [regex]::Matches($text, $pattern)) {
        [pscustomobject]@{
            Identifier=$match.Groups['Identifier'].Value; Operation=$match.Groups['Operation'].Value
            File=$entry.data.path.text
            Line=$entry.data.line_number + ([regex]::Matches($text.Substring(0,$match.Index), "`n").Count)
        }
    }
})
$sites | Export-Csv (Join-Path $output 'sites.csv') -NoTypeInformation -Encoding utf8
$groups = @($sites | Group-Object Identifier | Where-Object { $_.Group.Operation -contains 'INCREMENT' } | ForEach-Object {
    $operations = @($_.Group.Operation | Sort-Object -Unique)
    [pscustomobject]@{
        Identifier=$_.Name; Increments=@($_.Group | Where-Object Operation -eq 'INCREMENT').Count
        Operations=$operations -join ','
        HasDecrease=($operations -contains 'DECREMENT' -or $operations -contains 'SUBTRACT' -or $operations -contains 'SUBTRACT_IF_NOT_ZERO')
        HasSet=($operations -contains 'SET' -or $operations -contains 'SET_IF_DIFFERENT')
    }
})
$groups | Export-Csv (Join-Path $output 'increment-lifecycles.csv') -NoTypeInformation -Encoding utf8
$summary = [pscustomobject]@{
    IncrementSites=@($sites | Where-Object Operation -eq 'INCREMENT').Count
    IncrementNames=$groups.Count
    NamesWithDecrease=@($groups | Where-Object HasDecrease).Count
    NamesWithSet=@($groups | Where-Object HasSet).Count
    NamesWithDecreaseOrSet=@($groups | Where-Object { $_.HasDecrease -or $_.HasSet }).Count
    IncrementSitesWithDecreaseOrSet=($groups | Where-Object { $_.HasDecrease -or $_.HasSet } | Measure-Object Increments -Sum).Sum
    Limitations='Textual .cpp/.h inventory; comment-only and #define lines excluded. Does not resolve namespace, linkage, wrappers, runtime frequency or semantics. Other operations require review; not an automatic gauge classification.'
}
$summary | ConvertTo-Json | Set-Content (Join-Path $output 'summary.json') -Encoding utf8
$summary | ConvertTo-Json
$groups | Where-Object { $_.HasDecrease -or $_.HasSet } | Format-Table
Write-Output "Artifacts: $output"
