# Ordenação alfabética da árvore de pastas Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ordenar a árvore de pastas exibida na UI (`FolderTreeViewModel.RootNodes`/`Children`) com as 5 pastas especiais sempre no topo em ordem fixa, seguidas das demais pastas em ordem alfanumérica natural — em todo nível da árvore, recalculada automaticamente a cada rebuild (abrir perfil, criar/mover/apagar pasta).

**Architecture:** Um novo comparador estático `FolderSortOrder` (em `LEC.App.ViewModels`) decide a ordem entre dois `FolderNodeViewModel`: primeiro por "rank" de pasta especial (tabela fixa, não a ordem do enum), depois por comparação alfanumérica natural do nome. `FolderTreeViewModel.Build()` passa a acumular cada grupo de irmãos (raízes e filhos de cada pai) em listas temporárias, ordená-las com esse comparador, e só então popular as `ObservableCollection` finais — em vez de inserir direto durante a resolução de `ParentId` como faz hoje.

**Tech Stack:** C# / .NET, WPF (`ObservableCollection`), xUnit.

## Global Constraints

- Não altera `IMailStore`/`DbxStore`/`DbxFormat`, nem a ordem física dos registros no `.dbx` — é puramente exibição em `LEC.App`.
- Ordem fixa das especiais: Caixa de Entrada (Inbox) → Rascunho (Drafts) → Caixa de Saída (Outbox) → Itens Enviados (SentItems) → Itens Excluídos (DeletedItems). Não é a ordem declarada em `SpecialFolderKind` (que é Inbox, Outbox, SentItems, DeletedItems, Drafts).
- Ordenação alfanumérica natural (ex.: "Pasta 2" antes de "Pasta 10"), comparação de texto sensível à cultura pt-BR e insensível a maiúsc./minúsc.
- A regra vale em qualquer nível da árvore (todo grupo de irmãos, não só a raiz), incluindo raízes "extras" promovidas pela varredura de ciclo já existente em `Build()`.

---

### Task 1: Ordenar pastas especiais primeiro, depois alfanumérica natural

**Files:**
- Create: `src/LEC.App/ViewModels/FolderSortOrder.cs`
- Modify: `src/LEC.App/ViewModels/FolderTreeViewModel.cs` (método `Build`, linhas 11-63)
- Test: `tests/LEC.App.Tests/FolderTreeViewModelTests.cs`

**Interfaces:**
- Consumes: `FolderNodeViewModel` (já existe, `Folder.SpecialFolderKind` e `DisplayName`), `SpecialFolderKind` enum (`LEC.MailStore`).
- Produces: `internal static class FolderSortOrder` com `public static int Compare(FolderNodeViewModel a, FolderNodeViewModel b)` — assinatura compatível com `Comparison<FolderNodeViewModel>`, usada por `List<FolderNodeViewModel>.Sort(...)`.

- [ ] **Step 1: Escrever os testes que falham em `FolderTreeViewModelTests.cs`**

Adicionar ao final da classe `FolderTreeViewModelTests` (antes do `}` final):

