# Compactar pastas `.dbx` (mensagens) — Design

**Status:** Aprovado para virar plano de implementação.
**Depende de:** `DbxEmailFileWriter.Rebuild` / `DbxStore.AddMessageAsync` (purga de caches pós-rebuild — já mesclado), `MovedMessageCache` / `DeletedMessageCache` (já mesclados — definem o “lixo” que a compactação remove), `MoveMessagesProgressDialog` (já mesclado — padrão de modal de progresso a reutilizar).
**Referência:** `docs/superpowers/specs/2026-08-04-apagar-mensagem-design.md` (apagar = esconder; byte permanece no `.dbx`); `docs/superpowers/specs/2026-08-04-mover-mensagem-motor-design.md` (mover = esconder na origem); `docs/superpowers/specs/2026-08-10-progresso-mover-mensagens-lote-design.md` (modal de progresso).
**Contexto:** fatia da versão 1.2. Apagar/mover mensagens hoje só escondem via cache local; o `.dbx` de origem não encolhe. O usuário pediu compactação explícita estilo Outlook Express: botão sob demanda, pendências entre sessões, e alerta ao fechar se ainda houver espaço a recuperar. **Renomear pastas/arquivos** também foi pedido para 1.2, mas fica **fora desta fatia** — só depois que compactar estiver concluído.

## 1. Objetivo

Permitir que o usuário recupere espaço em disco reescrevendo o `.dbx` de mensagens de uma pasta (ou de todas as pastas pendentes) sem o lixo deixado por mensagens apagadas/movidas. A compactação é manual, pode ficar pendente entre sessões, e o app avisa ao fechar um perfil ou o aplicativo se ainda houver pendências.

## 2. Decisões de escopo (confirmadas com o usuário)

- **Abordagem A — Manual + pendente + alerta no fechar.** Não compacta a cada apagar/mover; não há auto-compactação em background.
- **Só mensagens.** Compactar reescreve `.dbx` de pastas de email com lixo interno. **Não** remove do disco `.dbx` de pastas já escondidas no `HiddenFolderCache`.
- **Pasta atual + “tudo pendente”.** Contexto da pasta compacta uma; menu Arquivo/Ferramentas e o alerta de fechar oferecem compactar todas as pendentes do escopo relevante.
- **Sem backup nesta fatia.** O rewrite grava direto (mesmo risco consciente de outros writes já existentes). Backup automático fica para depois.
- **Modal com progresso, sem cancelar.** Bloqueia a UI enquanto compacta (`Compactando “Inbox”… (3/10)`), no espírito de `MoveMessagesProgressDialog`, mas sem botão Cancelar.
- **Gatilhos:** menu de contexto da pasta (“Compactar esta pasta…”) + item de menu (“Compactar tudo pendente…”). Sem indicador visual na árvore nesta fatia.
- **Alerta ao fechar sempre que houver qualquer pendência** (sem limiar de MB): “Há N pastas com espaço a recuperar (~X MB). Compactar agora?” → Sim (tudo do escopo) / Não (fechar mesmo assim).
- **Falha no meio de “tudo”:** para e avisa. Pastas já compactadas ficam; a que falhou e as seguintes intactas.
- **Fechar o app:** agrega pendências de **todos** os perfis abertos.
- **Fechar um perfil:** alerta **exclusivo** daquele perfil.
- **Fora de escopo:** renomear; backup; indicador na árvore; cancelar no meio; limpeza de pastas órfãs no disco; limiar de MB para o alerta.

## 3. O que é “pendente” e estimativa de espaço

Uma pasta tem compactação **pendente** se:

1. Tem arquivo de apoio `.dbx` de mensagens resolvido, e
2. Existe pelo menos um `IndexNodeOffset` dessa pasta em `MovedMessageCache` **ou** `DeletedMessageCache` (conjuntos em memória / SQLite já usados por `EnumerateHeadersAsync`).

**Espaço recuperável estimado** ≈ soma de `MessageSize` das entradas de índice correspondentes a esses offsets. Overhead de árvore de índice e blocos não entra. Se `MessageSize` estiver ausente (0) em mensagens antigas, a estimativa pode ser baixa ou zero; o diálogo ainda mostra N pastas. Preferir estimativa conservadora a inventar bytes.

Não há tabela SQLite nova de “pendências”: o estado pendente **é** a presença de entradas nos caches de movidas/apagadas. Compactar com sucesso purga esses caches da pasta (como `AddMessageAsync` já faz após `Rebuild`).

## 4. API do motor (`IMailStore` / `DbxStore`)

Tipos de retorno sugeridos (nomes ajustáveis no plano):

```csharp
public sealed record CompactFolderStatus(
    int FolderId,
    string FolderName,
    long EstimatedReclaimableBytes);

public sealed record CompactStatus(
    IReadOnlyList<CompactFolderStatus> PendingFolders)
{
    public int PendingFolderCount => PendingFolders.Count;
    public long TotalEstimatedReclaimableBytes =>
        PendingFolders.Sum(f => f.EstimatedReclaimableBytes);
}
```

```csharp
Task<CompactStatus> GetCompactStatusAsync(CancellationToken ct = default);

Task CompactFolderAsync(int folderId, CancellationToken ct = default);

/// <summary>
/// Compacts every pending folder in this store, in a stable order (e.g. folder tree
/// order or FolderId ascending). Stops at the first failure; folders already compacted
/// stay compacted. Optional progress callback reports (completedCount, totalCount, currentFolderName).
/// </summary>
Task CompactAllPendingAsync(
    IProgress<CompactProgress>? progress = null,
    CancellationToken ct = default);
```

### 4.1 `CompactFolderAsync`

