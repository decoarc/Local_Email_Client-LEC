# Apagar e mover pastas — motor (sem UI) — Design

**Status:** Aprovado para virar plano de implementação.
**Depende de:** `DbxFolderFileWriter.Rebuild`/`DbxFolderWriteEntry` (fatia "criar pastas — motor", já mesclada), `DbxStore.CreateFolderAsync` (mesmo arquivo, mesmo padrão de validação/backup/rebuild), `DeletedMessageCache`/`MovedMessageCache` (fatias de mensagem, modelo direto pro cache novo desta fatia).
**Referência:** `docs/superpowers/specs/2026-08-03-criar-pastas-motor-dbx-design.md` (mesmo padrão de escrita de `Folders.dbx`: árvore-B achatada, atômica, com backup); `docs/superpowers/specs/2026-08-04-apagar-mensagem-design.md` §2 (mesma filosofia "esconder, nunca tocar no arquivo original", adaptada de mensagem pra pasta).
**Contexto:** próximo sub-projeto do replaneio, a partir de `fase4-simplificada`. Estende a árvore de pastas (hoje só cria) com apagar e mover uma pasta inteira, incluindo toda a subárvore de subpastas e mensagens que ela contiver. Decisões confirmadas com o usuário (2026-08-05): apagar esconde a pasta inteira (não exige que esteja vazia); mover leva junto toda a subárvore; escopo de mover é só dentro do mesmo perfil; pastas especiais (Caixa de Entrada, Enviados, Rascunhos, Caixa de Saída, Itens Excluídos) não podem ser apagadas nem movidas.

## 1. Objetivo

Dar ao `LEC.MailStore` a capacidade de:
1. **Apagar uma pasta** — escondê-la, junto com toda a sua subárvore de subpastas, sem tocar em nenhum arquivo `.dbx` de mensagens nem em `Folders.dbx`. As mensagens dentro continuam fisicamente intactas; ficam só inacessíveis, porque o nó da pasta some de `DbxStore.Folders`.
2. **Mover uma pasta** — trocar o pai de uma pasta na árvore, reescrevendo só a entrada dela em `Folders.dbx`. Como a hierarquia é inteiramente definida pelo `ParentId` de cada pasta (não por aninhamento físico de arquivos), a subárvore inteira "vem junto" automaticamente: as descendentes continuam apontando pro mesmo id de pai, sem precisar de nenhuma mudança nelas.

Nenhuma tela nesta fatia; tudo testável via `DbxStore.DeleteFolderAsync`/`DbxStore.MoveFolderAsync` chamados diretamente.

## 2. Decisões de escopo (confirmadas com o usuário)

- **Apagar esconde a subárvore inteira, sem exigir pasta vazia.** Mesmo padrão "hide, don't touch" já usado pra mensagens — mais conveniente que bloquear em pastas com conteúdo, e mais barato de implementar que apagar de verdade (que exigiria reescrever/apagar cada `.dbx` de mensagens da subárvore).
- **Mover leva a subárvore inteira junto**, de graça — decorre da arquitetura (`ParentId`), não é esforço extra.
- **Mover só dentro do mesmo perfil.** Diferente de mover mensagem (que já suporta cross-profile porque o motor de mensagem não faz suposição nenhuma sobre isso), mover pasta exigiria orquestrar a cópia de N arquivos `.dbx` de mensagens pra outro diretório de perfil — fora de escopo por decisão explícita.
- **Pastas especiais nunca podem ser apagadas ou movidas** (nem como a pasta-alvo da operação). Podem, porém, **receber** uma pasta movida como nova filha — só a pasta especial em si, como origem de apagar/mover, é bloqueada.
- **Apagar/mover uma pasta inexistente ou já apagada** → mesmo tratamento: `InvalidOperationException` (pasta apagada não aparece mais em `Folders`, então "já apagada" e "nunca existiu" são indistinguíveis pro motor, o que é aceitável).
- **Sem lixeira, sem undo.** Mesma exceção leve já usada por apagar mensagem — grava só o necessário, sem esperar a "Biblioteca" completa do `ARQUITETURA.md` §8.7.

## 3. `HiddenFolderCache` e `IMailStore.DeleteFolderAsync`

