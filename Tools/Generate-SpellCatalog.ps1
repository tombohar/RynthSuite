#Requires -Version 5.1
<#
.SYNOPSIS
  Builds RynthAi's embedded spell catalog (Combat\SpellCatalog.tsv) from ACECustom's
  Reports\SpellCatalog-Full.csv.

.DESCRIPTION
  The Spells window and buff profiles list spells from this catalog. ACECustom's report
  is the source of truth for level, school, Self/Other/Item target, effect type and spell
  family on the ILT server. The full CSV is ~13 MB because of its Supersedes columns;
  this keeps only the columns RynthAi reads, one spell per line, tab separated:

    Id  Name  Level  Effect  Target  FamilyId  FamilyName  Power  DurationSec  School  Mana  Description

  Re-run after ACECustom regenerates its catalog (Scripts\export_spell_catalog.py).

.PARAMETER Source
  ACECustom SpellCatalog-Full.csv.

.PARAMETER Out
  Output TSV (UTF-8, no BOM).
#>
param(
    [string]$Source = (Join-Path $PSScriptRoot '..\..\ACECustom\Reports\SpellCatalog-Full.csv'),
    [string]$Out    = (Join-Path $PSScriptRoot '..\Plugins\RynthCore.Plugin.RynthAi\Combat\SpellCatalog.tsv')
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $Source)) { throw "ACECustom spell catalog not found: $Source" }

# Tabs and line breaks would break the one-row-per-line format.
function Clean([string]$s) {
    if ($null -eq $s) { return '' }
    return ($s -replace "[`t`r`n]+", ' ').Trim()
}

$rows = Import-Csv -Path $Source
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add('# RynthAi spell catalog generated from ACECustom Reports\SpellCatalog-Full.csv by Tools\Generate-SpellCatalog.ps1. Do not edit by hand.')
$lines.Add("# Id`tName`tLevel`tEffect`tTarget`tFamilyId`tFamilyName`tPower`tDurationSec`tSchool`tMana`tDescription")

foreach ($r in ($rows | Sort-Object { [int]$_.SpellId })) {
    $fields = @(
        (Clean $r.SpellId), (Clean $r.Name), (Clean $r.Level), (Clean $r.EffectType), (Clean $r.Target),
        (Clean $r.FamilyId), (Clean $r.FamilyName), (Clean $r.Power), (Clean $r.DurationSec),
        (Clean $r.School), (Clean $r.Mana), (Clean $r.Description)
    )
    $lines.Add($fields -join "`t")
}

$outFull = [System.IO.Path]::GetFullPath($Out)
[System.IO.File]::WriteAllLines($outFull, $lines, (New-Object System.Text.UTF8Encoding($false)))
Write-Host ("Wrote {0} spells to {1} ({2:N0} bytes)" -f ($lines.Count - 2), $outFull, (Get-Item $outFull).Length)
