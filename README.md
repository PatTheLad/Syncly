# Syncly

A block-based notes app for **Linux**, **Windows**, and **Android**. Each device keeps a local
CRDT of your pages. Devices sync through a mailbox you pick in Settings: a folder on disk, a
network share, or a Proton Drive folder opened from an Editor share link. The mailbox only ever
sees ciphertext.

```
Edit locally → encrypt with the chain code → write {deviceId}.syncly → other devices pull and merge
```

## What it is

Pages are trees of blocks — paragraphs, headings, bullets, to-dos, quotes, code, dividers — the way
Obsidian and Anytype work. Pages nest, `[[wikilinks]]` connect them, and every page shows what links
back to it.

Syncly does not last-write-wins your notes. Every edit is a small operation in an append-only log,
and the log is a CRDT, so two devices editing the same paragraph while offline both keep their
words when they next sync.

## How sync works

Pick a mailbox and a **chain code** in Settings.

- **Local folder** — a directory both devices can see (USB, NAS, shared disk).
- **Proton Drive** — paste a public folder link with **Editor** access, including the `#password`. If the share also has an extra password, enter that in Settings.
- **Chain code** — 24 BIP39 words (or a QR / `syncly:sync:v1:…` URI), like Brave Sync. It is the
  AES-256-GCM key for every blob. Anyone with the words can read the mailbox; Proton cannot.
  With a mailbox saved, **Show QR** encodes an invite (`syncly:invite:v1:…`) that also carries the
  Proton link and share password so a phone can scan once after install. **Leave chain** drops the
  local chain so you can join another without wiping notes.

Each device writes `{deviceId}.syncly` and a plaintext `chain.json` that only stores a hash of the
secret, so a device pointed at the wrong folder notices before it tries to decrypt. Sync is a
push of this device's ops plus a pull of everyone else's. The local SQLite database stays the
source of truth.

## Layout

```
src/
  Syncly.Crdt/                 HLC, op log, RGA text, fractional-index block tree, version vectors
  Syncly.Model/                Blocks, pages, sync settings, wikilinks
  Syncly.Storage/              SQLite: ops, projections, snapshots, links, FTS5 search
  Syncly.Security/             Device identity, BIP39 chain, blob encryption
  Syncly.Sync/                 Mailbox engine, local folder backend
  Syncly.Backend.ProtonDrive/  Proton Drive public-link mailbox
  Syncly.App/                  Workspace commands, inline markup, composition root
  Syncly.UI/                   Blazor components (shared by every host)
  Syncly.Desktop/              Photino host — Linux and Windows
  Syncly.Mobile/               MAUI host — Android (needs the MAUI workload, so it is not in the solution)
tests/
  Syncly.Crdt.Tests/           Randomized convergence and fuzz
  Syncly.Sync.Tests/           Chain, encryption, two-device folder sync
  Syncly.Storage.Tests/
```

## Running it

```bash
dotnet build Syncly.slnx
dotnet test  Syncly.slnx

# Desktop (Linux/Windows)
dotnet run --project src/Syncly.Desktop

# Android
dotnet build src/Syncly.Mobile -t:Run -f:net10.0-android
```

Two instances on one machine, sharing a folder:

```bash
SYNCLY_DATA_DIR=.local/a SYNCLY_DEVICE_NAME="Peer A" dotnet run --project src/Syncly.Desktop &
SYNCLY_DATA_DIR=.local/b SYNCLY_DEVICE_NAME="Peer B" dotnet run --project src/Syncly.Desktop
```

Then in Settings on both: the same folder as the mailbox, and the same 24-word chain.

VS Code has the same thing as the **Syncly (both peers)** compound launch configuration.

| Variable | Meaning |
|----------|---------|
| `SYNCLY_DATA_DIR` | Where `syncly.db` lives (default: local app data) |
| `SYNCLY_DEVICE_NAME` | Name written into this device's mailbox pack (default: machine name) |

## Installers and updates

Every push to `main` publishes a GitHub Release (`v2.0.<build>`) with:

- **Windows** — Velopack setup (`Syncly-win-Setup.exe`)
- **Linux** — Velopack setup for x64
- **Android** — `Syncly-android.apk`

Installed copies check that feed on startup. When a newer release exists, the shell and Settings show an **Update** button. On desktop the download is silent and Syncly restarts into the new version. On Android the APK downloads silently, then Android's package installer asks once to replace the app (sideloaded APKs cannot skip that system prompt).

## Editing

| Key | Does |
|-----|------|
| `Ctrl`+`K` | Command palette: jump to a page, new page, sync now |
| `Ctrl`+`E` | Reading mode — pages open read-only until you ask to edit |
| `Ctrl`+`N` / `Ctrl`+`S` | New page / sync now |
| `/` | Block menu on an empty block |
| `# `, `- `, `1. `, `[] `, `> `, `--- ` | Turn the block into that type as you type |
| `Enter` / `Backspace` | Split a block / merge it into the one above |
| `Tab` / `Shift`+`Tab` | Indent / outdent |
| `Alt`+`↑` / `Alt`+`↓` | Move a block |
| `Ctrl`+`B` / `Ctrl`+`I` / `Ctrl`+`U` | Bold, italic, underline |

Marks are stored in the text itself (`**bold**`, `__underline__`), so two people formatting
overlapping words merge as text instead of fighting over a range.

## Upgrading from v1

The first launch after the rewrite reads the old `notes` table, turns each note into a page whose
body lines become blocks, and leaves the old table untouched as a backup.

## Not in this pass

Graph view, extra cloud providers beyond Proton Drive, encryption at rest of the local database,
and file attachments.
