# LEC — Local Email Client

**English** · [Português (Brasil)](README.pt-BR.md)

A Windows application for opening an Outlook Express profile (the folder that contains `Folders.dbx` and one `.dbx` file per folder), reading the messages, and reorganizing them.

LEC works on files that are already on disk. There are no accounts, no sending, and no sync with a server.

## What you can do

- Open one or more profiles at the same time and see their folders side by side.
- Read a message as HTML, with its attachments listed and a way to save each one.
- Search by sender, subject, or body.
- Sort the list by column.
- Flag duplicates inside a folder and delete them in one step.
- Move messages, including from one open profile to another, and delete messages.
- Create, move, and delete folders. Outlook Express special folders (Inbox, Sent Items, Drafts, and the rest) stay where they are.
- In a damaged folder, look for messages the index no longer lists.
- Compact one folder, or everything still pending, so the `.dbx` file stops storing what was deleted or moved.

When you close a profile or the program, LEC warns you if folders still need compacting.

The `LEC.Cli` command-line tool inspects a single `.dbx` file without opening the window:

```text
dotnet run --project src/LEC.Cli -- dump path\to\file.dbx
dotnet run --project src/LEC.Cli -- extract path\to\messages.dbx output-folder
```

`dump` lists folders or messages. `extract` writes each message as an `.eml` file.

## Requirements

- Windows 10 or 11, 64-bit.
- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) to build.
- [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/), already present on Windows 11, for the reading pane. Remote images in the HTML are loaded.

## Build and run

From the repository root:

```powershell
dotnet build src/LEC.App/LEC.App.csproj
dotnet run --project src/LEC.App
```

Open `src/LEC.App/LEC.App.csproj`.

In **File → Open Profile...** (**Arquivo → Abrir Perfil...**), choose the profile folder, not a single `.dbx` file.

## What changes on disk

Reading, searching, and saving an attachment do not change the profile. Creating, moving, or deleting a folder, moving or adding a message, and compacting rewrite the corresponding `.dbx` file.

Before a rewrite, LEC stores a copy under `%LOCALAPPDATA%\LEC\backups\`. The first state is saved as `file.original` and is never replaced. The copy from immediately before the rewrite is saved as `file.bak`.

Deleting or moving a message removes it from the list right away. The bytes leave the source `.dbx` only when that folder is compacted. Until then, the program remembers what was hidden in a local cache.

Keep your own copy of the profile folder before reorganizing an archive that has no other backup.

## Projects

| Folder              | Role                                           |
| ------------------- | ---------------------------------------------- |
| `src/LEC.App`       | WPF window                                     |
| `src/LEC.MailStore` | Profile, folders, messages, cache, and backups |
| `src/LEC.DbxFormat` | Reading and writing the `.dbx` format          |
| `src/LEC.Cli`       | `dump` and `extract`                           |
