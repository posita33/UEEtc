param(
    [string]$EngineRoot = 'C:/Program Files/Epic Games/UE_5.8/Engine'
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$output = Join-Path $repoRoot ('artifacts/engine-counters/' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $output -Force | Out-Null
$pattern = '(?<Macro>TRACE_COUNTER_INCREMENT|TRACE_DECLARE_(?:(?:UNCHECKED|ATOMIC|INT|FLOAT|MEMORY)_)*COUNTER(?:_EXTERN)?)\s*\(\s*(?<Identifier>[A-Za-z_]\w*)'
$roots = @((Join-Path $EngineRoot 'Source'), (Join-Path $EngineRoot 'Plugins'))
# Textual census: preprocessor branches and macro wrappers are not expanded.
$jsonLines = & rg --json --multiline -g '*.cpp' -g '*.h' $pattern @roots
if ($LASTEXITCODE -gt 1) { throw 'Engine search failed.' }
$records = @(foreach ($jsonLine in $jsonLines) {
    $entry = $jsonLine | ConvertFrom-Json
    if ($entry.type -ne 'match') { continue }
    $text = $entry.data.lines.text
    $trimmed = $text.TrimStart()
    if ($trimmed.StartsWith('//') -or $trimmed.StartsWith('/*') -or $trimmed.StartsWith('*') -or $trimmed -match '^#\s*define') { continue }
    foreach ($match in [regex]::Matches($text, $pattern)) {
        $macro = $match.Groups['Macro'].Value
        if ($macro -eq 'TRACE_DECLARE_COUNTER') { continue }
        $prefix = $text.Substring(0, $match.Index)
        [pscustomobject]@{
            Macro = $macro; Identifier = $match.Groups['Identifier'].Value
            Kind = $(if ($macro -eq 'TRACE_COUNTER_INCREMENT') { 'Increment' } elseif ($macro.EndsWith('_EXTERN')) { 'Extern' } else { 'Definition' })
            File = $entry.data.path.text; Line = $entry.data.line_number + ([regex]::Matches($prefix, "`n").Count)
            Source = $match.Value
        }
    }
})
$increments = @($records | Where-Object Kind -eq 'Increment')
$definitions = @($records | Where-Object Kind -eq 'Definition')
$usageGroups = @($increments | Group-Object Identifier)
$duplicateDefinitions = @($definitions | Group-Object Identifier | Where-Object Count -gt 1)
$definitionCounts = @{}
$definitions | Group-Object Identifier | ForEach-Object { $definitionCounts[$_.Name] = $_.Count }
$records | Export-Csv (Join-Path $output 'sites.csv') -NoTypeInformation -Encoding utf8
$usageGroups | Sort-Object Count -Descending | Select-Object Name,Count |
    Export-Csv (Join-Path $output 'increment-counts.csv') -NoTypeInformation -Encoding utf8
$duplicateDefinitions | ForEach-Object { $_.Group } |
    Export-Csv (Join-Path $output 'repeated-definitions.csv') -NoTypeInformation -Encoding utf8
$summary = [pscustomobject]@{
    EngineRoot = $EngineRoot
    IncrementSites = $increments.Count
    IncrementIdentifiers = $usageGroups.Count
    ReusedIncrementIdentifiers = @($usageGroups | Where-Object Count -gt 1).Count
    DefinitionSites = $definitions.Count
    DefinitionIdentifiers = @($definitions | Group-Object Identifier).Count
    RepeatedDefinitionIdentifiers = $duplicateDefinitions.Count
    IncrementIdentifiersWithoutSingleDefinition = @($usageGroups | Where-Object { $definitionCounts[$_.Name] -ne 1 }).Count
    ExternSites = @($records | Where-Object Kind -eq 'Extern').Count
    Limitations = 'Textual .cpp/.h census; comment-only lines and #define excluded; conditional branches, namespaces and wrappers not resolved.'
} | ConvertTo-Json
$summary | Set-Content -LiteralPath (Join-Path $output 'summary.json') -Encoding utf8
Write-Output $summary
$usageGroups | Sort-Object Count -Descending | Select-Object -First 12 Name,Count | Format-Table
$duplicateDefinitions | Select-Object Name,Count | Format-Table
Write-Output "Artifacts: $output"
