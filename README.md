# Buscador de Notas de Saída

Sistema em C# (.NET 8) de custo zero para obter, guardar e **pesquisar** NF-e de saída, seguindo o `guia_de_desenvolvimento.md`:

1. **Robô de NSU** (`sync`) – consulta `nfeDistDFeInteresse` com certificado A1, descompacta `docZip`, grava os XMLs em `{PastaXml}/{ano}/{mes}/{chave}.xml` e indexa no SQLite.
2. **Resgate histórico** (`importar-sped` + `baixar-pendentes`) – lê registros `C100` de saída do SPED Fiscal, cria pendências e consulta cada chave via `nfeConsultaProtocolo`.
3. **Importações manuais** (`importar-xml`, `importar-chaves`) – não precisam de certificado: indexam XMLs obtidos do emissor/contador e chaves vindas de planilhas. Por padrão só aceitam notas emitidas pelo `Cnpj` configurado (`--todos` desativa).
4. **Busca** (`buscar`) – pesquisa local por chave, número, série, período, destinatário, valor e status.

## Uso

```bash
cp src/BuscadorNotas/appsettings.exemplo.json src/BuscadorNotas/appsettings.json   # preencha
export NFE_PFX_SENHA='senha-do-certificado'                                       # nunca no arquivo

dotnet run --project src/BuscadorNotas -- sync --loop
dotnet run --project src/BuscadorNotas -- importar-sped SPED_2024_01.txt SPED_2024_02.txt
dotnet run --project src/BuscadorNotas -- baixar-pendentes --max 50
dotnet run --project src/BuscadorNotas -- importar-xml C:\\EmissorAntigo\\XML      # indexa XMLs de saída de uma pasta (copia p/ ano/mes)
dotnet run --project src/BuscadorNotas -- importar-chaves chaves.csv                 # lista/CSV com chaves de 44 dígitos (valida o DV)
dotnet run --project src/BuscadorNotas -- buscar --de 2024-10-01 --ate 2024-10-31 --destinatario "cliente" --vmin 100
dotnet test
```

## Tipos de documento

| Tipo | Como entra | Cancelamento |
|---|---|---|
| NF-e (55) e NFC-e (65) | XML/ZIP na pasta de entrada ou linha de comando; **NF-e também pela sincronização com a Sefaz** (a confirmar) e pelo SPED (chaves) | evento 110111/110112 |
| CT-e (57) e CT-e OS (67) | XML/ZIP | evento 110111 (`procEventoCTe`) |
| MDF-e (58) | XML/ZIP | evento 110111 (`procEventoMDFe`); o 110112 é *encerramento* e **não** cancela |
| CF-e SAT (59) | XML/ZIP | `CFeCanc` (aponta o original em `chCanc`) |
| NFS-e | XML/ZIP, melhor esforço (ABRASF e padrão nacional) | só se o XML trouxer o cancelamento |

- Só entram documentos **emitidos pelo seu CNPJ** (para NFS-e, o prestador). Pode filtrar por tipo na tela, na API (`?tipo=CTE`) e no CSV.
- O **MDF-e** é um manifesto de transporte: fica fora da contagem e do faturamento do mês (o valor dele é o da carga). O painel mostra a contagem por tipo.
- A **NFS-e** não tem chave de 44 dígitos: o identificador é `NFSE-{CNPJ do prestador}-{município}-{número}`. Cada arquivo deve ter uma NFS-e; para várias, use um ZIP.
- XMLs de tipos novos ficam em `xmls\{tipo}\{ano}\{mês}\`; as NF-e continuam em `xmls\{ano}\{mês}\`.
- **Busca automática (EXPERIMENTAL, desligada por padrão):** CT-e e MDF-e podem ser buscados junto com a NF-e na sincronização (*Configurações > Outros documentos na Sefaz*, ou `DistribuirCte`/`DistribuirMdfe` no `appsettings.json`). Cada serviço tem o seu próprio controle de NSU. **O formato desses serviços (URL, namespaces, operação, versão) foi escrito de memória e nunca foi testado contra a Sefaz.** Para o MDF-e não sei ao certo se existe serviço de distribuição, por isso não há URL padrão: preencha `UrlDistribuicaoMdfe` conforme o Portal. Uma falha nesses serviços nunca derruba a busca de NF-e: aparece como aviso no histórico. Para investigar: `sync --diagnostico --servico cte` (ou `mdfe`).
- CF-e SAT e NFS-e não têm busca automática (o SAT é do equipamento; a NFS-e depende de cada prefeitura). Também não há importação desses tipos pelo SPED.
- ⚠️ Os nomes dos elementos desses XMLs foram escritos **de memória** e testados só com XMLs montados à mão. Confirme com arquivos reais do seu emissor; a NFS-e varia muito entre municípios. Se algum for rejeitado ou ficar com campos vazios, mande um exemplo (sem dados sensíveis) para ajuste.

## Executável único (Windows)

Um só arquivo, `BuscadorNotas.exe` (~50 MB): não exige instalar o .NET, traz a interface e o SQLite dentro dele.

- **Baixar pronto:** a cada envio ao GitHub, a aba **Actions** monta o `.exe` (e roda os testes no Windows). Abra a execução mais recente e baixe **BuscadorNotas-win-x64** em *Artifacts*.
- **Gerar você mesmo** (precisa do SDK .NET 8): `.\deploy\publicar-exe.ps1` e pegue `dist\BuscadorNotas.exe`.

**Uso:** coloque o `.exe` numa pasta própria (ex.: `C:\BuscadorNotas`) e dê **duplo clique**. Na primeira vez ele cria, ao lado dele, o `appsettings.json`, o banco `notas.db`, a pasta `entrada` e a pasta `xmls`, e abre o navegador em `http://127.0.0.1:5080`. Informe o **CNPJ** em *Configurações*. Para importar sem certificado, solte XMLs/ZIPs/SPED na pasta `entrada`. Para encerrar, feche a janela preta. O Windows pode avisar "aplicativo não reconhecido" (o arquivo não é assinado digitalmente): *Mais informações > Executar assim mesmo*.

