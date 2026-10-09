# Interface do Buscador de Notas de Saída — arquitetura visual e UX

Protótipo navegável: `ui-prototipo/index.html` (abra direto no navegador; dados fictícios, sem backend).
Para alterar estilos: `cd ui-prototipo && npm install && npm run build` (Tailwind 3.4, gera `app.css`).

## 1. Árvore de componentes

```
App
├─ AppShell
│  ├─ Sidebar                     (≥ md)  / MobileTabs (< md)
│  ├─ Topbar
│  │  ├─ PageTitle
│  │  ├─ StatusPills              CertPill · SefazPill (estado do último contato)
│  │  ├─ SyncButton               "Sincronizar agora" (desativa em running/blocked)
│  │  ├─ ThemeToggle
│  │  └─ SyncProgressBar          faixa de 2px no rodapé da Topbar, só em running
│  └─ ToastHost                   aria-live="polite"
├─ SyncStatusBanner               um componente, 5 variantes (idle | running | empty | blocked | error)
├─ DashboardPage
│  ├─ KpiGrid → KpiCard ×4        notas no mês · valor faturado · baixado/pendente · última sync
│  ├─ RecentInvoices
│  └─ SyncHistory
├─ InvoicesPage
│  ├─ NewInvoicesBanner           "N notas novas — Atualizar lista"
│  ├─ FilterBar                   busca · período · situação · XML · limpar
│  ├─ Toolbar → BulkActionBar     (aparece com seleção) · ExportCsvButton
│  ├─ InvoiceTable
│  │  ├─ InvoiceRow               Checkbox · NumeroSerie · AccessKeyCell(+CopyButton) · RecipientCell
│  │  │                           · Money · SituacaoChip · XmlChip · RowActions
│  │  ├─ TableSkeleton
│  │  └─ EmptyState
│  └─ Pagination
├─ InvoiceDetailDrawer            foco preso, Esc fecha, devolve o foco ao botão de origem
└─ SettingsPage
   ├─ CertificateCard             titular · CNPJ · validade · dropzone .pfx · senha
   └─ SyncScheduleCard            intervalo · ambiente · ativar/desativar
```

Em React, cada nó acima vira um componente; o estado de sincronização e os filtros ficam em hooks
(`useSyncStatus` consumindo SSE, `useInvoices(filtros)` com paginação no servidor).

## 2. Decisões de design (tokens)

| Item | Decisão |
|---|---|
| Neutros | `slate` (50–950); fundo `slate-50` / `slate-950`, cartões brancos / `slate-900` |
| Ação | `blue-600` (botão primário, foco, progresso) |
| Semânticas | verde = autorizada/ok · vermelho sutil = cancelada/erro · âmbar = pendente/espera/denegada |
| Tipografia | Inter; `font-mono` para chave de acesso, CNPJ e códigos; `tabular-nums` em valores e datas |
| Tema | claro/escuro via classe `dark`; respeita `prefers-color-scheme`, lembra a escolha em `localStorage` (em try/catch) |
| Acessibilidade | foco visível, `aria-live` para toasts/banner, `aria-busy` na tabela durante carregamento, `aria-current` na navegação, alvos ≥ 32 px, `prefers-reduced-motion` respeitado |

A cor nunca é o único sinal: todo chip tem texto e o status do XML tem ícone.

## 3. Interações durante a busca de NSUs em segundo plano

Princípio: **a sincronização nunca bloqueia o uso, nunca muda a tela sob o cursor do usuário e nunca esconde um erro.**

### Máquina de estados

```
idle ─(agendado ou "Sincronizar agora")─▶ running ─▶ idle   (cStat 138, notas novas → toast + NewInvoicesBanner)
                                              ├────▶ empty   (cStat 137, nada novo)
                                              ├────▶ blocked (cStat 656, consumo indevido)
                                              └────▶ error   (certificado, rede, HTTP, resposta inesperada)
```

### O que o usuário vê em cada estado

| Estado | Pill SEFAZ | Botão Sincronizar | Banner | Outros |
|---|---|---|---|---|
| **idle** | "SEFAZ online" (verde) | ativo | oculto | KPI "Última sincronização" mostra horário e próxima execução |
| **running** | "Sincronizando…" (azul pulsante) | desativado, ícone girando | progresso determinado `ultNSU / maxNSU`, nº do lote, contador de notas novas, **Cancelar** | faixa de progresso na Topbar; usuário segue navegando |
| **empty** | "SEFAZ online" | ativo | "Tudo em dia. Nenhuma nota nova. Próxima consulta …" (dispensável) | sem toast (não interromper à toa) |
| **blocked** | "Consulta em espera" (âmbar) | **desativado** | explica o erro 656 em linguagem simples + contagem regressiva (≈65 min) | volta sozinho a idle; não oferecer "tentar de novo" (pioraria o bloqueio) |
| **error** | "Sem conexão" (vermelho) | ativo | causa provável + "Abrir configurações" + "Tentar de novo"; informa que nada foi perdido (retoma do último NSU salvo) | o erro persiste até ação ou próxima tentativa bem-sucedida |