Novo, mesmo padrão do `DeletedMessageCache`/`MovedMessageCache` (`src/LEC.MailStore/`): SQLite em `%LOCALAPPDATA%\LEC\`, aberto com fallback pra `:memory:` se o caminho real falhar (mesmo `OpenDeletedMessageCacheWithFallback` espelhado como `OpenHiddenFolderCacheWithFallback`).

```sql
CREATE TABLE hidden_folder (
    profile_path TEXT    NOT NULL,
    folder_id    INTEGER NOT NULL,
    PRIMARY KEY (profile_path, folder_id)
);
```

```csharp
HashSet<int> GetHiddenFolderIds(string profilePath);
void MarkHidden(string profilePath, int folderId);
```

**Diferença importante em relação aos caches de mensagem:** `MovedMessageCache`/`DeletedMessageCache` chaveiam por `index_node_offset`, que muda toda vez que o `.dbx` daquela pasta é reconstruído (`AddMessageAsync`) — por isso precisam de `ClearFolder` chamado a cada rebuild. `HiddenFolderCache` chaveia por `FolderId`, e `CreateFolderAsync`/`MoveFolderAsync` (abaixo) **preservam o `FolderId` de toda entrada existente** ao reescrever `Folders.dbx` — só acrescentam ou alteram o `ParentId` de uma entrada, nunca renumeram. Não existe nenhum cenário em que um `FolderId` marcado como oculto passa a identificar uma pasta diferente. **Não precisa de `ClearFolder`/purga nenhuma.**

**Onde a ocultação é aplicada:** não em `EnumerateHeadersAsync` (que opera em mensagens, não em pastas) — em `DbxStore.Open`, ao montar `_folders`/`_backingFileNames`: qualquer `FolderId` presente em `HiddenFolderCache.GetHiddenFolderIds(profileDirectoryPath)` é excluído dessas duas coleções antes de a store existir. Isso significa que `Folders` (a propriedade pública já consumida por `FolderTreeViewModel.Build`, `CreateFolderAsync`'s checagem de sibling, etc.) nunca sabe que a pasta oculta existiu — nenhum outro método precisa aprender a filtrar. `DeleteFolderAsync` (abaixo) faz a mesma remoção em memória, na hora, sem esperar reabrir o perfil.

```csharp
/// <summary>
/// Hides a folder — and every folder in its subtree — from DbxStore.Folders, without
/// touching Folders.dbx or any message .dbx. The folder's own backing file (and every
/// descendant's) stays on disk untouched; only DbxStore.Folders and HiddenFolderCache
/// change. A no-op, not an error, if called twice for the same folder (matching
/// DeleteMessageAsync's idempotence).
/// </summary>
/// <exception cref="InvalidOperationException">
/// Thrown when: <paramref name="folderId"/> does not match any existing (visible) folder;
/// or the folder, or any folder in its subtree, is a special folder (SpecialFolderKind
/// != None) — special folders can never be deleted, including as a side effect of
/// deleting an ancestor.
/// </exception>
Task DeleteFolderAsync(int folderId, CancellationToken ct = default);
```

Algoritmo em `DbxStore` (dentro de `Task.Run`, mesmo padrão de `CreateFolderAsync`):
1. `folderId` existe em `_folders`? Senão, `InvalidOperationException`.
2. Calcula a subárvore inteira (o próprio `folderId` + todo descendente, via `ParentId`) percorrendo `_folders` — mesma varredura com conjunto de visitados que `FolderTreeViewModel.Build` já usa pra tolerar ciclo/referência pendente, adaptada aqui pra coletar ids em vez de montar nós de UI.
3. Se qualquer pasta da subárvore é especial (`SpecialFolderKind != None`) → `InvalidOperationException`, **antes** de marcar qualquer coisa (tudo ou nada — nunca esconde metade de uma subárvore por causa de uma pasta especial encontrada no meio).
4. Pra cada id da subárvore: `_hiddenFolderCache.MarkHidden(profilePath, id)`; remove de `_folders`/`_backingFileNames`; se houver um handle aberto (`_openFolders`), descarta (`Dispose`) e remove — junto com qualquer entrada em `_messagesByFolder`/`_hiddenOffsetsByFolder`/`_deletedOffsetsByFolder`/`_scavengeStatusByFolder`/`_recoveredItemsByFolder`/`_recoveredByFolder` pra aquele id. Essa limpeza é higiene de memória/handle (a pasta ocultada nunca mais é referenciada por esta instância de `DbxStore`), não uma correção de bug — nada nela precisa sobreviver a esconder a pasta.

## 4. `IMailStore.MoveFolderAsync`

```csharp
/// <summary>
/// Moves a folder to a new parent within the same store — a from-scratch rewrite of
/// Folders.dbx (see DbxFolderFileWriter.Rebuild) that changes only the moved folder's
/// ParentId entry. Every descendant folder's own entry is untouched: since the tree is
/// defined entirely by ParentId, the whole subtree "comes along" without needing any
/// other entry to change.
/// </summary>
/// <exception cref="InvalidOperationException">
/// Thrown when: <paramref name="folderId"/> does not match any existing folder; the
/// folder is special (SpecialFolderKind != None); <paramref name="newParentId"/> is
/// non-null and does not match any existing folder; <paramref name="newParentId"/> is
/// the folder itself or one of its own descendants (would create a cycle); a sibling
/// folder under <paramref name="newParentId"/> already has this folder's name
/// (case-insensitive, same rule as CreateFolderAsync); or Folders.dbx on disk is damaged
/// (Health != StoreHealth.Ok).
/// </exception>
Task MoveFolderAsync(int folderId, int? newParentId, CancellationToken ct = default);
```

Algoritmo em `DbxStore` (dentro de `Task.Run`, mesmo padrão de `CreateFolderAsync`):
1. `folderId` existe em `_folders`? Senão, `InvalidOperationException`. É especial? `InvalidOperationException`.
2. `newParentId` (se não nulo) existe em `_folders`? Senão, `InvalidOperationException`.
3. **Checagem de ciclo:** sobe a cadeia de `ParentId` a partir de `newParentId` (com conjunto de visitados, pra não entrar em loop infinito caso a árvore já tenha um ciclo pré-existente e tolerado — ver comentário de `FolderTreeViewModel.Build` sobre isso). Se `newParentId == folderId`, ou se `folderId` aparece em algum ponto dessa cadeia, → `InvalidOperationException`.
4. Colisão de nome: já existe uma pasta com `ParentId == newParentId` (que não seja a própria `folderId`) com o mesmo `Name` (case-insensitive)? → `InvalidOperationException` — mesma regra de `CreateFolderAsync`.
5. Abre `Folders.dbx` (`DbxFolderFile.Open`), mesma checagem de `Health != Ok` → `InvalidOperationException`, mesmo motivo de `CreateFolderAsync` (reconstruir a partir de leitura parcial descartaria pastas ilegíveis).
6. Monta `List<DbxFolderWriteEntry>` a partir de `current.Folders` (igual a `CreateFolderAsync`), substitui a entrada de `folderId` por uma cópia idêntica exceto `ParentId = newParentId ?? -1`. Toda outra entrada (incluindo qualquer pasta oculta por `HiddenFolderCache` — o `Folders.dbx` físico ainda tem a entrada dela) passa inalterada.
7. Backup: `.bak` sobrescrito (mesmo padrão; `.original` já existe de uma chamada anterior de `CreateFolderAsync`/primeira escrita, não recriado aqui). `DbxFolderFileWriter.Rebuild` escreve em arquivo temporário e troca via `File.Move(overwrite: true)`.
8. Atualiza `_folders` em memória: substitui o `StoreFolder` de `folderId` por uma cópia com `ParentId = newParentId` (os demais campos, incluindo `Id`/`Name`/`SpecialFolderKind`/`MessageCount`/`IsLoadable`/`HasBackingFile`, continuam iguais). Nenhuma outra entrada de `_folders` muda.

## 5. Erros e casos de borda

- `folderId` inexistente (nunca existiu, ou já apagado) → `InvalidOperationException` em ambos os métodos.
- Apagar/mover uma pasta especial, ou uma subárvore que contenha uma especial → `InvalidOperationException`, nada é marcado/reescrito.
- Mover pra dentro de si mesma, ou de uma descendente própria → `InvalidOperationException`.
- Mover pra um `newParentId` inexistente → `InvalidOperationException`.
- Colisão de nome no novo destino → `InvalidOperationException`.
- `Folders.dbx` danificado → `InvalidOperationException` (mover apenas; apagar nunca toca em `Folders.dbx`, então não tem essa checagem).
- Apagar a mesma pasta duas vezes → **não** é idempotente como `DeleteMessageAsync`: a segunda chamada lança `InvalidOperationException` (checagem 1), porque `folderId` já não existe em `_folders` depois da primeira chamada. Decisão deliberada: ao contrário de uma mensagem (que continua com o mesmo `MessageRef` estável e pode ser re-clicada), a primeira chamada já faz o nó sumir da árvore da UI — não há caminho normal pelo qual o usuário dispara uma segunda chamada pro mesmo id, então tratar isso como erro alto é preferível a mascarar silenciosamente uma chamada que só aconteceria por bug.
- Mover uma pasta pro mesmo pai que já tem (no-op reordenamento) → permitido, sem erro (mesma pasta, checagem de colisão de nome ignora a própria `folderId`); reescreve `Folders.dbx` de qualquer forma (sem otimização de "detectar no-op e pular"), igual ao raciocínio de simplicidade já aplicado em outras fatias.

## 6. Estratégia de testes

- `HiddenFolderCacheTests`: espelha `DeletedMessageCacheTests` (marcar/consultar, idempotência via `INSERT OR IGNORE`, isolamento entre perfis).
- `DbxStoreDeleteFolderTests`:
  - Apagar pasta folha (sem subpastas) some de `Folders`.
  - Apagar pasta com subárvore (subpastas + netas) esconde todas de uma vez.
  - Apagar pasta com mensagens: mensagens continuam abríveis via `OpenMessageAsync`/o `.dbx` físico continua intacto em disco, mas a pasta não aparece mais em `Folders` nem é possível chamar `EnumerateHeadersAsync` nela (`folder` não existe mais pra obter).
  - Apagar pasta especial → `InvalidOperationException`, `Folders` inalterado.
  - Apagar pasta cuja subárvore contém uma especial (caso sintético/improvável, mas a validação existe) → `InvalidOperationException`, nada marcado (nem a pasta raiz da tentativa).
  - Apagar pasta inexistente → `InvalidOperationException`.
  - Reabrir a store (novo `DbxStore.Open` no mesmo diretório de perfil) → pasta apagada continua ausente de `Folders` (prova que `HiddenFolderCache` persiste e é lido em `Open`).
- `DbxStoreMoveFolderTests`:
  - Caminho feliz: mover pasta pra outro pai existente, `Folders` reflete o novo `ParentId`; subpastas continuam com o mesmo `ParentId` de antes (apontando pra `folderId`), provando que a subárvore "veio junto" sem serem tocadas.
  - Mover pra raiz (`newParentId = null`).
  - Mover pra dentro de si mesma → `InvalidOperationException`.
  - Mover pra dentro de uma descendente própria (neta) → `InvalidOperationException`.
  - Mover pasta especial → `InvalidOperationException`.
  - Mover pra `newParentId` inexistente → `InvalidOperationException`.
  - Colisão de nome no destino → `InvalidOperationException`.
  - `Folders.dbx.bak` atualizado depois da chamada.
  - Reabrir a store → `ParentId` novo persiste (prova que a reescrita de `Folders.dbx` foi de verdade, não só em memória).
  - Mover pra dentro de uma pasta oculta (`HiddenFolderCache`) → tratado como "pasta inexistente" (`newParentId` não está em `_folders`) → `InvalidOperationException`.

## 7. Fora de escopo / decisões adiadas

- Apagar de verdade (remover fisicamente o(s) `.dbx` de mensagens e a(s) entrada(s) de `Folders.dbx`) — mesma decisão já tomada pra mensagens, pelo mesmo motivo de risco/tamanho de arquivo.
- Restaurar uma pasta apagada pela UI — sem lixeira, sem undo, mesma decisão de apagar mensagem.
- Mover pasta entre perfis diferentes.
- Renomear pasta — não pedido nesta fatia.
- Atualizar `MessageCount` cacheado de qualquer pasta afetada — mesma decisão já tomada em `AddMessageAsync` (número cosmético, não exibido).
- UI (menu de contexto na árvore, diálogo de escolha de destino) — próxima fatia.
