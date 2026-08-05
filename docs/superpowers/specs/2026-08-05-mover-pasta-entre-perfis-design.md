# Mover pasta entre perfis — Design

**Status:** Aprovado para virar plano de implementação.
**Depende de:** `DbxStore.CreateFolderAsync`/`ProfileViewModel.CreateFolderAsync` (fatia "criar pastas"), `IMailStore.AddMessageAsync`/`OpenMessageAsync`/`EnumerateHeadersAsync` (fatia "mover mensagem — motor"), `DbxStore.DeleteFolderAsync`/`ProfileViewModel.DeleteFolderAsync` e `MoveFolderDialog`/`MoveFolderDestinationRules`/`MoveFolderDestinationValidityConverter` (fatia "apagar/mover pastas", já mesclada) — todos reaproveitados sem nenhuma mudança de contrato.
**Referência:** `docs/superpowers/specs/2026-08-05-apagar-mover-pastas-motor-design.md` (`DeleteFolderAsync`, reaproveitado tal como está pra esconder a origem no fim); `docs/superpowers/specs/2026-08-04-mover-mensagem-motor-design.md` (`MoveMessageAsync`, o padrão "copia pro destino, só esconde na origem depois" que esta fatia estende de uma mensagem pra uma subárvore inteira).
**Contexto:** extensão pontual da fatia "apagar/mover pastas" (mesclada em 2026-08-05), motivada por um relato real de uso: mover pasta hoje só funciona dentro do mesmo perfil — decisão de escopo confirmada explicitamente na época — mas o usuário quer mover uma pasta entre dois perfis abertos ao mesmo tempo (ex.: "BCP" → "BCP - cópia"), do mesmo jeito que mover mensagem já permite.

## 1. Objetivo

Deixar o usuário mover uma pasta (com toda a subárvore de subpastas e mensagens) de um perfil aberto pra outro, sem nunca escrever ou apagar nada nos arquivos `.dbx` de origem — mesma filosofia "esconde, nunca toca no original" já usada em toda a Fase 4. Mover dentro do mesmo perfil continua exatamente como está hoje (retarget de `ParentId`, sem cópia); mover entre perfis passa a existir como um caminho novo, só quando o destino escolhido é um perfil diferente do de origem.

## 2. Decisões de escopo (confirmadas com o usuário)

- **Mesma filosofia de mover mensagem: copia pro destino, só esconde na origem depois que a cópia inteira terminar.** Nunca apaga nem reescreve nenhum `.dbx` de origem.
- **Tudo ou nada.** Se qualquer parte da cópia falhar (uma subpasta de destino bate no limite de 255 mensagens, colisão de nome, `Folders.dbx`/`.dbx` de destino danificado, etc.), a operação inteira para ali: nada é escondido na origem (ela fica intacta), e o que já tiver sido criado no perfil de destino até aquele ponto **fica lá** (possível duplicata parcial) — sem tentativa de desfazer. O usuário vê o erro e decide o que fazer.
- **Itens recuperáveis (achados por varredura) ficam de fora.** Só mensagens normais, indexadas, são copiadas. Dois motivos: `AddMessageAsync` não tem como marcar uma mensagem como "recuperada" no destino (viraria uma mensagem normal, silenciosamente perdendo a proveniência), e itens recuperados não têm remetente/data confiáveis pra começo de conversa. Continuam só no arquivo de origem, que nunca é tocado — se um dia existir uma tela de "restaurar apagadas/pastas ocultas", eles continuam lá pra recuperar.
- **Tela de destino mostra todos os perfis abertos**, mesmo padrão já usado em "Mover mensagem..." — uma árvore por perfil, cada uma com sua própria opção "Pastas locais (raiz)".
- **Pasta de origem especial continua bloqueada**, igual já vale pra mover dentro do mesmo perfil — validado antes de copiar qualquer coisa (não depois, quando já teria copiado tudo em vão).
- **Nenhum método novo em `DbxStore`/`IMailStore`.** A orquestração inteira usa primitivas que já existem e já estão testadas (`CreateFolderAsync`, `EnumerateHeadersAsync`, `OpenMessageAsync`, `AddMessageAsync`, `DeleteFolderAsync`) — mesmo espírito de `MainWindowViewModel.MoveMessageAsync`, que já orquestra duas instâncias de `IMailStore` sem precisar de nenhum método "ciente" de que é uma mensagem movida.

## 3. `MainWindowViewModel.MoveFolderAsync` (novo)

Ponto de entrada único, decide internamente qual dos dois caminhos usar:

