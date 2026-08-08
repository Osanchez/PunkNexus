#!/usr/bin/env python3
"""Validate the PUNK Nexus catalogue.

Run on every pull request that touches manifest/. The registry is the one place a mistake reaches
every user at once — a bad id, an unreachable manifest or a mod whose manifest disagrees with its
listing all surface as a broken row in the client, so they are caught here instead.

    python3 tools/validate-manifest.py            # validate and fetch each mod manifest
    python3 tools/validate-manifest.py --offline  # structure only, no network

Exits non-zero if anything failed. Warnings never fail the build.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
import urllib.error
import urllib.request
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
REGISTRY = REPO_ROOT / "manifest" / "mods.json"
SERVERS = REPO_ROOT / "manifest" / "servers.json"

ID_PATTERN = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]*$")
SUPPORTED_SCHEMA = 1
TIMEOUT = 20

errors: list[str] = []
warnings: list[str] = []


def error(msg: str) -> None:
    errors.append(msg)


def warn(msg: str) -> None:
    warnings.append(msg)


def load_json(path: Path):
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError:
        error(f"{path.relative_to(REPO_ROOT)} is missing.")
    except json.JSONDecodeError as exc:
        error(f"{path.relative_to(REPO_ROOT)} is not valid JSON: {exc}")
    return None


def fetch(url: str):
    request = urllib.request.Request(url, headers={"User-Agent": "punknexus-manifest-validator"})
    with urllib.request.urlopen(request, timeout=TIMEOUT) as response:
        return json.loads(response.read().decode("utf-8"))


def validate_source(where: str, source, required: bool) -> None:
    """A download block is either a fixed url, or a repo plus an asset glob."""
    if source is None:
        if required:
            error(f"{where}: no 'download' block, so nothing can be installed.")
        return

    if not isinstance(source, dict):
        error(f"{where}: 'download' must be an object.")
        return

    url, repo, pattern = source.get("url"), source.get("repo"), source.get("assetPattern")

    if url:
        if not str(url).startswith("https://"):
            error(f"{where}: download url must be https.")
        return

    if not repo or not pattern:
        error(f"{where}: 'download' needs either a 'url', or both 'repo' and 'assetPattern'.")
        return

    if "/" not in str(repo):
        error(f"{where}: download repo '{repo}' should look like 'owner/name'.")
    if "*" not in str(pattern):
        warn(f"{where}: assetPattern '{pattern}' has no wildcard, so it will not track new versions.")


def validate_mod_manifest(entry_id: str, url: str, manifest, target_game_version, known_ids) -> None:
    where = f"{entry_id} manifest"

    if not isinstance(manifest, dict):
        error(f"{where}: not a JSON object.")
        return

    schema = manifest.get("schemaVersion", 1)
    if schema != SUPPORTED_SCHEMA:
        warn(f"{where}: schemaVersion {schema} is newer than this validator understands ({SUPPORTED_SCHEMA}).")

    mod_id = manifest.get("id")
    if not mod_id:
        error(f"{where}: no 'id'.")
    elif mod_id != entry_id:
        error(f"{where}: declares id '{mod_id}' but the registry lists it as '{entry_id}'.")

    if not manifest.get("name"):
        error(f"{where}: no 'name'.")

    if not manifest.get("version"):
        error(f"{where}: no 'version'. The client reads this to detect updates.")

    game_version = manifest.get("gameVersion")
    if not game_version:
        error(f"{where}: no 'gameVersion'. The client refuses to install mods that do not declare one.")
    elif target_game_version and game_version != target_game_version:
        # Not an error: the registry can target a newer build than a mod has caught up to. The
        # client blocks it, and this tells the reviewer that will happen.
        warn(
            f"{where}: built for game {game_version} but the registry targets {target_game_version}, "
            "so the client will refuse to install it."
        )

    plugin_folder = manifest.get("pluginFolder") or mod_id or entry_id
    if any(sep in str(plugin_folder) for sep in ("/", "\\")) or ".." in str(plugin_folder):
        error(f"{where}: pluginFolder '{plugin_folder}' must be a single folder name.")

    for dependency in manifest.get("dependencies") or []:
        if dependency not in known_ids:
            error(f"{where}: depends on '{dependency}', which is not in the registry.")

    validate_source(where, manifest.get("download"), required=True)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--offline", action="store_true", help="skip fetching mod manifests")
    args = parser.parse_args()

    registry = load_json(REGISTRY)
    servers = load_json(SERVERS)

    if servers is not None and not isinstance(servers.get("servers"), list):
        error("manifest/servers.json: 'servers' must be an array.")

    if registry is None:
        return report()

    if registry.get("schemaVersion", 1) != SUPPORTED_SCHEMA:
        warn(f"registry schemaVersion {registry.get('schemaVersion')} is not {SUPPORTED_SCHEMA}.")

    target_game_version = registry.get("targetGameVersion")
    if not target_game_version:
        warn("registry has no 'targetGameVersion'; reviewers lose the compatibility cross-check.")

    validate_source("loader", registry.get("loader", {}).get("source"), required=False)
    loader = registry.get("loader") or {}
    if not loader.get("source") and not loader.get("downloadUrl"):
        error("registry loader: needs a 'downloadUrl' or a 'source'.")

    mods = registry.get("mods")
    if not isinstance(mods, list):
        error("registry: 'mods' must be an array.")
        return report()

    seen: dict[str, int] = {}
    entries = []

    for index, entry in enumerate(mods):
        where = f"registry entry #{index}"
        if not isinstance(entry, dict):
            error(f"{where}: not an object.")
            continue

        entry_id = entry.get("id")
        if not entry_id:
            error(f"{where}: no 'id'.")
            continue
        if not ID_PATTERN.match(entry_id):
            error(f"{where}: id '{entry_id}' must be alphanumeric with . _ - only.")
        if entry_id in seen:
            error(f"{where}: duplicate id '{entry_id}' (already at #{seen[entry_id]}).")
            continue
        seen[entry_id] = index

        if not entry.get("name"):
            error(f"{entry_id}: no 'name'.")

        url = entry.get("manifestUrl")
        if not url:
            error(f"{entry_id}: no 'manifestUrl'. The client has nowhere to read its version from.")
        elif not str(url).startswith("https://"):
            error(f"{entry_id}: manifestUrl must be https.")
        else:
            entries.append((entry_id, url))

    known_ids = set(seen)
    print(f"registry: {len(mods)} entries, targetGameVersion={target_game_version}")

    if args.offline:
        print("offline: skipped fetching mod manifests")
        return report()

    for entry_id, url in entries:
        try:
            manifest = fetch(url)
        except urllib.error.HTTPError as exc:
            error(f"{entry_id}: manifestUrl returned HTTP {exc.code} ({url}).")
            continue
        except Exception as exc:  # noqa: BLE001 - any failure here is a reviewable problem
            error(f"{entry_id}: could not fetch {url} — {exc}")
            continue

        validate_mod_manifest(entry_id, url, manifest, target_game_version, known_ids)
        version = manifest.get("version") if isinstance(manifest, dict) else "?"
        game = manifest.get("gameVersion") if isinstance(manifest, dict) else "?"
        print(f"  ok  {entry_id:<28} v{version} (game {game})")

    return report()


def report() -> int:
    for message in warnings:
        print(f"warning: {message}")
    for message in errors:
        print(f"error: {message}")

    if errors:
        print(f"\n{len(errors)} error(s), {len(warnings)} warning(s).")
        return 1

    print(f"\nManifest valid. {len(warnings)} warning(s).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
