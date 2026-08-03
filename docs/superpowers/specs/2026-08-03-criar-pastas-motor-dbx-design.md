# Criar pastas — motor de escrita .dbx — Design

**Status:** Aprovado para virar plano de implementação.
**Depende de:** `LEC.DbxFormat` (leitor `.dbx` existente: `DbxHeader`, `DbxFolderFile`, `DbxFolderInfo`, `DbxAttributeReader`, `DbxIndexTreeReader`, `DbxStringDecoder`), `LEC.MailStore.DbxStore` (Fase 2).
**Referência:** `ARQUITETURA.md` §2 (arquivos originais somente-leitura — esta fatia é a única exceção deliberada, escopo explicado abaixo), §7 (constantes verificadas do formato `.dbx`).
**Contexto:** replaneio do projeto a partir do ponto anterior à Fase 4 original (branch `fase4-simplificada`, criada em `0a54d22`). A Fase 4/5 anterior (biblioteca SQLite própria, `.lecarchive`, blobs endereçados por conteúdo, undo) foi descartada — não referenciada por este documento. Este é o primeiro sub-projeto do novo plano: criar pastas de verdade, no mesmo modelo do Outlook Express (uma pasta = um arquivo `.dbx`), sem UI ainda.

## 1. Objetivo

Dar ao `LEC.MailStore`/`LEC.DbxFormat` a capacidade de criar uma pasta nova dentro da árvore de um perfil aberto — um arquivo `.dbx` de mensagens vazio, mais uma entrada nova em `Folders.dbx` apontando pra ele — replicando o modelo original do Outlook Express. Nenhuma tela nesta fatia; tudo testável via `DbxStore.CreateFolderAsync` chamado diretamente.

## 2. Escopo

**Dentro:**
- `DbxEmailFileWriter.CreateEmpty(string path)` — cabeçalho `.dbx` válido, `itemCount = 0`.
- `DbxFolderFileWriter.Rebuild(string path, IReadOnlyList<DbxFolderInfo> allFolders)` — reescreve `Folders.dbx` inteiro com uma árvore-B de nó único (até 255 pastas).
- `DbxStore.CreateFolderAsync(string name, int? parentId, CancellationToken ct = default) : Task<StoreFolder>`.
- Subpasta de qualquer pasta existente (`parentId` = qualquer `StoreFolder.Id` já carregado, ou `null` = raiz).
- Backup automático (`Folders.dbx.bak`, sobrescrito a cada chamada) antes de tocar no `Folders.dbx` original.
- Escrita atômica do `Folders.dbx` novo (arquivo temporário + `File.Move(overwrite: true)`).

**Fora:**
- Qualquer UI (árvore, menu de contexto, diálogo) — sub-projeto seguinte.
- Mover mensagens para a pasta nova.
- Apagar/renomear pasta.
- Verificação de round-trip pós-escrita (reabrir e conferir com o próprio leitor antes de assumir a troca como definitiva) — decisão explícita do usuário: escreve direto, só com o backup `.bak` como rede de segurança.
- Compatibilidade byte-a-byte com o Outlook Express real — não há como testar contra o OE de verdade (software descontinuado); o alvo é o **próprio leitor deste projeto** entender o arquivo corretamente (round-trip verificado nos testes).
- `PstStore` — não existe ainda; só `DbxStore` implementa a criação.

## 3. Layout binário

### 3.1 `.dbx` de mensagens vazio (`DbxEmailFileWriter.CreateEmpty`)

Cabeçalho mínimo de `DbxFormat.HeaderIndexPointerOffset + 4` = 232 bytes:
- Assinatura de 16 bytes: `SignatureWord0`, `SignatureWord1Email`, `SignatureWord2`, `SignatureWord3`.
- `itemCount = 0` em `HeaderItemCountOffset` (`0xC4`).
- Resto (incluindo `HeaderIndexPointerOffset`, `0xE4`) zerado — irrelevante, porque o leitor (`DbxFolderFile.Open`/equivalente de e-mail) nunca monta a árvore-B quando `itemCount == 0`.

### 3.2 `Folders.dbx` reconstruído (`DbxFolderFileWriter.Rebuild`)

Mesmo cabeçalho de 232 bytes, mas:
- `itemCount = N` (todas as pastas existentes + a nova).
- `rootOffset` aponta pro nó de índice único, logo após o cabeçalho.

Nó de índice único (`IndexNodeHeaderSize` = 24 bytes de cabeçalho):
- `ptrCount = N` (byte em `IndexNodePtrCountOffset`) — cabe porque é um único byte (máx. 255); o BCP real tem 26 pastas.
- `indexCount = 0` (`IndexNodeIndexCountOffset`) — nó achatado, sem filhos, sem recursão.
- N entradas de 12 bytes (`IndexNodeEntrySize`), cada uma só com `IndexEntryIndexPtrOffset` preenchido (aponta pro info-node da pasta); `EntryNextTable`/`EntryIndexCount` zerados.