```csharp
    [Fact]
    public void SortsSpecialFoldersFirstInFixedOrderRegardlessOfInputOrder()
    {
        var folders = new[]
        {
            new StoreFolder { Id = 1, ParentId = null, Name = "Itens excluídos", IsLoadable = true, HasBackingFile = true, SpecialFolderKind = SpecialFolderKind.DeletedItems },
            new StoreFolder { Id = 2, ParentId = null, Name = "Itens enviados", IsLoadable = true, HasBackingFile = true, SpecialFolderKind = SpecialFolderKind.SentItems },
            new StoreFolder { Id = 3, ParentId = null, Name = "Caixa de saída", IsLoadable = true, HasBackingFile = true, SpecialFolderKind = SpecialFolderKind.Outbox },
            new StoreFolder { Id = 4, ParentId = null, Name = "Rascunho", IsLoadable = true, HasBackingFile = true, SpecialFolderKind = SpecialFolderKind.Drafts },
            new StoreFolder { Id = 5, ParentId = null, Name = "Caixa de entrada", IsLoadable = true, HasBackingFile = true, SpecialFolderKind = SpecialFolderKind.Inbox },
        };

        var vm = new FolderTreeViewModel();
        vm.Build(folders);

        Assert.Equal(
            new[] { "Caixa de entrada", "Rascunho", "Caixa de saída", "Itens enviados", "Itens excluídos" },
            vm.RootNodes.Select(n => n.DisplayName));
    }

    [Fact]
    public void SortsOrdinaryFoldersAlphabeticallyAfterSpecials()
    {
        var folders = new[]
        {
            new StoreFolder { Id = 1, ParentId = null, Name = "Zebra", IsLoadable = true, HasBackingFile = true },
            new StoreFolder { Id = 2, ParentId = null, Name = "Árvore", IsLoadable = true, HasBackingFile = true },
            new StoreFolder { Id = 3, ParentId = null, Name = "banana", IsLoadable = true, HasBackingFile = true },
            new StoreFolder { Id = 4, ParentId = null, Name = "Caixa de entrada", IsLoadable = true, HasBackingFile = true, SpecialFolderKind = SpecialFolderKind.Inbox },
        };

        var vm = new FolderTreeViewModel();
        vm.Build(folders);

        // "Caixa de entrada" ordenaria depois de "Árvore" alfabeticamente, mas é
        // especial -- o rank de especial tem prioridade sobre a ordem alfabética.
        Assert.Equal(
            new[] { "Caixa de entrada", "Árvore", "banana", "Zebra" },
            vm.RootNodes.Select(n => n.DisplayName));
    }

    [Fact]
    public void SortsNumberedFoldersNaturallyNotLexically()
    {
        var folders = new[]
        {
            new StoreFolder { Id = 1, ParentId = null, Name = "Pasta 10", IsLoadable = true, HasBackingFile = true },
            new StoreFolder { Id = 2, ParentId = null, Name = "Pasta 2", IsLoadable = true, HasBackingFile = true },
            new StoreFolder { Id = 3, ParentId = null, Name = "Pasta 1", IsLoadable = true, HasBackingFile = true },
        };

        var vm = new FolderTreeViewModel();
        vm.Build(folders);

        Assert.Equal(
            new[] { "Pasta 1", "Pasta 2", "Pasta 10" },
            vm.RootNodes.Select(n => n.DisplayName));
    }

    [Fact]
    public void SortingAppliesToSubfoldersToo()
    {
        var folders = new[]
        {
            Folder(1, null, "Pastas locais"),
            Folder(2, 1, "Zebra"),
            Folder(3, 1, "Amora"),
        };

        var vm = new FolderTreeViewModel();
        vm.Build(folders);

        var root = vm.RootNodes.Single();
        Assert.Equal(new[] { "Amora", "Zebra" }, root.Children.Select(n => n.DisplayName));
    }

    [Fact]
    public void CyclePromotedRootsAreSortedTogetherWithOrdinaryRoots()
    {
        var folders = new[]
        {
            Folder(1, 2, "Zebra"),
            Folder(2, 1, "Amora"),
        };

        var vm = new FolderTreeViewModel();
        vm.Build(folders);

        Assert.Equal(new[] { "Amora", "Zebra" }, vm.RootNodes.Select(n => n.DisplayName));
    }
```

- [ ] **Step 2: Rodar os testes e confirmar que falham**

Run: `rtk dotnet test tests/LEC.App.Tests/LEC.App.Tests.csproj --filter "FullyQualifiedName~FolderTreeViewModelTests"`

Expected: FAIL nos 5 testes novos (a ordem atual segue a ordem de entrada da lista, não a esperada). Os testes pré-existentes continuam passando.

- [ ] **Step 3: Criar `src/LEC.App/ViewModels/FolderSortOrder.cs`**

