# Árvore de índice multi-nível para mensagens .dbx — Design

**Status:** Aprovado para virar plano de implementação.
**Depende de:** Nada — mudança isolada em `LEC.DbxFormat`, sem alterar nenhum contrato consumido por `LEC.MailStore`/`LEC.App`.
**Referência:** `src/LEC.DbxFormat/DbxIndexTreeReader.cs` (o leitor já percorre árvores multi-nível; nenhuma mudança nele é necessária); `src/LEC.DbxFormat/DbxEmailFileWriter.cs` (arquivo que muda).
**Contexto:** Usuário reportou erro reproduzível ao mover mensagens em lote: `Message-store .dbx flat-node format holds at most 255 messages; got 256`. Investigação encontrou a causa raiz — `DbxEmailFileWriter.Rebuild` só sabe escrever um único nó de índice "plano" (`ptrCount` de 1 byte, até 255 ponteiros), embora o formato `.dbx` real (e o leitor deste próprio projeto) suporte árvores de índice de múltiplos níveis. Pedido explícito do usuário: remover esse teto por completo, sem substituí-lo por outro número fixo.

## 1. Objetivo

`DbxEmailFileWriter.Rebuild` deixa de ter qualquer limite artificial de mensagens por pasta. Uma pasta pode crescer para qualquer quantidade de mensagens (limitada só pelos tetos defensivos já existentes em `DbxSafetyLimits`, pensados para arquivos corrompidos, não para uso normal).

## 2. Por que isso é seguro sem tocar no leitor ou no formato em disco

`DbxIndexTreeReader.ReadNode` (já existente, não muda) percorre, para cada nó:
1. Se `indexCount > 0`, recursa primeiro em `nextTable` (subárvore "antes" das entradas do nó).
2. Para cada uma das até 255 entradas do nó: adiciona o `indexPtr` dela ao resultado e, se `entryIndexCount > 0`, recursa em `entryNextTable` (subárvore pendurada *nessa entrada específica*).

Ou seja, cada entrada já pode carregar uma mensagem própria **e** uma subárvore com mais mensagens. O escritor atual (`WriteIndexNode`) só usa a primeira metade dessa capacidade — grava só as próprias entradas, sempre com `entryIndexCount = 0`. Basta o escritor passar a usar `entryNextTable`/`entryIndexCount` para pendurar subárvores quando sobrarem mensagens.

## 3. Algoritmo de escrita

Substitui a chamada única `WriteIndexNode(bytes, infoNodeOffsets)` por uma função recursiva:

```csharp
// Escreve a subárvore de índice para os offsets de info-node dados (já escritos antes,
// na área de bytes anterior) e retorna o offset do nó raiz dessa subárvore. Filhos são
// sempre escritos antes do pai (post-order) porque o pai precisa conhecer o offset deles.
private static int BuildIndexSubtree(List<byte> bytes, IReadOnlyList<int> offsets)
{
    int ownCount = Math.Min(offsets.Count, byte.MaxValue);
    IReadOnlyList<int> ownOffsets = offsets.Take(ownCount).ToList();
    IReadOnlyList<int> remaining = offsets.Skip(ownCount).ToList();

    // Nenhuma mensagem sobrando: caso-base, idêntico ao WriteIndexNode de hoje (nó plano,
    // toda entrada com entryIndexCount = 0).
    var childOffsets = new int?[ownCount];
    if (remaining.Count > 0)
    {
        // Distribui o restante em até `ownCount` grupos o mais equilibrados possível, um
        // por entrada própria -- mantém o fan-out em 255 por nível, então a profundidade
        // cresce em log_255(N) em vez de N/255 (uma cadeia linear degeneraria pra uma
        // árvore profunda demais em mailboxes muito grandes).
        var groups = SplitEvenly(remaining, ownCount);
        for (int i = 0; i < ownCount; i++)
        {
            if (groups[i].Count > 0)
                childOffsets[i] = BuildIndexSubtree(bytes, groups[i]);
        }
    }

    return WriteIndexNode(bytes, ownOffsets, childOffsets);
}

private static List<List<int>> SplitEvenly(IReadOnlyList<int> items, int groupCount)
{
    var result = new List<List<int>>(groupCount);
    int baseSize = items.Count / groupCount;
    int remainder = items.Count % groupCount;
    int pos = 0;
    for (int i = 0; i < groupCount; i++)
    {
        int size = baseSize + (i < remainder ? 1 : 0);
        result.Add(items.Skip(pos).Take(size).ToList());
        pos += size;
    }
    return result;
}
```

