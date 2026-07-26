# Fase 2 — Visualizador — Design

**Status:** Aprovado para virar plano de implementação.
**Depende de:** Fase 1 (`LEC.DbxFormat`), já concluída e mesclada.
**Referência:** `ARQUITETURA.md`, especialmente §5 (arquitetura em camadas), §7.1 (contrato `IMailStore`), §9 (interface), §12 (Fase 2).

## 1. Objetivo

Um shell WPF de três painéis (árvore de pastas / lista de mensagens / painel de leitura) que abre um **perfil** do Outlook Express — uma pasta contendo `Folders.dbx` e os `.dbx` de cada pasta — e permite navegar e ler as mensagens reais. Só "espiar" (§5): sem biblioteca própria, sem importação, sem edição. É a primeira interface gráfica do projeto.

## 2. Escopo

**Dentro:**
- Abrir um perfil (pasta com `Folders.dbx`) e montar a árvore de pastas real, incluindo pastas órfãs (ver §6).
- Lista de mensagens virtualizada por pasta, populada por leitura nível 1 (só cabeçalhos, via `DbxMessageInfo`/`DbxFolderInfo` da Fase 1).
- Painel de leitura: corpo renderizado em HTML via WebView2, script desligado, imagens remotas bloqueadas por padrão com barra "Exibir imagens".
- Ícones/rótulos de pastas especiais (Inbox, Rascunhos, etc. — de `DbxSpecialFolderType`, já existe).
- Estado de saúde (`StoreHealth.Ok/Partial/Corrupt`, já existe na Fase 1) visível na UI, não só no CLI.
- Degradação elegante: pasta cujo `.dbx` está ausente, travado ou ilegível vira estado "não carregável" visível, não uma exceção.
- Link no corpo do e-mail nunca abre direto — sempre confirma o destino antes.

**Fora (fases futuras, ARQUITETURA.md §12):**
- `PstStore` — adiado para um plano futuro, quando houver um `.pst` real para verificar contra. `IMailStore` é desenhado para acomodá-lo depois sem mudar a UI, mas ele não é implementado nesta fase.
- Varredura/recuperação (Fase 3), biblioteca própria/importação/edição (Fase 4+).
- Abrir um `.dbx` avulso sem perfil (decisão desta rodada: só perfil completo entra agora).
- Busca (Fase 4, `ISearchProvider`/FTS5) e exportação.

## 3. Estrutura de projetos

```
LEC.sln
src/
  LEC.DbxFormat/        Fase 1 — inalterado
  LEC.MailStore/         NOVO — IMailStore, StoreFolder, MessageHeader, MessageRef, DbxStore
  LEC.App/                NOVO — WPF: Views, ViewModels, MIME/render, App.xaml
tests/
  LEC.DbxFormat.Tests/   Fase 1 — inalterado
  LEC.MailStore.Tests/    NOVO — testes de DbxStore (xUnit), incluindo teste contra BCP/ real
```

Dependências: `LEC.MailStore` → `LEC.DbxFormat`. `LEC.App` → `LEC.MailStore` (nunca `LEC.DbxFormat` diretamente — a UI só conhece `IMailStore`). `LEC.App` também referencia `MimeKit` e `Microsoft.Web.WebView2` (ambos já decididos em ARQUITETURA.md §4).

`StoreHealth` é reaproveitado de `LEC.DbxFormat` (não duplicar o enum) — `IMailStore.Health` tem esse tipo.

## 4. Contrato `IMailStore` e tipos de dados

Contrato conforme ARQUITETURA.md §7.1, em `LEC.MailStore`:

```csharp
public interface IMailStore : IDisposable
{
    IReadOnlyList<StoreFolder> Folders { get; }
    IAsyncEnumerable<MessageHeader> EnumerateHeadersAsync(StoreFolder folder, CancellationToken ct);
    Task<Stream> OpenMessageAsync(MessageRef id, CancellationToken ct);
    StoreHealth Health { get; }
}
```

- `StoreFolder`: `Id`, `ParentId` (nullable — `null` significa raiz), `Name`, `SpecialFolderType`, `MessageCount` (cache do `Folders.dbx`, mostrado antes mesmo de carregar), `IsLoadable` (bool).
- `MessageHeader`: `Subject`, `SenderName`, `SenderAddress`, `Date` (send ou receive, o que existir), `Size`, `MessageId`, `Health` (por mensagem, refletindo se `DbxMessageInfo.HasOutOfRangeAttribute` foi sinalizado). **Não inclui** indicador de anexo nesta fase — exigiria decodificar MIME por mensagem só pra popular a lista, o que contraria a leitura nível 1 ser barata; fica pra quando houver motivo real de uso.
- `MessageRef`: opaco o bastante pra `DbxStore` re-localizar a mensagem (internamente carrega pasta + offset do nó de índice).

## 5. `DbxStore` — implementação