Info-node por pasta, logo em seguida (formato de `DbxAttributeReader`/`DbxFolderInfo`):
- Array de registros de atributo (4 bytes cada, packed: byte de tipo com `AttributeDirectFlag` + `DbxFolderFieldId` nos 7 bits baixos, valor de 24 bits).
- `FolderId`, `ParentId`, `Flags` (= 0), `MessageCount` (= 0), `SpecialFolderType` (= `None`/0): gravados **diretos** (`AttributeDirectFlag` setado, valor embutido nos 24 bits — cabem folgado).
- `Name`, `FileName`: gravados **por ponteiro** (`AttributeDirectFlag` limpo), apontando (relativo ao fim do array de atributos daquele info-node) pra bytes UTF-8 (`DbxStringDecoder`-compatível: sem BOM) terminados em `\0`, numa área de dados logo após o array.
- `NodeKind` (`0x0A`): omitido — `DbxFolderInfo.Read` não expõe esse campo, o leitor não depende dele.
- Contagem de atributos gravada no formato de `InfoNodeCountWordOffset`/`InfoNodeCountMask`/`InfoNodeCountShift` (byte na posição +2 do int32 em `+8`); bytes 0-7 e os demais 3 bytes do int em `+8` zerados (reservados, não lidos por `DbxAttributeReader`).

## 4. `DbxStore.CreateFolderAsync`

```csharp
public Task<StoreFolder> CreateFolderAsync(string name, int? parentId, CancellationToken ct = default);
```

Algoritmo:
1. Valida `parentId` (se não nulo) existe em `Folders`; nenhuma pasta com o mesmo `parentId` já tem `name` (comparação exata, mesma capitalização) → senão `InvalidOperationException`.
2. Deriva `fileName` = `name` sanitizado (remove `\ / : * ? " < > |`) + `.dbx`. Nome vazio antes ou depois de sanitizar → `InvalidOperationException`. Se já existir um arquivo com esse nome no diretório do perfil → `InvalidOperationException`.
3. Novo `FolderId` = `Folders.Max(f => f.Id) + 1`.
4. `DbxEmailFileWriter.CreateEmpty(Path.Combine(profileDirectory, fileName))` — primeiro, porque não toca em nada original; se falhar aqui, nenhum dano.
5. Copia `Folders.dbx` → `Folders.dbx.bak` (sobrescreve backup anterior, se houver).
6. `DbxFolderFileWriter.Rebuild` escreve num arquivo temporário e troca por `Folders.dbx` original via `File.Move(overwrite: true)` — atômico (protege contra escrita pela metade em caso de queda; não é verificação de conteúdo).
7. Atualiza `Folders` em memória (novo `StoreFolder` adicionado) e devolve o registro criado.

## 5. Erros e casos de borda

- `parentId` inexistente → `InvalidOperationException`.
- Nome vazio/só espaços, ou vazio depois de sanitizado (ex.: `"???"`) → `InvalidOperationException`.
- Nome colidindo com pasta irmã (mesmo `parentId`) → `InvalidOperationException`.
- Arquivo `.dbx` derivado já existe no diretório do perfil → `InvalidOperationException`.
- Perfil com zero pastas existentes (caso degenerado, ainda assim válido: `N = 1` após a criação).

## 6. Estratégia de testes

xUnit, arquivo/diretório temporário (mesmo padrão de `TestProfileBuilder`/`LibraryArchiveTests`):

- `DbxEmailFileWriterTests`: `CreateEmpty` produz arquivo que `DbxHeader.TryDetectType` identifica como `Email` e que abre com `itemCount = 0`, saudável.
- `DbxFolderFileWriterTests`: `Rebuild` com pastas existentes (lidas de um `Folders.dbx` real de teste) + uma nova → reabrir com `DbxFolderFile.Open` confirma todas as pastas (antigas e nova) com os campos certos (`FolderId`, `ParentId`, `Name`, `FileName`) e `StoreHealth.Ok`.
- `DbxStoreCreateFolderTests`:
  - Caminho feliz, pasta raiz (`parentId = null`).
  - Caminho feliz, subpasta de uma pasta existente.
  - `Folders.dbx.bak` criado com o conteúdo anterior.
  - `Store.Folders` reflete a pasta nova depois da chamada.
  - Arquivo `.dbx` novo existe em disco, vazio.
  - Um teste de guarda por caso de erro da seção 5.

## 7. Fora de escopo / decisões adiadas

- UI (árvore, menu de contexto, diálogo de nome/pasta-mãe) — sub-projeto seguinte.
- Mover mensagens, apagar/renomear pasta — sub-projetos futuros.
- Verificação de round-trip pós-escrita — recusada deliberadamente; só o backup `.bak` existe como rede de segurança.
- Compatibilidade real com Outlook Express (não testável, software descontinuado).
