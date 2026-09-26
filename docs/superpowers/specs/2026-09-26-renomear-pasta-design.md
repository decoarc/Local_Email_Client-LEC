# Renomear pasta e o arquivo `.dbx` — Design

**Status:** Aguardando revisão do usuário.
**Depende de:** `DbxStore.CreateFolderAsync` (validação de nome, backup e rewrite de `Folders.dbx`), `DbxFolderFileWriter.Rebuild`, `MovedMessageCache` / `DeletedMessageCache` / `ScavengeCache` (chave `profile_path` + `folder_file`).
**Contexto:** fatia seguinte à compactação de `.dbx`. O nome visível da pasta mora em `Folders.dbx`; as mensagens moram num `.dbx` à parte, citado pelo atributo `FileName`. O usuário pediu renomear a pasta e fazer esse arquivo no disco acompanhar o nome.

## 1. Objetivo

O usuário renomeia uma pasta comum. A árvore passa a mostrar o nome novo, o arquivo de mensagens é renomeado para `<nome>.dbx`, e `Folders.dbx` grava os dois. Mensagens apagadas, movidas e a varredura dessa pasta continuam valendo, porque a chave do arquivo nas listas é atualizada e o conteúdo do `.dbx` não é reescrito.

## 2. Decisões confirmadas

- **Abordagem 1.** Mover o arquivo no disco, regravar `Folders.dbx`, atualizar a chave `folder_file` nas três listas. As posições das mensagens não mudam.
- **Só pastas comuns.** `SpecialFolderKind != None` (entrada, saída, enviados, excluídos, rascunhos) não renomeia. O item de menu fica oculto, no mesmo critério de "Mover pasta" e "Apagar pasta". O store também recusa.
- **Conflito recusa tudo.** Sem sufixo `(1)`. Nome ou arquivo já ocupado: erro, nome antigo e arquivo antigo permanecem.
- **Fora de escopo:** renomear pastas especiais; sufixo automático no arquivo; reescrever o conteúdo das mensagens; mover a pasta para outro pai (isso já é "Mover pasta").

## 3. Validação

A mesma higiene de `CreateFolderAsync`, aplicada ao nome digitado:

1. A pasta existe neste store.
2. `SpecialFolderKind` é `None`.
3. O nome sanitizado (sem `\ / : * ? " < > |`, sem espaços nas pontas) não fica vazio.
4. O nome sanitizado não é um device reservado do Windows (`CON`, `PRN`, `AUX`, `NUL`, `COM1`–`COM9`, `LPT1`–`LPT9`), no radical, sem diferenciar maiúsculas.
5. Nenhum irmão (mesmo pai, outra pasta) tem esse nome sem diferenciar maiúsculas. A própria pasta não conta como conflito.
6. O arquivo `<nome sanitizado>.dbx` não existe neste perfil, salvo quando é o próprio arquivo desta pasta (comparação sem diferenciar maiúsculas).
7. `Folders.dbx` está `Health == Ok`. Arquivo danificado recusa a operação: regravar descartaria pastas ilegíveis.

Se o nome sanitizado é igual ao nome visível atual sem diferenciar maiúsculas, a operação termina com sucesso e não mexe em disco nem nas listas. `Notas` para `notas` é esse caso: nada muda. Essa comparação é só com o nome visível. Ela não realinha um arquivo que já esteja com outro nome (por exemplo a pasta já se chama `Notas` e o arquivo ainda é `Notas (1).dbx`). Para realinhar, o usuário escolhe um nome visível diferente.

Pasta sem arquivo de apoio (`HasBackingFile == false`): não há `.dbx` para renomear. `Folders.dbx` ainda atualiza só o nome visível, depois das mesmas validações de nome (passos 3–5 e 7). Não há passo 6.

## 4. Ordem no `DbxStore`

Novo membro em `IMailStore`:

```csharp
Task<StoreFolder> RenameFolderAsync(int folderId, string name, CancellationToken ct = default);
```

Devolve a pasta com o nome novo (mesmo `Id`, mesmo pai, mesmo tipo). A UI troca esse objeto no nó da árvore.

Sequência, depois da validação:

