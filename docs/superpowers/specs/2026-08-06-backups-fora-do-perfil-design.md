# Backups (.bak/.original) fora da pasta do perfil — Design

**Status:** Aprovado para virar plano de implementação.
**Depende de:** nenhuma fatia anterior especificamente — toca em todo lugar de `src/LEC.MailStore/DbxStore.cs` que já cria `.bak`/`.original` (`CreateFolderAsync`, `MoveFolderAsync`, `DeleteFolderAsync`, `AddMessageAsync`), todos já existentes antes desta fatia.
**Referência:** mesmo padrão de classe pequena + `DefaultXxxPath` estático + parâmetro opcional em `DbxStore.Open` já usado por `ScavengeCache`/`MovedMessageCache`/`DeletedMessageCache`/`HiddenFolderCache`.
**Contexto:** o usuário quer que a pasta de perfil fique contendo só o que o Outlook Express reconheceria (`Folders.dbx` + os `.dbx` de cada pasta) — hoje toda reescrita (criar/mover/apagar pasta, adicionar mensagem) deixa `<arquivo>.bak`/`<arquivo>.original` espalhados na própria pasta do perfil, ao lado do arquivo real. Decisões confirmadas com o usuário (2026-08-06): os backups continuam existindo (não é pra remover a rede de segurança), só mudam de lugar; se uma pasta é apagada de verdade, o backup dela também é apagado; se só a cópia de segurança falhar (não o arquivo real), a operação inteira continua sendo abortada, mesmo comportamento de hoje.

## 1. Objetivo

Mover onde `.bak`/`.original` são escritos: de "ao lado do arquivo real, dentro da pasta do perfil" para um novo diretório fora da pasta do perfil, um por perfil, em `%LOCALAPPDATA%\LEC\backups\<hash-do-perfil>\`. Nenhuma outra coisa muda: mesmo gatilho (toda reescrita `Rebuild`-based), mesma semântica (`.original` write-once, `.bak` sempre sobrescrito), mesmo "aborta a operação inteira se a cópia falhar".

## 2. Decisões de escopo (confirmadas com o usuário, 2026-08-06)

- **Backups continuam existindo, só mudam de local.** Remover de vez (sem substituto) foi descartado — perder a única rede de segurança contra uma reescrita malsucedida (bug, queda de energia no meio do `Rebuild`) é um risco maior do que o ganho de "pasta limpa".
- **Apagar uma pasta de verdade também apaga o backup dela.** Consistente com "sem lixeira, sem undo" — evita acumular backups órfãos de pastas que não existem mais em `%LOCALAPPDATA%\LEC\backups\`.
- **Falha isolada do backup (não do arquivo real) continua abortando a operação inteira.** Antes, backup e arquivo real ficavam na mesma pasta/mesmo volume, então uma falha praticamente implicava a outra. Agora que o backup mora em `%LOCALAPPDATA%` (localização/disponibilidade potencialmente diferente do perfil), essa premissa deixa de valer automaticamente — mas o usuário confirmou que quer manter a garantia de "toda reescrita sempre tem backup" em vez de degradar silenciosamente pra "reescreveu sem rede de segurança".
- **Perfis já usados em sessões anteriores** (antes desta mudança) podem ter `.bak`/`.original` órfãos dentro da própria pasta. Esta fatia não limpa isso automaticamente — fica pra remoção manual do usuário, se quiser. Apagar arquivos existentes silenciosamente, sem pedir, seria uma ação destrutiva não solicitada.

## 3. Nova classe `FileBackupStore`

Nova, em `src/LEC.MailStore/FileBackupStore.cs`. Mesmo padrão das outras classes de estado auxiliar (`ScavengeCache` etc.: `DefaultXxx` estático, parâmetro de override em `DbxStore.Open` pra testes apontarem pra um diretório temporário), mas sem SQLite — é só um diretório e arquivos.

```csharp
public sealed class FileBackupStore
{
    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LEC", "backups");

    private readonly string _root;

    public FileBackupStore(string root) => _root = root;

