# Anexos visíveis e baixáveis — Design

**Status:** Aprovado para virar plano de implementação.
**Depende de:** Fase 2 (`LEC.App`, painel de leitura via MimeKit), já concluída e mesclada.
**Referência:** `ARQUITETURA.md` §9 ("Anexos visíveis e baixáveis no painel de leitura — lista de anexos da mensagem selecionada, com opção de salvar em disco. Ainda somente-leitura") e §12 (Fase 2), memória de projeto `roadmap-future-requirements` (item 5, confirmado pelo usuário em 2026-07-27 como extensão de Fase 2, sem conflito de arquitetura).

## 1. Objetivo

Mostrar, para a mensagem selecionada no painel de leitura, a lista de anexos reais (nome, tipo, tamanho) — distintos das imagens `cid:` já resolvidas inline no corpo HTML (Fase 2) — e permitir baixar cada um para o disco, um de cada vez. Continua somente-leitura: nada é escrito de volta no `.dbx`/`.pst` de origem; a única escrita é o arquivo de destino que o próprio usuário escolhe.

## 2. Escopo

**Dentro:**
- `MessageRenderer` (já usa MimeKit pra montar o HTML do corpo) passa a também extrair a lista de anexos reais da mensagem via `MimeMessage.Attachments` — a propriedade do MimeKit já desenhada pra separar anexo de verdade de parte inline referenciada por `cid:` no corpo. Comportamento verificado contra anexos reais do `BCP/` antes de fechar a implementação (mesma disciplina de sempre deste projeto — ver §7).
- Metadado leve por anexo: nome do arquivo, tipo MIME, tamanho em bytes (decodificado, não o tamanho codificado em base64 do MIME). Nenhum conteúdo de anexo fica retido em memória entre a abertura da mensagem e um eventual clique em "Salvar" — só metadado.
- Salvar um anexo específico: a mensagem é relida do zero a partir do `.dbx` de origem (mesmo `MessageRef` já usado pra abrir a mensagem), reparseada, e só o anexo escolhido é decodificado e escrito no destino que o usuário indicar via caixa de diálogo padrão do Windows (`SaveFileDialog`, pré-preenchida com o nome do arquivo do anexo). Mesmo padrão que a Fase 3 já usa pra reler mensagens recuperadas sob demanda em vez de guardar bytes em cache — evita manter qualquer parser MimeKit "vivo" entre a hora que a mensagem foi aberta e a hora (potencialmente minutos depois) que o usuário decide baixar algo.
- UI: uma barra acima do painel de leitura (WebView2), no mesmo padrão já usado duas vezes nesta janela (`ImagesBlockedBar`, Fase 2; `RecoverableBar`, Fase 3) — visível só quando a mensagem selecionada tem pelo menos um anexo real. Cada linha mostra nome + tamanho + botão "Salvar".
- Anexos de imagem tratados exatamente como qualquer outro anexo (nome + tamanho + Salvar) — sem preview/thumbnail nesta fase.
- Mensagem sem anexos: a barra não aparece, nenhuma mudança visual pro caso comum.

**Fora (decisão explícita do usuário, 2026-07-28):**
- Preview/thumbnail de conteúdo do anexo (abrir imagem inline antes de baixar, etc.) — extensão futura, não esta fase.
- "Salvar todos" em lote — só download individual, um por vez.
- Qualquer edição, remoção ou substituição de anexo — é tudo somente-leitura, igual o resto do app.
- Múltiplos perfis (Fase anterior, já implementada) não interage com esta feature além do óbvio: cada perfil tem sua própria árvore/lista/leitura, e a barra de anexos reflete a mensagem selecionada no painel de leitura compartilhado, igual `RecoverableBar` já faz hoje.

## 3. Estrutura de projetos

```
src/LEC.App/
  Services/
    RenderedMessage.cs           MODIFICADO — +Attachments: IReadOnlyList<AttachmentInfo>
    AttachmentInfo.cs             NOVO — record leve: FileName, MimeType, Size, Index
    MessageRenderer.cs             MODIFICADO — extrai metadado de MimeMessage.Attachments
  ViewModels/
    MessageReaderViewModel.cs      MODIFICADO — guarda o MessageRef atual; +SaveAttachmentAsync
  MainWindow.xaml                  MODIFICADO — barra de anexos acima do ReaderWebView
  MainWindow.xaml.cs                MODIFICADO — botão "Salvar" por linha, SaveFileDialog

tests/LEC.App.Tests/
  MessageRendererTests.cs         MODIFICADO — extensão dos testes já existentes de RenderAsync
  MessageReaderViewModelTests.cs   MODIFICADO — cobertura de SaveAttachmentAsync
```

Nenhuma mudança em `LEC.MailStore` ou `LEC.DbxFormat` — `MessageRenderer` já vive em `LEC.App` e já depende de MimeKit (decisão da Fase 2); extrair anexos é a mesma classe de trabalho que já faz (parsear a mensagem, ler propriedades do `MimeMessage`), não uma camada nova.

## 4. Modelo de dados