```csharp
/// <summary>
/// Moves a folder — same-profile is the existing cheap ParentId retarget
/// (ProfileViewModel.MoveFolderAsync); cross-profile copies the whole subtree (folders +
/// non-recovered messages) into destinationProfile, then hides it in sourceProfile via
/// the already-shipped DeleteFolderAsync — never touches sourceProfile's Folders.dbx or
/// any message .dbx. Nothing is hidden in the source unless the ENTIRE subtree copied
/// successfully first.
/// </summary>
/// <exception cref="InvalidOperationException">
/// Thrown when sourceNode's folder, or any folder in its subtree, is special
/// (SpecialFolderKind != None) — checked before any copying starts. For a cross-profile
/// move, also propagates whatever CreateFolderAsync/AddMessageAsync throws on the
/// destination profile (name collision, 255-item caps, damaged files) — the source is
/// left completely untouched in every one of those cases.
/// </exception>
public async Task MoveFolderAsync(ProfileViewModel sourceProfile, FolderNodeViewModel sourceNode,
    ProfileViewModel destinationProfile, int? destinationParentId, CancellationToken ct = default)
{
    if (ReferenceEquals(sourceProfile, destinationProfile))
    {
        await sourceProfile.MoveFolderAsync(sourceNode.Folder.Id, destinationParentId);
        return;
    }

    if (sourceNode.Folder.SpecialFolderKind != SpecialFolderKind.None || ContainsSpecial(sourceNode))
        throw new InvalidOperationException("Pastas especiais não podem ser movidas.");

    await CopySubtreeAsync(sourceProfile, sourceNode, destinationProfile, destinationParentId);

    await sourceProfile.DeleteFolderAsync(sourceNode.Folder.Id);
}

private static bool ContainsSpecial(FolderNodeViewModel node) =>
    node.Children.Any(c => c.Folder.SpecialFolderKind != SpecialFolderKind.None || ContainsSpecial(c));

private async Task CopySubtreeAsync(ProfileViewModel sourceProfile, FolderNodeViewModel sourceNode,
    ProfileViewModel destinationProfile, int? destinationParentId)
{
    var created = await destinationProfile.CreateFolderAsync(sourceNode.DisplayName, destinationParentId);

    if (sourceNode.Folder.HasBackingFile)
    {
        await foreach (var header in sourceProfile.Store.EnumerateHeadersAsync(sourceNode.Folder))
        {
            if (header.RecoveredKind is not null)
                continue; // recovered items excluded — see §2

            using var stream = await sourceProfile.Store.OpenMessageAsync(header.Id);
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms);

            await destinationProfile.Store.AddMessageAsync(created.Id, ms.ToArray(),
                header.Subject, header.SenderName, header.SenderAddress, header.MessageId, header.Date);
        }
    }

    foreach (var child in sourceNode.Children)
        await CopySubtreeAsync(sourceProfile, child, destinationProfile, created.Id);
}
```

Pontos de design:

- **Validação de pasta especial acontece antes de qualquer cópia.** `DeleteFolderAsync` (chamado só no final) já tem essa mesma checagem, mas por padrão só dispararia depois de copiar a árvore inteira — repetir a checagem aqui, cedo, evita criar uma cópia inteira no destino só pra falhar no fim. `ContainsSpecial` percorre `sourceNode.Children` (a árvore de UI já montada, sem ciclo possível — ver nota abaixo), não a lista achatada de `ParentId` do motor.
- **Sem tabela de mapeamento explícita de ids.** Como cada `CopySubtreeAsync` recebe o `destinationParentId` já resolvido da chamada de cima (o `created.Id` que acabou de sair do `CreateFolderAsync`), não precisa guardar `sourceId → destId` em lugar nenhum — a árvore é percorrida de cima pra baixo e o id novo já está disponível na hora de criar cada filho.
- **`sourceNode.Children` nunca tem ciclo.** Diferente da lista achatada `ParentId` que o motor percorre (que tolera pai pendente/cíclico porque vem direto do arquivo), a árvore `FolderNodeViewModel` já foi montada por `FolderTreeViewModel.Build`, que resolve qualquer ciclo promovendo o nó a raiz — uma pasta só pode aparecer uma vez, como filha de um único nó, em toda a árvore. A recursão em `CopySubtreeAsync`/`ContainsSpecial` não precisa de conjunto de visitados.
- **Pasta sem arquivo de apoio (`HasBackingFile == false`, ex. "Pastas locais") não tenta enumerar mensagens** — mas ainda é criada no destino (com um arquivo de apoio de verdade, já que `CreateFolderAsync` sempre cria um; pequena divergência aceita, sem consequência prática já que essa pasta nunca tinha mensagens mesmo).
- **`ProfileViewModel.CreateFolderAsync`/`DeleteFolderAsync` já reconstroem a árvore a cada chamada.** Pra uma subárvore com N pastas, a árvore de destino é reconstruída N vezes (uma por `CreateFolderAsync`) — ineficiente pra árvores muito grandes, mas simples e correto; aceito pelo mesmo raciocínio de YAGNI já usado em outras fatias (nenhuma pasta real do usuário chega perto da escala em que isso importaria).

