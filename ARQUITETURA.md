# LEC — Local Email Client

**Documento de arquitetura**
Versão 0.1 — 25/07/2026
Status: desenho fechado, implementação não iniciada

---

## 1. O que é

Aplicação desktop para Windows que abre arquivos de email legados (`.dbx` do Outlook Express e `.pst` do Outlook), permite reorganizá-los e consolidá-los num arquivo morto único e pesquisável.

O caso de uso que originou o projeto: existem vários arquivos `.dbx` de épocas diferentes, com conteúdo sobreposto, e a necessidade é juntá-los, eliminar repetições e organizar o resultado.

### Não é

- Não é cliente de email. Sem POP3, IMAP, SMTP, sem contas, sem envio.
- Não escreve `.dbx`. Ver seção 6.
- Não substitui o Outlook.

---

## 2. Princípio que governa todo o resto

> **Os arquivos originais são somente-leitura, sempre. Nenhuma operação do programa escreve num `.dbx` ou `.pst` de origem, em nenhuma circunstância.**

Toda edição acontece numa biblioteca própria, separada. Isso permite que o usuário experimente, erre e recomece sem risco a arquivos de vinte anos que muitas vezes não têm outra cópia.

Essa regra não é negociável e deve ser verificada em revisão de código: nenhum handle de arquivo de origem é aberto com permissão de escrita.

---

## 3. Decisões fechadas

| # | Decisão | Escolha | Motivo |
|---|---|---|---|
| 1 | Plataforma | Windows 10 e 11 (x64) | Win8 tem uso residual e fim de suporte desde 2023; abandoná-lo libera ferramental moderno |
| 2 | Stack | .NET 9 + WPF, publicado self-contained | WPF é maduro para listas virtualizadas grandes; self-contained evita o usuário instalar runtime |
| 3 | Escopo | Leitor + reorganizador, sem rede | Recorte que entrega valor sem a complexidade de OAuth, TLS e sincronização |
| 4 | Visual | Layout clássico de três painéis, estética moderna | Ergonomia familiar do Outlook Express sem parecer datado |
| 5 | Leitura dos originais | Direta no arquivo, sem importação obrigatória para visualizar | Abertura instantânea, zero risco ao original |
| 6 | Edição | Numa biblioteca própria, alimentada por importação | Preserva o princípio da seção 2 e ainda assim dá edição total |
| 7 | Formato de saída | Biblioteca própria + exportação para `.eml`, `.mbox`, `.pst` | Sem lock-in; `.dbx` como saída foi descartado (seção 6) |
| 8 | Armazenamento das mensagens | `.eml` cru endereçado por hash de conteúdo | Dedup barata, sem duplicação de espaço, formato aberto |

---

## 4. Bibliotecas de terceiros

| Biblioteca | Licença | Papel |
|---|---|---|
| **MimeKit** | MIT | Parsing RFC822: headers, MIME, anexos, charsets. Crítico para arquivos brasileiros antigos em ISO-8859-1 / windows-1252 com headers malformados |
| **XstReader** | MIT | Leitura de `.pst` e `.ost` em C# puro, sem Outlook instalado e sem P/Invoke |
| **WebView2** | — | Renderização de corpo HTML, com script desligado e imagens remotas bloqueadas por padrão |
| **SQLite** (`Microsoft.Data.Sqlite`) | MIT | Metadados da biblioteca, índice de busca FTS5, journal de operações |

O parser de `.dbx` é escrito do zero — não existe biblioteca .NET mantida para o formato.

---

## 5. Arquitetura em camadas

```
┌─────────────────────────────────────────────┐
│  UI (WPF)  — três painéis, visual moderno   │
├─────────────────────────────────────────────┤
│  Exportação  →  .eml / .mbox / .pst         │
├─────────────────────────────────────────────┤
│  Biblioteca de trabalho  (.lecarchive)      │
│  mutável · dedup · undo · busca             │
├─────────────────────────────────────────────┤
│  Ingestão (IMailStore)  — SOMENTE LEITURA   │
│  DbxStore · PstStore · EmlStore · MboxStore │
└─────────────────────────────────────────────┘
```

A interface `IMailStore` isola o formato de origem. Tudo acima dela é agnóstico: acrescentar suporte a um formato novo não toca em UI, busca ou exportação.

Há dois modos de uso, e ambos importam:

- **Espiar** — abrir um `.dbx` direto e navegar, sem importar nada. Rápido, descartável, útil para responder "o que tem nesse arquivo?".
- **Importar** — trazer o conteúdo para a biblioteca, onde pode ser reorganizado.

---

