# Apagar pasta — apagar de verdade (permanente) — Design

**Status:** Aprovado para virar plano de implementação.
**Depende de:** `DbxStore.DeleteFolderAsync`/`HiddenFolderCache` (já mesclado, `docs/superpowers/specs/2026-08-05-apagar-mover-pastas-motor-design.md`), `DbxFolderFileWriter.Rebuild`/`DbxFolderWriteEntry` (mesmo padrão de reescrita já usado por `MoveFolderAsync`), `ScavengeCache.ClearScan`/`MovedMessageCache.ClearFolder`/`DeletedMessageCache.ClearFolder` (mesmo padrão de purga já usado por `AddMessageAsync` em rebuild).
**Contexto:** o design original de apagar pasta (2026-08-05) decidiu deliberadamente "esconder, nunca apagar de verdade" — a pasta some de `DbxStore.Folders`, mas o `.dbx` de mensagens continua intacto em disco e `Folders.dbx` nunca é reescrito. O usuário agora pede para essa decisão mudar: apagar uma pasta deve apagar o `.dbx` de verdade.

## 1. Objetivo

Trocar o comportamento de `DbxStore.DeleteFolderAsync` de "esconder" para "apagar de verdade":
1. Remove a entrada da pasta (e de toda a sua subárvore) de `Folders.dbx`, via uma reescrita completa (mesmo padrão atômico com backup que `MoveFolderAsync`/`CreateFolderAsync` já usam).
2. Apaga do disco o arquivo `.dbx` de mensagens de cada pasta da subárvore que tiver um, junto com seus companheiros de backup (`.bak`, `.original`).

Continua sem lixeira/undo — mesma filosofia de sempre neste projeto, agora levada ao limite lógico (a operação que o nome "apagar" sempre sugeriu).

## 2. Decisões de escopo (confirmadas com o usuário, 2026-08-06)

- **A limpeza da origem em mover-pasta-entre-perfis também passa a ser permanente.** `MainWindowViewModel.MoveFolderAsync` (cross-profile) copia a subárvore inteira pro destino e depois chama `DeleteFolderAsync` na origem — como essa é a mesma função, o comportamento muda ali também. Depois que a cópia pro destino já teve sucesso, apagar os arquivos de origem de verdade evita duplicação permanente de dados em disco.
- **Falha parcial ao apagar um arquivo físico não desfaz a operação.** Se `Folders.dbx` já foi reescrito com sucesso (a pasta já sumiu da árvore) mas um `.dbx` de mensagens não pôde ser apagado (ex.: bloqueado por outro processo), a operação continua best-effort pros demais arquivos e, ao final, lança `InvalidOperationException` listando quais arquivos não puderam ser removidos. A pasta permanece apagada da árvore de qualquer forma.
- **Pastas já escondidas por versões antigas do app (`HiddenFolderCache`) não são migradas.** Continuam escondidas para sempre, sem que o `.dbx` delas seja apagado automaticamente. `DeleteFolderAsync` para de escrever no `HiddenFolderCache` (nunca mais chama `MarkHidden`), mas `DbxStore.Open` continua lendo a tabela pra não ressuscitar nada que já foi escondido antes dessa mudança.
- **O diálogo de confirmação na UI passa a deixar explícito que a ação é permanente/irreversível** — hoje o texto não menciona isso porque a operação era só esconder.

## 3. `DbxStore.DeleteFolderAsync` — novo algoritmo

Ordem importa por causa da falha parcial (§2): a reescrita de `Folders.dbx` é a operação atômica e definitiva; a exclusão física dos arquivos é best-effort *depois* dela.

