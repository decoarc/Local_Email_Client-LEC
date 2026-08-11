# Ordenação alfabética da árvore de pastas — Design

**Status:** Aprovado para virar plano de implementação.
**Depende de:** `src/LEC.App/ViewModels/FolderTreeViewModel.cs` (`Build`), `src/LEC.MailStore/SpecialFolderKind.cs`. Não altera `IMailStore`/`DbxStore`/`DbxFormat` nem a ordem física dos registros no `.dbx` — é puramente uma decisão de exibição na árvore da UI.
**Contexto:** feature pedida diretamente pelo usuário. Hoje `FolderTreeViewModel.Build()` monta a árvore preservando a ordem em que `StoreFolder` aparece na lista vinda do `.dbx` (ordem de arquivo, não alfabética). O usuário quer que, ao abrir um perfil, as pastas fiquem ordenadas alfabeticamente, com as pastas especiais sempre no topo em uma ordem fixa; e que pastas novas, ao serem criadas, também apareçam já na posição correta.

## 1. Objetivo

Ordenar `RootNodes` e a lista `Children` de cada nó, em todo nível da árvore, seguindo a regra: pastas especiais primeiro (em ordem fixa), depois as demais pastas em ordem alfanumérica natural. A ordenação é recalculada a cada `Build()` — que já é chamado após toda mutação (abrir perfil, criar pasta, mover pasta, apagar pasta) — então nenhuma lógica adicional é necessária para "pasta nova aparece ordenada".

## 2. Decisões de escopo (confirmadas com o usuário)

- **"Destacados" não é uma 6ª pasta especial.** É o termo que o usuário usou para as pastas que já aparecem em negrito na árvore hoje (`FolderNodeViewModel.IsSpecial`). O conjunto de especiais continua sendo exatamente as 5 que já existem em `SpecialFolderKind`: Inbox, Outbox, SentItems, DeletedItems, Drafts.
- **Ordem fixa entre as especiais** (a "convencional" de cliente de email): Caixa de Entrada (Inbox) → Rascunho (Drafts) → Caixa de Saída (Outbox) → Itens Enviados (SentItems) → Itens Excluídos (DeletedItems). Essa ordem é independente da ordem em que os campos aparecem no `.dbx` e independente do valor numérico do enum `SpecialFolderKind` (que não segue essa sequência).
- **Ordem alfanumérica natural** para as demais pastas: "Pasta 2" vem antes de "Pasta 10" (compara blocos numéricos pelo valor, não caractere a caractere). Comparação de texto é sensível à cultura (pt-BR) e insensível a maiúsculas/minúsculas.
- **A regra vale em qualquer nível da árvore**, não só na raiz: dentro de cada grupo de irmãos (mesmo `ParentId`), especiais primeiro (se houver alguma entre os irmãos), depois o resto em ordem natural. Na prática as 5 especiais só existem na raiz (ou como filhas diretas da pasta organizacional "Pastas locais"), mas a regra não precisa de caso especial por nível — aplicar por grupo de irmãos já cobre isso.
- **Puramente uma decisão de exibição.** Não reescreve a ordem física dos registros no `Folders.dbx`, não muda `StoreFolder`, `DbxStore` ou qualquer contrato de `LEC.MailStore`. Só afeta como `FolderTreeViewModel` monta `RootNodes`/`Children`.

## 3. Onde a ordenação é aplicada

`ProfileViewModel` já chama `FolderTree.Build(store.Folders)` em todo ponto de mutação (construtor, `CreateFolderAsync`, `DeleteFolderAsync`, `MoveFolderAsync`) — é o único lugar onde a lista de `StoreFolder` vira nós da árvore. A ordenação entra dentro de `FolderTreeViewModel.Build()`:

- Hoje o método já separa duas responsabilidades: (1) resolver `ParentId` → nó pai (ou raiz, se `ParentId` for nulo ou apontar para algo inexistente) e (2) uma varredura de alcançabilidade que promove nós presos em ciclos a raízes "extras".
- `Build()` passa a acumular os filhos de cada pai (e as raízes) em listas temporárias em vez de inserir direto nas `ObservableCollection`; ordena cada lista com o comparador da seção 4; só então popula `RootNodes` e cada `Children` na ordem final.
- Os nós "extras" que a varredura de ciclo promove a raiz entram na mesma ordenação de `RootNodes` (não ficam sempre no fim) — um ciclo já é um caso degenerado hoje ("pasta vira raiz extra em vez de sumir"), não há motivo para tratá-lo como exceção à ordenação.
- O comportamento de preservar `IsExpanded` por identidade de pasta (não por instância de nó) continua exatamente como hoje — é ortogonal à ordem em que os nós entram nas coleções.

## 4. Comparador de ordenação

Novo tipo `FolderSortOrder` (está ao lado de `FolderTreeViewModel`, em `LEC.App.ViewModels` — é lógica de exibição, não pertence a `LEC.MailStore`) expõe um `Compare(FolderNodeViewModel, FolderNodeViewModel)`:

1. Calcula o "rank" de cada nó a partir de `Folder.SpecialFolderKind`, usando uma tabela fixa (não a ordem declarada do enum): `Inbox=0, Drafts=1, Outbox=2, SentItems=3, DeletedItems=4`, e `None` (ou qualquer valor fora da tabela) recebe rank 5 — depois de todas as especiais.
2. Se os ranks diferem, esse é o resultado da comparação.
3. Se os ranks são iguais (dois normais, ou duas especiais do mesmo tipo — não deveria acontecer na prática, mas o comparador precisa de um resultado determinístico mesmo assim), compara `DisplayName` com ordenação alfanumérica natural: percorre as duas strings em blocos alternados de dígitos/não-dígitos; blocos não-numéricos comparam como texto (cultura pt-BR, `CompareOptions.IgnoreCase`); blocos numéricos comparam pelo valor (`long`, com fallback para comparação de dígito a dígito se o número for grande demais para `long` — nomes de pasta não deveriam chegar nem perto disso, mas o comparador não deve estourar se acontecer).

## 5. Testes

Estende `tests/LEC.App.Tests/FolderTreeViewModelTests.cs`:

- Especiais sempre saem na ordem fixa (Inbox, Drafts, Outbox, SentItems, DeletedItems) mesmo quando a lista de entrada as declara em outra ordem.
- Pastas normais saem em ordem alfabética, incluindo acentos (`Itens Excluídos` vs `Itens Enviados` etc.), case-insensitive.
- Ordem numérica natural: `["Pasta 10", "Pasta 2", "Pasta 1"]` vira `["Pasta 1", "Pasta 2", "Pasta 10"]`.
- A regra se aplica a subpastas também: um grupo de irmãos no meio da árvore (não na raiz) fica ordenado do mesmo jeito.
- Uma pasta especial e uma pasta normal com nome que ordenaria antes alfabeticamente (ex.: "Arquivo" vs "Caixa de entrada") — a especial continua vindo primeiro, confirmando que o rank de especial tem prioridade sobre a ordem alfabética.