## 6. Por que não escrevemos `.dbx`

Foi considerado e descartado deliberadamente.

1. **Não há especificação oficial.** O formato é engenharia reversa. Ler com base em documentação incompleta é aceitável; escrever não.
2. **Não há como validar o resultado.** Não existe Outlook Express rodando em Windows 10/11 para abrir um arquivo gerado e confirmar que ficou íntegro. Estaríamos escrevendo às cegas.
3. **O modo de falha é silencioso.** Um bug num escritor de B-tree com gestão de espaço livre não estoura na hora — produz arquivo corrompido descoberto meses depois.
4. **O próprio Outlook Express corrompia seus arquivos**, notoriamente acima de 2 GB. É um formato frágil por natureza.
5. **Não há demanda real.** Nenhum sistema atual consome `.dbx`.

Se um dia surgir necessidade concreta, isso é um projeto próprio, com fixtures extensos e validação independente — não um recurso a mais.

---

## 7. Camada de ingestão

### 7.1 Contrato

```csharp
public interface IMailStore : IDisposable
{
    IReadOnlyList<StoreFolder> Folders { get; }

    // Nível 1: barato, só o índice — alimenta a lista
    IAsyncEnumerable<MessageHeader> EnumerateHeadersAsync(
        StoreFolder folder, CancellationToken ct);

    // Nível 2: caro, sob demanda — alimenta o painel de leitura
    Task<Stream> OpenMessageAsync(MessageRef id, CancellationToken ct);

    StoreHealth Health { get; }   // Ok | Parcial | Corrompido
}
```

Leitura preguiçosa em dois níveis: a árvore de índice do `.dbx` guarda remetente, assunto e data separados do corpo, então a lista é populada sem tocar nos blocos de conteúdo. Um arquivo de 2 GB abre em menos de um segundo.

Acesso via **memory-mapped file**, não `FileStream` — o padrão de acesso é aleatório por natureza e o sistema operacional cuida do cache. Abrir sempre com `FileShare.ReadWrite`, para não travar arquivos que estejam num backup, compartilhamento de rede ou abertos por outro processo.

### 7.2 O formato `.dbx` — estrutura geral

Um arquivo por pasta. Cabeçalho de tamanho fixo com ponteiros, seguido de uma árvore de nós de índice, que apontam para nós de informação (atributos da mensagem), que por sua vez apontam para uma lista encadeada de blocos de dados.

Os blocos concatenados formam a mensagem em **RFC822 puro** — é o ponto que torna o projeto viável: uma vez percorrida a árvore, o MimeKit assume e não há formato proprietário no conteúdo.

`Folders.dbx` usa a mesma estrutura de árvore, mas os nós de informação descrevem pastas (id, pai, nome, arquivo correspondente). É o que permite reconstruir a hierarquia em vez de exibir uma lista solta de arquivos.

> ⚠️ **Constantes ainda não verificadas.** Magic bytes, offsets do cabeçalho, tamanho dos nós e a tabela de IDs de atributo **não foram confirmados** e não devem ser escritos de memória. A referência canônica de engenharia reversa é a documentação de Arne Schloh (`oedbx.aroh.de`), base de onde saíram `undbx` e `DbxConv`. O procedimento é conferir contra um arquivo real antes de codificar, e transcrever tudo para uma classe `DbxFormat` com constantes nomeadas e comentadas — nenhum valor hexadecimal solto no meio do código.

**Escopo v1:** `*.dbx` de mensagens e `Folders.dbx`. Fora: `Offline.dbx`, `Pop3uidl.dbx`, `.wab`.

### 7.3 Blindagem contra arquivo corrompido

Arquivo `.dbx` corrompido é a regra, não a exceção. Sem as proteções abaixo, um arquivo danificado trava o programa ou tenta alocar gigabytes. Nada aqui é opcional.

- Todo ponteiro validado contra o tamanho do arquivo **antes** de ser seguido.
- Conjunto de offsets visitados, para detectar ciclo na árvore e na lista de fragmentos.
- Profundidade máxima de árvore.
- Teto de tamanho por mensagem remontada.
- Teto de número de fragmentos por mensagem.

### 7.4 Política diante de corrupção

A árvore de índice é sempre o caminho primário. Ao detectar inconsistência — ponteiro inválido, ciclo, contagem de mensagens divergente do cabeçalho — o parser **não aborta**: coleta o que conseguiu, marca `Health = Parcial` e segue.

A UI então oferece **varredura** (*scavenge*): percorrer o arquivo bloco a bloco procurando assinaturas de mensagem, ignorando o índice. Recupera arquivos que nenhum outro leitor abre.