1. `folderId` existe em `_folders`? Senão, `InvalidOperationException` (como hoje).
2. Calcula a subárvore inteira (mesma varredura com conjunto de visitados de hoje, via `ParentId`).
3. Alguma pasta da subárvore é especial (`SpecialFolderKind != None`)? → `InvalidOperationException`, **antes** de tocar em qualquer coisa (tudo ou nada, como hoje).
4. Abre `Folders.dbx` (`DbxFolderFile.Open`); `Health != StoreHealth.Ok` → `InvalidOperationException` (mesma checagem de `CreateFolderAsync`/`MoveFolderAsync` — reconstruir a partir de leitura parcial descartaria pastas ilegíveis). Nada foi tocado ainda.
5. Backup: `.bak` sobrescrito, `.original` criado se ainda não existir (mesmo padrão de sempre).
6. Monta `List<DbxFolderWriteEntry>` a partir de `current.Folders`, **excluindo** toda entrada cujo `FolderId` está na subárvore. `DbxFolderFileWriter.Rebuild(foldersDbxPath, entries)` — escreve em arquivo temporário e troca via `File.Move(overwrite: true)`. **A partir daqui a pasta já está apagada de verdade** — qualquer falha abaixo não desfaz isso.
7. Para cada id da subárvore, em memória: se houver handle aberto (`_openFolders`), descarta (`Dispose`) e remove; remove de `_folders`/`_backingFileNames`/`_messagesByFolder`/`_hiddenOffsetsByFolder`/`_deletedOffsetsByFolder`/`_scavengeStatusByFolder`/`_recoveredItemsByFolder`/`_recoveredByFolder` (mesma limpeza de hoje).
8. Para cada id da subárvore que tinha um `fileName` em `_backingFileNames` (capturado antes do passo 7 remover a entrada): purga os caches por arquivo — `_scavengeCache.ClearScan(_profileDirectory, fileName)`, `_movedMessageCache.ClearFolder(_profileDirectory, fileName)`, `_deletedMessageCache.ClearFolder(_profileDirectory, fileName)`. Necessário porque esses caches chaveiam por `(profilePath, fileName)`, não por `FolderId` — se uma pasta nova, no futuro, reusar o mesmo nome de arquivo (mesmo nome sanitizado), sem essa purga ela herdaria silenciosamente ofsets escondidos/apagados de uma pasta que não tem nada a ver.
9. Para cada id da subárvore com `fileName`: tenta apagar `Path.Combine(_profileDirectory, fileName)`, `fileName + ".bak"`, `fileName + ".original"` (cada um `File.Delete` best-effort, ignorando `FileNotFoundException` — nem todo arquivo teve rebuild alguma vez, então `.bak`/`.original` podem nunca ter existido). Captura `IOException`/`UnauthorizedAccessException` por arquivo-alvo e acumula numa lista de falhas (nome do arquivo + mensagem).
10. Se a lista de falhas do passo 9 não está vazia, lança `InvalidOperationException` com a lista (ex.: `"Pasta apagada, mas não foi possível remover do disco: Rascunhos.dbx (em uso por outro processo)."`). Note que isso é uma exceção **depois** de uma mutação bem-sucedida (a pasta já sumiu da árvore) — diferente de toda outra exceção deste método, que sempre significa "nada mudou".

Não chama mais `_hiddenFolderCache.MarkHidden` em nenhum ponto.

## 4. `IMailStore.DeleteFolderAsync` — doc

Atualiza o XML doc da interface (`src/LEC.MailStore/IMailStore.cs`) pra refletir que agora apaga de verdade (`Folders.dbx` reescrito, arquivos `.dbx` apagados), não mais "hides... never writes to Folders.dbx or any message .dbx".

## 5. Efeitos em quem já chama `DeleteFolderAsync`

- **`MainWindowViewModel.MoveFolderAsync`** (cross-profile): nenhuma mudança de código — só o XML doc, que hoje diz "hides it in sourceProfile via the already-shipped DeleteFolderAsync — never touches sourceProfile's Folders.dbx or any message .dbx", precisa ser atualizado pra descrever o novo comportamento (apaga de verdade a origem depois que a cópia pro destino já teve sucesso).
- **`MainWindow.xaml.cs` (`ConfirmAndDeleteFolderAsync`)**: texto do `MessageBox.Show` passa de `"Apagar a pasta \"{nome}\" e tudo dentro dela (subpastas e mensagens)?"` para algo como `"Apagar permanentemente a pasta \"{nome}\" e tudo dentro dela (subpastas e mensagens)? Essa ação não pode ser desfeita."`. Resto do fluxo (desabilitar árvore, limpar seleção se estava dentro da subárvore apagada, `catch` mostrando `ex.Message`) continua igual — inclusive o `catch` já cobre o novo caso de "apagou mas não conseguiu remover alguns arquivos" (`InvalidOperationException` do passo 10), que aparece pro usuário como um aviso, não bloqueia nada.