    public void BeforeRewrite(string profileDirectory, string fileName);
    public void Delete(string profileDirectory, string fileName);
}
```

- **`BeforeRewrite(profileDirectory, fileName)`** — substitui o trecho de 4 linhas que hoje se repete em `CreateFolderAsync`/`MoveFolderAsync`/`DeleteFolderAsync`/`AddMessageAsync`:
  1. Resolve o diretório de backup deste perfil: `Path.Combine(_root, HashProfilePath(profileDirectory))`. Cria se não existir (`Directory.CreateDirectory`).
  2. Escreve (se ainda não existir) um `profile-path.txt` nesse diretório, com o caminho completo do perfil em texto puro — só pra facilitar achar manualmente qual pasta de hash corresponde a qual perfil, já que o nome da pasta em si é um hash SHA-256 ilegível.
  3. `<fileName>.original` — write-once, mesma regra de hoje (só copia se ainda não existe).
  4. `<fileName>.bak` — sempre sobrescrito.
  - A cópia é sempre `Path.Combine(profileDirectory, fileName)` (o arquivo real, dentro da pasta do perfil) → `Path.Combine(diretório-de-backup, fileName + ".bak"/".original")`.
  - **Sem fallback silencioso.** Ao contrário das caches SQLite (que caem pra `:memory:` se o caminho real falhar), aqui uma falha em `Directory.CreateDirectory`/`File.Copy` propaga a exceção pra fora, abortando a operação chamadora inteira — decisão confirmada com o usuário (§2).
- **`Delete(profileDirectory, fileName)`** — apaga (best-effort, silencioso — nunca lança) `<fileName>.bak` e `<fileName>.original` no diretório de backup deste perfil. Usado só por `DeleteFolderAsync`. Diferente de `BeforeRewrite`, essa falha nunca deve travar nem aparecer pro usuário: perder um backup órfão de uma pasta que já foi apagada de verdade não é uma perda de dado visível, é limpeza de um artefato interno.
- **`HashProfilePath`** — `Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(profileDirectory))))`. `Path.GetFullPath` normaliza antes de fazer hash, pra que o mesmo perfil sempre resolva pro mesmo diretório de backup independente de como o caminho foi passado (barra no fim, etc.).
- **Sem `IDisposable`** — não segura nenhum recurso (sem conexão SQLite), então `DbxStore.Dispose()` não precisa mudar por causa dela.

## 4. Mudanças em `DbxStore.cs`

- Novo campo `private readonly FileBackupStore _backupStore;`, atribuído no construtor privado (mesmo padrão dos outros campos de cache).
- `DbxStore.Open` ganha um novo parâmetro opcional `string? backupRootDirectory = null`, no mesmo lugar/estilo dos outros quatro (`scavengeCacheDatabasePath` etc.): `var backupStore = new FileBackupStore(backupRootDirectory ?? FileBackupStore.DefaultRoot);`. Sem `OpenXxxWithFallback` — a construção em si nunca toca disco (só resolve o caminho), então não tem como falhar aqui; a falha só pode acontecer dentro de `BeforeRewrite`, na hora de uma reescrita de verdade.
- **`CreateFolderAsync`** (bloco atual nas linhas ~124-131 de `DbxStore.cs`): troca as 8 linhas de `.original`/`.bak` manual por uma chamada: `_backupStore.BeforeRewrite(_profileDirectory, "Folders.dbx");`.
- **`DeleteFolderAsync`** (bloco atual nas linhas ~223-226): mesma troca: `_backupStore.BeforeRewrite(_profileDirectory, "Folders.dbx");`.
- **`MoveFolderAsync`** (bloco atual nas linhas ~375-378): mesma troca: `_backupStore.BeforeRewrite(_profileDirectory, "Folders.dbx");`.
- **`AddMessageAsync`** (bloco atual nas linhas ~445-448): `_backupStore.BeforeRewrite(_profileDirectory, fileName);` (`fileName` já é o nome do `.dbx` daquela pasta, ex.: `"Notas.dbx"`).
- **`DeleteFolderAsync`'s loop de limpeza física** (bloco atual nas linhas ~251-276): hoje tenta apagar `fileName`, `fileName + ".bak"` e `fileName + ".original"` dentro da pasta do perfil (os dois últimos não existem mais lá depois desta fatia). Simplifica pra:
  ```csharp
  var failures = new List<string>();
  foreach (string fileName in fileNames)
  {
      _scavengeCache.ClearScan(_profileDirectory, fileName);
      _movedMessageCache.ClearFolder(_profileDirectory, fileName);
      _deletedMessageCache.ClearFolder(_profileDirectory, fileName);
      _backupStore.Delete(_profileDirectory, fileName);

      try
      {
          File.Delete(Path.Combine(_profileDirectory, fileName)); // no-op if it never existed
      }
      catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
      {
          failures.Add($"{fileName} ({ex.Message})");
      }
  }
  ```
  `_backupStore.Delete` nunca lança (best-effort interno, §3), então não entra na lista `failures` — só o arquivo `.dbx` real (o único que o usuário/Outlook Express veria) conta pra esse aviso.

## 5. Erros e casos de borda

- Falha ao criar o diretório de backup ou copiar `.bak`/`.original` (ex.: `%LOCALAPPDATA%` cheio, sem permissão) → propaga a exceção original de `Directory.CreateDirectory`/`File.Copy`, abortando `CreateFolderAsync`/`MoveFolderAsync`/`DeleteFolderAsync`/`AddMessageAsync` inteiro — igual ao comportamento atual (backup falhou = reescrita não acontece).
- Apagar uma pasta cujo backup já não existe (nunca foi reescrita, então nunca teve `.bak`/`.original`) → `FileBackupStore.Delete` não lança nem registra nada — `File.Delete` em cima de um caminho inexistente é um no-op silencioso.
- Dois perfis diferentes cujo hash colidisse (praticamente impossível com SHA-256, mas por completude): não tratado — mesma tolerância a colisão de hash que qualquer outro uso de hash criptográfico neste porte de projeto.
- Perfil aberto duas vezes ao mesmo tempo (duas instâncias de `DbxStore` pro mesmo diretório, cada uma com seu próprio `FileBackupStore` apontando pro mesmo diretório de backup real): mesma divergência de estado já documentada em `docs/.../scavengecache-per-store-divergence` pra outras caches — `.bak` da última reescrita "vence", `.original` continua estável (write-once). Não é uma regressão introduzida por esta fatia.

## 6. Estratégia de testes

- **`FileBackupStoreTests.cs`** (novo, testa a classe isoladamente, sem `DbxStore`):
  - `BeforeRewrite` cria o diretório de backup (dentro de um `root` temporário passado no construtor) se não existir.
  - `BeforeRewrite` escreve `<fileName>.original` só na primeira chamada (segunda chamada com conteúdo diferente não sobrescreve `.original`, mas atualiza `.bak`).
  - `BeforeRewrite` escreve `profile-path.txt` com o caminho completo do perfil.
  - Dois perfis diferentes (caminhos diferentes) resolvem pra diretórios de backup diferentes (hashes diferentes).
  - O mesmo perfil, chamado duas vezes (ex.: barra no fim vs. sem barra), resolve pro mesmo diretório de backup (normalização via `Path.GetFullPath`).
  - `Delete` remove `.bak`/`.original` existentes; chamado numa pasta que nunca teve backup não lança.
- **`DbxStoreCreateFolderTests.cs` / `DbxStoreMoveFolderTests.cs` / `DbxStoreDeleteFolderTests.cs` / `DbxStoreAddMessageTests.cs`**: todo teste que hoje verifica `File.Exists(Path.Combine(dir, "Folders.dbx.bak"))` (ou `.original`, ou o `.dbx` de uma pasta) passa a: (a) verificar que esses arquivos **não** existem mais em `dir` (a pasta do perfil), e (b) verificar que existem no novo diretório de backup, resolvido via um `backupRootDirectory` temporário passado explicitamente pra `DbxStore.Open` nesses testes (mesmo padrão já usado pelas outras caches nos testes existentes).
  - Isso inclui, por exemplo, `DeleteFolderAsyncRewritesFoldersDbxAndCreatesBackups`, `MoveFolderAsyncUpdatesTheBackupFile`, `CreateFolderAsyncKeepsThePristineBackupAcrossMultipleCreates`, e o teste de falha-de-`Folders.dbx`-danificado em cada um desses arquivos (que hoje afirma `Assert.False(File.Exists(...".bak"))` — continua valendo, só que agora aponta pro diretório de backup em vez da pasta do perfil).
- Novo teste em `DbxStoreDeleteFolderTests.cs`: apagar uma pasta de verdade também remove o `.bak`/`.original` dela do diretório de backup (não só o `.dbx` da pasta do perfil).

## 7. Fora de escopo / decisões adiadas

- Migrar/limpar `.bak`/`.original` órfãos já existentes dentro de pastas de perfil usadas em sessões anteriores a esta mudança — fica pra remoção manual.
- Apagar o diretório de backup de um perfil inteiro (a pasta com hash) quando fica vazio — o `profile-path.txt` e qualquer backup remanescente de pastas ainda existentes continuam lá; não é limpeza automática de diretório.
- Qualquer tela/comando de restaurar a partir de um `.bak`/`.original` — continuam sendo puramente um artefato de recuperação manual (fora do app), igual a hoje.
