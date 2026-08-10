# Janela de progresso ao mover mensagens em lote — Design

**Status:** Aprovado para virar plano de implementação.
**Depende de:** `MainWindowViewModel.MoveMessagesAsync`/`MoveMessageAsync` (fatia "apagar/mover mensagens em lote", já mesclada — assinatura estendida, não substituída), `MoveMessageDialog` (já mesclado, reaproveitado sem mudança).
**Referência:** `docs/superpowers/specs/2026-08-05-apagar-mover-mensagens-em-lote-design.md` (`MoveMessagesAsync`, o laço que este design estende); `docs/superpowers/specs/2026-07-27-fase3-varredura-design.md` (padrão `IProgress<double>`/`CancellationToken` já usado em `RunScavengeAsync`/`ScavengeButton_Click`, reaproveitado aqui).
**Contexto:** bug real encontrado em uso: mover 340 mensagens de uma subpasta A para uma subpasta B e, durante a transferência, navegar até a pasta B na árvore causou `UnauthorizedAccessException: Access to the path is denied` na 16ª mensagem (`"15 de 340 mensagens movidas antes de falhar"`).

## 1. Causa raiz (contexto do bug)

`DbxEmailFileWriter.Rebuild` escreve num arquivo temporário e faz `File.Move(tempPath, path, overwrite: true)` sobre o `.dbx` de destino. `MemoryMappedDbxSource` abre esse mesmo arquivo com `FileShare.ReadWrite` (sem `FileShare.Delete`) para leitura. `DbxStore.AddMessageAsync` já despeja seu próprio handle em cache antes de reescrever (`_openFolders.Remove`), mas isso só cobre o caso "pasta B já estava aberta antes do lote começar". Como `MoveMessage_Click` faz `await MoveMessagesAsync(...)` sem travar a `FolderTreeView`, o dispatcher do WPF continua processando cliques entre uma mensagem e outra do lote — navegar até a pasta B *durante* o lote abre um novo handle (`EnumerateHeadersAsync` → `DbxEmailFile.Open`) que corre com o próximo `Rebuild`, violando o contrato já documentado em `DbxStore.cs`: *"Concurrent calls against the same DbxStore instance ... must be serialized by the caller."*

Este design corrige isso trocando a espera silenciosa por uma janela modal: `Window.ShowDialog()` desabilita a janela dona (`MainWindow`) enquanto estiver aberta, então nenhuma navegação — e portanto nenhuma chamada concorrente ao `DbxStore` — é possível durante o lote.

## 2. Decisões de escopo (confirmadas com o usuário)

- **Só para lotes (2+ mensagens).** Mover 1 mensagem continua exatamente como hoje — instantâneo, sem janela.
- **Progresso via `IProgress<double>` + `CancellationToken`**, mesmo padrão já usado por `RunScavengeAsync`/`ScavengeButton_Click`. Cancelamento é checado só entre mensagens, nunca no meio de um `Rebuild` — o `.dbx` de destino nunca fica pela metade.
- **Cancelamento é cooperativo e parcial**: as `succeeded` mensagens já movidas antes do cancelamento continuam movidas (mesma filosofia "nada a desfazer" que `MoveMessagesAsync` já documenta). Fechar a janela (X) durante a transferência tem o mesmo efeito que clicar "Cancelar".
- **Sucesso total → janela fecha sozinha.** Cancelamento ou falha parcial → janela permanece aberta mostrando o resultado final ("15 de 340 mensagens movidas antes de falhar: ..." ou "Cancelado: 15 de 340 mensagens movidas."), com um botão "Fechar".
- **Escopo limitado a mover mensagens.** Apagar mensagens em lote não ganha essa janela (não reescreve `.dbx`, não sofre a mesma condição de corrida) nem mover pastas entre perfis (fora do pedido original) — ver seção 7.

## 3. `MainWindowViewModel.MoveMessagesAsync` (assinatura estendida)