A varredura **nunca roda automaticamente** — é lenta e traz lixo junto. Sempre por ação explícita do usuário.

### 7.5 Mensagens recuperadas

A varredura encontra blocos não referenciados pela árvore. Como o Outlook Express usa um arquivo por pasta, um bloco órfão dentro de `Caixa de entrada.dbx` pertencia mesmo à caixa de entrada — a atribuição de pasta é confiável, diferente do que seria num `.pst`.

**Terminologia:** chamar de **"Recuperada"**, nunca de "Excluída". Sabemos que o bloco não estava referenciado; não sabemos por quê. Pode ter sido apagada, pode ser resto de mensagem movida, pode ser sobra de compactação interrompida. "Excluída" afirmaria algo que o arquivo não informa.

**Dois níveis de marcador**, porque são situações diferentes para quem lê:

- `Recuperada` — cadeia de fragmentos íntegra, headers parseáveis.
- `Fragmento` — incompleta ou truncada.

**Escondidas por padrão.** Um arquivo antigo pode devolver centenas de fragmentos que afogam as mensagens reais. Barra discreta no topo da lista: *"47 mensagens recuperáveis neste arquivo — mostrar"*. Quando ligada, entram na ordenação normal por data, com linha em tom mais fraco e a tag. Estado por pasta, lembrado entre sessões.

O marcador precisa sobreviver à exportação — header `X-LEC-Recovered` no `.eml` ou subpasta própria. Sem isso, daqui a um ano ninguém distingue o original do resgatado.

### 7.6 Codificação de caracteres

As strings dos nós de índice são bytes crus, sem charset declarado — normalmente a codepage do sistema da época, tipicamente windows-1252 no Brasil.

Política: tentar UTF-8, cair para windows-1252 se falhar, e expor override manual por arquivo. O corpo da mensagem não tem esse problema — declara o próprio charset e o MimeKit resolve.

### 7.7 Índice versus headers reais

Os atributos do índice divergem dos headers RFC822 do corpo em arquivos antigos e corrompidos.

- **A lista** usa os atributos do índice, porque são instantâneos.
- **O painel de leitura** usa os headers reais, porque são corretos.
- Divergência **não é erro** e não deve gerar aviso.
- **A importação** usa os headers reais, porque a biblioteca precisa ser confiável a longo prazo.

---

## 8. Biblioteca de trabalho

### 8.1 Formato

Uma pasta `.lecarchive/`:

```
MinhaBiblioteca.lecarchive/
├── library.db          SQLite: pastas, metadados, FTS5, journal, proveniência
├── blobs/
│   ├── a3/f2/a3f2....eml
│   └── ...             .eml cru, nome = SHA-256 do conteúdo
└── lock                instância única
```

### 8.2 Por que endereçar por conteúdo

Nomear cada blob pelo SHA-256 do próprio conteúdo resolve três problemas de uma vez:

1. Duplicata exata vira detecção O(1).
2. Uma mensagem presente em cinco backups ocupa espaço uma única vez, ainda que referenciada em várias pastas.
3. Integridade é verificável a qualquer momento.

E o conteúdo continua sendo `.eml` legível por qualquer ferramenta — não há aprisionamento no formato do LEC.

### 8.3 A distinção que sustenta o resto

> **Blob é conteúdo imutável. Item é uma mensagem dentro de uma pasta, e é mutável.**

- Mover uma mensagem altera o item, não o blob.
- Apagar remove o item; o blob só desaparece na compactação.
- A mesma mensagem em duas pastas são dois itens apontando para um blob.

### 8.4 Esquema SQLite