- **`AttachmentInfo`** (novo, `LEC.App.Services`): `record AttachmentInfo(string FileName, string MimeType, long Size, int Index)`. `Index` é a posição do anexo dentro de `message.Attachments` no momento da renderização — usado só internamente pra relocalizar o mesmo anexo numa releitura futura (§5), nunca exposto como "identidade" estável entre reaberturas da mensagem (se o `.dbx` de origem nunca muda — e não muda, é somente-leitura — a ordem de `message.Attachments` é determinística pra uma dada mensagem, então isso é seguro).
- **`RenderedMessage`** ganha um membro: `IReadOnlyList<AttachmentInfo> Attachments`. Lista vazia quando a mensagem não tem anexos reais (nunca `null`).
- **`FileName`** ausente no MIME (alguns clientes antigos não setam `Content-Disposition`/`name` de forma consistente): usa um nome de fallback determinístico (ex.: `anexo-{Index+1}`) em vez de deixar vazio — mesma filosofia de degradação elegante do resto do projeto.

## 5. Fluxo de salvar

`MessageReaderViewModel` já guarda `_store` (o `IMailStore` do perfil ativo); passa a guardar também o `MessageRef` da mensagem atualmente carregada (hoje só usado localmente dentro de `LoadAsync`, não retido). `SaveAttachmentAsync(int attachmentIndex, string destinationPath)`:
1. Rechama `_store.OpenMessageAsync(currentId)` — releitura do `.dbx`, não usa nenhum estado do carregamento anterior.
2. Reparseia com MimeKit, localiza `message.Attachments` no índice pedido.
3. Decodifica só o conteúdo desse anexo (`MimePart.Content.DecodeTo(...)`) e escreve no `destinationPath`.

Isso significa reparsear a mensagem inteira de novo a cada download — aceitável porque mensagens individuais são pequenas (KB a poucos MB, nunca o arquivo `.dbx` inteiro) e porque evita qualquer gestão de tempo de vida de stream/parser entre o carregamento da mensagem e um clique que pode vir bem depois.

## 6. Fluxo da UI

Ao selecionar uma mensagem (`MessageListView_SelectionChanged`, já existente), depois de renderizar o HTML no `ReaderWebView`, o code-behind também atualiza a barra de anexos a partir de `_viewModel.MessageReader.Current.Attachments`: escondida se vazia, senão populada com uma linha por anexo. Clicar "Salvar" numa linha abre `SaveFileDialog` pré-preenchido com o `FileName` daquele anexo; confirmando, chama `SaveAttachmentAsync` e mostra sucesso/erro via `StatusText` (mesmo canal que todo o resto do app já usa pra feedback não-modal).

## 7. Erros e degradação

- Mensagem cujo corpo não parseia como MIME válido (já tratado hoje — cai no fallback de texto puro): sem lista de anexos, mesma coisa que "sem anexos". Não é um erro novo, é o mesmo fallback que já existe.
- Falha ao decodificar um anexo específico no momento de salvar (conteúdo corrompido, por exemplo): mensagem clara em `StatusText`, não exceção não tratada — mesmo padrão de toda ação de I/O deste app.
- `SaveFileDialog` cancelado pelo usuário: no-op silencioso, sem mensagem de erro (cancelar não é uma falha).
- Anexo cujo índice não é mais encontrado numa releitura (cenário teoricamente impossível já que o `.dbx` de origem não muda entre a abertura e o salvamento, mas defendido mesmo assim): mensagem clara em vez de exceção.

## 8. Estratégia de testes

- `MessageRendererTests`: extensão dos testes já existentes de `RenderAsync` — mensagem com um anexo real (construída com MimeKit no teste, como os testes atuais já fazem) resulta em `RenderedMessage.Attachments` com o metadado certo; mensagem só com imagem `cid:` embutida (já coberta hoje) resulta em `Attachments` vazia (a imagem não deve aparecer como anexo, só inline no HTML); mensagem sem MIME válido (fallback de texto) resulta em `Attachments` vazia sem lançar.
- `MessageReaderViewModelTests`: `SaveAttachmentAsync` contra um `FakeMailStore`/mensagem MIME de teste com anexo real — verifica que o arquivo escrito no destino tem os bytes decodificados corretos.
- Verificação manual contra `BCP/`: confirmar que pelo menos um e-mail real do arquivo tem anexo de verdade (não só `cid:`) antes de considerar a extração via `MimeMessage.Attachments` validada contra dado real — se `BCP/` não tiver nenhum anexo real, isso vira um gap documentado (mesmo tratamento que a Fase 3 deu pro caso de não achar conteúdo recuperável em `BCP/` antes de verificar — que acabou tendo, mas a postura de "verificar antes de assumir" é a mesma).
- Sem automação de teste de UI/XAML — mesmo padrão de todas as fases anteriores.

## 9. Fora de escopo / decisões adiadas

- Preview/thumbnail de anexo antes de baixar: adiado, sem data.
- "Salvar todos" em lote: adiado, sem data — decisão explícita do usuário nesta sessão.
- Anexos em mensagens recuperadas pela varredura (Fase 3, `RecoveredKind != null`): `ReadRecoveredMessageRaw` já devolve os bytes brutos de uma cadeia recuperada, que `MessageRenderer` já processa pelo mesmo caminho de sempre — deve funcionar sem mudança adicional, mas não foi verificado explicitamente contra um anexo dentro de uma mensagem recuperada real; se `BCP/` não tiver esse caso específico, fica como gap documentado, mesmo espírito do §8.
