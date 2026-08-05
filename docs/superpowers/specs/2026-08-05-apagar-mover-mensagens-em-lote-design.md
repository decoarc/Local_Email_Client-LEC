# Apagar/mover mensagens em lote — Design

**Status:** Aprovado para virar plano de implementação.
**Depende de:** `MainWindowViewModel.DeleteMessageAsync`/`MoveMessageAsync` (fatias "mover mensagem — motor"/"apagar mensagem", já mescladas — reaproveitados sem nenhuma mudança de contrato), `MoveMessageDialog` (já mesclado, reaproveitado sem mudança).
**Referência:** `docs/superpowers/specs/2026-08-04-mover-mensagem-motor-design.md` (`MoveMessageAsync`, a operação por mensagem que este lote chama repetidamente); `docs/superpowers/specs/2026-08-04-apagar-mensagem-design.md` (`DeleteMessageAsync`, idem).
**Contexto:** extensão pontual das fatias de apagar/mover mensagem (ambas já mescladas), que hoje só operam sobre uma mensagem por vez. Pedido direto do usuário: selecionar várias mensagens com Ctrl e aplicar apagar/mover a todas de uma vez. Escopo explicitamente limitado a mensagens — não afeta pastas.

## 1. Objetivo

Deixar o usuário selecionar várias mensagens na lista (Ctrl+clique, Shift+clique) e aplicar "Apagar" ou "Mover para..." a todas de uma vez, reaproveitando as operações por-mensagem já existentes e testadas — sem nenhuma mudança no motor (`IMailStore`/`DbxStore`).

## 2. Decisões de escopo (confirmadas com o usuário)

- **Seleção múltipla via `ListView.SelectionMode="Extended"`** (Ctrl+clique alterna item, Shift+clique seleciona intervalo) — comportamento padrão do Windows, não uma seleção customizada.
- **Menu de contexto e tecla Delete agem sobre a seleção atual** (`MessageListView.SelectedItems`), não sobre a linha especificamente clicada — clicar com o botão direito numa linha fora da seleção atual **mantém** a seleção existente e age sobre ela (decisão explícita: não replica o comportamento do Explorer de trocar a seleção pro item clicado). Exceção: se nada estiver selecionado no momento do clique direito (situação normal na primeira interação com a lista), cai de volta pra tratar a própria linha clicada como uma seleção de 1 item — sem essa exceção, o primeiro clique direito da sessão não faria nada.
- **Falha no meio do lote para a operação ali**, sem tentar as mensagens restantes. As que já tiveram sucesso continuam com sucesso (cada mensagem já é uma operação atômica e independente por si só — não há nada pra desfazer). O erro mostrado ao usuário informa quantas tiveram sucesso antes da falha.
- **Painel de leitura fica vazio sempre que a seleção não for de exatamente 1 mensagem** (0 ou 2+) — mesmo padrão já usado por clientes de e-mail populares.
- **Mover em lote abre o diálogo de destino uma única vez** — todas as mensagens da lista vêm da mesma pasta (a lista só exibe uma pasta por vez), então a validação de "pasta de origem inválida como destino" já existente (`MoveDestinationRules`) continua válida sem nenhuma mudança.
- **Sem novo método em `IMailStore`/`DbxStore`.** Toda a orquestração do lote é feita chamando `MainWindowViewModel.DeleteMessageAsync`/`MoveMessageAsync` (já existentes) uma vez por mensagem.

## 3. `MainWindowViewModel.DeleteMessagesAsync`/`MoveMessagesAsync` (novos)

```csharp
/// <summary>
/// Deletes multiple messages, one at a time via the existing DeleteMessageAsync, stopping
/// at the first failure. Messages already deleted before the failure stay deleted — each
/// message is its own atomic, independent operation, so there's nothing to roll back.
/// </summary>
/// <exception cref="Exception">
/// Whatever DeleteMessageAsync throws for the failing message, rewrapped with a prefix
/// stating how many messages succeeded before it, e.g. "3 de 5 mensagens apagadas antes
/// de falhar: {mensagem original}".
/// </exception>
public async Task DeleteMessagesAsync(ProfileViewModel profile, IReadOnlyList<MessageHeader> messages, CancellationToken ct = default)
{
    int succeeded = 0;
    foreach (var message in messages)
    {
        try
        {
            await DeleteMessageAsync(profile, message, ct);
            succeeded++;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"{succeeded} de {messages.Count} mensagens apagadas antes de falhar: {ex.Message}", ex);
        }
    }
}

/// <summary>
/// Moves multiple messages (all from the same source folder — the message list only ever
/// shows one folder at a time) into a single chosen destination, one at a time via the
/// existing MoveMessageAsync, stopping at the first failure. Same "nothing to roll back"
/// reasoning as DeleteMessagesAsync.
/// </summary>
/// <exception cref="Exception">
/// Same rewrapping as DeleteMessagesAsync, e.g. "3 de 5 mensagens movidas antes de falhar:
/// {mensagem original}".
/// </exception>
public async Task MoveMessagesAsync(ProfileViewModel sourceProfile, IReadOnlyList<MessageHeader> messages,
    ProfileViewModel destinationProfile, int destinationFolderId, CancellationToken ct = default)
{
    int succeeded = 0;
    foreach (var message in messages)
    {
        try
        {
            await MoveMessageAsync(sourceProfile, message, destinationProfile, destinationFolderId, ct);
            succeeded++;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"{succeeded} de {messages.Count} mensagens movidas antes de falhar: {ex.Message}", ex);
        }
    }
}
```

