# Apagar mensagem — Design

**Status:** Aprovado para virar plano de implementação.
**Depende de:** `IMailStore.HideMovedMessageAsync`/`MovedMessageCache` (fatia "mover mensagem — motor", já mesclada — modelo direto pro cache novo desta fatia), `DbxStore.AddMessageAsync`/`Rebuild`-purga (mesma fatia — precisa da mesma disciplina de purga ao reconstruir uma pasta), `MainWindow.xaml`'s menu de contexto por item na lista de mensagens (fatia "mover mensagem — UI", já mesclada).
**Referência:** `docs/superpowers/specs/2026-08-04-mover-mensagem-motor-design.md` §5 (`MovedMessageCache`/`HideMovedMessageAsync`, o padrão que esta fatia copia).
**Contexto:** próximo sub-projeto do replaneio, a partir de `fase4-simplificada`. Ao contrário de `ARQUITETURA.md` §8.7 (lixeira com estado reversível, journal de undo multinível, purga em dois estágios — parte da futura "Biblioteca", Fase 4), esta é a mesma exceção leve e pontual já usada por "criar pastas" e "mover mensagem": grava só o necessário, nunca toca no `.dbx` de origem, sem esperar a Biblioteca inteira existir. Decisão confirmada com o usuário (2026-08-04) — a versão "Biblioteca" completa continua sendo a visão de longo prazo, não descartada, só adiada.

## 1. Objetivo

Deixar o usuário apagar uma mensagem da tela principal. "Apagar" aqui significa esconder a mensagem da pasta onde ela está — o byte original permanece intacto no `.dbx` de origem, exatamente como "mover" já faz com a pasta de origem. Sem lixeira, sem undo pela UI.

## 2. Decisões de escopo (confirmadas com o usuário)

- **Vazio leve, mesmo padrão de "mover"/"criar pasta"** — não o sistema completo de lixeira/undo do `ARQUITETURA.md` §8.7.
- **Esconder direto, sem lixeira.** Nenhuma pasta de destino, nenhuma cópia — só esconde da pasta de origem. Diferente de "mover", que precisa de um destino; apagar não precisa.
- **Cache separado (`DeletedMessageCache`), não reaproveita o `MovedMessageCache` existente.** "Apagada" e "movida" ficam duas coisas distintas nos dados salvos, mesmo a mecânica por baixo (esconder um offset via cache local) sendo idêntica — mantém os conceitos honestos e abre espaço pra qualquer coisa futura que precise diferenciar os dois motivos (ex.: uma tela futura de "restaurar apagadas" não deve mexer em mensagens que só foram movidas).
- **Gatilho: menu de contexto na lista de mensagens + tecla Delete.** Mesmo menu já usado por "Mover para...", mais um atalho de teclado padrão de cliente de email.
- **Com confirmação** (`MessageBox` Sim/Não) antes de esconder — diferente de "mover", que não pede confirmação extra, porque apagar sem lixeira é mais difícil de desfazer visualmente pela UI (não tem uma pasta de destino pra ir buscar a mensagem de volta).
- **Seleção múltipla continua fora de escopo** — mesma decisão já tomada pra "mover".

## 3. `DeletedMessageCache` e `IMailStore.DeleteMessageAsync`