```sql
-- Conteúdo imutável, endereçado por hash
CREATE TABLE blob (
    hash        TEXT PRIMARY KEY,       -- SHA-256 hex do .eml cru
    size        INTEGER NOT NULL,
    created_at  INTEGER NOT NULL
);

-- Árvore de pastas da biblioteca
CREATE TABLE folder (
    id          INTEGER PRIMARY KEY,
    parent_id   INTEGER REFERENCES folder(id),
    name        TEXT NOT NULL,
    sort_order  INTEGER NOT NULL DEFAULT 0,
    UNIQUE(parent_id, name)
);

-- Mensagem numa pasta — a entidade mutável
CREATE TABLE item (
    id            INTEGER PRIMARY KEY,
    blob_hash     TEXT    NOT NULL REFERENCES blob(hash),
    folder_id     INTEGER NOT NULL REFERENCES folder(id),
    state         INTEGER NOT NULL DEFAULT 0,   -- 0=ativo 1=lixeira
    -- cache desnormalizado, para listar sem abrir o blob
    subject       TEXT,
    from_name     TEXT,
    from_addr     TEXT,
    date_utc      INTEGER,
    size          INTEGER,
    has_attach    INTEGER NOT NULL DEFAULT 0,
    is_read       INTEGER NOT NULL DEFAULT 0,
    identity_key  TEXT,                          -- ver 8.5
    integrity     INTEGER NOT NULL DEFAULT 0     -- 0=normal 1=recuperada 2=fragmento
);

CREATE INDEX ix_item_folder   ON item(folder_id, state, date_utc DESC);
CREATE INDEX ix_item_identity ON item(identity_key) WHERE identity_key IS NOT NULL;
CREATE INDEX ix_item_blob     ON item(blob_hash);

-- Cada execução de importação
CREATE TABLE import (
    id            INTEGER PRIMARY KEY,
    source_path   TEXT    NOT NULL,
    source_kind   TEXT    NOT NULL,   -- dbx | pst | eml | mbox
    source_hash   TEXT,               -- detecta reimportação do mesmo arquivo
    started_at    INTEGER NOT NULL,
    finished_at   INTEGER,
    item_count    INTEGER,
    health        TEXT                -- ok | parcial | corrompido
);

-- De onde veio cada mensagem
CREATE TABLE provenance (
    item_id       INTEGER PRIMARY KEY REFERENCES item(id) ON DELETE CASCADE,
    import_id     INTEGER NOT NULL REFERENCES import(id),
    source_folder TEXT,               -- pasta no arquivo de origem
    source_offset INTEGER,            -- offset do nó de índice ou bloco
    was_scavenged INTEGER NOT NULL DEFAULT 0
);

-- Journal reversível
CREATE TABLE operation (
    id            INTEGER PRIMARY KEY,
    kind          TEXT    NOT NULL,   -- import|move|delete|merge|dedup|rename|purge
    performed_at  INTEGER NOT NULL,
    undone_at     INTEGER,
    summary       TEXT    NOT NULL,   -- descrição legível p/ a UI
    payload       TEXT    NOT NULL    -- JSON com o necessário p/ reverter
);

-- Busca em texto completo
CREATE VIRTUAL TABLE item_fts USING fts5(
    subject, from_text, body,
    content=''                        -- externo; item_id no rowid
);
```

Notas:

- O cache desnormalizado em `item` existe para que listar 200 mil mensagens não exija abrir nenhum blob.
- `provenance` é chave primária em `item_id` — um item vem de exatamente uma importação.
- `payload` em JSON, e não colunas rígidas, porque cada tipo de operação precisa reverter coisas diferentes.

### 8.5 Deduplicação — dois níveis

Hash exato só pega bytes idênticos, e o mesmo email vindo de dois backups quase nunca é idêntico: headers `Received` a mais, `X-` inseridos pelo servidor, CRLF virando LF, ordem de headers diferente.

**Nível 1 — exata.** Igualdade de `blob_hash`. Segura. Pode ser aplicada automaticamente na importação.

**Nível 2 — por identidade.** Chave em `identity_key`:

1. `Message-ID` normalizado (minúsculas, sem espaços, sem `<>`). É o identificador global do email por design.
2. Na ausência dele — comum em mensagens muito antigas e em recuperadas — chave derivada de `From` + `Date` + `Subject` + primeiros N bytes do corpo normalizado.

> **O nível 2 nunca remove sozinho. Apenas propõe, para revisão do usuário.**

Quando duas variantes compartilham `identity_key` mas têm bytes diferentes, a regra de desempate é **manter a mais completa** — mais headers, maior tamanho, integridade melhor — e não a primeira encontrada. O restante vai para a lixeira, não para a purga.

### 8.6 Integridade de escrita

Blob e SQLite não compartilham transação. A ordem importa:

1. Blob gravado em arquivo temporário.
2. `rename` atômico para o caminho final.
3. **Só então** commit no SQLite.

Queda de energia no meio deixa um blob órfão, que a compactação limpa — inofensivo. Na ordem inversa sobraria metadado apontando para arquivo inexistente, o que é corrupção real.

### 8.7 Exclusão em dois estágios

O usuário vai consolidar vinte anos de arquivo com uma ferramenta nova. O pior resultado possível é apagar errado sem conseguir voltar.

- **Apagar** move o item para a lixeira (`state = 1`). Reversível, e o blob continua no disco.
- **Purgar** só acontece no comando explícito *Compactar biblioteca*, que remove blobs sem nenhum item referenciando.
- Compactar exibe o que será removido em definitivo e exige confirmação.

### 8.8 Desfazer

