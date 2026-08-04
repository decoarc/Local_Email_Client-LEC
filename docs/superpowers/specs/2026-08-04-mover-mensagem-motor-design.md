# Mover mensagem — motor (sem UI) — Design

**Status:** Aprovado para virar plano de implementação.
**Depende de:** `DbxEmailFile`/`DbxMessageInfo`/`DbxMessageBlockReader` (Fase 1, leitores existentes), `DbxFolderFileWriter`/`DbxEmailFileWriter.CreateEmpty` (fatia "criar pastas — motor", já mesclada em `fase4-simplificada`), `ScavengeCache` (Fase 3, padrão de referência pro cache novo desta fatia), `ProfileViewModel`/`MainWindowViewModel` (Fase 2).
**Referência:** `docs/superpowers/specs/2026-08-03-criar-pastas-motor-dbx-design.md` (mesmo padrão de escrita: árvore-B achatada, atômica, com backup).
**Contexto:** terceiro sub-projeto do replaneio do projeto, a partir de `fase4-simplificada`. Primeiro sub-projeto que grava numa mensagem (não numa pasta): "mover" um e-mail entre pastas.

## 1. Objetivo

Dar ao `LEC.MailStore`/`LEC.DbxFormat` a capacidade de copiar uma mensagem de qualquer pasta pra outra pasta (gravando de verdade no `.dbx` de mensagens de destino) e esconder essa mensagem na pasta de origem — sem nunca escrever no arquivo `.dbx` de origem. Inclui a orquestração completa (`MainWindowViewModel.MoveMessageAsync`), testável de ponta a ponta sem nenhuma tela.

## 2. Decisões de escopo (confirmadas com o usuário)

- **"Mover" nunca apaga nada do arquivo de origem.** Os arquivos de mensagens reais do usuário chegam a centenas de MB (`BCP/Caixa de entrada (1).dbx` = 521 MB) — remover uma entrada do meio de uma árvore-B e cadeia de blocos desse tamanho, sem corromper mensagens vizinhas, é uma frente de engenharia muito maior (comparável à verificação original da Fase 1, agora pra escrever) e mais arriscada do que o valor justifica agora. Decisão explícita: "mover" = copiar pro destino + esconder na origem, nunca apagar/reescrever o arquivo original.
- **Pasta de DESTINO limitada a até 255 mensagens.** Mesmo truque de árvore-B "achatada" (nó único) já usado em `Folders.dbx`/pastas — cabe folgado numa pasta organizacional nova que o usuário vai populando aos poucos, mas não serve pra mover mensagens pra dentro de um arquivo legado já grande. Acima de 255, a operação bloqueia com erro claro em vez de arriscar corromper.
- **Mensagem "movida" some da pasta de origem**, mesmo o byte continuando intacto no arquivo original — via um cache local pequeno (nunca tocando o `.dbx`), mesmo padrão já usado pelo `ScavengeCache` (Fase 3).
- **Funciona entre perfis diferentes** (ex.: mover de BCP pra BCP2, se ambos estiverem abertos lado a lado) — decorre naturalmente da arquitetura (cada `IMailStore` é independente), não é esforço extra.

## 3. `DbxEmailFileWriter.Rebuild`

```csharp
public sealed record DbxMessageWriteEntry(
    byte[] RawBytes, string? Subject, string? SenderName, string? SenderAddress,
    string? MessageId, DateTime? SendTimeUtc, int Flags = 0);

public static void Rebuild(string path, IReadOnlyList<DbxMessageWriteEntry> messages);
```

Mesma estrutura de `DbxFolderFileWriter.Rebuild` (cabeçalho, árvore-B de nó único, `ArgumentException` se `messages.Count > 255`, escrita atômica via arquivo temporário + `File.Move(overwrite: true)`), com duas peças novas:

