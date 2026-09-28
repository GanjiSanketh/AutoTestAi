#Requires -Version 5.1
<#
.SYNOPSIS
  Restores, builds and tests the .NET solution (Phase-0 validation).
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $root
dotnet restore AutoTestAi.sln
dotnet build AutoTestAi.sln --no-restore -c Release
dotnet test AutoTestAi.sln --no-build -c Release