```csharp
using System;
using System.Globalization;
using LEC.MailStore;

namespace LEC.App.ViewModels;

/// <summary>
/// Ordena a árvore de pastas para exibição: pastas especiais primeiro, numa ordem
/// fixa (não a ordem declarada em SpecialFolderKind), depois as demais pastas em
/// ordem alfanumérica natural. Puramente uma decisão de exibição -- não afeta
/// LEC.MailStore nem a ordem física dos registros no .dbx.
/// </summary>
internal static class FolderSortOrder
{
    private static readonly SpecialFolderKind[] SpecialRankOrder =
    {
        SpecialFolderKind.Inbox,
        SpecialFolderKind.Drafts,
        SpecialFolderKind.Outbox,
        SpecialFolderKind.SentItems,
        SpecialFolderKind.DeletedItems,
    };

    private static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("pt-BR");

    public static int Compare(FolderNodeViewModel a, FolderNodeViewModel b)
    {
        var rankA = SpecialRank(a.Folder.SpecialFolderKind);
        var rankB = SpecialRank(b.Folder.SpecialFolderKind);
        if (rankA != rankB)
            return rankA.CompareTo(rankB);

        return NaturalCompare(a.DisplayName, b.DisplayName);
    }

    private static int SpecialRank(SpecialFolderKind kind)
    {
        var index = Array.IndexOf(SpecialRankOrder, kind);
        return index == -1 ? SpecialRankOrder.Length : index;
    }

    /// <summary>
    /// Compara duas strings em blocos alternados de dígitos/não-dígitos: blocos de
    /// texto comparam como texto (cultura pt-BR, sem diferenciar maiúsc./minúsc.);
    /// blocos numéricos comparam pelo valor (zeros à esquerda descartados, depois
    /// por tamanho e por dígitos) em vez de caractere a caractere -- "Pasta 2" vem
    /// antes de "Pasta 10". Evita fazer parse para long: números arbitrariamente
    /// grandes não estouram, só nunca deveriam ocorrer em nome de pasta.
    /// </summary>
    internal static int NaturalCompare(string x, string y)
    {
        var i = 0;
        var j = 0;

        while (i < x.Length && j < y.Length)
        {
            if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
            {
                var startI = i;
                while (i < x.Length && char.IsDigit(x[i])) i++;
                var startJ = j;
                while (j < y.Length && char.IsDigit(y[j])) j++;

                var numX = x.Substring(startI, i - startI).TrimStart('0');
                var numY = y.Substring(startJ, j - startJ).TrimStart('0');

                if (numX.Length != numY.Length)
                    return numX.Length.CompareTo(numY.Length);

                var digitCompare = string.CompareOrdinal(numX, numY);
                if (digitCompare != 0)
                    return digitCompare;

                continue;
            }

            var startTextI = i;
            while (i < x.Length && !char.IsDigit(x[i])) i++;
            var startTextJ = j;
            while (j < y.Length && !char.IsDigit(y[j])) j++;

            var textX = x.Substring(startTextI, i - startTextI);
            var textY = y.Substring(startTextJ, j - startTextJ);

            var textCompare = string.Compare(textX, textY, Culture, CompareOptions.IgnoreCase);
            if (textCompare != 0)
                return textCompare;
        }

        return (x.Length - i).CompareTo(y.Length - j);
    }
}
```

- [ ] **Step 4: Modificar `Build()` em `src/LEC.App/ViewModels/FolderTreeViewModel.cs`**

Substituir o método `Build` inteiro (linhas 11-63) por:

