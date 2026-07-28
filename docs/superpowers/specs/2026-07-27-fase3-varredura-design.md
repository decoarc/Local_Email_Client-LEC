# Fase 3 — Varredura e Recuperação — Design

**Status:** Aprovado para virar plano de implementação.
**Depende de:** Fase 1 (`LEC.DbxFormat`) e Fase 2 (`LEC.MailStore`, `LEC.App`), já concluídas e mescladas.
**Referência:** `ARQUITETURA.md`, especialmente §7.3 (blindagem contra corrupção), §7.4 (política diante de corrupção), §7.5 (mensagens recuperadas), §9 (interface), §12 (Fase 3).

## 1. Objetivo

Modo de varredura (*scavenge*) que percorre um arquivo `.dbx` bloco a bloco, ignorando a árvore de índice, procurando cadeias de mensagem que a árvore não referencia — recuperando conteúdo que nenhum outro leitor abre. Disparado explicitamente pelo usuário por pasta, nunca automático (arquivos grandes tornam a operação lenta). Resultado marcado como **Recuperada** ou **Fragmento**, nunca "Excluída" (ARQUITETURA.md §7.5 — não sabemos por que o bloco ficou órfão). Escondido por padrão na lista de mensagens; estado lembrado entre sessões via um cache leve.

## 2. Escopo

**Dentro:**
- Motor de varredura em `LEC.DbxFormat` (`DbxScavenger`), reaproveitando `DbxMessageBlockReader` já existente e validado pela Fase 1.
- Duas constantes novas em `DbxFormat.cs` (offset 0 do bloco = auto-referência ao próprio offset no arquivo; offset 4 = marcador fixo `0x200`), verificadas contra `BCP/` antes de virar constante — mesma disciplina da Fase 1. A Fase 1 nunca precisou delas porque só seguia ponteiros já confiáveis vindos do índice; para varredura elas são a validação principal contra falso positivo ao escanear bytes não confiáveis.
- Classificação **Recuperada** (cadeia íntegra, headers parseáveis) vs. **Fragmento** (truncada ou headers não parseáveis).
- Cache leve (SQLite, `Microsoft.Data.Sqlite` — mesma dependência já planejada para a Fase 4) em `%LOCALAPPDATA%`, nunca perto do perfil original. Guarda por perfil+arquivo `.dbx`: quando foi varrido, offsets/tamanhos/classificação encontrados, e o estado do toggle mostrar/esconder.
- UI: gatilho **contextual** — só aparece quando a pasta selecionada está `Health = Parcial` (ARQUITETURA.md §7.4: "a UI oferece varredura" ao detectar inconsistência). Depois de rodar: barra "N mensagens recuperáveis — mostrar/esconder" (o toggle só controla visibilidade do resultado já varrido, não dispara nova varredura). Itens recuperados na lista, quando visíveis: linha em tom mais fraco + tag, entram na ordenação normal por data (ARQUITETURA.md §7.5).
- Varredura roda em background, cancelável, com progresso — nunca trava a UI.

**Fora (fases futuras, ARQUITETURA.md §12):**
- Sobrevivência do marcador `Recuperada`/`Fragmento` na exportação (header `X-LEC-Recovered` no `.eml`) — não há exportação ainda (Fase 7). Fica registrado como pendência para quando a Fase 7 chegar.
- Biblioteca própria / importação / deduplicação (Fase 4+). O cache desta fase é deliberadamente menor e mais simples que `.lecarchive` — guarda só o resultado da varredura, não é uma antecipação da biblioteca. Reaproveitar `Microsoft.Data.Sqlite` agora significa que a Fase 4 estende um schema já existente em vez de migrar algo.
- Varredura de `.pst` — `PstStore` nem existe ainda (adiado desde a Fase 2).
- Varredura automática ao abrir uma pasta, ou oferecida em pastas já `Health = Ok` — sempre ação explícita, sempre condicionada a corrupção já detectada.

## 3. Estrutura de projetos

