# Visualizar arquivos .dbx ao selecionar a pasta do perfil — Design

**Status:** Aprovado para virar plano de implementação.
**Depende de:** `MainWindow.xaml.cs` (`OpenProfile_Click`, hoje usa `Microsoft.Win32.OpenFolderDialog`) — nenhum contrato novo em `IMailStore`/`DbxStore`/`DbxFormat`. Não altera como um perfil é aberto (`MainWindowViewModel.OpenProfile`), só como a pasta é escolhida.
**Contexto:** feature pedida diretamente pelo usuário. Hoje, ao clicar em "Abrir perfil", aparece o seletor de pastas nativo do Windows, título "Selecione a pasta do perfil (contendo Folders.dbx)". Esse diálogo nativo não mostra nenhuma informação sobre o conteúdo das pastas — o usuário quer, enquanto navega procurando a pasta certa, ver quais arquivos `.dbx` existem dentro da pasta atualmente selecionada, para confirmar visualmente que é a pasta certa antes de abrir o perfil.

## 1. Objetivo

Substituir o `OpenFolderDialog` nativo por um diálogo próprio (`OpenProfileDialog`) que, a cada pasta navegada, mostra a lista de arquivos `.dbx` presentes nela (só os nomes, não recursivo). O restante do fluxo — o que acontece depois de escolher a pasta — continua idêntico: `_viewModel.OpenProfile(caminhoEscolhido)`.

## 2. Decisões de escopo (confirmadas com o usuário)

- Só lista de **nomes** dos `.dbx` da pasta atual — sem tamanho, data de modificação ou preview do conteúdo. Isso pode ser adicionado depois se o usuário sentir falta, mas não foi pedido agora.
- Abrir o perfil em si **não muda** — mesmo `MainWindowViewModel.OpenProfile(path)`, mesmo comportamento de múltiplos perfis abertos simultaneamente.
- Navegação **não é recursiva**: a lista de `.dbx` mostra só os arquivos diretamente dentro da pasta atual, não de subpastas.
- Caminho digitável no topo (colar/digitar um caminho), para não regredir a capacidade que o diálogo nativo já oferece hoje via barra de endereço.

## 3. `ProfileFolderBrowserViewModel` (lógica de navegação, sem WPF)

Novo arquivo `src/LEC.App/ViewModels/ProfileFolderBrowserViewModel.cs`, testável sem precisar instanciar UI — mesmo espírito de separação já usado em `FolderTreeViewModel`.

```csharp
public sealed class ProfileFolderBrowserViewModel
{
    public string? CurrentPath { get; private set; }          // null = nível raiz (lista de drives)
    public ObservableCollection<string> Subfolders { get; }   // nomes de subpastas da pasta atual
    public ObservableCollection<string> DbxFiles { get; }     // nomes dos .dbx da pasta atual

    public ProfileFolderBrowserViewModel() => NavigateToRoot();

    public void NavigateToRoot();                  // CurrentPath = null; Subfolders = drives (Environment.GetLogicalDrives)
    public void NavigateTo(string? path);           // valida e navega; no-op silencioso se inválido/inacessível
    public void NavigateUp();                       // Directory.GetParent(CurrentPath); null → NavigateToRoot()
    public void EnterSubfolder(string name);         // ".." delega pra NavigateUp(); senão combina com CurrentPath (ou trata como drive, se CurrentPath == null) e chama NavigateTo
}
```

Comportamento de `NavigateTo`:
- `path == null` → equivale a `NavigateToRoot()`.
- Caso contrário, tenta `Directory.GetDirectories(path)` e `Directory.GetFiles(path, "*.dbx")`. Se qualquer uma lançar (`UnauthorizedAccessException`, `IOException`, `ArgumentException`, caminho inexistente) — **a navegação falha silenciosamente**: `CurrentPath`/`Subfolders`/`DbxFiles` permanecem no estado anterior, sem crash e sem mensagem de erro (o usuário só percebe que o caminho digitado não "pegou").
- Em sucesso, `CurrentPath` atualiza, `Subfolders` recebe os nomes das subpastas (ordenados) e `DbxFiles` os nomes dos `.dbx` encontrados (ordenados). Pasta sem nenhum `.dbx` deixa `DbxFiles` vazio — a UI mostra uma mensagem de placeholder nesse caso.

