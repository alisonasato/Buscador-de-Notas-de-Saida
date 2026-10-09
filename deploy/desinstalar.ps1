<#
.SYNOPSIS
  Remove o serviço, a tarefa de backup e os arquivos do programa. Por padrão MANTÉM os dados (banco, XMLs, configuração).
#>
#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [string]$PastaInstalacao = "C:\BuscadorNotasSaida",
    [string]$PastaDados = "C:\ProgramData\BuscadorNotasSaida",
    [switch]$RemoverDados
)

$ErrorActionPreference = "Stop"
$NomeServico = "BuscadorNotasSaida"
$NomeTarefa = "BuscadorNotasSaida-Backup"

if ($RemoverDados) {
    Write-Warning "Isto apagará DEFINITIVAMENTE o banco, os XMLs e a configuração em $PastaDados."
    Write-Warning "Os XMLs fiscais devem ser guardados por 5 anos (confirme o prazo com seu contador). Faça backup antes."
    $conf = Read-Host "Digite REMOVER para confirmar"
    if ($conf -cne "REMOVER") { Write-Host "Cancelado."; return }
}

if (Get-Service -Name $NomeServico -ErrorAction SilentlyContinue) {
    Stop-Service -Name $NomeServico -Force -ErrorAction SilentlyContinue
    (Get-Service -Name $NomeServico).WaitForStatus("Stopped", [TimeSpan]::FromSeconds(60))
    & sc.exe delete $NomeServico | Out-Null
    Write-Host "Serviço removido."
}
if (Get-ScheduledTask -TaskName $NomeTarefa -ErrorAction SilentlyContinue) {
    Unregister-ScheduledTask -TaskName $NomeTarefa -Confirm:$false
    Write-Host "Tarefa de backup removida."
}
if (Test-Path -LiteralPath $PastaInstalacao) {
    Remove-Item -LiteralPath $PastaInstalacao -Recurse -Force
    Write-Host "Programa removido de $PastaInstalacao."
}
if ($RemoverDados -and (Test-Path -LiteralPath $PastaDados)) {
    Remove-Item -LiteralPath $PastaDados -Recurse -Force
    Write-Host "Dados removidos de $PastaDados."
}
elseif (Test-Path -LiteralPath $PastaDados) {
    Write-Host "Dados preservados em $PastaDados."
}
