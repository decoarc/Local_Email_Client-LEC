# Abrir perfil: voltar ao diálogo nativo do Windows, filtrado para .dbx — Design

**Status:** Aprovado para virar plano de implementação.
**Supersede:** `docs/superpowers/specs/2026-08-10-visualizar-dbx-selecionar-perfil-design.md` — reverte a decisão de construir um diálogo `OpenProfileDialog` próprio. `OpenProfileDialog.xaml`/`.xaml.cs` e `ProfileFolderBrowserViewModel` (implementados e mergeados em `fase4-simplificada`) são removidos por este design.
**Depende de:** `MainWindow.xaml.cs` (`OpenProfile_Click`, hoje usa o `OpenProfileDialog` customizado). Nenhum contrato novo em `IMailStore`/`DbxStore`/`DbxFormat`. Não altera como um perfil é aberto (`MainWindowViewModel.OpenProfile`), só como o caminho é escolhido.
**Contexto:** depois de ver o diálogo customizado em funcionamento, o usuário pediu para manter a aparência 100% nativa do Windows (igual ao seletor de pastas de sempre) — só queria que os arquivos `.dbx` aparecessem enquanto navega, sem um diálogo próprio construído do zero.

## 1. Objetivo

Trocar `Microsoft.Win32.OpenFileDialog` (aberto com filtro `*.dbx`) no lugar do `OpenProfileDialog` customizado. O Explorer nativo do Windows, ao filtrar por tipo de arquivo, já lista apenas os `.dbx` de cada pasta conforme o usuário navega — exatamente o efeito pedido, sem nenhuma UI própria.

## 2. Decisões de escopo (confirmadas com o usuário)

- Abordagem escolhida entre duas apresentadas: usar o diálogo nativo de **seleção de arquivo** (filtrado para `.dbx`), em vez de customizar o diálogo nativo de seleção de pasta via API do Windows (opção descartada por complexidade).
- O usuário seleciona qualquer `.dbx` dentro da pasta do perfil (ex.: `Folders.dbx`, ou um `.dbx` de pasta específica como `Inbox.dbx` — tanto faz, todos vivem na mesma pasta de perfil); a pasta do perfil passa a ser a pasta que contém o arquivo escolhido.
- Abrir o perfil em si **não muda**: mesmo `MainWindowViewModel.OpenProfile(path)`, mesmo comportamento de múltiplos perfis abertos simultaneamente.
- Sem lista de nomes própria, sem navegação customizada, sem caixa de texto de caminho própria — tudo isso já vem de graça do diálogo nativo do Windows.

## 3. `MainWindow.xaml.cs`

```csharp
private void OpenProfile_Click(object sender, RoutedEventArgs e)
{
    var dialog = new OpenFileDialog
    {
        Title = "Selecione um arquivo .dbx do perfil (ex: Folders.dbx)",
        Filter = "Arquivos .dbx (*.dbx)|*.dbx",
        CheckFileExists = true,
    };

    if (dialog.ShowDialog() == true)
        _viewModel.OpenProfile(Path.GetDirectoryName(dialog.FileName)!);
}
```

`Path.GetDirectoryName` de um `FileName` retornado por um `OpenFileDialog` bem-sucedido é sempre um caminho absoluto com diretório pai — não pode ser `null` nesse ponto (só retorna `null` para uma raiz sem pai, o que um arquivo selecionável nunca é), daí o `!`.

`using Microsoft.Win32;` já existe no topo do arquivo (usado por `SaveFileDialog` no handler de anexos); `using System.IO;` também já é necessário em outros pontos do arquivo — confirmar durante a implementação e adicionar se faltar.

## 4. Remoção

Ficam obsoletos e são deletados:
- `src/LEC.App/OpenProfileDialog.xaml`
- `src/LEC.App/OpenProfileDialog.xaml.cs`
- `src/LEC.App/ViewModels/ProfileFolderBrowserViewModel.cs`
- `tests/LEC.App.Tests/ProfileFolderBrowserViewModelTests.cs`

## 5. Casos de borda

- **Pasta sem `.dbx`:** o próprio Explorer nativo mostra "Nenhum item corresponde à pesquisa" (comportamento padrão do filtro de tipo, já visto no print que o usuário mandou) — nenhum código adicional necessário.
- **Cancelar:** `ShowDialog() != true`, nada acontece — igual ao comportamento atual.
- **Arquivo `.dbx` selecionado não é `Folders.dbx`:** não há validação de nome no momento da seleção; a pasta é aberta do mesmo jeito e `MainWindowViewModel.OpenProfile` trata a ausência de `Folders.dbx` como já trata hoje (comportamento downstream inalterado por este design).

## 6. Testes

Nenhum teste automatizado é possível ou necessário: `OpenProfile_Click` só monta e mostra um `OpenFileDialog` nativo do Windows, sem lógica própria testável (mesma limitação já registrada no design anterior para janelas WPF). A remoção de `ProfileFolderBrowserViewModel` também remove os únicos testes que existiam para este fluxo — não há substituto, porque não sobra lógica não-trivial para testar.

Validação: build limpo (`dotnet build`), suite completa continua passando (nenhum teste restante referencia os arquivos removidos), e checklist manual abaixo.

## 7. Checklist manual (GUI)

- Clicar "Abrir perfil": abre o Explorer nativo do Windows, filtro já em "Arquivos .dbx (*.dbx)".
- Navegar até uma pasta de perfil real (com `Folders.dbx` + `.dbx` de pastas): os nomes `.dbx` aparecem na listagem, como qualquer arquivo filtrado no Explorer.
- Navegar até uma pasta sem `.dbx`: lista vazia / "Nenhum item corresponde à pesquisa" (comportamento nativo).
- Selecionar um `.dbx` e clicar "Abrir": perfil abre exatamente como abre hoje (árvore de pastas populada, etc.).
- Clicar "Cancelar": nenhum perfil é aberto.
- Abrir dois perfis diferentes em sequência: ambos continuam abertos simultaneamente (sem regressão do suporte a múltiplos perfis).