`WriteIndexNode` ganha um segundo parâmetro (`childOffsets`, um por entrada, `null` quando a entrada não tem subárvore) e passa a gravar, por entrada, `entryNextTable`/`entryIndexCount` de acordo — hoje grava sempre zero nesses dois campos:

```csharp
private static int WriteIndexNode(List<byte> bytes, IReadOnlyList<int> infoNodeOffsets, IReadOnlyList<int?> childOffsets)
{
    int start = bytes.Count;

    AddZeros(bytes, 8);
    AddZeros(bytes, 4);                                   // nextTable — sempre 0 (não usado neste nível)
    AddZeros(bytes, 5);
    bytes.Add((byte)infoNodeOffsets.Count);                // ptrCount
    AddZeros(bytes, 2);
    bytes.AddRange(BitConverter.GetBytes(0));              // indexCount — sempre 0 (mesmo motivo)

    for (int i = 0; i < infoNodeOffsets.Count; i++)
    {
        bytes.AddRange(BitConverter.GetBytes(infoNodeOffsets[i])); // indexPtr

        int? child = childOffsets[i];
        bytes.AddRange(BitConverter.GetBytes(child ?? 0));          // entryNextTable
        bytes.AddRange(BitConverter.GetBytes(child.HasValue ? 1 : 0)); // entryIndexCount
    }

    return start;
}
```

`Rebuild` passa a chamar `int rootOffset = BuildIndexSubtree(bytes, infoNodeOffsets);` no lugar da chamada direta a `WriteIndexNode`. A checagem `if (messages.Count > byte.MaxValue) throw ...` (linhas 66-67 hoje) é removida.

## 4. Efeitos colaterais aceitos

- **Ordem das mensagens no arquivo deixa de ser sequencial** quando há mais de 255: a leitura intercalará entradas próprias de um nó com as subárvores penduradas nelas, em vez da ordem de escrita original. Confirmado que isso não importa — nada no código depende da ordem física de `DbxEmailFile.Messages`; a UI já ordena por data separadamente.
- **Nenhuma mudança em `DbxFolderFileWriter`** (mesma limitação de 255, só que para número de pastas) — fora de escopo, não foi o erro relatado e o volume de pastas de um perfil real fica muito longe de 255.
- **Nenhuma mudança em `DbxIndexTreeReader`, `DbxFormat`, ou em qualquer struct do formato em disco** — a mudança é inteiramente no lado de escrita.

## 5. Impacto em testes existentes

- `DbxEmailFileWriterTests.RebuildThrowsWhenGivenMoreThan255Messages` deixa de fazer sentido (o comportamento que ela testa é removido) — substituída por um teste de round-trip com mais de 255 mensagens.

## 6. Estratégia de testes

- **Round-trip 256 mensagens**: `Rebuild` com 256 entradas, reabrir com `DbxEmailFile.Open`, `Messages.Count == 256` e cada assunto (`S0`..`S255`) presente — prova que o caso "just over the old limit" funciona.
- **Round-trip com árvore de 2+ níveis**: `Rebuild` com uma quantidade que force múltiplas subárvores por entrada (ex.: 600 mensagens, `ownCount=255` no nível raiz, restante de 345 distribuído em até 255 grupos — alguns grupos com 2 mensagens cada, exercitando `BuildIndexSubtree` recursivo de verdade, não só um nível a mais). Mesma asserção: contagem e presença de cada assunto.
- **Caso-base preservado**: teste já existente com poucas mensagens (ex. "Um"/"Dois", já presente no arquivo) continua passando sem mudança — cobre que o caminho `remaining.Count == 0` continua idêntico ao nó plano de hoje.
- **`SplitEvenly`**: testável indiretamente pelos testes de round-trip acima (a contagem total de mensagens lidas de volta já prova que a distribuição não perde nem duplica nenhuma).

## 7. Fora de escopo / decisões adiadas

- `DbxFolderFileWriter` (limite de 255 pastas) — mesma técnica se aplicaria, mas não foi pedido e não é um problema real observado.
- Balanceamento "ótimo" da árvore (ex. B-tree com reequilíbrio) — a distribuição gulosa (255 por nível, restante dividido igualmente) já mantém profundidade logarítmica; não há necessidade de nada mais sofisticado para este caso de uso (arquivo é sempre reescrito do zero, não há inserção incremental num nó existente).