1. **Cadeia de blocos por mensagem**, escrita antes do info-node correspondente: `RawBytes` dividido em blocos de até `DbxFormat.MessageBlockMaxDataSize` (512) bytes, cada um com cabeçalho de 16 bytes (`SelfOffset` = próprio offset, `Marker` = `0x200` fixo, `Size`, `NextOffset` — `0` no último bloco da cadeia). O info-node referencia o offset do primeiro bloco via o atributo `MessageBlockOffset` (`DbxEmailFieldId.MessageBlockOffset`, 0x04).
2. **`SendCreateTime`** é sempre gravado **por ponteiro de 8 bytes** (nunca direto) — o leitor usa `ReadInt64` pra esse campo (FILETIME do Windows), diferente de `ParentId`, que é um ponteiro de 4 bytes. `ReceiveCreateTime` não é gravado — o leitor já resolve `Date = SendTimeUtc ?? ReceiveTimeUtc`, um valor só basta. `Subject`/`SenderName`/`SenderAddress`/`MessageId` são sempre por ponteiro (strings, igual já era em `DbxFolderFileWriter`). `MessageSize` (`= (uint)RawBytes.Length`, calculado pelo escritor, não faz parte do record) e `Flags` seguem a regra "direto se couber em 24 bits, ponteiro se não couber" — mesma correção que a revisão final da fatia anterior aplicou em `DbxFolderFileWriter` pros campos `FolderId`/`Flags`/`MessageCount`, aplicada aqui de saída.

## 4. `IMailStore.AddMessageAsync`

```csharp
Task AddMessageAsync(int folderId, byte[] rawMessage, string? subject, string? senderName,
    string? senderAddress, string? messageId, DateTime? dateUtc, CancellationToken ct = default);
```

Algoritmo em `DbxStore` (dentro de `Task.Run`, mesmo padrão de `CreateFolderAsync`):
1. `folderId` existe em `_folders`? Senão, `InvalidOperationException`.
2. A pasta tem arquivo de apoio (`_backingFileNames[folderId]` não nulo — pastas-placeholder tipo "Pastas locais" não têm)? Senão, `InvalidOperationException`.
3. Abre o `.dbx` de destino atual (`DbxEmailFile.Open`). `Health != Ok`? `InvalidOperationException` — mesmo motivo do `Folders.dbx`: reconstruir a partir de uma leitura parcial descartaria mensagens que o leitor não conseguiu parsear.
4. Lê todas as mensagens existentes (`.Messages` + `.ReadMessageRaw` pra cada uma) e monta a lista de `DbxMessageWriteEntry`.
5. Backup: `.bak` (sobrescrito a cada chamada) + `.original` (só na primeira vez) — mesmo padrão do `Folders.dbx`.
6. Acrescenta a nova entrada à lista; `DbxEmailFileWriter.Rebuild` (que já bloqueia sozinho acima de 255 mensagens).

**Fora de escopo:** não atualiza `Folders.dbx`/o `MessageCount` cacheado da pasta — ficaria desatualizado até reabrir o perfil. Aceitável: esse número não é exibido em lugar nenhum da árvore hoje, e corrigi-lo custaria reescrever *outro* arquivo original por um valor cosmético.

## 5. `MovedMessageCache` e `IMailStore.HideMovedMessageAsync`

