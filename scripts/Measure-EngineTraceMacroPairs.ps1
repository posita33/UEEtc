param(
    [string]$EngineRoot = 'C:/Program Files/Epic Games/UE_5.8/Engine'
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$output = Join-Path $repoRoot ('artifacts/trace-pairing/' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $output -Force | Out-Null
$pattern = '\b(?<Macro>TRACE_CPUPROFILER_EVENT_SCOPE(?:_[A-Z_]+)?|TRACE_COUNTER_INCREMENT)\s*\('
$roots = @((Join-Path $EngineRoot 'Source'), (Join-Path $EngineRoot 'Plugins'))
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
        [pscustomobject]@{
            Macro = $macro
            Kind = $(if ($macro -eq 'TRACE_CPUPROFILER_EVENT_SCOPE_STR') { 'ExactStr' } elseif ($macro -eq 'TRACE_COUNTER_INCREMENT') { 'Increment' } else { 'OtherCpuScope' })
            File = $entry.data.path.text
            Line = $entry.data.line_number + ([regex]::Matches($text.Substring(0, $match.Index), "`n").Count)
        }
    }
})
$fileRows = @($records | Group-Object File | ForEach-Object {
    [pscustomobject]@{
        File = $_.Name
        ExactStr = @($_.Group | Where-Object Kind -eq 'ExactStr').Count
        Increment = @($_.Group | Where-Object Kind -eq 'Increment').Count
        OtherCpuScope = @($_.Group | Where-Object Kind -eq 'OtherCpuScope').Count
    }
})
$strFiles = @($fileRows | Where-Object ExactStr -gt 0)
$incrementFiles = @($fileRows | Where-Object Increment -gt 0)
$bothFiles = @($fileRows | Where-Object { $_.ExactStr -gt 0 -and $_.Increment -gt 0 })
$records | Export-Csv (Join-Path $output 'sites.csv') -NoTypeInformation -Encoding utf8
$fileRows | Export-Csv (Join-Path $output 'files.csv') -NoTypeInformation -Encoding utf8
$bothFiles | Export-Csv (Join-Path $output 'both-in-file.csv') -NoTypeInformation -Encoding utf8
$summary = [pscustomobject]@{
    EngineRoot = $EngineRoot
    ExactStrSites = @($records | Where-Object Kind -eq 'ExactStr').Count
    IncrementSites = @($records | Where-Object Kind -eq 'Increment').Count
    OtherCpuScopeSites = @($records | Where-Object Kind -eq 'OtherCpuScope').Count
    ExactStrFiles = $strFiles.Count
    IncrementFiles = $incrementFiles.Count
    BothInFile = $bothFiles.Count
    ExactStrWithoutIncrementFiles = @($strFiles | Where-Object Increment -eq 0).Count
    IncrementWithoutExactStrFiles = @($incrementFiles | Where-Object ExactStr -eq 0).Count
    IncrementWithoutAnyDirectCpuScopeFiles = @($incrementFiles | Where-Object { $_.ExactStr -eq 0 -and $_.OtherCpuScope -eq 0 }).Count
    Limitations = 'Textual .cpp/.h census; comment-only lines and #define excluded; branches/wrappers unresolved. File co-occurrence does not imply function or scope pairing.'
} | ConvertTo-Json
$summary | Set-Content (Join-Path $output 'summary.json') -Encoding utf8
Write-Output $summary
$bothFiles | ConvertTo-Json
Write-Output "Artifacts: $output"
