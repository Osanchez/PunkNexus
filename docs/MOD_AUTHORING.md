# Publishing a mod to PUNK Nexus

The client is strict about this on purpose: it installs files into someone's game folder and gates
installs on an exact game-version match, so it refuses to act on metadata it cannot trust.

There are two things to get right — a `mod.json` in your repo, and one pull request here.

---

## The shape of it

```
  your repo                     PunkNexus repo                 the user's game
  ─────────                     ──────────────                 ───────────────
  mod.json  ◄──── manifestUrl ── manifest/mods.json
     │                                                     BepInEx/plugins/<Mod>/
     └──────────── packaged into your release zip ───────────► mod.json
```

**One document, three jobs.** The `mod.json` in your repo is the version the client reads to decide
whether an update exists. The same file goes inside your zip, and lands in the plugin folder, where
the client reads it back to know what is installed. You author it once; nothing has to be kept in
sync by hand, because nothing is written down twice.

That is also why the registry entry carries no version number. If it did, every release would need a
pull request here, and the day someone forgot, the client would confidently show the wrong version.

---

## 1. Add `mod.json` to your repo

At the root of your mod's folder, next to its project file:

```json
{
  "schemaVersion": 1,
  "id": "PunkScoreboard",
  "name": "Scoreboard",
  "author": "Trihardest",
  "version": "1.1.0",
  "gameVersion": "0.12.10",
  "description": "Hold Tab for a co-op scoreboard.",
  "category": "Co-op",
  "iconUrl": null,
  "homepage": "https://github.com/Osanchez/PunkMods",
  "tags": ["co-op", "hud"],
  "pluginFolder": "PunkScoreboard",
  "dependencies": [],
  "download": {
    "repo": "Osanchez/PunkMods",
    "assetPattern": "PunkScoreboard-v*.zip"
  },
  "sha256": null
}
```

| Field | Required | Notes |
|---|---|---|
| `id` | yes | Unique across the catalogue. Letters, digits, `.`, `_`, `-`. Never change it — it is the identity the client tracks installs by. |
| `version` | yes | Your mod's current version. **Bump it in the same commit that builds the release.** |
| `gameVersion` | yes | The game version you built against, e.g. `0.12.10`. Matched **exactly**. |
| `name` | yes | Display name. |
| `pluginFolder` | no | Folder under `BepInEx/plugins/`. Defaults to `id`. Must be a single folder name. |
| `dependencies` | no | Mod **ids**. The client installs these first, and refuses if one of them is incompatible. |
| `download` | yes | Either `{"url": "https://…"}` or `{"repo": "owner/name", "assetPattern": "Mod-v*.zip"}`. |
| `sha256` | no | Checked against the download when present. |

### `assetPattern`, and why it exists

Release assets usually embed a version in the filename, so a fixed URL 404s the moment you publish
a new build. The pattern is matched against the latest release's assets, and the text the `*`
matches is read as the version — so `PunkScoreboard-v1.1.0.zip` also tells the client the download
really is 1.1.0, independently of what your manifest claims.

Use exactly one `*`, and place it where the version goes.

---

## 2. Ship `mod.json` inside your zip

Your release zip must extract to `BepInEx/plugins/<pluginFolder>/`, and `mod.json` has to be in
there beside the DLL:

```
BepInEx/
└─ plugins/
   └─ PunkScoreboard/
      ├─ PunkScoreboard.dll
      └─ mod.json          ← required
```

Without it the client cannot tell which version is installed. It will write one from your published
manifest so the user is not stuck, and log a warning — but that is a repair, not the contract. Ship
the file.

---

## 3. Open a pull request adding your registry entry

Add one object to the `mods` array in [`manifest/mods.json`](../manifest/mods.json):

```json
{
  "id": "PunkScoreboard",
  "manifestUrl": "https://raw.githubusercontent.com/Osanchez/PunkMods/main/PunkScoreboard/mod.json",
  "name": "Scoreboard",
  "author": "Trihardest",
  "category": "Co-op",
  "description": "Hold Tab for a co-op scoreboard.",
  "iconUrl": null,
  "homepage": "https://github.com/Osanchez/PunkMods",
  "tags": ["co-op", "hud"],
  "enabled": true
}
```

`manifestUrl` must be an **https raw** link to the `mod.json` on your default branch. Everything
else here is presentation, and the live manifest overrides it once fetched.

**This is the only pull request you ever open.** Releasing new versions, changing your description,
supporting a new game version — all of that happens in your own `mod.json`.

CI validates the pull request: ids unique and well-formed, `manifestUrl` reachable over https, the
fetched manifest's `id` matching your entry, a `version`, a `gameVersion`, a resolvable `download`,
a safe `pluginFolder`, and every dependency present in the registry. Run it yourself first:

```bash
python3 tools/validate-manifest.py --offline   # structure only
python3 tools/validate-manifest.py             # also fetches every mod manifest
```

---

## Game-version compatibility

`gameVersion` is compared for **exact equality** against the version the client reads out of the
user's own install (`Punk_Data/globalgamemanagers`), not against anything the catalogue claims.

| Situation | What the user sees |
|---|---|
| Exact match | Installs normally. |
| Different version | **Install blocked**, red badge, "Built for game X, but yours is Y." |
| No `gameVersion` | **Install blocked**, "does not declare which game version it was built for". |
| Client cannot read the game's version | Nothing blocked — an amber note says compatibility was not checked. |

Two consequences worth planning around:

**A game update makes your mod uninstallable until you publish a build for it.** That is the
intended behaviour of exact matching — it trades convenience for never installing a mod into a game
it was not built against. When the game updates, rebuild, bump `gameVersion`, release.

**Users who already installed you keep the mod**, and see an amber *outdated* badge instead: the
client compares the `gameVersion` in their installed `mod.json` against their current game. They can
still remove it from the list, which is why installed mods are never filtered out.

---

## Checklist

- [ ] `mod.json` in the repo, with `id`, `version`, `gameVersion`, `download`
- [ ] `version` bumped in the same commit as the release build
- [ ] `gameVersion` matches the build you compiled against
- [ ] Release zip extracts to `BepInEx/plugins/<pluginFolder>/` and contains `mod.json`
- [ ] `assetPattern` matches the published asset name, with one `*` at the version
- [ ] Registry entry added here, with an https raw `manifestUrl`
- [ ] `python3 tools/validate-manifest.py` passes
