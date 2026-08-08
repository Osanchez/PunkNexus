# PUNK Nexus

Mod browser and installer for **PUNK** (the Steam Playtest build). A single Windows executable that
finds your game install, sets up BepInEx, and installs, updates and removes mods from a published
catalogue.

> No installer, no .NET runtime to install, nothing to configure. Download `PunkNexus.exe` from the
> [latest release](https://github.com/Osanchez/PunkNexus/releases/latest) and run it.

## What it does

- **Finds your install.** Reads the Steam registry keys and every `libraryfolders.vdf` library, then
  the `appmanifest_2850470.acf` install dir. If that comes up empty, you pick the folder yourself.
- **Verifies before it writes.** A folder is only accepted when `Punk.exe` and `Punk_Data\` are
  present *and* the folder is actually writable (probed, not assumed). Each check is shown
  individually so a near miss is obvious.
- **Installs BepInEx** once, from the loader zip published by `Osanchez/PunkMods`.
- **Installs, updates and removes mods**, recording every file it writes so an uninstall removes
  exactly what was added.
- **Pulls in dependencies.** A mod whose manifest entry declares `dependencies` installs those
  first, so you never end up with a mod that loads and silently does nothing because its framework
  is missing.
- **Server browser** with filters for name, address, players, game mode and installed mods. The feed
  is empty until the relay described in [`docs/SERVER_LIST.md`](docs/SERVER_LIST.md) exists.

## First run

1. Run `PunkNexus.exe`.
2. It scans for your install. Confirm the one it found, or browse to the folder containing
   `Punk.exe` (Steam → PUNK Playtest → Manage → Browse local files).
3. On the Mods tab, click **Install BepInEx**, then launch the game once and quit.
4. Install the mods you want. **Mods Menu** is the one most others plug their toggles into.

The game folder can be changed later under **Settings → Game folder**. If the install is moved,
deleted or verified away by Steam, the client notices and drops back to the setup screen rather than
writing into a folder that is no longer there.

## The catalogue

Both lists are plain JSON in this repo, read at runtime over `raw.githubusercontent.com`:

| File | Contents |
|---|---|
| [`manifest/mods.json`](manifest/mods.json) | The mod catalogue and the BepInEx loader entry |
| [`manifest/servers.json`](manifest/servers.json) | The server list (empty for now) |

**Adding or updating a mod needs no new build of the client** — edit the manifest, push, done. The
client picks it up on its next refresh.

Mod downloads are resolved by pattern rather than by fixed URL:

```json
"source": { "repo": "Osanchez/PunkMods", "assetPattern": "PunkScoreboard-v*.zip" }
```

Release assets are named `<Mod>-v<version>.zip`, so a fixed URL would 404 the moment a mod is
rebuilt. Matching the glob against the latest release's assets keeps the manifest correct across
version bumps, and the matched filename is where the client gets the true current version — the
`version` field in the manifest is only a fallback for display before that lookup lands.

If the manifest cannot be fetched, the client falls back to the last copy it cached, and then to a
copy compiled into the executable, so it always opens to a usable window.

## Build

```bash
dotnet build src/PunkNexus/PunkNexus.csproj

dotnet publish src/PunkNexus/PunkNexus.csproj -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

**Every push builds the Windows exe** ([`.github/workflows/build.yml`](.github/workflows/build.yml)).
Branch pushes leave it as a downloadable workflow artifact; pushes to `main` also publish a Release
tagged `vYYYY.MM.DD.<run-number>`.

Built on [Avalonia](https://avaloniaui.net/) targeting `net8.0`, which keeps the project compiling
and runnable on Linux CI and dev machines while still shipping a native Windows executable. Trimming
is deliberately off — Avalonia resolves XAML types by name at runtime and a trimmed build dies on
first navigation.

## Layout

| Path | Contents |
|---|---|
| `src/PunkNexus/Models/` | Manifest and local-state DTOs |
| `src/PunkNexus/Services/` | Install detection, download, extraction, manifests, settings |
| `src/PunkNexus/ViewModels/` | One per screen, plus the shared `GameSession` |
| `src/PunkNexus/Views/` | Avalonia XAML |
| `src/PunkNexus/Themes/` | The PUNK theme — colours and control styles |
| `manifest/` | The published mod and server catalogues |
| `docs/` | Design notes |

## Where it keeps things

`%LOCALAPPDATA%\PunkNexus\` holds `settings.json`, the cached catalogue, downloaded icons, the
per-install file records under `installs\`, and `punknexus.log`. Nothing in there affects the game;
deleting it resets the client to a first run. **Settings → Maintenance** opens it.

## Safety notes

- Archives are validated entry by entry before a single byte is written, so an entry pointing
  outside the game folder is rejected rather than followed.
- The client refuses to install or remove while `Punk.exe` is running, since the game holds its
  DLLs open.
- It never elevates itself. If the game folder is not writable it says so and asks you to relaunch
  as administrator.
- `sha256` is honoured for any manifest entry that sets it; entries without one are not verified
  beyond the transport.