```csharp
public async Task MoveMessagesAsync(ProfileViewModel sourceProfile, IReadOnlyList<MessageHeader> messages,
    ProfileViewModel destinationProfile, int destinationFolderId,
    IProgress<double>? progress = null, CancellationToken ct = default)
{
    int succeeded = 0;
    foreach (var message in messages)
    {
        ct.ThrowIfCancellationRequested();

        try
        {
            await MoveMessageAsync(sourceProfile, message, destinationProfile, destinationFolderId, ct);
            succeeded++;
            progress?.Report((double)succeeded / messages.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"{succeeded} de {messages.Count} mensagens movidas antes de falhar: {ex.Message}", ex);
        }
    }
}
```

- `progress`/`ct` são opcionais com valor padrão — a chamada existente de 1 mensagem (sem janela) não muda.
- `ct.ThrowIfCancellationRequested()` fica no topo do laço, antes de `MoveMessageAsync`: garante que o cancelamento só interrompe *entre* mensagens.
- `OperationCanceledException` continua propagando sem o `catch`/rewrap (já excluído pelo `when`), então a janela recebe a exceção "crua" e monta seu próprio texto de "cancelado" usando o `succeeded` que ela mesma já rastreou via `progress`.

## 4. `MoveMessagesProgressDialog` (novo)

Novo arquivo `src/LEC.App/MoveMessagesProgressDialog.xaml`(`.cs`), ao lado de `MoveMessageDialog`/`MoveFolderDialog`.

**Construtor:** recebe `MainWindowViewModel`, `sourceProfile`, `messages`, `destinationProfile`, `destinationFolderId` — tudo que precisa para chamar `MoveMessagesAsync` sozinho.

**`Loaded`:** dispara a chamada:
```csharp
_cts = new CancellationTokenSource();
var progress = new Progress<double>(p =>
{
    ProgressBar.Value = p;
    StatusText.Text = $"Movendo mensagens... {_succeeded} de {_total} ({p:P0})";
});
try
{
    await _viewModel.MoveMessagesAsync(sourceProfile, messages, destinationProfile, destinationFolderId, progress, _cts.Token);
    DialogResult = true;
    Close(); // sucesso total: fecha sozinha
}
catch (OperationCanceledException)
{
    ShowFinalState($"Cancelado: {_succeeded} de {_total} mensagens movidas.");
}
catch (Exception ex)
{
    Failure = ex;
    ShowFinalState(ex.Message); // já vem formatada como "N de M mensagens movidas antes de falhar: ..."
}
```
(`_succeeded` é incrementado dentro do próprio callback de `progress`, calculado a partir de `p * _total` arredondado — ou, mais simples, um segundo campo capturado por referência atualizado junto com `progress.Report`. Detalhe de implementação, não afeta o contrato.)

**`ShowFinalState(string message)`:** troca `StatusText.Text`, esconde o botão "Cancelar", mostra "Fechar" habilitado. `DialogResult` fica `false` (cancelado) ou permanece `null` até o usuário fechar manualmente (falha).

**Botão "Cancelar":** `_cts.Cancel()`, desabilita o próprio botão (evita cliques repetidos) até o `catch (OperationCanceledException)` acima assumir a UI.

**`Closing` event:** se a operação ainda está rodando (`_cts` não cancelado e diálogo ainda sem `DialogResult` definido), chama `_cts.Cancel()` e faz `e.Cancel = true` — a janela só fecha de verdade quando o `catch` correspondente rodar e chamar `Close()` por conta própria (evita fechar com o `Rebuild` ainda em andamento em outra thread).

**`Failure` (propriedade pública):** exceção real, se houve uma (não populada em caso de cancelamento puro) — usada pelo chamador pra decidir se mostra o `MessageBox` de erro.

## 5. Wiring em `MoveMessage_Click` (`MainWindow.xaml.cs:686`)

