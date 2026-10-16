$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$fixture = Join-Path $repoRoot ('artifacts/trace-pairing-fixtures/' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $fixture 'Source'), (Join-Path $fixture 'Plugins/Example') -Force | Out-Null
$utf8 = [System.Text.UTF8Encoding]::new($false)
$examples = @{
    'Source/Timer.cpp' = 'void Timer() { TRACE_CPUPROFILER_EVENT_SCOPE_STR("Time"); }'
    'Source/Counter.cpp' = 'void Count() { TRACE_COUNTER_INCREMENT(Hits); }'
    'Source/DifferentFunctions.cpp' = "void Timer() { TRACE_CPUPROFILER_EVENT_SCOPE_STR(`"Time`"); }`nvoid Count() { TRACE_COUNTER_INCREMENT(Hits); }"
    'Source/OtherScope.cpp' = 'void Work() { TRACE_CPUPROFILER_EVENT_SCOPE(Work); TRACE_COUNTER_INCREMENT(Hits); }'
    'Source/Conditional.cpp' = "#if A`nvoid Timer() { TRACE_CPUPROFILER_EVENT_SCOPE_STR(`"Time`"); }`n#else`nvoid Count() { TRACE_COUNTER_INCREMENT(Hits); }`n#endif"
    'Source/Ignored.cpp' = "// TRACE_COUNTER_INCREMENT(CommentOnly);`n#define A TRACE_CPUPROFILER_EVENT_SCOPE_STR(`"DefinitionLine`")"
    'Plugins/Example/Counter.h' = "void Count() { TRACE_COUNTER_INCREMENT(`n Hits); }"
}
foreach ($relative in $examples.Keys) {
    [System.IO.File]::WriteAllText((Join-Path $fixture $relative), $examples[$relative], $utf8)
}
$result = @(& (Join-Path $PSScriptRoot 'Measure-EngineTraceMacroPairs.ps1') -EngineRoot $fixture)
$artifactLine = @($result | Where-Object { $_ -is [string] -and $_.StartsWith('Artifacts: ') })
if ($artifactLine.Count -ne 1) { throw 'Missing artifact output.' }
$output = $artifactLine[0].Substring('Artifacts: '.Length)
$summary = Get-Content (Join-Path $output 'summary.json') -Raw -Encoding utf8 | ConvertFrom-Json
$expected = @{
    ExactStrSites = 3; IncrementSites = 5; OtherCpuScopeSites = 1
    ExactStrFiles = 3; IncrementFiles = 5; BothInFile = 2
    ExactStrWithoutIncrementFiles = 1; IncrementWithoutExactStrFiles = 3
    IncrementWithoutAnyDirectCpuScopeFiles = 2
}
foreach ($key in $expected.Keys) {
    if ($summary.$key -ne $expected[$key]) { throw "$key expected $($expected[$key]), actual $($summary.$key)" }
}
$both = Import-Csv (Join-Path $output 'both-in-file.csv')
if (@($both | Where-Object { $_.File.EndsWith('DifferentFunctions.cpp') }).Count -ne 1 -or
    @($both | Where-Object { $_.File.EndsWith('Conditional.cpp') }).Count -ne 1) {
    throw 'File co-occurrence fixture was not detected.'
}
Write-Output 'PASS: 9 census assertions and co-occurrence fixtures (different functions / alternative preprocessor branches).'
Write-Output "Artifacts: $output"