```
src/
  LEC.DbxFormat/
    DbxFormat.cs              MODIFICADO — +2 constantes (auto-referência, marcador 0x200)
    DbxScavenger.cs            NOVO — varredura linear + reconstrução de cadeia órfã
    RecoveredMessage.cs        NOVO — resultado por mensagem achada (offset da cadeia, tamanho,
                                 classificação Recuperada/Fragmento)
  LEC.MailStore/
    ScavengeCache.cs           NOVO — leitura/escrita do SQLite em AppData
    ScavengeStatus.cs          NOVO — tipo exposto ao App: nunca-varrida | varrida (contagem,
                                 visível?)
    DbxStore.cs                 MODIFICADO — expõe varredura através de IMailStore
    IMailStore.cs                MODIFICADO — novos membros (ver §4)
    MessageHeader.cs              MODIFICADO — sinaliza proveniência (mensagem normal, recuperada
                                 ou fragmento)
  LEC.App/
    ViewModels/...              MODIFICADO — gatilho contextual, barra de recuperáveis, estado
                                 mostrar/esconder, estilo visual do item recuperado na lista
tests/
  LEC.DbxFormat.Tests/         + testes de DbxScavenger: bloco órfão real isolado, cadeia truncada,
                                 ciclo/auto-referência inválida, arquivo sem nada pra recuperar
  LEC.MailStore.Tests/          + testes de ScavengeCache (persistência) e da varredura exposta via
                                 DbxStore, incluindo teste contra um `.dbx` real do `BCP/` que tenha
                                 conteúdo recuperável (se existir um; ver §9)
```

Dependências inalteradas: `LEC.MailStore → LEC.DbxFormat`, `LEC.App → LEC.MailStore` (nunca `LEC.DbxFormat` direto). `LEC.MailStore` ganha a dependência em `Microsoft.Data.Sqlite`.

## 4. Contrato — extensão do `IMailStore`

O congelamento do `IMailStore` na Fase 2 foi uma restrição *daquele plano*, não uma regra permanente — cada fase pode estender o contrato quando há necessidade real, mantendo-o agnóstico de formato (a varredura é descrita em termos genéricos o bastante em ARQUITETURA.md §7.3-7.5 para, em princípio, se aplicar a um `PstStore` futuro também).

Formato exato dos novos membros é decisão do plano de implementação, não deste design — mas conceitualmente:
- Uma forma de consultar o estado atual de varredura de uma pasta sem rodar nada (nunca-varrida vs. já-varrida-com-N-resultados-visível-ou-não).
- Uma operação assíncrona, cancelável, com progresso, que roda a varredura de uma pasta e atualiza o cache.
- Uma forma de alternar a visibilidade dos itens recuperados de uma pasta já varrida.
- `EnumerateHeadersAsync` passa a incluir os itens recuperados (quando visíveis) misturados aos normais, ordenados por data como qualquer outro — a proveniência (normal/recuperada/fragmento) vai num campo novo em `MessageHeader`, não reaproveitando `Health` (que já significa outra coisa: corrupção de atributo numa mensagem normalmente indexada).

## 5. Algoritmo de varredura

Baseado na abordagem do `undbx` (`dbxread.c`, já citado em `DbxFormat.cs`), verificada contra o comportamento real esperado:

1. Antes de escanear, `DbxScavenger` recebe o conjunto de offsets de bloco já cobertos pelas mensagens que `DbxEmailFile.Messages` (Fase 1) já encontrou pela árvore — nunca reportar como "recuperado" algo que o parser normal já viu (ARQUITETURA.md §7.5: "a varredura encontra blocos **não referenciados** pela árvore").
2. Varre o arquivo inteiro em incrementos alinhados de 4 bytes procurando um cabeçalho de bloco válido:
   - 1º word (offset 0): deve ser igual à própria posição do candidato no arquivo (auto-referência — descarta a esmagadora maioria do ruído).
   - 2º word (offset 4): deve ser exatamente `0x200` (marcador fixo de capacidade do bloco).
   - 3º word (offset 8, já existe como `MessageBlockSizeOffset`): tamanho real dos dados, `1..0x200`.
   - 4º word (offset 12, já existe como `MessageBlockNextOffset`): offset do próximo fragmento, ou `0`.
3. Todo candidato que passa na validação e cujo offset não está no conjunto já-referenciado (passo 1) vira ponto de partida: remonta a cadeia com o `DbxMessageBlockReader` existente (mesmo código que a Fase 1 já usa para cadeias confiáveis — mesmas proteções contra ciclo e teto de fragmentos).
4. Cadeias sobrepostas (dois candidatos que levam à mesma sequência de blocos) contam uma vez só.
5. Tenta parsear o resultado como RFC822 real (mesma tolerância que `MessageRenderer` já tem a headers malformados, Task 17). Classifica: **Recuperada** se a cadeia não foi truncada e os headers parseiam; **Fragmento** caso contrário.

Operação O(tamanho do arquivo) com custo fixo pequeno por posição — mesmo assim, arquivos na casa de 1+ GB (já vistos em `BCP/`) tornam isso perceptível, daí rodar em background com `IProgress`/`CancellationToken`.