Ambos são invólucros finos por cima dos métodos já existentes e testados — nenhuma lógica nova de apagar/mover, só o laço e o reempacotamento da mensagem de erro.

## 4. UI (`MainWindow.xaml`/`.xaml.cs`)

**`MessageListView.SelectionMode="Extended"`** (era o padrão implícito `Single`).

**Resolução do alvo da operação** — novo método auxiliar, usado tanto pelo menu de contexto quanto pela tecla Delete:

```csharp
private List<MessageHeader> ResolveTargetMessages(object sender)
{
    if (MessageListView.SelectedItems.Count > 0)
        return MessageListView.SelectedItems.Cast<MessageHeader>().ToList();

    // No selection yet (e.g. the very first right-click in a freshly loaded list) --
    // fall back to treating the clicked row itself as a 1-item selection.
    if ((sender as MenuItem)?.DataContext is MessageHeader header)
        return new List<MessageHeader> { header };

    return new List<MessageHeader>();
}
```

`MoveMessage_Click`/`DeleteMessage_Click`/`MessageListView_KeyDown` passam a resolver a lista de mensagens-alvo assim (a tecla Delete não tem `sender` de `MenuItem`, então usa só `MessageListView.SelectedItems` diretamente, igual já faz hoje — sem a queda pro item clicado, que não existe nesse caminho).

**Confirmação de apagar** — texto pluralizado conforme a contagem: `"Apagar esta mensagem?"` pra 1, `"Apagar estas {N} mensagens?"` pra mais de uma.

**Mover** — `MoveMessageDialog` continua recebendo `sourceFolderId` de uma mensagem qualquer do lote (`messages[0].Id.FolderId`, já que todas vêm da mesma pasta), sem nenhuma mudança no diálogo em si.

**Painel de leitura** — `MessageListView_SelectionChanged` passa a só chamar `SelectMessageAsync`/renderizar quando `MessageListView.SelectedItems.Count == 1`; caso contrário (0 ou 2+), limpa o painel (`NavigateToRenderedMessage(string.Empty)` + colapsa `ImagesBlockedBar`), mesmo reset visual já usado em outros pontos.

**Reload pós-operação** — reaproveita exatamente o já existente (`SelectFolderAsync` no perfil/nó atualmente selecionados, se ainda os mesmos), sem mudança — já funciona igual pra 1 ou N mensagens, já que só recarrega a pasta inteira.

## 5. Erros e casos de borda

- Nada selecionado e tecla Delete pressionada → no-op (mesmo comportamento já existente).
- Nada selecionado e clique direito → menu aplica à própria linha clicada (fallback do `ResolveTargetMessages`).
- Lote de 1 mensagem → comportamento idêntico ao já existente hoje (texto de confirmação no singular, sem laço perceptível).
- Falha na 1ª mensagem do lote → mensagem de erro diz "0 de N... antes de falhar", nada foi alterado.
- Cancelar a confirmação de apagar, ou cancelar o diálogo de mover → nenhuma chamada ao motor acontece, nenhum estado muda (mesmo padrão já usado).
- Trocar de pasta (ou de perfil) com uma seleção múltipla ativa → a nova pasta carrega com sua própria lista/seleção vazia, sem herdar nada da pasta anterior (comportamento já existente do `ListView`/`ItemsSource` rebind, sem necessidade de código novo).

## 6. Estratégia de testes

- `MainWindowViewModelTests` (extensão, com `FakeMailStore`): `DeleteMessagesAsync` chama `DeleteMessageAsync` uma vez por mensagem, na ordem dada; para no meio quando uma falha (mensagens após a que falhou nunca chegam a ser chamadas) e a mensagem de exceção contém a contagem de sucesso; sucesso total quando todas passam. Mesmo conjunto de casos para `MoveMessagesAsync`, incluindo que a exceção da mensagem que falhou fica embutida na nova exceção lançada (`InnerException` ou texto).
- Sem teste automatizado pra `MainWindow.xaml.cs`/`ResolveTargetMessages` (mesmo precedente já estabelecido) — verificação manual: selecionar 3 mensagens com Ctrl, apagar todas de uma vez; selecionar um intervalo com Shift, mover todas pra outra pasta; clicar direito numa mensagem já selecionada dentro de um grupo de 3 (menu deve continuar valendo pras 3); clicar direito numa mensagem fora da seleção de 3 (deve continuar valendo pras 3, não pela clicada); com 2+ selecionadas, confirmar que o painel de leitura fica vazio; selecionar 1 mensagem, confirmar que o painel volta a mostrar conteúdo normalmente.

## 7. Fora de escopo / decisões adiadas

- Seleção múltipla de pastas — só mensagens, conforme pedido explícito do usuário.
- "Selecionar tudo" (Ctrl+A) — não pedido; `SelectionMode="Extended"` já habilita isso de graça via o comportamento padrão do `ListView`, mas não foi um requisito explícito nem precisa de código adicional.
- Barra de progresso durante um lote grande — a operação roda do início ao fim sem feedback incremental, mesmo nível de fricção já aceito em "mover pasta entre perfis".
- Desfazer um lote parcialmente aplicado — mesma decisão de sempre (sem lixeira/undo).
