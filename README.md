# Syncly

A block-based notes app for **Linux**, **Windows**, and **Android** that syncs directly between your
own devices. No account, no server, no cloud: two devices on the same network find each other, prove
who they are with a 6-digit code, and merge their edits character by character.

```
Discover → Pair (6 digits) → Encrypted session → Anti-entropy sync → Live streaming
```

## What it is

Pages are trees of blocks — paragraphs, headings, bullets, to-dos, quotes, code, dividers — the way
Obsidian and Anytype work. Pages nest, `[[wikilinks]]` connect them, and every page shows what links
back to it.

The interesting part is underneath. Syncly does not last-write-wins your notes. Every edit is a small
operation in an append-only log, and the log is a CRDT, so two devices editing the same paragraph
while offline both keep their words when they meet again.

## How sync works

Each device keeps a version vector: the highest operation sequence it has seen from every device.
When two devices connect they trade vectors, and each sends only what the other is missing.

- A device that was offline for months catches up in a single pass.
- Convergence is transitive, so three devices reconcile with no device acting as a hub.
- The connection stays open after the first pass, so edits appear as you type them.
- Batches are chunked, Brotli-compressed, and acked before peer state advances, so a dropped
  connection resumes instead of restarting.
- Tombstones are collected only once every paired device has acknowledged past them.

Pairing is ECDH over P-256 with the signature covering the whole handshake transcript, then AES-GCM
with strictly increasing counters. The 6-digit code is derived from that transcript, so if the digits
match on both screens there is no one in the middle.

## Layout

```
src/
  Syncly.Crdt/                 HLC, op log, RGA text, fractional-index block tree, version vectors
  Syncly.Model/                Blocks, pages, peers, wikilinks
  Syncly.Storage/              SQLite: ops, projections, snapshots, links, FTS5 search
  Syncly.Security/             Device identity, handshake, secure channel
  Syncly.Sync/                 Protocol, sessions, anti-entropy engine
  Syncly.Transport.Lan/        UDP discovery + framed TCP
  Syncly.Transport.WifiDirect/ Platform stubs
  Syncly.App/                  Workspace commands, inline markup, composition root
  Syncly.UI/                   Blazor components (shared by every host)
  Syncly.Desktop/              Photino host — Linux and Windows
  Syncly.Mobile/               MAUI host — Android (needs the MAUI workload, so it is not in the solution)
tests/
  Syncly.Crdt.Tests/           Randomized convergence and fuzz
  Syncly.Sync.Tests/           Multi-device partition simulation
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

Two instances on one machine, to watch sync happen:

```bash
SYNCLY_DATA_DIR=.local/a SYNCLY_DEVICE_NAME="Peer A" SYNCLY_PORT=45678 dotnet run --project src/Syncly.Desktop &
SYNCLY_DATA_DIR=.local/b SYNCLY_DEVICE_NAME="Peer B" SYNCLY_PORT=45688 dotnet run --project src/Syncly.Desktop
```

VS Code has the same thing as the **Syncly (both peers)** compound launch configuration.

| Variable | Meaning |
|----------|---------|
| `SYNCLY_DATA_DIR` | Where `syncly.db` lives (default: local app data) |
| `SYNCLY_DEVICE_NAME` | Name other devices see (default: machine name) |
| `SYNCLY_PORT` | TCP listen port (default: 45654; discovery beacons use 45655) |

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

Graph view, sync over the internet, real Wi-Fi Direct connection setup, encryption at rest, and file
attachments.
