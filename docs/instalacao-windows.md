# Instalação no Windows (serviço, logs e backup)

> **Aviso:** os scripts `deploy/instalar.ps1` e `deploy/desinstalar.ps1` tiveram a **sintaxe validada** e a lógica de configuração testada em Linux, mas **não foram executados em um Windows** (criação do serviço, tarefa agendada, `icacls`, registro). Rode primeiro em uma máquina de teste e leia a saída. O programa em si (backup, logs, configuração) tem testes automatizados.

## O que será instalado

| Item | Local padrão |
|---|---|
| Programa (autocontido, não exige instalar o .NET) | `C:\BuscadorNotasSaida` |
| Configuração, banco, XMLs, logs | `C:\ProgramData\BuscadorNotasSaida` (só SYSTEM e Administradores) |
| Serviço do Windows | `BuscadorNotasSaida` (inicia com o Windows, reinicia sozinho se cair) |
| Tarefa de backup (opcional) | `BuscadorNotasSaida-Backup`, diária |

Programa e dados ficam separados: atualizar o programa não toca nos dados.

## Instalar

No PowerShell **como Administrador**, na pasta do repositório (precisa do SDK do .NET 8 para publicar; ou use `-Origem` com uma pasta já publicada):

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\deploy\instalar.ps1 -Cnpj 11222333000181 -Certificado C:\certs\empresa.pfx -SenhaAgora -PastaBackup D:\Backups\Buscador -PastaEntrada C:\Entrada
```

- `-SenhaAgora` pergunta a senha do certificado (sem exibir) e a grava na configuração do serviço (`HKLM\SYSTEM\CurrentControlSet\Services\BuscadorNotasSaida\Environment`, legível só por Administradores/SYSTEM). É o que permite o serviço subir sozinho após reiniciar. **A senha não vai para o `appsettings.json`.**
- Sem `-SenhaAgora`, envie o certificado pela interface (**Configurações**): a senha fica só na memória e **precisa ser enviada de novo após cada reinício** do serviço.
- A interface fica em `http://127.0.0.1:5080` (apenas esta máquina).

## Primeiro uso (recomendado)

1. Antes de ligar a sincronização automática, rode o diagnóstico (veja se a Sefaz devolve notas de saída para o seu CNPJ):
   ```powershell
   $env:NFE_PFX_SENHA = "..."
   & "C:\BuscadorNotasSaida\BuscadorNotas.exe" sync --diagnostico --config C:\ProgramData\BuscadorNotasSaida\appsettings.json
   ```
2. Se os resultados fizerem sentido, ligue a sincronização automática em **Configurações** (intervalo mínimo de 60 min).

## Pasta de entrada (importação automática)

Com `PastaEntrada` configurada (parâmetro `-PastaEntrada` do instalador ou no `appsettings.json`), tudo o que for colocado ali é importado sozinho, sem certificado e sem comando:

| Arquivo | O que acontece |
|---|---|
| `.xml` de NF-e | indexado e copiado (bytes originais) para `xmls\ano\mes`; só notas emitidas pelo seu CNPJ |
| `.xml` de evento de cancelamento | a nota passa a "Cancelada" (vale mesmo se a nota chegar depois) |
| `.zip` com XMLs | cada XML é tratado; mostra quantos eram novos, repetidos, ignorados e inválidos |
| `.txt` SPED Fiscal (começa com `|0000|`) | cria as chaves de saída como pendentes |
| `.csv`/`.txt` com chaves de 44 dígitos | cria as chaves como pendentes (valida o dígito verificador) |

Depois de tratado, o arquivo é **movido** (nunca apagado) para `processados\aaaa-mm`; o que não deu para ler vai para `rejeitados`, com um `.motivo.txt` ao lado. A pasta é verificada a cada 60 s (funciona em pasta de rede), e a tela **Configurações** mostra o estado, os últimos resultados e o botão "Verificar agora". Arquivos alterados há menos de 5 s (ainda sendo gravados) esperam a próxima verificação.

Pontos de atenção: a pasta **não pode** ser a pasta de XMLs nem estar dentro dela; o serviço (SYSTEM) precisa de acesso a ela; para aceitar notas de outros emitentes use `EntradaAceitarOutrosCnpjs`. Exemplo de uso: o emissor/contador exporta os XMLs do mês e você solta o `.zip` na pasta.

## Logs

Com o serviço, a saída vai para `C:\ProgramData\BuscadorNotasSaida\logs\buscador-AAAAMMDD.log` (um arquivo por dia, 30 dias). Falhas de inicialização aparecem também no Visualizador de Eventos.

## Backup

```powershell
& "C:\BuscadorNotasSaida\BuscadorNotas.exe" backup --destino D:\Backups\Buscador --manter 14 --config C:\ProgramData\BuscadorNotasSaida\appsettings.json
```

- **Banco:** cópia consistente (API de backup do SQLite) mesmo com o serviço em uso, com verificação de integridade; guarda os 14 mais recentes (`db\notas-AAAAMMDD-HHMMSS.db`).
- **XMLs:** cópia incremental (só o que é novo) em `xmls\ano\mes`. Nada é apagado da origem.
- Não inclui o **certificado** nem a senha (guarde-os separadamente e com segurança).
- Se o destino for um compartilhamento de rede, lembre que a tarefa roda como SYSTEM (usa a conta do computador na rede): teste antes. Guarde ao menos uma cópia **fora da máquina**.
- Os XMLs fiscais devem ser guardados por 5 anos (confirme o prazo com o seu contador).

### Restaurar

```powershell
Stop-Service BuscadorNotasSaida
Copy-Item C:\ProgramData\BuscadorNotasSaida\notas.db C:\ProgramData\BuscadorNotasSaida\notas.db.antes -Force
Copy-Item D:\Backups\Buscador\db\notas-AAAAMMDD-HHMMSS.db C:\ProgramData\BuscadorNotasSaida\notas.db -Force
robocopy D:\Backups\Buscador\xmls C:\ProgramData\BuscadorNotasSaida\xmls /E
Start-Service BuscadorNotasSaida
```

## Atualizar

Atualize o repositório (`git pull`) e rode `instalar.ps1` de novo com os mesmos parâmetros. Configuração e dados existentes são preservados.

## Desinstalar

```powershell
.\deploy\desinstalar.ps1                 # remove serviço, tarefa e programa; MANTÉM os dados
.\deploy\desinstalar.ps1 -RemoverDados   # também apaga banco, XMLs e configuração (pede confirmação)
```

## Segurança

- Por padrão só `localhost` acessa. Para outras máquinas é preciso `ApiToken` e, de preferência, HTTPS por proxy reverso; **não há login por usuário**.
- O serviço roda como `LocalSystem`. Se preferir uma conta dedicada, crie-a, dê acesso à pasta de dados e altere o serviço (`sc.exe config BuscadorNotasSaida obj= ...`).

## Solução de problemas

| Sintoma | O que verificar |
|---|---|
| Interface não abre | `Get-Service BuscadorNotasSaida`; log do dia; porta 5080 ocupada (use `-Porta`) |
| "Informe a senha do certificado" | Rode `instalar.ps1 -SenhaAgora` ou envie o certificado pela interface |
| Backup não roda | `Get-ScheduledTaskInfo BuscadorNotasSaida-Backup`; execute o comando de backup manualmente e leia o erro |
| Sefaz responde 656 | A interface mostra a contagem regressiva; reduza a frequência |
