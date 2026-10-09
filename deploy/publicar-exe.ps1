<#
.SYNOPSIS
  Gera o executável único para Windows (dist\BuscadorNotas.exe): autocontido (não exige instalar o .NET),
  com a interface web embutida e o SQLite incluído. Requer o SDK do .NET 8.
#>
[CmdletBinding()]
param([string]$Saida = "dist")

$ErrorActionPreference = "Stop"
$raiz = Split-Path -Parent $PSScriptRoot
& dotnet publish (Join-Path $raiz "src\BuscadorNotas") -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -p:DebugSymbols=false -o $Saida
if ($LASTEXITCODE -ne 0) { throw "dotnet publish falhou (código $LASTEXITCODE)." }
Write-Host "Pronto: $(Join-Path (Resolve-Path $Saida) 'BuscadorNotas.exe')" -ForegroundColor Green
