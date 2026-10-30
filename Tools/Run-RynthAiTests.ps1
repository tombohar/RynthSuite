<#
.SYNOPSIS
    Builds and runs RynthAi's offline tests: both runners, no game, no engine, no deploy.

.DESCRIPTION
    Tools\RynthCore.RynthAiTests      pure logic (nav route files, stall watchdog, weakness data)
    Tools\RynthCore.RynthAiHostTests  the plugin on a fake engine host (loot, metas, nav walking,
                                      buffs, weapon choice, damage text, target range)

    Each test prints PASS, FAIL, KNOWN (a real bug that is registered and not fixed yet) or
    XPASS (a KNOWN test that now passes, so its marker should be removed).

    Exit code: 0 when nothing unexpected happened (KNOWN is expected); 1 when a runner failed
    to build, a test FAILed or XPASSed, or a runner could not run.

    Nothing is written outside the two runners' bin folders: the host runner points every file
    RynthAi would write (pvars, gvars, ItemGiver) at a scratch folder next to its exe.

.PARAMETER Filter
    Only run tests whose name contains this text (for example "nav" or "buff").

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File Tools\Run-RynthAiTests.ps1
.EXAMPLE
    pwsh Tools\Run-RynthAiTests.ps1 -Filter rebuff
#>
param([string]$Filter = "")

$ErrorActionPreference = 'Continue'
$runners = @('RynthCore.RynthAiTests', 'RynthCore.RynthAiHostTests')
$problems = @()
$summaries = @()
$ranAny = $false

foreach ($name in $runners) {
    $proj = Join-Path $PSScriptRoot "$name\$name.csproj"
    Write-Host ""
    Write-Host "=== Building $name ===" -ForegroundColor Cyan
    & dotnet build $proj -c Release -nologo -v q | Where-Object { $_ -match 'error|Error\(s\)' } | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) {
        $problems += "$name did not build"
        continue
    }

    Write-Host "=== Running $name ===" -ForegroundColor Cyan
    $runArgs = @('run', '--project', $proj, '-c', 'Release', '--no-build')
    if ($Filter) { $runArgs += @('--', $Filter) }
    $lines = @()
    & dotnet @runArgs 2>&1 | ForEach-Object {
        $line = "$_"
        $lines += $line
        if ($line -match '^\[FAIL\]|^\[XPASS\]') { Write-Host $line -ForegroundColor Red }
        elseif ($line -match '^\[KNOWN\]') { Write-Host $line -ForegroundColor Yellow }
        else { Write-Host $line }
    }
    $code = $LASTEXITCODE
    $summary = $lines | Where-Object { $_ -match 'test\(s\), .* check\(s\)' } | Select-Object -Last 1
    if ($summary -and $summary -notmatch '^0 test') { $summaries += "${name}: $summary"; $ranAny = $true }

    if ($code -ne 0) {
        if ($Filter -and ($lines -match 'ABORT: no test matches')) {
            $summaries += "${name}: no test matches '$Filter'"
        } else {
            $problems += "$name had unexpected failures (exit $code)"
        }
    }
}

if ($Filter -and -not $ranAny -and $problems.Count -eq 0) { $problems += "no test matches '$Filter'" }

Write-Host ""
Write-Host "=== Summary ===" -ForegroundColor Cyan
$summaries | ForEach-Object { Write-Host $_ }
if ($problems.Count -gt 0) {
    $problems | ForEach-Object { Write-Host "PROBLEM: $_" -ForegroundColor Red }
    exit 1
}
Write-Host "No unexpected failures (KNOWN tests are registered bugs)." -ForegroundColor Green
exit 0