## Interface web e API

```bash
dotnet run --project src/BuscadorNotas -- serve            # http://127.0.0.1:5080
dotnet run --project src/BuscadorNotas -- serve --url http://127.0.0.1:8080
```

O comando `serve` sobe a interface (`src/BuscadorNotas/wwwroot`) e a API (`/api`) no mesmo processo, usando o mesmo banco SQLite e a mesma pasta de XMLs da linha de comando.

| Rota | Função |
|---|---|
| `GET /api/notas?busca&de&ate&situacao&xml&pagina&tamanho` | lista paginada (`itens`, `total`, `pagina`, `totalPaginas`) |
| `GET /api/notas/{chave}` · `GET /api/notas/{chave}/xml` | detalhe · download do XML |
| `POST /api/notas/zip` `{"chaves":[...]}` | ZIP dos XMLs disponíveis (cabeçalhos `X-Incluidas`, `X-Sem-Xml`) |
| `GET /api/notas/export.csv?...` | CSV do resultado filtrado |
| `GET /api/dashboard?mes=aaaa-mm` | KPIs, histórico e estado da sincronização |
| `GET /api/sync/status` · `POST /api/sync/start` · `POST /api/sync/cancel` · `GET /api/sync/events` (SSE) | sincronização |
| `GET/PUT /api/config` · `GET/POST /api/certificado` | configurações e certificado A1 |

**Segurança.** Sem `ApiToken`, o servidor só atende `localhost` (valida o cabeçalho `Host`) e recusa `ApiUrl` fora de loopback. Com `ApiToken`, toda chamada a `/api` exige `Authorization: Bearer <token>` (o endpoint SSE também aceita `?token=`, porque o navegador não envia cabeçalhos em `EventSource`; evite expor esse endereço em logs). Requisições que alteram estado são recusadas se o cabeçalho `Origin` não for o próprio servidor. **Não há login por usuário**; se for expor na rede, use HTTPS (por exemplo, atrás de um proxy reverso) e um token forte.

**Certificado pela interface.** O `.pfx` é salvo ao lado do banco (`certificado.pfx`) e a **senha fica só na memória** do processo: após reiniciar, defina `NFE_PFX_SENHA` ou envie o certificado novamente.

**Sincronização.** Uma execução por vez. A Sefaz respondendo 656 coloca o servidor em espera (`blocked`) por `EsperaConsumoIndevidoMinutos`, e isso sobrevive a reinício. A sincronização automática vem desligada; ligue na tela de configurações (intervalo mínimo de 60 min).

## Pasta de entrada

