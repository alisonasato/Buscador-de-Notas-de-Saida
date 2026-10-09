# Buscador de Notas de Saída

Sistema em C# (.NET 8) de custo zero para obter, guardar e **pesquisar** NF-e de saída, seguindo o `guia_de_desenvolvimento.md`:

1. **Robô de NSU** (`sync`) – consulta `nfeDistDFeInteresse` com certificado A1, descompacta `docZip`, grava os XMLs em `{PastaXml}/{ano}/{mes}/{chave}.xml` e indexa no SQLite.
2. **Resgate histórico** (`importar-sped` + `baixar-pendentes`) – lê registros `C100` de saída do SPED Fiscal, cria pendências e consulta cada chave via `nfeConsultaProtocolo`.
3. **Busca** (`buscar`) – pesquisa local por chave, número, série, período, destinatário, valor e status.

## Uso

```bash
cp src/BuscadorNotas/appsettings.exemplo.json src/BuscadorNotas/appsettings.json   # preencha
export NFE_PFX_SENHA='senha-do-certificado'                                       # nunca no arquivo

dotnet run --project src/BuscadorNotas -- sync --loop
dotnet run --project src/BuscadorNotas -- importar-sped SPED_2024_01.txt SPED_2024_02.txt
dotnet run --project src/BuscadorNotas -- baixar-pendentes --max 50
dotnet run --project src/BuscadorNotas -- buscar --de 2024-10-01 --ate 2024-10-31 --destinatario "cliente" --vmin 100
dotnet test
```

## ⚠️ Pontos a verificar antes de usar em produção

Este código **não foi compilado nem executado** no ambiente em que foi escrito (sem .NET SDK/Sefaz disponíveis). Rode `dotnet build` e `dotnet test` primeiro. Além disso, há pontos sobre os quais não tenho certeza e que dependem da documentação oficial vigente (Portal Nacional da NF-e):

- **O DF-e de Distribuição pode não devolver notas que a própria empresa emitiu.** Pelo que conheço, o serviço entrega documentos em que o CNPJ é *destinatário* (e eventos relacionados), não necessariamente as notas de saída emitidas por ele. O código filtra `emit/CNPJ == seu CNPJ`, mas pode ser que nada de saída apareça. Valide em homologação/produção com seu CNPJ.
- **`nfeConsultaProtocolo` retorna situação e protocolo, geralmente não o XML completo.** O sistema grava o XML só se a resposta trouxer `nfeProc`; caso contrário marca a nota como `CONSULTADA_SEM_XML`. O guia assume que baixa o XML; não tenho confirmação disso.
- **Envelope SOAP:** a versão (1.1 × 1.2), o `cUFAutor` (guia usa `91`), o elemento-operação no corpo e as URLs devem ser conferidos no WSDL/Manual de Orientação do Contribuinte. Há `Soap12` e `UrlDistribuicao` em configuração; `UrlsConsultaProtocolo` (por UF) vem sem valor propositalmente.
- **Layout do SPED:** posições do `C100` usadas em `SpedParser.cs` (IND_OPER=2, COD_SIT=6, SER=7, NUM_DOC=8, CHV_NFE=9, DT_DOC=10, VL_DOC=12) conferir com o Guia Prático da EFD da sua versão.
- Códigos de retorno tratados: 138 (docs), 137 (nenhum), 656 (consumo indevido → espera ~65 min). Demais lançam erro.
- Eventos (cancelamento, CC-e) ainda não são processados.
