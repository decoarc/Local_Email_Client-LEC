# Múltiplos perfis abertos simultaneamente — Design

**Status:** Aprovado para virar plano de implementação.
**Depende de:** Fase 2 (`LEC.App`, `LEC.MailStore`) e Fase 3 (Varredura), já concluídas e mescladas.
**Referência:** `ARQUITETURA.md` §9 (interface, "Múltiplos perfis abertos simultaneamente") e §12 (Fase 2), memória de projeto `roadmap-future-requirements` (decisão do usuário de 2026-07-27: múltiplos perfis é requisito novo, deve entrar antes da Fase 4 existir, sempre somente-leitura nesta fase).

## 1. Objetivo

Permitir abrir mais de um perfil de origem ao mesmo tempo (ex.: `BCP` e `BCP2`), cada um com sua própria árvore de pastas visível lado a lado, para comparar e preparar consolidação antes de uma futura importação para a biblioteca (Fase 4+). Continua **somente-leitura** nos dois — mover mensagens entre árvores, apagar ou criar pastas ficam para a Fase 4/5, que resolvem isso através da biblioteca de trabalho, nunca escrevendo nos arquivos de origem (princípio da seção 2 do `ARQUITETURA.md`).

## 2. Escopo

**Dentro:**
- Abrir múltiplos perfis: cada clique em "Abrir Perfil..." **adiciona** um novo perfil aos já abertos — nunca substitui. Sem limite fixo de quantos podem ficar abertos.
- Uma árvore de pastas por perfil aberto, todas visíveis ao mesmo tempo, empilhadas verticalmente (com scroll) na coluna esquerda que hoje mostra uma única árvore.
- Cada árvore identificada pelo nome da pasta do perfil (o nome do diretório escolhido no diálogo "Abrir Perfil...") — sem tratamento especial para nomes duplicados (abrir o mesmo diretório duas vezes é permitido e inofensivo, por ser leitura).
- Fechar um perfil individualmente, via botão "×" no cabeçalho da sua árvore.
- Lista de mensagens, painel de leitura e gatilho/barra de varredura (Fase 3) continuam **únicos** — mostram sempre a pasta clicada por último, em qualquer uma das árvores abertas.

**Fora (fases futuras ou já cobertas em `ARQUITETURA.md` §12):**
- Mover, copiar ou comparar mensagens entre perfis abertos — Fase 4 (Biblioteca) e Fase 5 (Reorganização); "mover" um item de uma árvore pra outra sempre importa para a biblioteca e organiza lá.
- Qualquer escrita nos arquivos `.dbx`/`.pst` de origem — nunca, em nenhuma fase (princípio da seção 2).
- Deduplicar perfis abertos com o mesmo caminho, ou detectar/avisar sobre isso.
- Persistir entre sessões quais perfis estavam abertos — cada abertura do LEC começa sem nenhum perfil aberto, como hoje.

## 3. Estrutura de projetos

```
src/
  LEC.App/
    ViewModels/
      ProfileViewModel.cs         NOVO — um perfil aberto: Store, FolderTree, DisplayName,
                                    ProfileDirectory
      MainWindowViewModel.cs      MODIFICADO — OpenProfiles (coleção) substitui o Store único;
                                    Store/CurrentFolderScavengeStatus passam a refletir o
                                    perfil ativo; +CloseProfile
      FolderTreeViewModel.cs      inalterado — reaproveitado, uma instância por ProfileViewModel
    MainWindow.xaml               MODIFICADO — TreeView único vira ItemsControl de árvores
                                    (uma por perfil aberto), lista/leitura/varredura inalteradas
    MainWindow.xaml.cs            MODIFICADO — handler de seleção de pasta passa a identificar
                                    de qual perfil veio o evento; handler do botão fechar
tests/
  LEC.App.Tests/                 MODIFICADO — MainWindowViewModelTests, FakeMailStore (se
                                    necessário) cobrindo abrir múltiplo, fechar, seleção
                                    trocando o perfil ativo
```

Nenhuma mudança em `LEC.MailStore` ou `LEC.DbxFormat` — cada perfil já é só uma instância independente de `IMailStore`/`DbxStore`; a novidade é inteiramente de orquestração na `LEC.App`.

## 4. Modelo de estado

