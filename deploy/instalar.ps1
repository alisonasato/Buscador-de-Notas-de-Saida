<#
.SYNOPSIS
  Instala (ou atualiza) o Buscador de Notas de Saída como serviço do Windows.

.DESCRIPTION
  - publica o programa (autocontido, não exige instalar o .NET) ou usa uma pasta já publicada (-Origem);
  - copia para -PastaInstalacao e guarda configuração, banco e XMLs em -PastaDados (separados, para atualizar sem risco);
  - cria o serviço "BuscadorNotasSaida" (inicia com o Windows e reinicia sozinho se cair);
  - opcionalmente agenda o backup diário.
  Pode ser executado de novo para atualizar: a configuração e os dados existentes são preservados.

.EXAMPLE
  .\deploy\instalar.ps1 -Cnpj 11222333000181 -Certificado C:\certs\empresa.pfx -SenhaAgora -PastaBackup D:\Backups\Buscador -PastaEntrada C:\Entrada
#>
#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Cnpj,
    [string]$Certificado,
    [string]$PastaInstalacao = "C:\BuscadorNotasSaida",
    [string]$PastaDados = "C:\ProgramData\BuscadorNotasSaida",
    [string]$PastaBackup,
    [string]$PastaEntrada,
    [string]$HoraBackup = "02:00",
    [string]$Origem,
    [int]$Porta = 5080,
    [switch]$SenhaAgora
)

$ErrorActionPreference = "Stop"
$NomeServico = "BuscadorNotasSaida"
$NomeTarefa = "BuscadorNotasSaida-Backup"

function Escrever([string]$msg) { Write-Host "==> $msg" -ForegroundColor Cyan }

# ---------- validações ----------
$cnpjNumeros = ($Cnpj -replace "\D", "")
if ($cnpjNumeros.Length -ne 14) { throw "O CNPJ deve ter 14 dígitos (recebido: '$Cnpj')." }
if ($Certificado -and -not (Test-Path -LiteralPath $Certificado)) { throw "Certificado não encontrado: $Certificado" }
if ($HoraBackup -notmatch "^([01]\d|2[0-3]):[0-5]\d$") { throw "HoraBackup deve estar no formato HH:mm (ex.: 02:00)." }

# ---------- 1. obter os binários ----------
$raizRepo = Split-Path -Parent $PSScriptRoot
if (-not $Origem) {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        throw "O SDK do .NET 8 não foi encontrado para publicar. Instale-o ou passe -Origem com uma pasta já publicada (dotnet publish)."
    }
    $Origem = Join-Path ([IO.Path]::GetTempPath()) ("buscador-publish-" + [Guid]::NewGuid().ToString("N"))
    Escrever "Publicando (isso pode levar alguns minutos)..."
    & dotnet publish (Join-Path $raizRepo "src\BuscadorNotas") -c Release -r win-x64 --self-contained true -o $Origem
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish falhou (código $LASTEXITCODE)." }
}
$exeOrigem = Join-Path $Origem "BuscadorNotas.exe"
if (-not (Test-Path -LiteralPath $exeOrigem)) { throw "BuscadorNotas.exe não encontrado em '$Origem'." }

# ---------- 2. parar o serviço (se for atualização) ----------
$servicoExistia = $null -ne (Get-Service -Name $NomeServico -ErrorAction SilentlyContinue)
if ($servicoExistia) {
    Escrever "Parando o serviço existente..."
    Stop-Service -Name $NomeServico -Force -ErrorAction SilentlyContinue
    (Get-Service -Name $NomeServico).WaitForStatus("Stopped", [TimeSpan]::FromSeconds(60))
}

# ---------- 3. copiar o programa ----------
Escrever "Copiando o programa para $PastaInstalacao ..."
New-Item -ItemType Directory -Force -Path $PastaInstalacao | Out-Null
# /MIR só na pasta do programa (nunca na pasta de dados)
& robocopy $Origem $PastaInstalacao /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy falhou (código $LASTEXITCODE)." }
$exe = Join-Path $PastaInstalacao "BuscadorNotas.exe"

# ---------- 4. dados e configuração ----------
New-Item -ItemType Directory -Force -Path $PastaDados | Out-Null
$arquivoConfig = Join-Path $PastaDados "appsettings.json"

if (-not (Test-Path -LiteralPath $arquivoConfig)) {
    $pfxDestino = ""
    if ($Certificado) {
        $pfxDestino = Join-Path $PastaDados "certificado.pfx"
        Copy-Item -LiteralPath $Certificado -Destination $pfxDestino -Force
    }
    $cfg = [ordered]@{
        Cnpj                          = $cnpjNumeros
        CertificadoPfx                = $pfxDestino
        Ambiente                      = 1
        PastaXml                      = "xmls"
        BancoSqlite                   = "notas.db"
        PastaBackup                   = $(if ($PastaBackup) { $PastaBackup } else { "" })
        PastaEntrada                  = $(if ($PastaEntrada) { $PastaEntrada } else { "" })
        ApiUrl                        = "http://127.0.0.1:$Porta"
        SincronizacaoAutomatica       = $false
        EsperaSemNovosMinutos         = 90
        EsperaConsumoIndevidoMinutos  = 65
        PausaEntreRequisicoesSegundos = 2
    }
    $json = $cfg | ConvertTo-Json -Depth 4
    [IO.File]::WriteAllText($arquivoConfig, $json, (New-Object System.Text.UTF8Encoding($false)))
    Escrever "Configuração criada em $arquivoConfig"
}
else {
    Write-Warning "Configuração já existe e foi preservada: $arquivoConfig"
    if ($Certificado) {
        Copy-Item -LiteralPath $Certificado -Destination (Join-Path $PastaDados "certificado.pfx") -Force
        Write-Warning "Certificado copiado para $PastaDados\certificado.pfx (confira CertificadoPfx no appsettings.json)."
    }
}

