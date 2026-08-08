# PUNK Nexus

Mod browser and installer for **PUNK** (the Steam Playtest build). A single Windows executable that
finds your game install, sets up BepInEx, and installs, updates and removes mods from a published
catalog.

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
- **Checks every download before extracting it.** The publisher's `sha256` and the `mod.json`
  packaged inside the archive are both verified, and the result is shown before a single file is
  written. A file that fails is never installed.
- **Server browser** with filters for name, address, players, game mode and installed mods. The feed
  is empty until the relay described in [`docs/SERVER_LIST.md`](docs/SERVER_LIST.md) exists.

## First run

1. Run `PunkNexus.exe`. It opens with a risk disclaimer that has to be accepted before anything
   else — mods are third-party code and installing them is at your own risk.
2. It scans for your install. Confirm the one it found, or browse to the folder containing
   `Punk.exe` (Steam → PUNK Playtest → Manage → Browse local files).
3. On the Mods tab, click **Install BepInEx**, then launch the game once and quit.
4. Install the mods you want. **Mods Menu** is the one most others plug their toggles into.

The game folder can be changed later under **Settings → Game folder**. If the install is moved,
deleted or verified away by Steam, the client notices and drops back to the setup screen rather than
writing into a folder that is no longer there.

## The catalog

Three tiers, and the split is the whole design. Full contract in
[`docs/MOD_AUTHORING.md`](docs/MOD_AUTHORING.md).

```
  developer's repo              this repo                      user's game
  ────────────────              ─────────                      ───────────
  mod.json  ◄──── manifestUrl ── manifest/mods.json
     │                                                     BepInEx/plugins/<Mod>/
     └──────────── packaged into the release zip ─────────────► mod.json
```

1. **The registry** — [`manifest/mods.json`](manifest/mods.json). A list of pointers: id,
   `manifestUrl`, and presentation. Developers open **one** pull request here, ever.
2. **The mod manifest** — `mod.json` in the developer's own repo, at that `manifestUrl`. Owns the
   `version`, the `gameVersion` it was built for, and the download.
3. **The installed manifest** — the same `mod.json`, shipped inside the zip, landing in the plugin
   folder. Read back to know exactly what is installed.

**The registry deliberately carries no version number.** If it did, every release would need a pull
request here, and the day someone forgot, the client would confidently show the wrong version. One
document, authored once by the developer, serves as the published truth and the installed record.

**Nothing here requires GitHub.** `manifestUrl` is any https URL, and a mod's `download` is either
a static URL on any host, or — as a convenience — a repo plus an asset glob, since release assets
embed their version in the filename and a pinned URL would 404 on the next bump:

```json
"download": { "url": "https://cdn.example.com/mods/MyMod-2.0.0.zip" }
"download": { "repo": "Osanchez/PunkMods", "assetPattern": "PunkScoreboard-v*.zip" }
```

The displayed version always comes from the manifest's `version` field either way; the two forms
differ only in how the download is located. Self-hosted mods should set `sha256`, which the client
verifies before extracting anything.

If the registry cannot be fetched the client falls back to its disk cache, then to a copy compiled
into the executable, so it always opens to a usable window.

Every pull request touching `manifest/` is validated by CI — ids unique, `manifestUrl` reachable,
the fetched manifest agreeing with its listing, dependencies resolvable. Run it locally with
`python3 tools/validate-manifest.py`.

## Game-version compatibility

The client reads the game's version out of `Punk_Data/globalgamemanagers` in the user's own install
— never from the catalog — and matches it **exactly** against each mod's declared `gameVersion`.

| Situation | Result |
|---|---|
| Exact match | Installs normally |
| Different version | Blocked, with the reason shown on the row |
| Mod declares no `gameVersion` | Blocked — a packaging error, treated as one |
| Version could not be read | Nothing blocked; an amber note says it was not checked |

The asymmetry in the last two rows is deliberate. A mod that declares nothing is the author's
failure and is blocked. The client failing to fingerprint an install is *our* failure, and is not
evidence against the mod, so it gates nothing.

A game update therefore makes mods uninstallable until their authors publish a build for it — the
cost of never installing a mod into a game it was not built against. Already-installed mods stay
put and get an amber *outdated* badge instead, and are never filtered out of the list, so they can
still be removed.

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
| `src/PunkNexus/Themes/` | The PUNK theme — colors and control styles |
| `manifest/` | The published mod and server catalogs |
| `tools/` | `validate-manifest.py`, the catalog checker CI runs |
| `docs/` | [Mod authoring contract](docs/MOD_AUTHORING.md), [server list design](docs/SERVER_LIST.md) |

## Where it keeps things

`%LOCALAPPDATA%\PunkNexus\` holds `settings.json`, the cached catalog, downloaded icons, the
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