- **`ProfileViewModel`** (novo): agrupa `Store` (`IMailStore`), `FolderTree` (`FolderTreeViewModel`), `DisplayName` (nome da pasta do perfil) e `ProfileDirectory` (caminho completo, guardado para eventual necessidade futura, não exibido). Uma instância por perfil aberto, com o mesmo tempo de vida do `Store` que carrega — descartado junto ao fechar.
- **`MainWindowViewModel.OpenProfiles`**: `ObservableCollection<ProfileViewModel>`. Cada "Abrir Perfil..." cria um `DbxStore.Open(...)` (mesma lógica de hoje, incluindo tratamento de erro com `StatusText`) e, em caso de sucesso, adiciona um novo `ProfileViewModel` a esta coleção.
- **Perfil ativo:** o perfil dono da última pasta selecionada em qualquer árvore. `MainWindowViewModel` rastreia isso via um `ProfileViewModel? _activeProfile` interno; `Store` e `CurrentFolderScavengeStatus` (já existentes, usados pelo gatilho de varredura da Fase 3) passam a refletir esse perfil ativo em vez de um único perfil fixo — preservando o contrato dessas propriedades para o código que já depende delas (`MainWindow.xaml.cs`: `HasPartialHealth`, `ScavengeButton_Click`, `ToggleRecoverableButton_Click`).
- **Selecionar pasta:** ao disparar em qualquer árvore, o code-behind identifica o `ProfileViewModel` dono via `DataContext` do `TreeView` que dispou o evento (cada instância de árvore, gerada pelo `ItemsControl`, tem o `ProfileViewModel` correspondente como `DataContext`) e chama `SelectFolderAsync(profile, node)`, que atualiza o perfil ativo e recarrega lista/status de varredura a partir do `Store` desse perfil.
- **Fechar perfil:** remove de `OpenProfiles`, descarta (`Dispose`) o `Store`. Se era o perfil ativo, limpa lista de mensagens, painel de leitura e status de varredura — mesmo estado visual de "nenhum perfil aberto".

## 5. Fluxo da UI

A coluna esquerda, hoje um único `TreeView`, vira um `ItemsControl` com scroll vertical ligado a `OpenProfiles`. Cada item mostra:
- Um cabeçalho com o nome do perfil (`DisplayName`) e um botão "×" para fechar só aquele perfil.
- Abaixo, a árvore de pastas daquele perfil, reaproveitando o `HierarchicalDataTemplate` já existente (ícones, cores de `IsLoadable`/`IsSpecial`, tooltip de pasta não carregável — nada disso muda).

Lista de mensagens, painel de leitura, gatilho de varredura contextual e barra de recuperáveis (Fase 3) continuam exatamente onde estão hoje, à direita — sem duplicação por perfil. Eles sempre refletem a pasta clicada por último, seja qual for a árvore.

Abrir o primeiro perfil se comporta exatamente como hoje. Abrir um segundo (ou terceiro...) apenas adiciona mais uma árvore à coluna esquerda, sem alterar o que já está selecionado à direita.

## 6. Erros e degradação

- Falha ao abrir um perfil (caminho inválido, `Folders.dbx` corrompido etc.): mesmo tratamento de hoje — mensagem em `StatusText`, nada é adicionado a `OpenProfiles`. Não afeta perfis já abertos.
- Fechar o único perfil ativo: lista/leitura/varredura voltam ao estado vazio, sem exceção — mesmo comportamento que já existe hoje para "nenhum perfil aberto ainda".
- Fechar um perfil que não é o ativo: nenhum efeito visível em lista/leitura — só a árvore dele some.

## 7. Estratégia de testes

- `MainWindowViewModelTests`: abrir dois perfis populates `OpenProfiles` com dois itens sem descartar o primeiro; selecionar pasta de um perfil depois do outro troca corretamente qual `Store`/`CurrentFolderScavengeStatus` está ativo; fechar um perfil remove da coleção e descarta seu `Store`; fechar o perfil ativo limpa lista/leitura.
- Sem automação de UI/XAML — mesmo padrão já estabelecido desde a Fase 2 (verificação manual: abrir dois perfis reais de `BCP/` — mesmo diretório duas vezes serve como teste de "sem tratamento especial para duplicata" — confirmar que ambas as árvores aparecem, que selecionar pastas alternando entre elas atualiza a lista corretamente, e que fechar uma não afeta a outra).

## 8. Fora de escopo / decisões adiadas

- Nome/forma exata de `SelectFolderAsync`, `CloseProfile` e como o code-behind identifica o perfil dono de um evento de seleção: decisão do plano de implementação, não deste design (a Parte 4 acima descreve a estratégia — `DataContext` do `TreeView` — mas os detalhes de assinatura ficam para lá).
- Layout do cabeçalho de cada árvore (estilo visual do botão "×", tipografia do nome do perfil): decisão de implementação/polish, não deste design.
- Persistir quais perfis estavam abertos entre sessões: não pedido, não incluído.