if ($PastaEntrada) {
    New-Item -ItemType Directory -Force -Path $PastaEntrada | Out-Null
    Write-Warning "A pasta de entrada ($PastaEntrada) mantém as permissões atuais: dê acesso de escrita a quem vai colocar os arquivos (emissor/contador) e de leitura/escrita à conta do serviço (SYSTEM)."
}

# Só SYSTEM e Administradores acessam a pasta de dados (SIDs: independem do idioma do Windows).
& icacls $PastaDados /inheritance:r /grant:r "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "icacls falhou ao restringir o acesso a $PastaDados." }

# ---------- 5. serviço ----------
if (-not $servicoExistia) {
    Escrever "Criando o serviço $NomeServico ..."
    New-Service -Name $NomeServico -BinaryPathName "`"$exe`" serve" `
        -DisplayName "Buscador de Notas de Saída" `
        -Description "Interface web e sincronização de NF-e de saída (SEFAZ). Acesso: http://127.0.0.1:$Porta" `
        -StartupType Automatic | Out-Null
}
else {
    # atualização: garante que o caminho aponta para a pasta atual
    & sc.exe config $NomeServico binPath= "`"$exe`" serve" | Out-Null
}
& sc.exe config $NomeServico start= delayed-auto | Out-Null
# reinicia sozinho após falhas (1 min, 1 min, 5 min); zera o contador após 1 dia
& sc.exe failure $NomeServico reset= 86400 actions= restart/60000/restart/60000/restart/300000 | Out-Null

# Variáveis de ambiente do serviço ficam em HKLM\...\Services\<nome>\Environment (REG_MULTI_SZ).
$chaveReg = "HKLM:\SYSTEM\CurrentControlSet\Services\$NomeServico"
$ambiente = @()
$atual = (Get-ItemProperty -Path $chaveReg -Name Environment -ErrorAction SilentlyContinue).Environment
if ($atual) { $ambiente = @($atual) }

function DefinirVariavel([string[]]$lista, [string]$nome, [string]$valor) {
    $sem = @($lista | Where-Object { $_ -notlike "$nome=*" })
    return @($sem + "$nome=$valor")
}
$ambiente = DefinirVariavel $ambiente "BUSCADOR_CONFIG" $arquivoConfig

if ($SenhaAgora) {
    $segura = Read-Host "Senha do certificado A1 (não será exibida)" -AsSecureString
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($segura)
    try { $senha = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
    if (-not $senha) { throw "Senha vazia." }
    $ambiente = DefinirVariavel $ambiente "NFE_PFX_SENHA" $senha
    Escrever "Senha gravada na configuração do serviço (legível apenas por Administradores/SYSTEM)."
}
Set-ItemProperty -Path $chaveReg -Name Environment -Value $ambiente -Type MultiString

# ---------- 6. backup diário ----------
if ($PastaBackup) {
    Escrever "Agendando backup diário às $HoraBackup ..."
    $acao = New-ScheduledTaskAction -Execute $exe -Argument "backup --config `"$arquivoConfig`""
    $gatilho = New-ScheduledTaskTrigger -Daily -At $HoraBackup
    $principal = New-ScheduledTaskPrincipal -UserId "SYSTEM" -LogonType ServiceAccount -RunLevel Highest
    $config = New-ScheduledTaskSettingsSet -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Hours 2)
    Register-ScheduledTask -TaskName $NomeTarefa -Action $acao -Trigger $gatilho -Principal $principal -Settings $config -Force | Out-Null
}

# ---------- 7. iniciar e verificar ----------
Escrever "Iniciando o serviço..."
Start-Service -Name $NomeServico
$ok = $false
for ($i = 0; $i -lt 30 -and -not $ok; $i++) {
    Start-Sleep -Seconds 1
    try {
        $r = Invoke-RestMethod -UseBasicParsing -Uri "http://127.0.0.1:$Porta/api/sync/status" -TimeoutSec 3
        $ok = $true
    }
    catch { }
}
if (-not $ok) {
    Write-Warning "O serviço iniciou, mas a interface não respondeu em 30 s. Veja os logs em $PastaDados\logs e o Visualizador de Eventos."
}
else {
    Write-Host ""
    Write-Host "Pronto! Interface: http://127.0.0.1:$Porta" -ForegroundColor Green
}

Write-Host ""
Write-Host "Próximos passos:"
if (-not $Certificado -and -not $SenhaAgora) {
    Write-Host " - Abra a interface > Configurações e envie o certificado A1 (a senha fica só na memória) OU rode de novo com -Certificado e -SenhaAgora."
}
elseif (-not $SenhaAgora) {
    Write-Host " - Defina a senha do certificado: rode de novo com -SenhaAgora (ou envie o certificado pela interface)."
}
Write-Host " - Configurações > ative a sincronização automática, se desejar (comece com 'sync --diagnostico', veja o guia)."
if (-not $PastaEntrada) { Write-Host " - Para importar XMLs/SPED automaticamente, rode de novo com -PastaEntrada C:\Entrada (ou edite PastaEntrada no appsettings.json)." }
if (-not $PastaBackup) { Write-Host " - Sem backup agendado: rode de novo com -PastaBackup D:\Backups\Buscador." }
Write-Host " - Guia completo: docs\instalacao-windows.md"