Novo, mesmo padrão do `MovedMessageCache` (`src/LEC.MailStore/MovedMessageCache.cs`): SQLite em `%LOCALAPPDATA%\LEC\`, aberto com fallback pra `:memory:` se o caminho real falhar.

```sql
CREATE TABLE deleted_message (
    profile_path      TEXT    NOT NULL,
    folder_file       TEXT    NOT NULL,
    index_node_offset INTEGER NOT NULL,
    PRIMARY KEY (profile_path, folder_file, index_node_offset)
);
```

```csharp
Task DeleteMessageAsync(MessageRef id, CancellationToken ct = default);
```

Grava no cache (persistente) e atualiza um `HashSet<int>` em memória por pasta (`_deletedOffsetsByFolder`, carregado sob demanda — mesmo padrão de `_hiddenOffsetsByFolder`/`EnsureMovedLoaded`). `EnumerateHeadersAsync` pula qualquer `IndexNodeOffset` presente no conjunto de *movidas* **ou** no de *apagadas* pra aquela pasta — os dois HashSets são consultados de forma independente, uma mensagem só precisa estar em um deles pra sumir da lista.

**Purga ao reconstruir uma pasta.** `DbxStore.AddMessageAsync` já purga `MovedMessageCache`/`ScavengeCache` (e os dicionários em memória correspondentes) depois de um `Rebuild` bem-sucedido, porque `Rebuild` reescreve o arquivo do zero e invalida todo `IndexNodeOffset` daquela pasta — ver a nota da revisão final da fatia "mover mensagem — motor" sobre esse padrão. `DeletedMessageCache`/`_deletedOffsetsByFolder` entram na mesma purga, pela mesma razão: sem isso, uma pasta que também recebe mensagens movidas (`AddMessageAsync`) pode ressuscitar uma mensagem apagada nela antes, ou — pior — esconder silenciosamente uma mensagem diferente que por acaso caiu no offset antigo.

## 4. `MainWindowViewModel.DeleteMessageAsync`

```csharp
public async Task DeleteMessageAsync(ProfileViewModel profile, MessageHeader message, CancellationToken ct = default)
{
    await profile.Store.DeleteMessageAsync(message.Id, ct);
}
```

Chamada fina, mesmo estilo de `RunScavengeAsync` — não orquestra nada entre dois stores (ao contrário de `MoveMessageAsync`), só repassa pro `IMailStore` do perfil.

## 5. UI (`MainWindow.xaml`/`.xaml.cs`)

- Item "Apagar" no mesmo `ContextMenu` por item já usado por "Mover para..." (mesmo `EventSetter`, mesmo `DataContext` resolvendo pro `MessageHeader` da linha).
- Atalho de teclado: `Delete` na `MessageListView`, agindo sobre `MessageListView.SelectedItem`.
- Handler comum aos dois gatilhos: `MessageBox.Show(..., MessageBoxButton.YesNo, ...)` — se `Sim`, chama `MainWindowViewModel.DeleteMessageAsync`; sucesso recarrega a lista da pasta atual (`SelectFolderAsync`) e limpa o painel de leitura (`NavigateToRenderedMessage(string.Empty)` + colapsar `ImagesBlockedBar`), mesmo reset de "mover"; falha (defensivo — `DeleteMessageAsync` normalmente não lança) → `MessageBox` de erro, mesmo padrão de mover/criar pasta.

## 6. Erros e casos de borda

- Apagar a mesma mensagem duas vezes → idempotente (`INSERT OR IGNORE`, mesma garantia de `HideMovedMessageAsync`).
- Pasta sem arquivo de apoio → no-op silencioso (mesmo comportamento de `HideMovedMessageAsync` hoje); não aparece na prática, já que essa pasta nunca lista mensagens.
- Cancelar a confirmação → nenhuma chamada ao motor, nenhum estado muda.

## 7. Estratégia de testes

- `DeletedMessageCacheTests`: espelha `MovedMessageCacheTests` (marcar/consultar, idempotência, isolamento entre perfis).
- `DbxStoreTests` (extensão): `DeleteMessageAsync` remove da `EnumerateHeadersAsync`, sobrevive a reabrir a store; apagar uma mensagem não afeta uma mensagem *movida* na mesma pasta, e vice-versa (os dois caches/conjuntos são independentes).
- Teste de purga: mesmo padrão do teste já existente pra `MovedMessageCache` (`AddMessageAsyncPurgesStaleHiddenOffsetsWhenTheFolderIsAlsoAMoveSource`) — provar que apagar uma mensagem e depois adicionar outra na mesma pasta (disparando `Rebuild`) não ressuscita nem esconde a mensagem errada.
- `MainWindowViewModelTests` (com `FakeMailStore` estendido): `DeleteMessageAsync` chama `IMailStore.DeleteMessageAsync` corretamente.
- Sem teste automatizado pra `MainWindow.xaml.cs` (mesmo precedente já estabelecido pelas fatias anteriores) — verificação manual cobrindo especificamente a tecla Delete e o cancelar da confirmação.

## 8. Fora de escopo / decisões adiadas

- Lixeira, undo multinível, purga em dois estágios, journal de operações — visão completa do `ARQUITETURA.md` §8.7, parte da futura Biblioteca (Fase 4), não descartada, só adiada.
- Restaurar uma mensagem apagada pela UI (o byte continua no `.dbx` original, mas não há tela pra reverter isso).
- Seleção múltipla pra apagar em lote.
- Apagar de verdade da origem (remoção cirúrgica em árvore-B) — mesma decisão já tomada pra "mover", pelo mesmo motivo (arquivos reais chegam a centenas de MB).