### Regras de comportamento

1. **Progresso honesto.** Barra determinada porque o retorno traz `ultNSU` e `maxNSU`. Se `maxNSU` for desconhecido, usar barra indeterminada.
2. **Cancelar é seguro.** O NSU só é gravado após processar cada lote, então cancelar mantém o que já foi baixado.
3. **Sem reordenar a tabela sozinha.** Notas novas geram o aviso "N notas novas chegaram — Atualizar lista" e a lista só muda quando o usuário clicar. Seleção e página atual são preservadas.
4. **Atualizar lista** mostra *skeleton* (nunca spinner sobre a tabela) por até ~600 ms e então os dados.
5. **Conclusão:** toast "Sincronização concluída: N notas novas" (anunciado por leitores de tela). Se o usuário estiver em outra aba do navegador, atualizar o `document.title` com o contador.
6. **Uma sincronização por vez.** Cliques repetidos são ignorados; o servidor também rejeita execuções concorrentes.
7. **Agendada × manual.** Se a automática disparar com o usuário na tela, o comportamento é idêntico ao manual (banner + progresso).
8. **Persistência.** Recarregar a página durante `running` deve reconectar ao estado real do servidor (não assumir `idle`).
9. **Cuidado com o limite da SEFAZ.** O intervalo mínimo configurável deve ser ≥ 1 h; avisar na tela de configurações.

### Contrato de eventos sugerido (SSE: `GET /api/sync/events`)

```json
{"tipo":"inicio","iniciadoEm":"2026-10-09T08:40:00-03:00","origem":"manual"}
{"tipo":"lote","pagina":3,"ultNsu":"000000000004240","maxNsu":"000000000004260","novasNotas":4}
{"tipo":"fim","resultado":"138","novasNotas":12,"proximaExecucao":"2026-10-09T10:10:00-03:00"}
{"tipo":"fim","resultado":"656","retomarEm":"2026-10-09T09:45:00-03:00"}
{"tipo":"erro","codigo":"CERTIFICADO_INVALIDO","mensagem":"..."}
```

## 4. Contrato de API (implementado em `src/BuscadorNotas/Api.cs`)

Implementado como ASP.NET Core Minimal API no mesmo projeto (`serve`), reutilizando `Repositorio`, `Robo` e `SyncService`. A interface real está em `src/BuscadorNotas/wwwroot/index.html`; `ui-prototipo/` continua sendo a referência de design com dados fictícios.

| Rota | Função | Observação |
|---|---|---|
| `GET /api/notas?busca&de&ate&situacao&xml&pagina&tamanho` | lista paginada com total | `Repositorio.BuscarPagina` (LIMIT/OFFSET + COUNT) |
| `GET /api/notas/{chave}` / `GET /api/notas/{chave}/xml` | detalhe / download | XML só existe se `Status = BAIXADO` |
| `POST /api/notas/zip` (`{chaves:[…]}`) | ZIP de XMLs selecionados | ignorar e informar as sem XML |
| `GET /api/notas/export.csv?filtros…` | CSV do resultado | |
| `GET /api/dashboard` | KPIs | queries de agregação a criar |
| `GET /api/sync/status` · `POST /api/sync/start` · `POST /api/sync/cancel` · `GET /api/sync/events` | sincronização | `Robo` hoje roda direto no console |
| `GET/PUT /api/config` · `POST /api/certificado` | configurações | validade do certificado: `X509Certificate2.NotAfter`; senha nunca trafega de volta |

### Estados no servidor

O servidor expõe 4 estados (`idle`, `running`, `blocked`, `error`). O aviso "Tudo em dia" (cStat 137) é derivado na interface: `idle` + último resultado 137 + não dispensado.

## 5. Lacunas que continuam

- **"Situação: Cancelada"** só reflete o que está em `SituacaoSefaz`: cStat 101/135/155 vindo de consulta, ou `COD_SIT` 02/03 do SPED. O sistema ainda **não processa eventos** de cancelamento da distribuição, então uma nota cancelada depois de baixada continua "Autorizada".
- **Mapeamento do SPED:** `COD_SIT` 00/01/06/07/08 é tratado como "Autorizada" e 04 como "Denegada"; conferir com o Guia Prático da EFD.
- **Pill "SEFAZ"** mostra o resultado do **último contato** (não há consulta ao `NfeStatusServico`).
- **Notas pendentes** (chave conhecida, sem XML) aparecem com "Baixar XML" desativado. Notas vindas só de `importar-chaves` não têm data até o XML chegar, então não entram nos totais do mês.
- **Autenticação:** há token único opcional (`ApiToken`), sem login por usuário.
- **Certificado pela web:** a senha fica só em memória; após reiniciar é preciso `NFE_PFX_SENHA` ou novo upload.
- **Executar a API contra a Sefaz real** depende das mesmas verificações do README (SOAP, `cUFAutor`, se a distribuição devolve notas de saída).