## 6. Erros e casos de borda

- `folderId` inexistente → `InvalidOperationException`, nada tocado (igual a hoje).
- Pasta especial na subárvore → `InvalidOperationException`, nada tocado (igual a hoje).
- `Folders.dbx` danificado (`Health != Ok`) → `InvalidOperationException`, nada tocado (checagem nova, espelhando `MoveFolderAsync`).
- Falha ao apagar um ou mais arquivos físicos depois que `Folders.dbx` já foi reescrito → `InvalidOperationException` com a lista de arquivos, mas a pasta **já está apagada** da árvore (§3 passo 10).
- Apagar a mesma pasta duas vezes → continua **não** sendo idempotente: a segunda chamada lança `InvalidOperationException` (checagem 1), porque `folderId` já não existe em `_folders` (mesma decisão do design original).
- Pasta sem arquivo de apoio (`HasBackingFile == false`, ex.: nó puramente organizacional) → passos 8/9 simplesmente não têm nada a fazer para esse id; sem erro.

## 7. Estratégia de testes

- `DbxStoreDeleteFolderTests` (reescreve os testes existentes, que hoje verificam "esconder"):
  - Apagar pasta folha: some de `Folders`, `Folders.dbx` reescrito sem a entrada (reabrir a store confirma — prova que não é só cache em memória), arquivo `.dbx` da pasta não existe mais em disco.
  - Apagar pasta com subárvore (subpastas + netas): todas as entradas somem de `Folders.dbx` de uma vez, todos os `.dbx` correspondentes são apagados.
  - Apagar pasta com mensagens: o `.dbx` correspondente deixa de existir em disco (não dá mais pra abrir `OpenMessageAsync` nela nem por fora, o arquivo não existe).
  - Apagar pasta especial → `InvalidOperationException`, `Folders.dbx` inalterado, nenhum arquivo apagado.
  - Apagar pasta cuja subárvore contém uma especial → `InvalidOperationException`, nada tocado.
  - Apagar pasta inexistente → `InvalidOperationException`.
  - `Folders.dbx.bak`/`.original` atualizados depois da chamada.
  - Purga de cache: cria pasta, popula `ScavengeCache`/`MovedMessageCache`/`DeletedMessageCache` pra ela (ex.: via `ScavengeAsync` + `HideMovedMessageAsync`/`DeleteMessageAsync`), apaga a pasta, recria uma pasta com o mesmo nome sanitizado (mesmo `fileName`), confirma que o cache está limpo (nenhuma mensagem aparece escondida/scavenged na pasta nova).
  - Falha parcial: simula um arquivo travado (ex.: mantém um `FileStream` aberto sobre o `.dbx` de uma subpasta) e confirma que `Folders.dbx` foi reescrito sem essa entrada (pasta já some de `Folders`) mas `DeleteFolderAsync` lança `InvalidOperationException` mencionando o arquivo.
- `HiddenFolderCacheTests`: sem mudança de comportamento da classe em si — nenhum teste novo necessário além de confirmar (num teste de `DbxStoreDeleteFolderTests` ou similar) que `MarkHidden` não é mais chamado (ex.: apaga uma pasta, verifica que `HiddenFolderCache.GetHiddenFolderIds` pro perfil continua vazio).
- Teste de UI/integração pro fluxo cross-profile (se existir cobertura de `MainWindowViewModel.MoveFolderAsync` cross-profile), atualizar qualquer asserção que hoje verifique "arquivo de origem ainda existe depois do move" — passa a esperar que não exista mais.

## 8. Fora de escopo / decisões adiadas

- Lixeira/undo — mesma decisão de sempre neste projeto.
- Migrar pastas já escondidas por `HiddenFolderCache` (versões antigas do app) pra apagar de verdade — ficam escondidas para sempre, decisão explícita do usuário.
- Apagar mensagem individual (`DeleteMessageAsync`) continuar sendo "esconder" — fora de escopo desta fatia, que é só sobre pastas.
- Renomear pasta — não pedido aqui.