Defina `PastaEntrada` no `appsettings.json` e rode `serve`: XMLs, ZIPs de XMLs, SPED (.txt) e listas de chaves (.csv) colocados lá são importados automaticamente (sem certificado), e movidos para `processados` ou `rejeitados`. Detalhes em [docs/instalacao-windows.md](docs/instalacao-windows.md#pasta-de-entrada-importação-automática).

## Uso diário no Windows

Serviço do Windows, logs por dia e backup agendado: veja **[docs/instalacao-windows.md](docs/instalacao-windows.md)** (`deploy/instalar.ps1`). Comando de backup: `BuscadorNotas backup --destino D:\Backups\Buscador`.

## Erro 656 "Consumo indevido" na sincronização

É um **limite de uso** da Sefaz, não um defeito: depois de uma consulta sem novidades (137) ou que chegou ao fim do acervo (138), uma nova consulta em menos de 1 hora é recusada com 656 e o CNPJ fica bloqueado por um tempo. O programa respeita isso: depois dessas consultas o botão "Sincronizar agora" fica desativado até o horário liberado (`IntervaloMinimoMinutos`, padrão 60), o agendador também espera, e na linha de comando o `sync` recusa com aviso (`--forcar` ignora, por sua conta e risco). Depois de um 656 a espera é de `EsperaConsumoIndevidoMinutos` (65) e vale mesmo se você fechar e abrir o programa. **Não clique repetidamente, não reinicie para "destravar" e não rode `sync --diagnostico` durante a espera**: o bloqueio é na Sefaz e cada tentativa pode prolongá-lo.

## Erro 215 "Falha no esquema XML" na sincronização

Significa que a Sefaz recebeu o pedido (certificado e conexão estão ok) mas recusou o formato. O `cUFAutor` do guia original (`91`) não é uma UF válida e agora é ignorado: informe a **UF da empresa** em *Configurações* (ou `"CUFAutor": "35"` no `appsettings.json`, código IBGE de 2 dígitos). Sem UF configurada, o programa tenta inferi-la das chaves já importadas e, se não conseguir, omite o campo. O pedido enviado é impresso na janela/log (`Requisição enviada: ...`) para diagnóstico.

## ⚠️ Pontos a verificar antes de usar em produção

Este código **não foi compilado nem executado** no ambiente em que foi escrito (sem .NET SDK/Sefaz disponíveis). Rode `dotnet build` e `dotnet test` primeiro. Além disso, há pontos sobre os quais não tenho certeza e que dependem da documentação oficial vigente (Portal Nacional da NF-e):

- **O DF-e de Distribuição pode não devolver notas que a própria empresa emitiu.** Pelo que conheço, o serviço entrega documentos em que o CNPJ é *destinatário* (e eventos relacionados), não necessariamente as notas de saída emitidas por ele. O código filtra `emit/CNPJ == seu CNPJ`, mas pode ser que nada de saída apareça. Valide em homologação/produção com seu CNPJ.
- **`nfeConsultaProtocolo` retorna situação e protocolo, geralmente não o XML completo.** O sistema grava o XML só se a resposta trouxer `nfeProc`; caso contrário marca a nota como `CONSULTADA_SEM_XML`. O guia assume que baixa o XML; não tenho confirmação disso.
- **Envelope SOAP:** a versão (1.1 × 1.2), o `cUFAutor` (guia usa `91`), o elemento-operação no corpo e as URLs devem ser conferidos no WSDL/Manual de Orientação do Contribuinte. Há `Soap12` e `UrlDistribuicao` em configuração; `UrlsConsultaProtocolo` (por UF) vem sem valor propositalmente.
- **Layout do SPED:** posições do `C100` usadas em `SpedParser.cs` (IND_OPER=2, COD_SIT=6, SER=7, NUM_DOC=8, CHV_NFE=9, DT_DOC=10, VL_DOC=12) conferir com o Guia Prático da EFD da sua versão.
- Códigos de retorno tratados: 138 (docs), 137 (nenhum), 656 (consumo indevido → espera ~65 min). Demais lançam erro.
- **Diagnóstico:** `sync --diagnostico` mostra se a distribuição devolve notas em que seu CNPJ é emitente (não grava nada nem altera o NSU salvo). Rode antes de depender do `sync` para saídas.
- **Eventos de cancelamento:** o `sync` agora lê `resEvento`/`procEventoNFe` com `tpEvento` 110111/110112 de notas emitidas pelo seu CNPJ (o CNPJ é tirado da chave) e marca a nota como **Cancelada**; o cancelamento vence sobre o `cStat 100` do XML original e é reaplicado se a nota chegar depois do evento. Os nomes dos elementos e os tipos de evento estão **de memória**, sem fonte verificada: use `sync --diagnostico` (que agora conta os eventos de cancelamento) para confirmar com dados reais. Carta de correção (CC-e) e outros eventos continuam ignorados.
- **Aplicar cancelamentos que já passaram:** o NSU salvo já estava à frente deles. Rode `sync --desde-nsu N` (ex.: `000000000000000` para o início) para reprocessar; é idempotente, mas consome consultas da Sefaz (respeite o limite de uso, cStat 656).
