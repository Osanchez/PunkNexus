# Virus scanning

Every downloadable artifact in the catalog is scanned with VirusTotal on a schedule, and the result
is published in [`reports/`](../reports/) so the client can show it. This document is the design of
record: what is scanned, how a report is tied to a file, when a scan runs, and — the part that
matters most — how the results are allowed to be presented.

---

## The problem this solves, and the one it does not

The catalog already carries a developer-declared `sha256` in each mod's `mod.json`. That is a real
protection: it catches a corrupted download and a file swapped at the URL after publication.

It is also authored by the same person who ships the zip. It proves the bytes you received are the
bytes they intended to send. It cannot say anything about whether those bytes were ever looked at,
and a developer who wanted to ship something malicious would simply publish the matching hash.

Scanning closes that second gap a little, and only a little. It does not make mods safe, and the
client says so on first launch. What it adds is one piece of independent evidence, computed by
something other than the mod's author, about the exact file you are about to install.

---

## The scanner never trusts the declared hash

`tools/virus-scan.py` resolves every registry entry to its real download, fetches the bytes, and
computes the sha256 itself. **That** hash is the report's identity, and it is what change detection
and the client's install-time check both compare against.

The declared hash is recorded alongside it as a cross-check (`declaredMatches`). A mismatch means
the catalog is promising users a file that is not the one being served, which is worth surfacing
either way — the scanner warns, and the client's existing checksum gate would block such an install
regardless.

## What gets scanned: the zip, not the DLLs

The downloadable zip is what the user receives and what the client can hash at install time. Only by
scanning that exact object can the report and the artifact be provably the same thing, which is the
whole requirement. Per-DLL detail would be a useful addition later; it can never be a replacement,
because a report about a DLL cannot be matched against a download.

---

## Report schema

Two kinds of file, both under `reports/`.

### `reports/index.json` — what the client fetches

One request for the whole catalog. The client refreshes the mod list as a unit, and one conditional
GET per mod to render a badge would be a poor trade.

```jsonc
{
  "schemaVersion": 1,
  "generatedUtc": "2026-08-09T16:41:02Z",
  "complete": true,              // false when a run ran out of quota before finishing
  "unreached": [],               // ids that needed a scan this run and did not get one
  "scans": [
    {
      "modId": "PunkScoreboard",
      "modName": "Scoreboard",
      "sha256": "…",             // the SCANNER's hash of the file. The report's identity.
      "fileName": "PunkScoreboard-v1.1.0.zip",
      "sizeBytes": 10623,
      "modVersion": "1.1.0",
      "declaredMatches": true,   // did the author's own checksum agree
      "scannedUtc": "…",         // when this tool recorded the result
      "analyzedUtc": "…",        // when VirusTotal last analyzed the file
      "status": "completed",     // completed | queued | error
      "enginesTotal": 62,        // engines that returned a verdict
      "detections": 0,
      "detectedBy": ["Engine: verdict", …],
      "permalink": "https://www.virustotal.com/gui/file/…",
      "reportPath": "reports/PunkScoreboard.json"
    }
  ]
}
```

`enginesTotal` counts engines that actually returned a verdict — malicious, suspicious, undetected
and harmless. VirusTotal's raw stats also carry `type-unsupported`, `timeout` and `failure` buckets;
folding those into the denominator would quietly inflate it, and *4/70* reads very differently from
*4/62* to somebody deciding whether to install something.

### `reports/<modId>.json` — the history

The same objects, newest first, capped at ten, plus the fields the client has no use for
(`downloadUrl`, `declaredSha256`, `analysisId`). These exist for humans reading the repo and for
change detection; the client only ever reads the index.

BepInEx has a report too, filed under the id `BepInEx`. The client downloads and installs the loader
exactly like a mod, so leaving it unscanned would put the gap in the one place a user has no choice
but to accept.

---

## Change detection

An entry is sent to VirusTotal only when the hash the scanner just computed differs from the hash in
the newest stored report, or when that report never completed. Everything else — the whole catalog,
on a typical day — costs one download and **zero** API requests.

Within a scan, a hash lookup comes before any upload:

1. `GET /api/v3/files/{sha256}` returns an existing report for free if VirusTotal has ever seen the
   file. Public GitHub release assets frequently have been.
2. Only on a 404 does the tool `POST /api/v3/files`, then poll `GET /api/v3/analyses/{id}` until the
   analysis completes.

## Rate limits, and the arithmetic behind the schedule