Novo, mesmo padrão do `ScavengeCache` (`src/LEC.MailStore/ScavengeCache.cs`): SQLite em `%LOCALAPPDATA%\LEC\`, aberto com fallback pra `:memory:` se o caminho real falhar (mesmo `OpenScavengeCacheWithFallback` reaproveitado/espelhado).

```sql
CREATE TABLE moved_message (
    profile_path      TEXT    NOT NULL,
    folder_file       TEXT    NOT NULL,
    index_node_offset INTEGER NOT NULL,
    PRIMARY KEY (profile_path, folder_file, index_node_offset)
);
```

```csharp
Task HideMovedMessageAsync(MessageRef id, CancellationToken ct = default);
```

Grava no cache (persistente) e atualiza um `HashSet<int>` em memória por pasta (`_hiddenOffsetsByFolder`, carregado sob demanda — mesmo padrão de `EnsureScavengeLoaded`/`_scavengeStatusByFolder` — pra não bater no SQLite a cada mensagem enumerada). `EnumerateHeadersAsync` pula qualquer `IndexNodeOffset` presente nesse conjunto pra aquela pasta.

## 6. `MainWindowViewModel.MoveMessageAsync`

```csharp
public async Task MoveMessageAsync(ProfileViewModel sourceProfile, MessageHeader message,
    ProfileViewModel destinationProfile, int destinationFolderId)
{
    using var stream = await sourceProfile.Store.OpenMessageAsync(message.Id);
    using var ms = new MemoryStream();
    await stream.CopyToAsync(ms);

    await destinationProfile.Store.AddMessageAsync(destinationFolderId, ms.ToArray(),
        message.Subject, message.SenderName, message.SenderAddress, message.MessageId, message.Date);

    await sourceProfile.Store.HideMovedMessageAsync(message.Id);
}
```

Ordem importa: só esconde na origem **depois** que a cópia no destino teve sucesso — se `AddMessageAsync` falhar, nada muda em lugar nenhum (nem origem, nem destino além do que já existia) e a exceção sobe pra quem chamou. `sourceProfile`/`destinationProfile` podem ser o mesmo `ProfileViewModel` ou dois diferentes — a orquestração não faz nenhuma suposição sobre isso, funciona igual nos dois casos. Refresh de lista (recarregar o que estiver sendo exibido) fica pra fatia 2 (UI) — decisão de tela, não de orquestração.

## 7. Erros e casos de borda

- `folderId` de destino inexistente, ou sem arquivo de apoio → `InvalidOperationException`.
- O `.dbx` de mensagens de **destino** danificado (`Health != Ok`) → `InvalidOperationException`.
- Destino já com 255 mensagens → `ArgumentException` (`DbxEmailFileWriter.Rebuild`).
- Falha em `AddMessageAsync` → `HideMovedMessageAsync` nunca é chamado; mensagem continua visível na origem, nada foi criado no destino além do que já existia lá.
- `HideMovedMessageAsync` chamado duas vezes pro mesmo `MessageRef` → idempotente (`PRIMARY KEY` já cobre; um segundo `INSERT` só precisa não falhar — usar `INSERT OR IGNORE`).

## 8. Estratégia de testes

- `DbxEmailFileWriterTests`: round-trip byte-a-byte da mensagem (grava, relê com `DbxEmailFile.Open` + `ReadMessageRaw`, bytes idênticos); `Subject`/`SenderName`/`MessageId`/`SendTimeUtc` sobrevivem ao round-trip; `Flags`/`MessageSize` grande (precisa de forma-ponteiro) sobrevive; guarda de >255 mensagens; sobrescrita atômica sem arquivo temporário sobrando.
- `DbxStoreAddMessageTests`: caminho feliz (pasta vazia e pasta com mensagens existentes); pasta inexistente; pasta sem arquivo de apoio; destino danificado; backup `.bak`/`.original` criados; mensagem aparece em `EnumerateHeadersAsync` depois de reabrir a store.
- `MovedMessageCacheTests`: marcar e consultar (mesmo padrão de `ScavengeCacheTests`); persiste entre instâncias diferentes do cache (arquivo real, não `:memory:`).
- `DbxStoreTests` (extensão): `HideMovedMessageAsync` remove a mensagem de `EnumerateHeadersAsync`; sobrevive a reabrir a store (mesmo profile directory).
- `MainWindowViewModelTests` (extensão, com `FakeMailStore` estendido): `MoveMessageAsync` copia pro destino e esconde na origem; funciona com `sourceProfile`/`destinationProfile` sendo perfis diferentes; falha em `AddMessageAsync` **não** chama `HideMovedMessageAsync` (fake configurável pra lançar, e um jeito de verificar que o hide nunca aconteceu).

## 9. Fora de escopo / decisões adiadas

- Apagar de verdade da origem (remoção cirúrgica em árvore-B grande) — decisão explícita de não fazer agora, dado o tamanho real dos arquivos.
- Mover pra dentro de pastas de destino com mais de 255 mensagens.
- Atualizar `MessageCount` em `Folders.dbx` depois de mover.
- UI (menu de contexto na lista de mensagens, escolha de pasta de destino) — fatia 2, próxima.
- Desfazer/restaurar uma mensagem escondida (reverter o "mover") — não pedido ainda.