Toda operação — importar, mover, mesclar, deduplicar, apagar, renomear — grava registro em `operation` com o necessário para reverter. Undo multinível, incluindo **desfazer uma importação inteira**, o que é essencial ao mesclar backups de épocas diferentes e perceber que um deles estava errado.

Única operação irreversível: purga na compactação. Por isso ela é explícita e confirmada.

### 8.9 Busca

- **Espiando um arquivo direto:** busca por remetente, assunto e data é instantânea (tudo em memória). Busca no corpo exige varrer o arquivo — alguns segundos em 2 GB, sempre assíncrona, com progresso e cancelável.
- **Na biblioteca:** FTS5 cobre tudo, inclusive corpo, instantaneamente.

Interface `ISearchProvider` com duas implementações (`ScanSearch`, `IndexedSearch`), para que a UI não saiba a diferença.

---

## 9. Interface

Três painéis, na ergonomia do Outlook Express: árvore de pastas à esquerda, lista de mensagens à direita em cima, painel de leitura embaixo. Tipografia, espaçamento e ícones atuais.

Pontos que exigem cuidado:

- **Lista virtualizada** de verdade — precisa aguentar centenas de milhares de itens.
- **Corpo HTML sempre isolado:** script desligado, imagens remotas bloqueadas por padrão com barra *"Exibir imagens"*. Emails antigos são cheios de pixels de rastreamento que ainda funcionam.
- **Links nunca abrem sozinhos** — sempre mostrar o destino real antes.
- **Degradação elegante:** arquivo aberto por outro processo, mídia somente-leitura, arquivo truncado ou caminho de rede indisponível precisam virar mensagem clara, não exceção.
- **Estado de saúde visível:** quando `Health = Parcial`, dizer isso na cara do usuário, com a oferta de varredura.

---

## 10. Riscos conhecidos

| Risco | Gravidade | Mitigação |
|---|---|---|
| Constantes do `.dbx` erradas ou incompletas | Alta | Verificar contra arquivos reais antes de codificar; fixtures desde o início |
| Arquivo corrompido travando ou estourando memória | Alta | Blindagem da seção 7.3, tratada como requisito, não como remendo |
| Dedup agressiva apagando mensagem única | Alta | Nível 2 nunca automático; exclusão em dois estágios; undo |
| Perda de dados na biblioteca | Alta | Ordem de escrita da 8.6; blob antes de metadado; nunca o inverso |
| `.dbx` acima de 2 GB com índice quebrado | Média | Varredura é justamente o caminho para esses casos |
| Charset errado deixando acentuação corrompida | Baixa | Override manual por arquivo |

---

## 11. Pendências

1. **Verificar as constantes do formato `.dbx`** contra arquivos reais — bloqueia toda a codificação do parser. O usuário possui arquivos, inclusive antigos.
2. Obter ou construir ao menos um `.dbx` **corrompido** para testar a varredura.
3. Definir o formato de exportação `.pst` — se será escrita própria via `[MS-PST]` ou fica para depois.
4. Decidir se `.wab` (contatos) entra numa versão futura.
5. Definir política de anexos na exportação: embutidos no `.eml` ou extraídos à parte.

---

## 12. Fases

**Fase 1 — Parser `.dbx` verificado.**
Constantes confirmadas contra arquivo real, travessia da árvore, remontagem de mensagem, blindagem completa, testes com arquivos reais e sintéticos. Sem UI: saída em linha de comando.

**Fase 2 — Visualizador.**
Shell WPF de três painéis, `IMailStore` com `DbxStore` e `PstStore`, leitura preguiçosa, `Folders.dbx`, render HTML isolado. Só espiar, sem biblioteca.

**Fase 3 — Varredura e recuperação.**
Modo scavenge, marcadores `Recuperada` e `Fragmento`, barra de mensagens recuperáveis.

**Fase 4 — Biblioteca.**
`.lecarchive`, esquema SQLite, importação, blobs endereçados por conteúdo, proveniência, FTS5.

**Fase 5 — Reorganização.**
Mover, mesclar, renomear, lixeira, journal de undo, compactação.

**Fase 6 — Deduplicação.**
Nível 1 automático, nível 2 com revisão do usuário, regra de desempate por completude.

**Fase 7 — Exportação.**
`.eml`, `.mbox`, e `.pst` conforme a pendência 3.

---

## Histórico

| Versão | Data | Mudança |
|---|---|---|
| 0.1 | 25/07/2026 | Documento inicial. Escopo passou de somente-leitura para leitura + reorganização em biblioteca própria; escrita em `.dbx` descartada. |