The free tier allows **4 lookups/minute, 500/day, 15,500/month**.

The per-minute figure is the pacing constraint. The tool sleeps to respect it rather than firing and
retrying on 429s — a scheduled job has time, and a 429 halfway through leaves a catalog half
scanned.

Measured on the first two real runs:

| Run | Requests | Wall time |
|---|---|---|
| Cold catalog, 17 artifacts, none known to VirusTotal | 81 | ~20 min |
| Next run, nothing changed | 1 | ~1 min |

An unchanged mod costs **zero** VirusTotal requests: the hash comparison happens before any API
call, and needs only the download, which comes from GitHub. Steady state is free. The budget is
only ever spent on the day a developer ships something:

```
worst realistic day: all 16 mods release at once
16 x (1 lookup + 1 upload + up to 8 polls)  =  ~160 requests    against 500/day
```

which fits, at about a 40-minute job. **Daily is chosen because a mod release is a daily-scale
event, not because the quota forces it** — scanning more often would spend the same quota to learn
the same thing and re-download every zip to do it. Redo that multiplication before changing the
cron: what can bite is a release-day burst, not the steady state.

Polling is bounded to 8 polls per uploaded file for the same reason: polls come out of the same
budget, and one slow analysis left to run for a full five-minute timeout could spend the day's quota
watching a single file. A file still queued when the polls run out is recorded as `queued`, and the
**next** run resolves it with a single free hash lookup rather than uploading it again — which is
exactly what happened to `PunkReviveItem` between the two runs above.

## Running out mid-run

Reports already written are kept — they are real results. The run stops, names every entry it did
not reach, stamps `complete: false` and `unreached` into the index, and exits non-zero so the failed
pass is visible rather than looking like a clean sweep. The CI job commits what it produced anyway
(`if: always()`) and stays red.

---

## How results may be presented

This is the part to read before changing any UI.

BepInEx mods are unsigned .NET assemblies whose entire purpose is to patch a running process.
Behavior-based antivirus engines are built to catch exactly that. **A handful of detections out of
~70 engines is the ordinary result for honest work in this community.**

On the first real pass of this catalog every mod came back **0 detections out of 66–67 engines** —
so the risk did not materialize here, on this day, for these builds. That is a fact about one
snapshot, not a property of the design. Antivirus signature sets change weekly, a new mod build is
a new file with a new verdict, and the rules below cost nothing when everything is clean.

A client that renders "4/70 detected" as a verdict would make this community's legitimate mods look
like malware, and would do real harm to people who have given their work away for free. So:

- **Never present a detection count as a conclusion.** Lead with what is actually a fact about the
  download: was this exact file scanned, when, and by how many engines.
- **Never show a number without its context on the same screen.** The mod list's pill therefore
  states coverage only — `scanned`, `scanned v1.0.0`, `not scanned yet` — and every count lives in
  the modal, next to the sentence explaining why unsigned mods trip heuristics.
- **Never color a detection as a failure.** The modal is `Info` even when engines flagged the file,
  and flagged engines get neutral bullets, not red crosses.
- **Always link the full VirusTotal report**, so nobody has to accept the client's summary of it.
- **Never block an install on a scan result.** Contrast the sha256 check, which does block: a
  checksum mismatch is a definite integrity failure with no benign reading. A heuristic detection is
  not that. Show, don't block.
- **"Not scanned yet" is the normal case** for a new or just-updated mod. Say it plainly and never
  imply it means anything is wrong.
- **"We could not load the reports" is not "there is no report."** An offline client must not put an
  unearned mark against a mod because the user's connection was down. `ScanIndex` being null means
  unknown, and the UI stays silent rather than guessing.

The wording that implements all of this lives in `src/PunkNexus/Services/ScanDialog.cs` and
`InstallService.ScanDetails`.

---

## Setup

The workflow needs a **`VT_API_KEY`** repository secret — a free key from
<https://www.virustotal.com/gui/my-apikey>, added under *Settings → Secrets and variables →
Actions*. The workflow fails with an actionable message when it is missing, and the key is never
logged or written into a report.

## Running it by hand

```bash
python3 tools/virus-scan.py --check          # resolve + hash + report drift; no API key needed
VT_API_KEY=… python3 tools/virus-scan.py     # scan whatever changed
VT_API_KEY=… python3 tools/virus-scan.py --force --only PunkScoreboard
```

`--check` is the one to reach for when changing the resolver: it exercises the whole pipeline up to
the API boundary and spends no quota.
