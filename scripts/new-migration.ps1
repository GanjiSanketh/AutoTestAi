#Requires -Version 5.1
<#
.SYNOPSIS
  Adds a new EF Core migration for the Phase-0 model.
.EXAMPLE
  .\scripts\new-migration.ps1 -Name AddTestSuites
#>
param(
  [Parameter(Mandatory = $true)]
  [string]$Name
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $root
dotnet ef migrations add $Name `
  --project src/AutoTestAi.Infrastructure `
  --startup-project src/AutoTestAi.Api `
  --output-dir Persistence/Migrations
