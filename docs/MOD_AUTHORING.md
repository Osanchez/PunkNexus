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
| `id` | yes | Unique across the catalog. Letters, digits, `.`, `_`, `-`. Never change it — it is the identity the client tracks installs by. |
| `version` | yes | Your mod's current version. **Bump it in the same commit that builds the release.** |
| `gameVersion` | yes | The game version you built against, e.g. `0.12.10`. Matched **exactly**. |
| `name` | yes | Display name. |
| `pluginFolder` | no | Folder under `BepInEx/plugins/`. Defaults to `id`. Must be a single folder name. |
| `dependencies` | no | Mod **ids**. The client installs these first, and refuses if one of them is incompatible. |
| `download` | yes | Either `{"url": "https://…"}` or `{"repo": "owner/name", "assetPattern": "Mod-v*.zip"}`. |
| `sha256` | no | Checked against the download when present. |

### Hosting your download anywhere

`download` has two forms. Nothing in the client requires GitHub.

**Any host** — a static URL:

```json
"download": { "url": "https://cdn.example.com/mods/MyMod-2.0.0.zip" },
"sha256": "9F2C…"
```

**GitHub releases** — a convenience, so you do not have to edit the URL on every release:

```json
"download": { "repo": "Osanchez/PunkMods", "assetPattern": "PunkScoreboard-v*.zip" }
```

Release assets usually embed a version in the filename, so a fixed URL 404s the moment you publish
a new build. The pattern is matched against the latest release's assets. Use exactly one `*`, and
put it where the version goes.

The version the client shows always comes from the `version` field in this manifest, either way —
the two forms differ only in how the download is located, never in how the version is known.

`manifestUrl` in the registry is likewise just an https URL. `raw.githubusercontent.com` is one
option; your own domain works identically.

> **If you host outside a release page, set `sha256`.** GitHub release assets are effectively
> immutable once published; a file on your own server is not, and neither is anything in front of a
> cache you do not control. With a hash present the client verifies the download and refuses to
> install a byte that does not match. Everything the registry lists must be served over **https** —
> the validator rejects `http://` for both `manifestUrl` and `download.url`.

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
user's own install (`Punk_Data/globalgamemanagers`), not against anything the catalog claims.

| Situation | What the user sees |
|---|---|
| Exact match | Installs normally, no badge. |
| Different version | Installs, amber badge, "Built for game X, but yours is Y." |
| No `gameVersion` | Installs, amber badge — it does not say what it was built for. |
| Client cannot read the game's version | Installs, amber note saying compatibility was not checked. |

**A mismatch warns; it does not block.** It was a refusal once, and the game moving 0.12.10 →
0.12.11 made all sixteen listed mods uninstallable in a single afternoon, most of them untouched by
the change. Exact matching is a good detector and a bad gate, so the badge is the whole consequence.

One consequence worth planning around:

**A game update makes your mod look stale until you publish a build for it.** Users can still
install it, over an amber badge naming the mismatch. When the game updates, rebuild, bump
`gameVersion`, release — and the badge clears itself.

**Users who already installed you keep the mod**, and see an amber *outdated* badge instead: the
client compares the `gameVersion` in their installed `mod.json` against their current game. They can
still remove it from the list, which is why installed mods are never filtered out.

---

## Virus scanning

Once you are listed, your release zip is scanned with VirusTotal automatically. Nothing is asked of
you: a scheduled job resolves your `download`, fetches the bytes, hashes them itself, and files a
report in [`reports/`](../reports/) under that hash. A new build is picked up because its hash
changed, so releasing is all you have to do.

**Expect some detections, and do not panic about them.** Your mod is an unsigned assembly whose
whole job is patching a running process, which is what behavior-based engines are built to catch. A
few hits out of ~70 engines is the normal result for a legitimate mod here.

The client is designed around that fact. It never shows a detection count as a verdict, never puts
one on your row in the list, never colors it as a failure, and **never blocks an install on a scan
result** — see [VIRUS_SCANNING.md](VIRUS_SCANNING.md) for the rules it follows. The only thing that
blocks an install is a `sha256` mismatch, which is an integrity failure rather than a judgement.

Between your release and the next scheduled scan your newest build shows as unscanned. That is
normal and is stated as such.

---

## Checklist

- [ ] `mod.json` in the repo, with `id`, `version`, `gameVersion`, `download`
- [ ] `version` bumped in the same commit as the release build
- [ ] `gameVersion` matches the build you compiled against
- [ ] Release zip extracts to `BepInEx/plugins/<pluginFolder>/` and contains `mod.json`
- [ ] `assetPattern` matches the published asset name, with one `*` at the version
- [ ] Registry entry added here, with an https raw `manifestUrl`
- [ ] `python3 tools/validate-manifest.py` passes
