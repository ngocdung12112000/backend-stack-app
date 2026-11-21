<#
.SYNOPSIS  Scaffold a new service from the stackshop-service template and add it to the root solution.
.EXAMPLE   pwsh tools/new-service.ps1 -Name Ordering
#>
param([Parameter(Mandatory)][string]$Name)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$lower = $Name.ToLower()
$out = Join-Path $root "services/$lower"

if (Test-Path $out) { throw "services/$lower already exists." }

dotnet new install (Join-Path $PSScriptRoot 'templates/stackshop-service') | Out-Null
dotnet new stackshop-service -n $Name -o $out
if ($LASTEXITCODE -ne 0) { throw 'dotnet new failed.' }

Push-Location $root
try {
  dotnet sln StackShop.Backend.sln add `
    "services/$lower/src/$Name.Domain" "services/$lower/src/$Name.Application" `
    "services/$lower/src/$Name.Infrastructure" "services/$lower/src/$Name.Api" `
    "services/$lower/tests/$Name.Tests"
} finally { Pop-Location }

Write-Host "Created services/$lower. Run: dotnet test services/$lower/$Name.sln"