## 4. `MoveFolderDialog`/`MoveFolderDestinationRules`/`MoveFolderDestinationValidityConverter` (reescritos pra multi-perfil)

Mesma estrutura de `MoveMessageDialog` (uma `TreeView` por perfil aberto, via `ItemsControl`), com a opção "Pastas locais (raiz)" repetida por perfil (uma por seção, não uma global).

```csharp
public static class MoveFolderDestinationRules
{
    public static bool IsValidDestination(FolderNodeViewModel candidate, ProfileViewModel candidateProfile,
        FolderNodeViewModel sourceNode, ProfileViewModel sourceProfile)
    {
        if (!ReferenceEquals(candidateProfile, sourceProfile))
            return true; // pastas de outro perfil nunca pertencem à subárvore de origem

        if (ReferenceEquals(candidate, sourceNode))
            return false;

        return !ContainsDescendant(sourceNode, candidate);
    }

    // ContainsDescendant sem mudança nenhuma.
}
```

`MoveFolderDestinationValidityConverter` vira `IMultiValueConverter` (era `IValueConverter`), recebendo `(FolderNodeViewModel candidate, ProfileViewModel candidateProfile)` via `MultiBinding` — mesmo padrão exato de `MoveDestinationValidityConverter` (mensagens): `Binding Path="."` pro nó, `Binding Path="DataContext" RelativeSource={RelativeSource AncestorType=TreeView}` pro perfil dono daquela árvore. `SourceNode`/`SourceProfile` setados uma vez pelo construtor do diálogo, antes de qualquer árvore renderizar.

Cada "Pastas locais (raiz)" (uma por perfil, agora dentro do `DataTemplate` de cada seção, com o `ProfileViewModel` daquela seção disponível via `DataContext`) sempre válida — raiz nunca é descendente de nada. Clicar arma `(SelectedProfile, SelectedFolderId = null)`.

`MoveFolderDialog` ganha `SelectedProfile` (novo, ao lado do já existente `SelectedFolderId`) e passa a receber `IEnumerable<ProfileViewModel> openProfiles` no construtor, no lugar de um único `ProfileViewModel profile` — mesmo formato de `MoveMessageDialog(openProfiles, sourceProfile, sourceFolderId)`.

## 5. `MoveFolder_Click` (`MainWindow.xaml.cs`)

Chamada única, sem branch nenhum no code-behind (a decisão "mesmo perfil ou não" já é interna ao `MainWindowViewModel.MoveFolderAsync`):

```csharp
var dialog = new MoveFolderDialog(_viewModel.OpenProfiles, profile, node) { Owner = this };
if (dialog.ShowDialog() != true || dialog.SelectedProfile is not { } destinationProfile)
    return;

await _viewModel.MoveFolderAsync(profile, node, destinationProfile, dialog.SelectedFolderId);
```

**Reset de seleção passa a considerar os dois perfis, não só a origem.** Hoje o bloco de reset (`_selectedNode = null`, `ClearCurrentFolder()`, etc.) só dispara `if (ReferenceEquals(_selectedProfile, profile))` (perfil de origem). Numa mudança entre perfis, o perfil de **destino** também teve sua árvore reconstruída (pelo menos uma vez, via `CreateFolderAsync`) — se `_selectedProfile` for o perfil de destino, a seleção lá também ficou órfã. Passa a disparar se `_selectedProfile` for a origem **ou** o destino:

```csharp
if (ReferenceEquals(_selectedProfile, profile) || ReferenceEquals(_selectedProfile, destinationProfile))
{
    _selectedNode = null;
    _selectedTreeView = null;
    ScavengeButton.Visibility = Visibility.Collapsed;
    RecoverableBar.Visibility = Visibility.Collapsed;
    NavigateToRenderedMessage(string.Empty);
    ImagesBlockedBar.Visibility = Visibility.Collapsed;
    _viewModel.ClearCurrentFolder();
}
```

(`ClearCurrentFolder()` — método já existente em `MainWindowViewModel`, adicionado na revisão final da fatia anterior — limpa `MessageList`/`MessageReader`/`CurrentFolderScavengeStatus`; sem ele, o bug que a revisão final da fatia anterior já corrigiu pra apagar/mover-mesmo-perfil voltaria a existir aqui, agora pro caminho cross-perfil.)