```csharp
    public void Build(IReadOnlyList<StoreFolder> folders)
    {
        // Build() always replaces every node with a brand-new instance, even for
        // folders untouched by whatever change triggered this rebuild -- the WPF
        // TreeView tracks expanded/collapsed state per item instance, so without this
        // every subfolder the user had open would collapse on any folder create,
        // delete, or move.
        var expandedIds = new HashSet<int>();
        CollectExpandedIds(RootNodes, expandedIds);

        RootNodes.Clear();

        var nodesById = new Dictionary<int, FolderNodeViewModel>();
        foreach (var folder in folders)
            nodesById[folder.Id] = new FolderNodeViewModel(folder) { IsExpanded = expandedIds.Contains(folder.Id) };

        // Children are grouped per parent (instead of added straight to the
        // ObservableCollection tree) so each sibling group can be sorted as a whole
        // before anything is added to RootNodes/Children -- sorting after the fact
        // would mean re-adding every node anyway.
        var childrenByParentId = new Dictionary<int, List<FolderNodeViewModel>>();
        var roots = new List<FolderNodeViewModel>();

        foreach (var folder in folders)
        {
            var node = nodesById[folder.Id];

            // A ParentId that doesn't resolve to a known folder (root sentinel already
            // mapped to null by DbxStore, or a genuine dangling reference like the real
            // profile's folder 73 -> 53) becomes a top-level node.
            if (folder.ParentId is int parentId && nodesById.ContainsKey(parentId))
            {
                if (!childrenByParentId.TryGetValue(parentId, out var siblings))
                    childrenByParentId[parentId] = siblings = new List<FolderNodeViewModel>();
                siblings.Add(node);
            }
            else
            {
                roots.Add(node);
            }
        }

        // A folder whose ParentId participates in a cycle (self-reference, or A -> B -> A)
        // resolves successfully as a child of its cycle-mate above and never reaches
        // roots via the pass that just ran -- it isn't dropped by a crash, it's dropped
        // by silently succeeding in the wrong way. Sweep for any node unreachable from an
        // actual root and surface it as a root too, so a cycle degrades into "extra
        // top-level folders" instead of "folder vanished".
        var reached = new HashSet<int>();
        var stack = new Stack<FolderNodeViewModel>(roots);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (!reached.Add(current.Folder.Id))
                continue;

            if (childrenByParentId.TryGetValue(current.Folder.Id, out var siblings))
                foreach (var child in siblings)
                    stack.Push(child);
        }

        foreach (var folder in folders)
        {
            if (!reached.Contains(folder.Id))
                roots.Add(nodesById[folder.Id]);
        }

        // Sorted last, once each sibling group (including cycle-promoted extra roots)
        // is fully assembled -- special folders first in a fixed order, then the rest
        // alphanumerically. See FolderSortOrder.
        roots.Sort(FolderSortOrder.Compare);
        foreach (var root in roots)
            RootNodes.Add(root);

        foreach (var (parentId, siblings) in childrenByParentId)
        {
            siblings.Sort(FolderSortOrder.Compare);
            foreach (var child in siblings)
                nodesById[parentId].Children.Add(child);
        }
    }
```

O resto do arquivo (`RootNodes`, `CollectExpandedIds`, usings, namespace) não muda.

- [ ] **Step 5: Rodar os testes e confirmar que passam**

Run: `rtk dotnet test tests/LEC.App.Tests/LEC.App.Tests.csproj --filter "FullyQualifiedName~FolderTreeViewModelTests"`

Expected: PASS em todos os testes (os 5 novos e os pré-existentes, que são independentes de ordem — conferir que nenhum quebrou).

- [ ] **Step 6: Rodar a suíte completa para checar regressões em outros testes**

Run: `rtk dotnet test`

Expected: PASS em tudo (`LEC.App.Tests`, `LEC.MailStore.Tests`, `LEC.DbxFormat.Tests`). Nenhum outro teste depende da ordem anterior (de arquivo) das pastas.

- [ ] **Step 7: Commit**

```bash
git add src/LEC.App/ViewModels/FolderSortOrder.cs src/LEC.App/ViewModels/FolderTreeViewModel.cs tests/LEC.App.Tests/FolderTreeViewModelTests.cs
git commit -m "Sort folder tree: special folders first, then natural alphanumeric order"
```

---

## Manual GUI checklist (Step final, rodar manualmente)

Depois do commit, abrir o app de verdade e confirmar visualmente:

1. Abrir um perfil `.dbx` com várias pastas, algumas especiais e algumas normais com nomes numerados (ex.: renomear/criar "Pasta 1", "Pasta 2", "Pasta 10" se o perfil de teste não tiver isso pronto).
2. Confirmar que as 5 especiais aparecem no topo na ordem: Caixa de Entrada, Rascunho, Caixa de Saída, Itens Enviados, Itens Excluídos.
3. Confirmar que as demais pastas aparecem em ordem alfabética, e que "Pasta 2" aparece antes de "Pasta 10".
4. Criar uma pasta nova com um nome que deveria cair no meio da lista alfabética (não no fim) e confirmar que ela aparece já na posição correta, sem precisar fechar/reabrir o perfil.
5. Se o perfil tiver subpastas, expandir uma e confirmar que a mesma regra de ordenação vale ali também.
