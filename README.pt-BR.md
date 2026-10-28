# LEC — Local Email Client

[English](README.md) · **Português (Brasil)**

Aplicação de Windows para abrir um perfil do Outlook Express (a pasta que contém `Folders.dbx` e os `.dbx` de cada pasta), ler as mensagens e reorganizá-las.

O LEC trabalha nos arquivos que já estão no disco. Não há contas, envio nem sincronização com servidor.

## O que dá para fazer

- Abrir um ou mais perfis ao mesmo tempo e ver as pastas lado a lado.
- Ler a mensagem em HTML, com anexos listados e opção de salvar cada um.
- Buscar por remetente, assunto ou conteúdo.
- Ordenar a lista por coluna.
- Marcar duplicatas dentro da pasta e apagá-las de uma vez.
- Mover mensagens, inclusive de um perfil aberto para outro, e apagar mensagens.
- Criar, mover e apagar pastas. As pastas especiais do Outlook Express (caixa de entrada, enviados, rascunhos e as demais) ficam no lugar.
- Em uma pasta danificada, procurar mensagens que o índice não lista mais.
- Compactar uma pasta, ou tudo que está pendente, para o `.dbx` deixar de guardar o que foi apagado ou movido.

Ao fechar um perfil ou o programa, o LEC avisa se ainda há pastas para compactar.

A linha de comando `LEC.Cli` inspeciona um `.dbx` solto, sem abrir a janela:

```text
dotnet run --project src/LEC.Cli -- dump caminho\arquivo.dbx
dotnet run --project src/LEC.Cli -- extract caminho\mensagens.dbx pasta-de-saida
```

`dump` lista pastas ou mensagens. `extract` grava cada mensagem como `.eml`.

## Requisitos

- Windows 10 ou 11, 64 bits.
- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) para compilar.
- [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/), já presente no Windows 11, para o painel de leitura. Imagens remotas do HTML são carregadas.

## Compilar e abrir

Na raiz do repositório:

```powershell
dotnet build src/LEC.App/LEC.App.csproj
dotnet run --project src/LEC.App
```

Abra `src/LEC.App/LEC.App.csproj`.

Em **Arquivo → Abrir Perfil...**, escolha a pasta do perfil, não um `.dbx` isolado.

## O que muda no disco

Ler, buscar e salvar anexo não alteram o perfil. Criar, mover ou apagar pasta, mover ou acrescentar mensagem e compactar reescrevem o `.dbx` correspondente.

Antes de reescrever, o LEC guarda cópia em `%LOCALAPPDATA%\LEC\backups\`. O primeiro estado vai para `arquivo.original` e não é substituído. A cópia imediatamente anterior à reescrita vai para `arquivo.bak`.

Apagar ou mover uma mensagem some com ela da lista na hora. Os bytes só deixam o `.dbx` de origem na compactação. Até lá, o programa lembra o que foi escondido num cache local.

Faça uma cópia da pasta do perfil antes de reorganizar um arquivo que não tenha outro backup.

## Projetos

| Pasta               | Papel                                      |
| ------------------- | ------------------------------------------ |
| `src/LEC.App`       | Janela WPF                                 |
| `src/LEC.MailStore` | Perfil, pastas, mensagens, cache e backups |
| `src/LEC.DbxFormat` | Leitura e escrita do formato `.dbx`        |
| `src/LEC.Cli`       | `dump` e `extract`                         |