`DbxStore.Open(profileDirectoryPath)`:
1. Abre `Folders.dbx` via `DbxFolderFile` (Fase 1). Se a assinatura não bater ou o arquivo não existir, falha com uma exceção clara de "não é um perfil válido" — isso é erro de invocação, não corrupção de conteúdo.
2. Constrói a lista `Folders` a partir de `DbxFolderInfo` (id/parent/nome/tipo especial/contagem).
3. `Health` do store agregado começa como o `Health` do `DbxFolderFile` (se `Folders.dbx` já veio `Partial`, o store inteiro reflete isso).

`EnumerateHeadersAsync(folder, ct)`: na primeira chamada pra uma pasta, tenta abrir o `.dbx` associado (`folder.FileName`, do `DbxFolderInfo`) via `DbxEmailFile`. Se abrir falhar (arquivo ausente/travado/sem permissão) ou lançar `DbxFormatException` (assinatura errada), a pasta fica `IsLoadable = false` e o método retorna vazio — nunca propaga a exceção pra UI. Se abrir com sucesso, guarda a instância aberta (dispose gerenciado pelo `DbxStore`) e itera `DbxEmailFile.Messages`, mapeando `DbxMessageInfo` → `MessageHeader`.

`OpenMessageAsync(id, ct)`: localiza a pasta/instância já aberta (ou abre sob demanda, mesma lógica de degradação acima) e chama `ReadMessageRaw`, devolvendo os bytes crus como `Stream`. Não faz parsing MIME — isso é responsabilidade de quem chamou (`LEC.App`).

## 6. Árvore de pastas: montagem e órfãs

O `Folders.dbx` real do usuário tem uma pasta órfã confirmada por evidência direta: id 73 ("seg" → `seg.dbx`), `ParentId = 53`, e a pasta 53 não existe no arquivo. A montagem da árvore (no ViewModel, não no `DbxStore` — isso é apresentação, não dado) precisa tolerar isso: qualquer `StoreFolder` cujo `ParentId` não corresponda a nenhum `Id` conhecido (e não seja `null`/raiz) é tratado como filho direto da raiz, não descartado nem motivo de falha.

## 7. Fluxo da UI (`LEC.App`)

MVVM com `CommunityToolkit.Mvvm`. `FolderTreeViewModel` consome `IMailStore.Folders` (lista plana) e monta a hierarquia observável, aplicando a tolerância a órfãs do §6. Selecionar uma pasta popula `MessageListViewModel` via `EnumerateHeadersAsync` numa coleção virtualizada (`VirtualizingStackPanel`, `VirtualizationMode=Recycling` — padrão WPF, sem componente novo). Selecionar uma mensagem chama `OpenMessageAsync`; o `Stream` bruto vira um `MimeMessage` (MimeKit); o corpo HTML (ou texto puro convertido, se não houver HTML) vai pro `WebView2` via `NavigateToString`, com as tags/atributos de imagem remota interceptados antes de renderizar (bloqueadas até o usuário clicar "Exibir imagens" — mecanismo exato de bloqueio é decisão de implementação, não de design). Cliques em link dentro do WebView2 são interceptados (`CoreWebView2.NavigationStarting`, cancelado) e mostram o destino real antes de decidir abrir externamente.

## 8. Erros e saúde visível

- Perfil inválido (sem `Folders.dbx`/assinatura errada): mensagem clara na tela de abertura, sem exceção não tratada.
- `Health.Partial` em qualquer nível (arquivo, pasta, mensagem) aparece como indicador discreto, não modal — consistente com a filosofia "nunca trava, sempre mostra o que conseguiu" da Fase 1.
- Pasta não carregável (`IsLoadable = false`): aparece acinzentada na árvore, com tooltip explicando por quê; clicável, mostra um estado explicativo em vez de lista vazia enganosa.

## 9. Estratégia de testes

- `LEC.MailStore.Tests` (xUnit): testes de `DbxStore` com arquivos sintéticos (padrão `TestDbxBuilder`-like, adaptado) cobrindo abertura de perfil, pasta não carregável, e — crucialmente — um teste contra o perfil real em `BCP/` confirmando que a árvore final inclui a pasta órfã (id 73) sob a raiz.
- ViewModels de `LEC.App` (não dependem de WPF diretamente, só `INotifyPropertyChanged`/`ObservableObject`): testáveis com xUnit puro.
- Views (XAML) e renderização WebView2: verificação manual — compilar, rodar o app de verdade contra `BCP/`, observar. Sem tentativa de automação de UI nesta fase.

## 10. Fora de escopo / decisões adiadas

- `PstStore`: adiado (decisão desta rodada).
- Indicador de anexo na lista de mensagens: adiado (custo de decodificar MIME por item não se paga ainda).
- Mecanismo exato de bloqueio/liberação de imagens remotas no corpo HTML: decisão de implementação, a plano detalha.
- Abrir `.dbx` avulso sem perfil: adiado (decisão desta rodada).