## 6. Erros e casos de borda

- Mover uma pasta especial (mesmo perfil ou entre perfis) → `InvalidOperationException`, nada copiado/escondido — menu de contexto já nem oferece a opção (`SpecialToVisibilityConverter`, existente).
- Falha em qualquer `CreateFolderAsync`/`AddMessageAsync` durante a cópia (colisão de nome, 255 mensagens/pastas, arquivo danificado) → propaga pro chamador; nada é escondido na origem; o que já foi criado no destino até ali permanece lá.
- Perfil de origem = perfil de destino, mas pasta de destino é a própria pasta de origem ou uma descendente → mesmo `InvalidOperationException` que `DbxStore.MoveFolderAsync` já lança hoje (caminho não muda).
- Pasta com mensagens recuperáveis: elas não são copiadas; se a subárvore inteira copiar com sucesso, a pasta de origem (com os itens recuperáveis nela) é escondida via `DeleteFolderAsync` de qualquer jeito — os itens recuperáveis ficam inacessíveis dali em diante (mesmo destino que teriam se a pasta fosse só apagada, não movida), mas o byte original nunca é tocado.
- Cancelar o diálogo → nenhuma chamada ao motor acontece, nenhum estado muda (mesmo padrão já usado em toda fatia anterior).
- **Assimetria conhecida e aceita:** o caminho mesmo-perfil (`DbxStore.MoveFolderAsync`, já existente) só rejeita se a própria pasta sendo movida for especial — não desce a subárvore procurando uma especial aninhada mais embaixo, ao contrário de `DeleteFolderAsync`. O caminho cross-perfil desta fatia, por copiar a árvore inteira antes de decidir qualquer coisa, precisa checar a subárvore inteira de qualquer jeito (pra não copiar tudo só pra falhar no `DeleteFolderAsync` final) — então tecnicamente é mais rigoroso que o caminho mesmo-perfil pro caso (extremamente incomum na prática, já que pasta especial normalmente só existe na raiz) de uma pasta especial aparecer aninhada dentro de uma subárvore não-especial. Não é uma inconsistência introduzida por esta fatia corrigir o caminho mesmo-perfil já mesclado — fora de escopo aqui.

## 7. Estratégia de testes

- `MainWindowViewModelTests` (extensão, com `FakeMailStore`/dois `ProfileViewModel` fakes): `MoveFolderAsync` com `sourceProfile == destinationProfile` chama `ProfileViewModel.MoveFolderAsync` e não `CreateFolderAsync`/`AddMessageAsync`; com perfis diferentes, cria a pasta e cada mensagem no destino, depois esconde na origem; subárvore de duas ou mais pastas é toda recriada com a hierarquia certa (pasta-filha aparece com o `ParentId` novo apontando pra pasta-mãe recém-criada no destino); item com `RecoveredKind != null` não gera chamada de `AddMessageAsync`; pasta especial (raiz ou dentro da subárvore) lança e não chama `CreateFolderAsync` nenhuma vez; falha simulada de `AddMessageAsync`/`CreateFolderAsync` no meio da subárvore propaga a exceção e não chama `DeleteFolderAsync` na origem.
- `MoveFolderDestinationRulesTests` (extensão): candidato em perfil diferente do de origem é sempre válido, mesmo sendo "igual" (mesma referência de nó) por coincidência de teste; candidato na própria árvore de origem continua seguindo a regra antiga (self/descendente inválido, resto válido).
- Sem teste automatizado pra `MoveFolderDialog`/`MoveFolder_Click` (mesmo precedente já estabelecido) — verificação manual: mover pasta com subpastas e mensagens de um perfil pra outro; mover pasta com item recuperável (confirma que o item não aparece na pasta de destino, mas a pasta de origem some da árvore do jeito que apagar já faz); cancelar; pasta de destino com nome colidente (confirma erro e que nada foi escondido na origem).

## 8. Fora de escopo / decisões adiadas

- Desfazer/restaurar uma pasta movida entre perfis — mesma decisão já tomada pra mover dentro do mesmo perfil e pra mover mensagem.
- Copiar itens recuperáveis — decisão explícita de deixar de fora (§2); pode ser revisitado se uma tela de "pastas ocultas"/varredura ganhar suporte a exportar itens recuperados de forma independente.
- Otimizar o número de vezes que a árvore do perfil de destino é reconstruída durante a cópia de uma subárvore grande.
- Progresso/cancelamento durante a cópia de uma subárvore grande (a operação roda do início ao fim sem barra de progresso nem `CancellationToken` responsivo no meio) — mesmo nível de fricção aceito por "mover mensagem" hoje.
