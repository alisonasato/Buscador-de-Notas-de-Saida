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

## ⚠️ Pontos a verificar antes de usar em produção

Este código **não foi compilado nem executado** no ambiente em que foi escrito (sem .NET SDK/Sefaz disponíveis). Rode `dotnet build` e `dotnet test` primeiro. Além disso, há pontos sobre os quais não tenho certeza e que dependem da documentação oficial vigente (Portal Nacional da NF-e):

- **O DF-e de Distribuição pode não devolver notas que a própria empresa emitiu.** Pelo que conheço, o serviço entrega documentos em que o CNPJ é *destinatário* (e eventos relacionados), não necessariamente as notas de saída emitidas por ele. O código filtra `emit/CNPJ == seu CNPJ`, mas pode ser que nada de saída apareça. Valide em homologação/produção com seu CNPJ.
- **`nfeConsultaProtocolo` retorna situação e protocolo, geralmente não o XML completo.** O sistema grava o XML só se a resposta trouxer `nfeProc`; caso contrário marca a nota como `CONSULTADA_SEM_XML`. O guia assume que baixa o XML; não tenho confirmação disso.
- **Envelope SOAP:** a versão (1.1 × 1.2), o `cUFAutor` (guia usa `91`), o elemento-operação no corpo e as URLs devem ser conferidos no WSDL/Manual de Orientação do Contribuinte. Há `Soap12` e `UrlDistribuicao` em configuração; `UrlsConsultaProtocolo` (por UF) vem sem valor propositalmente.
- **Layout do SPED:** posições do `C100` usadas em `SpedParser.cs` (IND_OPER=2, COD_SIT=6, SER=7, NUM_DOC=8, CHV_NFE=9, DT_DOC=10, VL_DOC=12) conferir com o Guia Prático da EFD da sua versão.
- Códigos de retorno tratados: 138 (docs), 137 (nenhum), 656 (consumo indevido → espera ~65 min). Demais lançam erro.
- **Diagnóstico:** `sync --diagnostico` mostra se a distribuição devolve notas em que seu CNPJ é emitente (não grava nada nem altera o NSU salvo). Rode antes de depender do `sync` para saídas.
- Eventos (cancelamento, CC-e) ainda não são processados.