Espelha a disciplina de `AddMessageAsync`:

1. Resolve o caminho do `.dbx` da pasta; se não houver arquivo ou não houver pendência → no-op (sucesso).
2. Fecha qualquer handle aberto dessa pasta (evitar lock no `File.Move` final do `Rebuild`).
3. Lê do arquivo as mensagens **vivas** (offsets não presentes em moved nem deleted), materializando `DbxMessageWriteEntry` como em `AddMessageAsync`.
4. `DbxEmailFileWriter.Rebuild(path, entries)`.
5. Só após Rebuild bem-sucedido: purge `MovedMessageCache` / `DeletedMessageCache` (e conjuntos em memória) **dessa pasta**; invalida `_messagesByFolder` / handles em cache.
6. Se Rebuild falhar: arquivo antigo permanece; **caches não são limpos**.

### 4.2 `CompactAllPendingAsync`

1. Obtém a lista pendente via a mesma lógica de `GetCompactStatusAsync`.
2. Para cada pasta, reporta progresso e chama `CompactFolderAsync`.
3. No primeiro erro: propaga exceção com contexto claro, ex.:  
   `Falha ao compactar “Arquivo”. 2 pasta(s) já compactada(s); as demais não foram alteradas.`

### 4.3 Escopo por store

Cada `DbxStore` / perfil tem seu próprio status. A UI agrega status de vários perfis só no **fechar do app**.

## 5. UI

### 5.1 Gatilhos

- **Contexto da pasta** (mesmo `ContextMenu` de Nova subpasta / Mover / Apagar): “Compactar esta pasta…” — `IsEnabled` só se aquela pasta estiver pendente.
- **Menu Arquivo** (junto de Abrir Perfil): “Compactar tudo pendente…” — `IsEnabled` se o **perfil ativo** (foco atual na UI de perfis) tiver ≥ 1 pasta pendente. A ação compacta só aquele perfil.

### 5.2 Modal de progresso

Novo diálogo (ou reuso adaptado de `MoveMessagesProgressDialog`) **sem Cancelar**:

- Uma pasta: `Compactando “Nome”…`
- Tudo: `Compactando “Nome”… (i/N)` + `ProgressBar` por pastas concluídas
- `Owner` = janela principal; modal até terminar ou falhar

### 5.3 Fechar perfil

Ao remover/fechar um perfil aberto, se `GetCompactStatusAsync` daquele store tiver pendência:

> Há N pastas com espaço a recuperar (~X MB) neste perfil. Compactar agora?

- **Sim** → `CompactAllPendingAsync` só naquele perfil + modal; sucesso → fecha o perfil; falha → perfil **permanece aberto**, erro mostrado.
- **Não** → fecha o perfil; pendências ficam no SQLite para a próxima abertura.

### 5.4 Fechar o aplicativo

No `Closing` de `MainWindow`, se **qualquer** perfil aberto tiver pendência:

> Há N pastas com espaço a recuperar (~X MB). Compactar agora?

N e X agregam **todos** os perfis abertos. Sim → compacta tudo pendente em cada perfil (sequencialmente, um store após o outro, progresso contínuo ou reiniciado por perfil — detalhe do plano); sucesso → permite fechar; falha → **cancela o close** (`e.Cancel = true`), mostra erro.

### 5.5 Pós-sucesso

Se a pasta atualmente listada foi compactada, recarregar a lista (offsets mudaram). Status bar: resumo curto (`Compactação concluída` / `Compactadas N pastas (~X MB)`).

## 6. Erros e bordas

| Caso | Comportamento |
|---|---|
| Pasta sem pendência | No-op; menu desabilitado |
| Pasta sem `.dbx` de apoio | Menu desabilitado / no-op |
| Falha numa pasta só | Mensagem com nome; outras pastas intactas |
| Falha no meio de “tudo” | Para; já feitas ficam; restantes intactas; mensagem com contagem |
| Close após falha no compactar-do-fechar | App/perfil **não** fecha |
| Estimativa 0 MB com N > 0 | Diálogo ainda mostra N pastas |
| Concurrent edit | Modal bloqueia UI; sem cancelamento |

## 7. Estratégia de testes

**Motor (`LEC.MailStore.Tests` / `DbxStore*Tests`):**

- Compactar pasta com apagadas e/ou movidas: headers vivos permanecem; tamanho do arquivo não retém o lixo esperado (assert em `FileInfo.Length` ou em ausência dos offsets antigos).
- Pós-compact: caches da pasta vazios; reabrir store não ressuscita apagadas/movidas.
- Sem pendência → no-op (arquivo não reescrito de forma observável, ou status vazio).
- `GetCompactStatus`: pastas e soma de bytes coerentes com fixtures.
- `CompactAllPendingAsync`: duas pastas ok; falha injetada na do meio → anteriores compactadas, seguintes intactas, exceção com contagem.

**App (`LEC.App.Tests`):**

- Handlers/VM chamam pasta vs tudo corretamente.
- Fechar perfil com pendência: Sim compacta só aquele; Não fecha sem compactar.
- Fechar app com dois perfis pendentes: agrega N/MB; Sim percorre ambos.

**Manual:** modal `i/N`, menus enable/disable, fechar perfil vs fechar app, falha simulada (arquivo read-only) no meio do lote.

## 8. Fora de escopo / adiados

- Renomear pasta / renomear arquivo `.dbx` (próxima fatia 1.2 após esta).
- Backup automático pré-compact.
- Indicador visual na árvore de pastas.
- Cancelar compactação em andamento.
- Remover `.dbx` de pastas escondidas (`HiddenFolderCache`).
- Limiar de MB para suprimir o alerta de fechar.
- Compactação automática em idle.