## 6. Cache

Schema mínimo, pensado para a Fase 4 poder estender sem migrar dado nenhum:

- Uma tabela por "varredura de um arquivo": caminho do perfil + nome do `.dbx`, timestamp, contagem de itens achados, estado do toggle mostrar/esconder.
- Uma tabela com os itens achados: offset da cadeia, tamanho, classificação (Recuperada/Fragmento).

Fica em `%LOCALAPPDATA%\LEC\scavenge-cache.db` (ou similar — nome exato é decisão do plano), nunca escrito perto do `.dbx` de origem, respeitando o espírito do princípio da seção 2 do `ARQUITETURA.md` mesmo esse não sendo tecnicamente um arquivo de origem — evita quebrar em mídia somente-leitura ou backup.

## 7. Fluxo da UI

Selecionar uma pasta com `Health = Parcial` mostra um link/botão contextual (ex.: na barra de status ou acima da lista) — "Procurar mensagens recuperáveis nesta pasta". Pastas `Health = Ok` não mostram nada. Ao clicar, roda a varredura (com indicação de progresso, cancelável) e persiste o resultado no cache.

Depois de rodar (nesta sessão ou em uma anterior — o cache já tem o resultado), aparece uma barra discreta acima da lista: *"N mensagens recuperáveis neste arquivo — mostrar"*. Clicar alterna `EnumerateHeadersAsync` para incluir os itens recuperados, que entram na lista misturados aos normais, ordenados por data, com estilo visual diferenciado (linha em tom mais fraco + tag "Recuperada"/"Fragmento") — reaproveitando o padrão de indicador visual que a Fase 2 já criou para `Health` por mensagem (Task 16: coluna `!`/tooltip).

## 8. Erros e degradação

- Varredura num arquivo que o usuário não tem mais permissão de ler, ou que sumiu entre a detecção de `Health = Parcial` e o clique no botão: mensagem clara, não exceção — mesmo padrão já estabelecido em toda a Fase 1/2.
- Cache indisponível (AppData sem permissão de escrita, disco cheio): a varredura ainda funciona nessa sessão, só não persiste entre reaberturas — degradação elegante, não motivo para bloquear a funcionalidade principal.
- Cancelamento no meio da varredura: resultado parcial é descartado (não persiste um cache incompleto que passaria a mentir sobre "já foi varrido").

## 9. Estratégia de testes

- `DbxScavenger`: testes sintéticos primeiro (construir um `.dbx` de teste com um bloco órfão de propósito, com `TestDbxBuilder`-like da Fase 1) cobrindo: bloco órfão isolado achado e reconstruído, cadeia truncada vira Fragmento, offset com auto-referência inválida é descartado como ruído, arquivo sem nada órfão devolve lista vazia sem erro.
- Teste contra arquivo real do `BCP/`: só faz sentido se existir de fato conteúdo recuperável em algum `.dbx` real do perfil — isso precisa ser verificado (rodar a varredura manualmente contra os arquivos reais) antes de escrever esse teste como parte do plano, mesma disciplina de "verificar contra arquivo real antes de codificar" que a Fase 1 seguiu para as constantes do formato. Se nenhum arquivo real tiver conteúdo recuperável, o teste real vira "roda sem erro e devolve vazio", e a cobertura de recuperação de verdade fica só nos sintéticos.
- `ScavengeCache`: testes de persistência (escreve, reabre, lê de volta) com um banco temporário, sem tocar `%LOCALAPPDATA%` real.
- ViewModels: mesmo padrão da Fase 2 — testáveis sem WPF, harness contra dados reais só para o passo de verificação manual final.
- UI/XAML: verificação manual, mesmo padrão da Fase 2 (sem automação de UI).

## 10. Fora de escopo / decisões adiadas

- `X-LEC-Recovered` na exportação: adiado para a Fase 7 (não há exportação ainda).
- Varredura de `.pst`: adiado (mesmo motivo do `PstStore` inteiro — Fase 2).
- Exposição de varredura para múltiplos perfis abertos simultaneamente (ARQUITETURA.md §9, adicionado nesta sessão): a varredura em si já é por `IMailStore`/pasta, então funciona naturalmente quando múltiplos perfis existirem na UI — não é uma decisão nova, só uma consequência de ambos os recursos serem ortogonais.
- Formato exato dos novos membros de `IMailStore` e nomes de classe: decisão do plano de implementação, não deste design.