```csharp
Exception? failure = null;
try
{
    if (messages.Count == 1)
    {
        await _viewModel.MoveMessagesAsync(sourceProfile, messages, destinationProfile, destinationFolderId);
    }
    else
    {
        var progressDialog = new MoveMessagesProgressDialog(_viewModel, sourceProfile, messages, destinationProfile, destinationFolderId) { Owner = this };
        progressDialog.ShowDialog();
        failure = progressDialog.Failure;
    }
}
catch (Exception ex)
{
    failure = ex; // caminho de 1 mensagem continua lançando direto, como hoje
}
```
O restante do método (reload da pasta atual via `SelectFolderAsync`, limpar reading pane, `MessageBox.Show` se `failure is not null`) não muda — já é executado incondicionalmente após o `try`, cobrindo tanto sucesso quanto falha/cancelamento parcial.

## 6. Erros e casos de borda

- Lote de 1 mensagem → nenhuma janela, comportamento idêntico ao atual.
- Cancelar logo na 1ª mensagem → "Cancelado: 0 de N mensagens movidas.", nenhuma mudança de estado (mas o reload da pasta atual ainda roda, por segurança — mesmo padrão já usado quando `succeeded == 0`).
- Falhar na 1ª mensagem (não relacionado a cancelamento, ex.: pasta de destino danificada) → janela mostra "0 de N mensagens movidas antes de falhar: ..." e fica aberta até o usuário clicar "Fechar"; o `MessageBox` de erro aparece depois, igual hoje.
- Fechar a janela pelo X enquanto a transferência ainda roda → equivalente a clicar "Cancelar" (via `Closing` + `e.Cancel = true` até a operação parar de verdade).
- Fechar a janela pelo X depois que o resultado final já está mostrado (falha/cancelamento) → fecha normalmente, sem lançar cancelamento de novo.
- Perfil de destino fechado pelo usuário enquanto a janela de progresso está aberta → impossível: a `MainWindow` está desabilitada (modal), então não há como clicar em "Fechar perfil" durante o lote.

## 7. Fora de escopo / decisões adiadas

- **Apagar mensagens em lote** não ganha janela de progresso nesta fatia — `DeleteMessageAsync` só marca um cache como apagado, não reescreve o `.dbx`, então não sofre a mesma condição de corrida com `File.Move`. Pode ganhar uma janela por consistência visual depois, mas não é uma correção de bug.
- **Mover pasta entre perfis** (`MoveFolder_Click`, que também copia mensagens uma a uma via o mesmo `AddMessageAsync`/`Rebuild`) tem uma exposição parecida, mas está fora do pedido original — revisitar depois se surgir o mesmo tipo de bug lá.
- **Barra de progresso indeterminada para lotes muito pequenos** (2-3 mensagens, onde a janela pisca e fecha quase instantaneamente) — aceito como está; não foi pedido nenhum atraso mínimo artificial pra manter a janela visível.
- **Pausar/retomar** — só cancelar (que já é uma parada permanente e cooperativa), sem retomar de onde parou.

## 8. Estratégia de testes

- `MainWindowViewModelTests` (extensão, `FakeMailStore`): `MoveMessagesAsync` com `IProgress<double>` capturando os valores reportados (deve ser `1/N, 2/N, ..., N/N` na ordem certa); cancelamento via `CancellationTokenSource` cancelado após a 1ª mensagem — confirma que a 2ª nunca é chamada, que a exceção é `OperationCanceledException` (não `InvalidOperationException`), e que exatamente 1 mensagem foi de fato movida no fake store. Caso de falha real no meio do lote continua coberto pelos testes já existentes (mensagem de erro com a contagem).
- Sem teste automatizado para `MoveMessagesProgressDialog` (WPF, mesmo precedente já estabelecido). Checklist manual: mover lote grande (50+) e observar a barra progredir; clicar "Cancelar" no meio e confirmar que a janela mostra a contagem parcial e a pasta de origem recarrega sem as mensagens já movidas; fechar pelo X no meio da transferência (mesmo efeito de "Cancelar"); mover lote pequeno com sucesso total e confirmar que a janela fecha sozinha; **reproduzir o bug original**: iniciar um lote grande e tentar navegar pra outra pasta na árvore — confirmar que a `FolderTreeView` está de fato bloqueada até a janela fechar.