`NavigateToRoot()` popula `Subfolders` com `Environment.GetLogicalDrives()` (ex.: `C:\`, `D:\`) e zera `DbxFiles` (nível raiz não tem arquivos, só drives).

## 4. `OpenProfileDialog` (janela WPF)

Novo `src/LEC.App/OpenProfileDialog.xaml` + `.xaml.cs`, seguindo o padrão de code-behind direto já usado em `MoveMessageDialog`/`NewFolderDialog` (sem instanciar um `ViewModel` de app inteiro, só o `ProfileFolderBrowserViewModel` acima como `DataContext`).

Layout:
- **Topo:** `TextBox` com `CurrentPath` (two-way, `UpdateSourceTrigger=PropertyChanged` não necessário — só reage a Enter). `KeyDown` (tecla Enter) chama `NavigateTo(textBox.Text)`.
- **Meio, dividido em duas colunas:**
  - Esquerda: `ListBox` ligado a `Subfolders`, mais uma entrada sintética `".."` no topo quando `CurrentPath != null` (permite subir). Duplo-clique num item chama `EnterSubfolder(nome)`.
  - Direita: `ListBox` (somente leitura, sem interação) ligado a `DbxFiles`. Quando vazio, mostra um `TextBlock` overlay "Nenhum arquivo .dbx nesta pasta" (visibilidade condicionada a `DbxFiles.Count == 0`, mesmo padrão de conversor booleano já usado em outros pontos da UI).
- **Rodapé:** botão "Selecionar esta pasta" (`IsEnabled` ligado a `CurrentPath != null` — não dá pra selecionar o nível raiz de drives) e "Cancelar" (`IsCancel="True"`).

`OpenProfileDialog` expõe `public string? SelectedPath { get; private set; }`. O clique em "Selecionar esta pasta" faz `SelectedPath = _browser.CurrentPath; DialogResult = true; Close();`.

## 5. `MainWindow.xaml.cs`

```csharp
private void OpenProfile_Click(object sender, RoutedEventArgs e)
{
    var dialog = new OpenProfileDialog { Owner = this };
    if (dialog.ShowDialog() == true)
        _viewModel.OpenProfile(dialog.SelectedPath!);
}
```

Remove o uso de `Microsoft.Win32.OpenFolderDialog` deste método (fica só nos outros usos do arquivo, se houver — a busca feita durante a exploração só encontrou este ponto de uso para abrir perfil).

## 6. Testes

Novo `tests/LEC.App.Tests/ProfileFolderBrowserViewModelTests.cs`, usando uma pasta temporária real (`Path.GetTempPath()` + `Guid.NewGuid()`, limpa no `Dispose`/fixture) com subpastas e arquivos `.dbx`/outros criados no `Arrange`:

- `NavigateTo` numa pasta com `.dbx` misturados com outros arquivos → `DbxFiles` só contém os `.dbx`, `Subfolders` só contém diretórios.
- `EnterSubfolder` desce um nível e atualiza `DbxFiles` para o conteúdo da subpasta.
- `EnterSubfolder("..")` (ou `NavigateUp()`) depois de descer volta pro estado anterior.
- Pasta sem nenhum `.dbx` → `DbxFiles` vazio.
- `NavigateTo` com caminho inexistente/inválido → estado (CurrentPath/Subfolders/DbxFiles) permanece inalterado, sem exceção.
- `NavigateToRoot()` (estado inicial) → `CurrentPath == null`, `Subfolders` não vazio e contém ao menos uma drive válida.

Não é viável simular de forma portável uma pasta com permissão negada em CI — esse caso (pasta inacessível) fica só no checklist manual abaixo, não em teste automatizado.

Sem teste de UI/XAML automatizado (mesma limitação já registrada em specs anteriores — sem framework de teste de interação de UI no projeto). Checklist manual cobre a janela em si.

## 7. Checklist manual (GUI)

- Abrir o diálogo ("Abrir perfil"): aparece lista de drives, painel de `.dbx` vazio, botão "Selecionar esta pasta" desabilitado.
- Navegar (duplo-clique) até uma pasta de perfil real (com `Folders.dbx` + `Inbox.dbx` etc.): nomes aparecem no painel direito.
- Navegar até uma pasta sem `.dbx`: aparece a mensagem de "nenhum arquivo .dbx".
- Usar `".."` para subir um nível: volta pra listagem anterior corretamente.
- Colar/digitar um caminho válido na caixa de texto e pressionar Enter: navega direto pra lá.
- Digitar um caminho inválido e pressionar Enter: nada quebra, permanece na pasta atual.
- Clicar "Selecionar esta pasta" numa pasta de perfil válida: perfil abre exatamente como abre hoje.
- Clicar "Cancelar": nenhum perfil é aberto, janela fecha.
- Abrir dois perfis diferentes em sequência usando o novo diálogo: continuam coexistindo em `OpenProfiles` como hoje (sem regressão do suporte a múltiplos perfis).