1. Fecha o handle aberto dessa pasta (`_openFolders`) para o `File.Move` não falhar por arquivo em uso. Limpa `_messagesByFolder` dessa pasta.
2. Se há arquivo de apoio, `File.Move` do caminho atual para `<nome sanitizado>.dbx`.
3. `_backupStore.BeforeRewrite` de `Folders.dbx` (igual a criar, apagar e mover pasta). `DbxFolderFileWriter.Rebuild` com a mesma lista de pastas, alterando só `Name` e `FileName` desta entrada. Pastas sem arquivo de apoio gravam `FileName` como já estava (nulo).
4. Só então atualiza `folder_file` de `old` para `new` em `MovedMessageCache`, `DeletedMessageCache` e `ScavengeCache` (`scan` e `recovered_item`). Não apaga as linhas. Não mexe em offsets. `HiddenFolderCache` fica: a chave é o id da pasta.
5. Substitui o `StoreFolder` em `_folders` e o valor em `_backingFileNames`.

Não chama `BeforeRewrite` no `.dbx` de mensagens: o conteúdo não muda. O backup antigo desse arquivo, se existir, continua com o nome antigo em `%LOCALAPPDATA%\LEC\backups`. A próxima reescrita da pasta (compactar, acrescentar mensagem) cria backup com o nome novo.

### 4.1 Falha

- Falhou antes do `File.Move`: nada mudou.
- `File.Move` ok e o rewrite de `Folders.dbx` falhou: `File.Move` de volta ao nome antigo. Listas intactas. A exceção é a da regravação.
- Rewrite ok e a atualização de alguma lista falhou: volta o `Folders.dbx` à lista antiga (rebuild com os entries de antes) e, se o arquivo foi movido, volta o `.dbx` ao nome antigo. Se essa compensação falhar, a exceção informa que a pasta pode ter ficado com o nome novo no disco e as listas com a chave antiga.
- As listas só mudam depois que `Folders.dbx` e o arquivo no disco já batem com o nome novo, e a compensação acima desfaz os dois se a lista falhar.

## 5. Interface

- Item **Renomear pasta...** no menu de contexto do nó, abaixo de "Nova subpasta aqui...". `Visibility` com `SpecialToVisibilityConverter`, como mover e apagar.
- Diálogo modal no estilo de `NewFolderDialog`: um campo, título `Renomear pasta`, texto inicial com o nome atual. OK vazio chega ao store, que recusa.
- Erros em `MessageBox`, `MessageBoxImage.Warning`, título `LEC — Renomear pasta`, como as outras falhas de pasta.
- `FolderNodeViewModel` passa a poder trocar o `StoreFolder` e notificar `DisplayName`. Filhos do nó não mudam. O id da pasta não muda, então a lista de mensagens aberta nessa pasta permanece.
- Cancelar o diálogo não chama o store.

## 6. Testes

**Store**

- Renomear uma pasta comum com mensagens: `Folders.dbx` tem o nome e o `FileName` novos; o arquivo velho não existe; o novo existe e o tamanho é o mesmo; uma linha de `MovedMessageCache` que apontava o arquivo velho passa a apontar o novo, com o mesmo offset.
- Pasta especial recusada; disco e `Folders.dbx` iguais.
- Irmão com o mesmo nome: recusa.
- `<nome>.dbx` já ocupado por outro arquivo: recusa, arquivo desta pasta intacto.
- `Folders.dbx` com `Health != Ok`: recusa, sem mover o `.dbx`.
- Rewrite de `Folders.dbx` falha depois do `File.Move`: o arquivo volta ao nome antigo.
- Nome igual sem diferenciar maiúsculas: no-op.
- Pasta sem arquivo de apoio: `Folders.dbx` muda o nome; nenhum `File.Move`.

**App**

- O comando de renomear do view model chama `RenameFolderAsync` e o nó passa a exibir o nome devolvido.
- Pasta especial: o view model nem precisa esconder o menu; o teste do store cobre a recusa. O XAML usa o converter já existente.

## 7. Fora deste corte

Renomear pastas especiais, sufixo `(1)`, reescrever mensagens, realinhar backups antigos para o nome novo, e mudar o id da pasta.
