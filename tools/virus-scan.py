#!/usr/bin/env python3
"""Scan every catalog download with VirusTotal and write a report per mod.

CI runs this on a schedule (see .github/workflows/virus-scan.yml). Reports land in reports/ and
are committed back, so the client can show "what was scanned, when, and by how many engines" for
the exact file a user is about to install.

    python3 tools/virus-scan.py --check     # resolve + hash + report drift, no API key needed
    python3 tools/virus-scan.py             # scan whatever changed (needs VT_API_KEY)
    python3 tools/virus-scan.py --force     # rescan everything, changed or not
    python3 tools/virus-scan.py --only PunkScoreboard

THE HASH IS THE REPORT'S IDENTITY
--------------------------------
Every mod.json carries a developer-declared `sha256`. That number is maintained by the same person
who ships the zip, so it proves the download arrived intact — it does not prove anyone looked
inside, and a hostile developer controls both sides of it. So this tool never trusts it: it
resolves the entry to its real download, fetches the bytes, and computes the hash itself. THAT
hash is what the report is filed under, and it is what the client matches at install time to
decide whether the report describes the file in front of it.

The declared hash is still recorded, as a cross-check — a mismatch means the catalog is telling
users to expect a file that is not the one being published, which is worth knowing either way.

WHAT GETS SCANNED
-----------------
The downloadable zip, not the DLLs inside it. The zip is what the user downloads and what the
client can hash at install time, so scanning it is the only thing that keeps the report and the
artifact provably the same object. Per-DLL detail would be an addition to this, never a
replacement for it.

CHANGE DETECTION
----------------
An entry is scanned when its computed hash differs from the hash of the newest stored report (or
that report never completed). Unchanged mods cost one download and zero VirusTotal requests, which
is what keeps a daily schedule inside the free tier.

RATE LIMITS, AND THE ARITHMETIC BEHIND THE SCHEDULE
---------------------------------------------------
The free VirusTotal API allows 4 lookups/minute, 500/day and 15,500/month.

The per-minute figure is the pacing constraint, and Pacer sleeps to respect it rather than firing
and retrying on 429s — a scheduled job has time, and a 429 mid-run leaves half a catalog scanned.
A full pass over ~17 entries is therefore roughly 4-5 minutes of mostly waiting.

The MONTHLY figure is what governs how often the schedule may fire. A steady-state pass costs one
lookup per entry, because a hash lookup is tried before any upload and unchanged mods are skipped
before even that:

    17 entries x 1 lookup x 30 days  ~=  510 lookups/month   against 15,500

So daily is comfortable with two orders of magnitude of headroom. Hourly would be 24x that
(~12,200) plus every first-sighting upload, which does not fit — if you change the cron in
.github/workflows/virus-scan.yml, redo this multiplication first.

Only a hash VirusTotal has never seen costs more than one lookup: an upload, then polling until
the analysis completes. That happens once per new mod build, not per run.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import sys
import time
import urllib.error
import urllib.request
import uuid
from collections import deque
from datetime import datetime, timezone
from pathlib import Path

# Same reason as tools/pin-downloads.py in PunkMods: a Windows runner defaults stdout to cp1252 and
# a single non-ASCII character would raise mid-print and fail the job.
if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")

REPO_ROOT = Path(__file__).resolve().parent.parent
REGISTRY = REPO_ROOT / "manifest" / "mods.json"
REPORTS_DIR = REPO_ROOT / "reports"

VT_API = "https://www.virustotal.com/api/v3"
VT_GUI = "https://www.virustotal.com/gui/file"
GITHUB_API = "https://api.github.com"

SCHEMA_VERSION = 1
USER_AGENT = "punknexus-virus-scan"

# How many scans to keep per mod. Enough to see a mod's recent history in the repo without the
# file growing without bound; the client only ever reads the newest.
HISTORY_LIMIT = 10

# VirusTotal refuses uploads past 32 MB on the free tier, and a mod zip that large is a signal in
# itself. Mod zips in this catalog are 4-24 KB.
MAX_UPLOAD_BYTES = 32 * 1024 * 1024

# Polls to spend waiting on one uploaded file's analysis before recording it as queued and moving
# on. Polls come out of the same request budget as the scans.
MAX_POLLS = 8

# Engine names are capped in the index so a heuristic pile-up cannot bloat the file the client
# fetches on every refresh. The full list stays in the per-mod report.
INDEX_ENGINE_LIMIT = 12

# Catalog conditions a human may want to look at. A mod between releases is a normal in-between
# state, so these never fail the run.
problems: list[str] = []

# Entries that NEEDED a VirusTotal request and did not get one — quota exhausted, a 429, an API
# error. These do fail the run: silently leaving a changed artifact unscanned is the one outcome
# that would make the reports quietly untrue.
unreached: list[str] = []


def warn(msg: str) -> None:
    problems.append(msg)


def now_utc() -> str:
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


# ----------------------------------------------------------------- http

def http_json(url: str, headers: dict[str, str] | None = None, timeout: int = 60):
    request = urllib.request.Request(url, headers={"User-Agent": USER_AGENT, **(headers or {})})
    with urllib.request.urlopen(request, timeout=timeout) as response:
        return json.load(response)


def http_bytes(url: str, timeout: int = 120) -> bytes:
    request = urllib.request.Request(url, headers={"User-Agent": USER_AGENT})
    with urllib.request.urlopen(request, timeout=timeout) as response:
        return response.read()


def github_headers() -> dict[str, str]:
    """Authenticated when a token is around: 60 requests/hour unauthenticated is not much when the
    catalog keeps growing, and CI hands us GITHUB_TOKEN for free."""
    headers = {"Accept": "application/vnd.github+json", "X-GitHub-Api-Version": "2022-11-28"}
    token = os.environ.get("GITHUB_TOKEN")
    if token:
        headers["Authorization"] = f"Bearer {token}"
    return headers


# ----------------------------------------------------------------- resolving downloads

def glob_to_regex(glob: str) -> re.Pattern[str]:
    """Mirrors ReleaseResolver.GlobToRegex in the client, so both sides pick the same asset."""
    parts = glob.split("*")
    return re.compile("^" + "(.+?)".join(re.escape(p) for p in parts) + "$", re.IGNORECASE)


def resolve_download(where: str, source) -> tuple[str, str, str | None] | None:
    """(url, file name, version) for a download block, or None when it cannot be resolved."""
    if not isinstance(source, dict):
        warn(f"{where}: no usable 'download' block.")
        return None

    url = source.get("url")
    if url:
        return url, url.rsplit("/", 1)[-1], None

    repo, pattern = source.get("repo"), source.get("assetPattern")
    if not repo or not pattern:
        warn(f"{where}: 'download' needs either a 'url', or both 'repo' and 'assetPattern'.")
        return None

    try:
        release = http_json(f"{GITHUB_API}/repos/{repo}/releases/latest", github_headers())
    except Exception as exc:  # noqa: BLE001 - any failure is reportable, none is fatal
        warn(f"{where}: could not read the latest release of {repo} - {exc}")
        return None

    regex = glob_to_regex(pattern)
    best = None
    for asset in release.get("assets") or []:
        match = regex.match(asset.get("name") or "")
        if not match:
            continue
        version = match.group(1) if match.groups() else None
        candidate = (asset.get("browser_download_url"), asset["name"], version)
        # A release normally carries one asset per mod; if the glob ever matches several, the
        # highest version wins, matching the client.
        if best is None or (version or "") > (best[2] or ""):
            best = candidate

    if best is None:
        warn(f"{where}: no asset in the latest release of {repo} matches '{pattern}'.")
    return best


# ----------------------------------------------------------------- VirusTotal

class Pacer:
    """Keeps VT calls inside the free tier's 4/minute, and inside a daily ceiling.

    Every call sleeps rather than failing: a scheduled job has all the time in the world, and a
    429 in the middle of a run leaves half the catalog scanned and half not.
    """

    def __init__(self, per_minute: int, max_requests: int) -> None:
        self.per_minute = max(1, per_minute)
        self.max_requests = max_requests
        self.used = 0
        self._recent: deque[float] = deque()

    @property
    def exhausted(self) -> bool:
        return self.used >= self.max_requests

    def take(self) -> None:
        if self.exhausted:
            raise BudgetExhausted(f"request budget of {self.max_requests} reached")

        now = time.monotonic()
        while self._recent and now - self._recent[0] >= 60:
            self._recent.popleft()

        if len(self._recent) >= self.per_minute:
            wait = 60 - (now - self._recent[0]) + 0.5
            if wait > 0:
                print(f"    (rate limit: waiting {wait:.0f}s)")
                time.sleep(wait)
            now = time.monotonic()
            while self._recent and now - self._recent[0] >= 60:
                self._recent.popleft()

        self._recent.append(time.monotonic())
        self.used += 1


class BudgetExhausted(Exception):
    pass


class VirusTotal:
    def __init__(self, api_key: str, pacer: Pacer) -> None:
        self._key = api_key
        self._pacer = pacer

    def _headers(self) -> dict[str, str]:
        # Never printed, never written to a report — the key only ever leaves here as a header.
        return {"x-apikey": self._key, "Accept": "application/json", "User-Agent": USER_AGENT}

    def file_report(self, sha256: str):
        """The existing report for a hash, or None when VirusTotal has never seen the file."""
        self._pacer.take()
        try:
            return http_json(f"{VT_API}/files/{sha256}", self._headers())
        except urllib.error.HTTPError as exc:
            if exc.code == 404:
                return None
            # Pacer should make this unreachable. If it happens anyway the budget assumption is
            # wrong, and continuing would burn the rest of the quota on more 429s.
            if exc.code == 429:
                raise BudgetExhausted("VirusTotal returned 429 (quota or rate limit)") from exc
            raise

    def upload(self, file_name: str, payload: bytes) -> str:
        """Submits a file and returns the analysis id."""
        boundary = uuid.uuid4().hex
        body = b"".join([
            f"--{boundary}\r\n".encode(),
            f'Content-Disposition: form-data; name="file"; filename="{file_name}"\r\n'.encode(),
            b"Content-Type: application/zip\r\n\r\n",
            payload,
            f"\r\n--{boundary}--\r\n".encode(),
        ])

        request = urllib.request.Request(
            f"{VT_API}/files",
            data=body,
            headers={**self._headers(), "Content-Type": f"multipart/form-data; boundary={boundary}"},
            method="POST",
        )

        self._pacer.take()
        with urllib.request.urlopen(request, timeout=300) as response:
            return json.load(response)["data"]["id"]

    def analysis(self, analysis_id: str):
        self._pacer.take()
        return http_json(f"{VT_API}/analyses/{analysis_id}", self._headers())


def summarize(stats: dict, results: dict) -> dict:
    """Turn VirusTotal's per-engine verdicts into the numbers a report carries.

    'Engines' counts the ones that actually returned a verdict. VT's stats also include
    type-unsupported, timeout and failure buckets, and folding those into the denominator would
    quietly inflate it — 4/70 and 4/62 read very differently to someone deciding whether to
    install something.
    """
    malicious = int(stats.get("malicious", 0))
    suspicious = int(stats.get("suspicious", 0))
    undetected = int(stats.get("undetected", 0))
    harmless = int(stats.get("harmless", 0))

    flagged = []
    for engine, result in sorted((results or {}).items()):
        if result.get("category") in ("malicious", "suspicious"):
            verdict = result.get("result") or result.get("category") or "flagged"
            flagged.append(f"{result.get('engine_name') or engine}: {verdict}")

    return {
        "enginesTotal": malicious + suspicious + undetected + harmless,
        "detections": malicious + suspicious,
        "malicious": malicious,
        "suspicious": suspicious,
        "detectedBy": flagged,
    }


def scan_bytes(vt: VirusTotal, sha256: str, file_name: str, payload: bytes,
               poll_timeout: int) -> dict:
    """Hash lookup first, upload only when VirusTotal has genuinely never seen the file."""
    existing = vt.file_report(sha256)
    if existing is not None:
        attributes = existing["data"]["attributes"]
        scanned = attributes.get("last_analysis_date")
        summary = summarize(
            attributes.get("last_analysis_stats") or {},
            attributes.get("last_analysis_results") or {},
        )
        summary["status"] = "completed"
        summary["analyzedUtc"] = (
            datetime.fromtimestamp(scanned, timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")
            if scanned else None
        )
        summary["source"] = "lookup"
        return summary

    if len(payload) > MAX_UPLOAD_BYTES:
        return {"status": "error", "error": f"file is {len(payload)} bytes, over the upload limit",
                "enginesTotal": 0, "detections": 0, "malicious": 0, "suspicious": 0,
                "detectedBy": [], "source": "upload"}

    print(f"    not seen before - uploading {file_name} ({len(payload)} bytes)")
    analysis_id = vt.upload(file_name, payload)

    # Bounded by polls as well as by time. Each poll is a request against the same 4/minute budget
    # as the scans themselves, so a slow analysis left to run for the full timeout could spend the
    # whole day's quota watching one file: 17 files x 20 polls is 340 requests of pure waiting.
    deadline = time.monotonic() + poll_timeout
    polls = 0
    while True:
        polls += 1
        analysis = vt.analysis(analysis_id)
        attributes = analysis["data"]["attributes"]
        status = attributes.get("status")

        if status == "completed":
            summary = summarize(attributes.get("stats") or {}, attributes.get("results") or {})
            summary["status"] = "completed"
            summary["analyzedUtc"] = now_utc()
            summary["analysisId"] = analysis_id
            summary["source"] = "upload"
            return summary

        if polls >= MAX_POLLS or time.monotonic() >= deadline:
            # A queued analysis is not a failure — it is recorded as queued so the next run
            # rescans it, and the client says "not scanned yet" rather than inventing a verdict.
            print(f"    still {status} after {polls} poll(s) - leaving it queued")
            return {"status": "queued", "analysisId": analysis_id, "analyzedUtc": None,
                    "enginesTotal": 0, "detections": 0, "malicious": 0, "suspicious": 0,
                    "detectedBy": [], "source": "upload"}


# ----------------------------------------------------------------- reports

def report_path(mod_id: str) -> Path:
    return REPORTS_DIR / f"{mod_id}.json"


def load_report(mod_id: str) -> dict:
    path = report_path(mod_id)
    if not path.exists():
        return {"schemaVersion": SCHEMA_VERSION, "modId": mod_id, "scans": []}
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except json.JSONDecodeError as exc:
        warn(f"{mod_id}: existing report is not valid JSON ({exc}); starting a new one.")
        return {"schemaVersion": SCHEMA_VERSION, "modId": mod_id, "scans": []}


def latest_scan(report: dict) -> dict | None:
    scans = report.get("scans") or []
    return scans[0] if scans else None


def write_json(path: Path, payload) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(payload, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def build_index(mod_ids: list[str]) -> dict:
    """One file the client fetches for the whole catalog.

    Deliberately one request rather than one per mod: the client refreshes the whole list at once,
    and eighteen conditional GETs against raw.githubusercontent to render a badge is a poor trade.
    """
    scans = []
    for mod_id in mod_ids:
        report = load_report(mod_id)
        scan = latest_scan(report)
        if not scan:
            continue
        scans.append({
            "modId": mod_id,
            "modName": report.get("modName"),
            "sha256": scan.get("sha256"),
            "fileName": scan.get("fileName"),
            "sizeBytes": scan.get("sizeBytes"),
            "modVersion": scan.get("modVersion"),
            "declaredMatches": scan.get("declaredMatches"),
            "scannedUtc": scan.get("scannedUtc"),
            "analyzedUtc": scan.get("analyzedUtc"),
            "status": scan.get("status"),
            "enginesTotal": scan.get("enginesTotal", 0),
            "detections": scan.get("detections", 0),
            "detectedBy": (scan.get("detectedBy") or [])[:INDEX_ENGINE_LIMIT],
            "permalink": scan.get("permalink"),
            "reportPath": f"reports/{mod_id}.json",
        })

    # `complete` and `unreached` exist so a run cut short by the quota cannot be mistaken for a
    # clean sweep. The index itself is still written and still accurate — every entry describes a
    # report that really exists — but it says plainly which entries this run failed to get to.
    return {
        "schemaVersion": SCHEMA_VERSION,
        "generatedUtc": now_utc(),
        "complete": not unreached,
        "unreached": sorted(unreached),
        "scans": sorted(scans, key=lambda s: s["modId"].lower()),
    }


# ----------------------------------------------------------------- the run

def targets(registry: dict, only: str | None) -> list[tuple[str, str, str | None, dict | None]]:
    """(id, label, manifest url, inline download) for everything the client can install.

    BepInEx is included: the client downloads and installs it exactly like a mod, so leaving it
    unscanned would put a gap in the one place a user has no choice but to accept.
    """
    out: list[tuple[str, str, str | None, dict | None]] = []

    loader = registry.get("loader") or {}
    loader_source = loader.get("source") or (
        {"url": loader["downloadUrl"]} if loader.get("downloadUrl") else None
    )
    if loader_source:
        out.append(("BepInEx", loader.get("name") or "BepInEx", None, loader_source))

    for entry in registry.get("mods") or []:
        if not entry.get("enabled", True):
            continue
        out.append((entry["id"], entry.get("name") or entry["id"], entry.get("manifestUrl"), None))

    if only:
        wanted = {o.strip().lower() for o in only.split(",")}
        out = [t for t in out if t[0].lower() in wanted]

    return out


def main() -> int:
    parser = argparse.ArgumentParser(description="Scan catalog downloads with VirusTotal.")
    parser.add_argument("--check", action="store_true",
                        help="resolve and hash everything, report what would be scanned, write nothing")
    parser.add_argument("--force", action="store_true", help="rescan even when the hash is unchanged")
    parser.add_argument("--only", help="comma-separated mod ids")
    parser.add_argument("--rate", type=int, default=4, help="VirusTotal requests per minute (free tier: 4)")
    parser.add_argument("--max-requests", type=int, default=400,
                        help="ceiling on VirusTotal requests for this run (free tier: 500/day)")
    parser.add_argument("--poll-timeout", type=int, default=300,
                        help="seconds to wait for an uploaded file's analysis")
    args = parser.parse_args()

    registry = json.loads(REGISTRY.read_text(encoding="utf-8"))
    entries = targets(registry, args.only)
    print(f"catalog: {len(entries)} downloadable artifact(s)\n")

    api_key = os.environ.get("VT_API_KEY", "").strip()
    if not args.check and not api_key:
        print("error: VT_API_KEY is not set.")
        print("       Add a VirusTotal API key as the VT_API_KEY repository secret "
              "(Settings > Secrets and variables > Actions), or run with --check to hash only.")
        return 2

    pacer = Pacer(args.rate, args.max_requests)
    vt = VirusTotal(api_key, pacer) if api_key and not args.check else None

    scanned, skipped, planned = 0, 0, 0
    seen_ids = []

    for position, (mod_id, label, manifest_url, inline_source) in enumerate(entries):
        seen_ids.append(mod_id)
        print(f"{mod_id}")

        source, mod_version = inline_source, None
        if manifest_url:
            try:
                manifest = http_json(manifest_url)
            except Exception as exc:  # noqa: BLE001
                warn(f"{mod_id}: could not fetch {manifest_url} - {exc}")
                print("    manifest unavailable - skipped")
                continue
            source = manifest.get("download")
            mod_version = manifest.get("version")
            declared = manifest.get("sha256")
            label = manifest.get("name") or label
        else:
            declared = (registry.get("loader") or {}).get("sha256")

        resolved = resolve_download(mod_id, source)
        if resolved is None:
            print("    no resolvable download - skipped")
            continue

        url, file_name, asset_version = resolved
        try:
            payload = http_bytes(url)
        except Exception as exc:  # noqa: BLE001
            warn(f"{mod_id}: could not download {url} - {exc}")
            print("    download failed - skipped")
            continue

        # The hash we computed ourselves, from the bytes a user would actually receive. Everything
        # downstream — the report's identity, change detection, the client's install-time match —
        # keys off this and never off the declared value.
        sha256 = hashlib.sha256(payload).hexdigest()
        declared_matches = (declared.strip().lower() == sha256) if declared else None
        if declared_matches is False:
            warn(f"{mod_id}: declared sha256 does not match the published file "
                 f"(declared {declared[:12]}..., actual {sha256[:12]}...).")

        report = load_report(mod_id)
        previous = latest_scan(report)
        unchanged = (
            previous is not None
            and previous.get("sha256") == sha256
            and previous.get("status") == "completed"
        )

        print(f"    {file_name}  {len(payload)} bytes  sha256 {sha256[:16]}...")

        if unchanged and not args.force:
            skipped += 1
            print("    unchanged since the last scan - no VirusTotal request")
            continue

        planned += 1
        if args.check or vt is None:
            reason = "never scanned" if previous is None else "hash changed since the last scan"
            print(f"    WOULD SCAN ({reason})")
            continue

        try:
            result = scan_bytes(vt, sha256, file_name, payload, args.poll_timeout)
        except BudgetExhausted as exc:
            # Everything already written stays written. What stops is this run, and every entry it
            # never got to is named so the next one — and anyone reading the log — can see the
            # difference between "scanned and fine" and "never looked at".
            remaining = [e[0] for e in entries[position:]]
            unreached.extend(remaining)
            warn(f"stopped early after {mod_id}: {exc}")
            print(f"    {exc} - stopping ({len(remaining)} entr(y/ies) not reached)")
            break
        except urllib.error.HTTPError as exc:
            if exc.code in (401, 403):
                print(f"    VirusTotal rejected the API key (HTTP {exc.code}).")
                print("    Check the VT_API_KEY secret is a valid VirusTotal API key.")
                return 2
            unreached.append(mod_id)
            warn(f"{mod_id}: VirusTotal returned HTTP {exc.code}.")
            print(f"    VirusTotal error HTTP {exc.code} - not scanned")
            continue
        except Exception as exc:  # noqa: BLE001
            unreached.append(mod_id)
            warn(f"{mod_id}: scan failed - {exc}")
            print(f"    scan failed - {exc}")
            continue

        scan = {
            "sha256": sha256,
            "fileName": file_name,
            "sizeBytes": len(payload),
            "downloadUrl": url,
            "modVersion": mod_version or asset_version,
            "declaredSha256": declared,
            "declaredMatches": declared_matches,
            "scannedUtc": now_utc(),
            "permalink": f"{VT_GUI}/{sha256}",
            **result,
        }

        report["schemaVersion"] = SCHEMA_VERSION
        report["modId"] = mod_id
        report["modName"] = label
        # Newest first, and a rescan of the same bytes replaces its predecessor rather than
        # stacking duplicate entries for one file.
        history = [s for s in (report.get("scans") or []) if s.get("sha256") != sha256]
        report["scans"] = [scan] + history[: HISTORY_LIMIT - 1]

        write_json(report_path(mod_id), report)
        scanned += 1

        if result["status"] == "completed":
            print(f"    scanned: {result['detections']} of {result['enginesTotal']} engines flagged it")
        else:
            print(f"    {result['status']}")

    if not args.check:
        # Built from every catalog entry, not just the ones this run touched: --only must not
        # quietly drop every other mod's report out of the file the client reads.
        all_ids = [t[0] for t in targets(registry, None)]
        write_json(REPORTS_DIR / "index.json", build_index(all_ids))
        print(f"\nindex: reports/index.json ({len(all_ids)} artifact(s) considered)")

    print(f"\n{scanned} scanned, {skipped} unchanged, {planned} needed a scan. "
          f"{pacer.used} VirusTotal request(s).")

    if problems:
        print("\nproblems:")
        for problem in problems:
            print(f"  - {problem}")

    if unreached:
        print("\nNOT SCANNED - these needed a VirusTotal request and did not get one:")
        for mod_id in sorted(set(unreached)):
            print(f"  - {mod_id}")
        print("\nReports already written are kept and are accurate; reports/index.json records")
        print("this run as incomplete. Re-run once the quota resets.")
        return 1

    # Anything left in `problems` is a catalog condition rather than a failure of this tool — a mod
    # between releases is a normal in-between state and must not turn the schedule red.
    return 0


if __name__ == "__main__":
    sys.exit(main())
