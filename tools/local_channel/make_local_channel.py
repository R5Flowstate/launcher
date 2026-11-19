#!/usr/bin/env python3
"""Emit local-testing CHANNEL_MANIFEST + INSTALL_STATE fixtures from live sources.

Reads:
  - unify basetypes.h  -> SDK_VERSION (WireVersion / r5fms gate)
  - s21-builds client/server base meta.json -> content_hash, catalog version, total_bytes

Does not invent hashes or wire version. Local fixture only (file:// / absolute install path).
"""
from __future__ import annotations

import argparse
import json
import os
import re
import sys
from datetime import datetime, timezone
from pathlib import Path

HERE = Path(__file__).resolve().parent


def env_path(name: str) -> Path | None:
    raw = os.environ.get(name, "").strip()
    return Path(raw) if raw else None


def env_join(root_name: str, *parts: str) -> Path | None:
    root = env_path(root_name)
    return root.joinpath(*parts) if root is not None else None

SDK_VERSION_RE = re.compile(
    r'#\s*define\s+SDK_VERSION\s+"([^"]+)"',
    re.MULTILINE,
)


def read_sdk_version(path: Path) -> str:
    text = path.read_text(encoding="utf-8", errors="replace")
    m = SDK_VERSION_RE.search(text)
    if not m:
        raise SystemExit(f"SDK_VERSION not found in {path}")
    value = m.group(1).strip()
    if not value:
        raise SystemExit(f"SDK_VERSION empty in {path}")
    if re.match(r"^\d+\.\d+\.\d+", value):
        raise SystemExit(
            f"SDK_VERSION looks like catalog semver (refused): {value!r} in {path}"
        )
    return value


def load_json(path: Path) -> dict:
    if not path.is_file():
        raise SystemExit(f"missing json: {path}")
    with path.open("r", encoding="utf-8") as f:
        data = json.load(f)
    if not isinstance(data, dict):
        raise SystemExit(f"expected object in {path}")
    return data


def tip_from_meta(meta: dict, catalog: dict | None, preset: str) -> dict:
    content_hash = meta.get("content_hash")
    if not content_hash or not isinstance(content_hash, str):
        raise SystemExit(f"{preset} meta missing content_hash")
    if content_hash.startswith("local-placeholder"):
        raise SystemExit(f"{preset} content_hash is still a placeholder")

    version = meta.get("version")
    if catalog:
        base_meta = catalog.get("base_meta") or {}
        version = (
            catalog.get("base_version")
            or base_meta.get("version")
            or version
        )
        # Prefer live meta hash; catalog hash is cross-check only.
        cat_hash = base_meta.get("content_hash")
        if cat_hash and cat_hash != content_hash:
            print(
                f"warn: {preset} catalog content_hash != meta "
                f"({cat_hash[:12]}... vs {content_hash[:12]}...); using meta",
                file=sys.stderr,
            )

    if not version:
        raise SystemExit(f"{preset} meta/catalog missing version")

    total_bytes = meta.get("total_bytes")
    if total_bytes is None and catalog:
        total_bytes = (catalog.get("base_meta") or {}).get("total_bytes")

    return {
        "preset": preset,
        "catalog_version": str(version),
        "content_hash": content_hash,
        "total_bytes": int(total_bytes) if total_bytes is not None else None,
    }


def as_file_uri(path: Path) -> str:
    # POSIX-ish file URI for Windows absolute paths (local fixture only).
    resolved = path.resolve()
    return resolved.as_uri()


def build_channel(
    sdk_version: str,
    client: dict,
    server: dict,
    install_root: Path,
    *,
    marketing_tag: str,
) -> dict:
    root = install_root
    root_s = str(root).replace("\\", "/")
    return {
        "schema": 1,
        "kind": "channel_manifest",
        "sdk_version": sdk_version,
        "gate_name": sdk_version,
        "marketing_tag": marketing_tag,
        "channel": "local",
        "prerelease": True,
        "base_url": as_file_uri(root),
        "notes": (
            "LOCAL TESTING fixture from make_local_channel.py. "
            "Points at master install root. Not a public ship document. "
            f"generated_utc={datetime.now(timezone.utc).strftime('%Y-%m-%dT%H:%M:%SZ')}"
        ),
        "client": {
            "preset": "client",
            "catalog_version": client["catalog_version"],
            "content_hash": client["content_hash"],
            "share_manifest_url": None,
            "share_manifest_sha256": None,
            "total_bytes": client.get("total_bytes"),
            "local_install_hint": root_s,
        },
        "server": {
            "preset": "server",
            "catalog_version": server["catalog_version"],
            "content_hash": server["content_hash"],
            "share_manifest_url": None,
            "share_manifest_sha256": None,
            "total_bytes": server.get("total_bytes"),
            "local_install_hint": root_s,
        },
        "script_checksums": None,
    }


def build_install_state(
    sdk_version: str,
    client: dict,
    server: dict,
    install_root: Path,
) -> dict:
    return {
        "schema": 1,
        "sdk_version_expected": sdk_version,
        "client_catalog_version": client["catalog_version"],
        "server_catalog_version": server["catalog_version"],
        "client_content_hash": client["content_hash"],
        "server_content_hash": server["content_hash"],
        "incomplete": False,
        "last_error": None,
        "install_path": str(install_root).replace("\\", "/"),
    }


def write_json(path: Path, obj: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", encoding="utf-8", newline="\n") as f:
        json.dump(obj, f, indent=2)
        f.write("\n")
    print(f"wrote {path}")


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--basetypes", type=Path, default=env_path("R5F_UNIFY_BASETYPES"))
    p.add_argument(
        "--client-meta",
        type=Path,
        default=env_join("R5F_BUILDS_ROOT", "client", "base", "1.0.0", "meta.json"),
    )
    p.add_argument(
        "--server-meta",
        type=Path,
        default=env_join("R5F_BUILDS_ROOT", "server", "base", "1.0.0", "meta.json"),
    )
    p.add_argument(
        "--client-catalog",
        type=Path,
        default=env_join("R5F_BUILDS_ROOT", "client", "catalog.json"),
    )
    p.add_argument(
        "--server-catalog",
        type=Path,
        default=env_join("R5F_BUILDS_ROOT", "server", "catalog.json"),
    )
    p.add_argument("--install-root", type=Path, default=env_path("R5F_INSTALL_PATH"))
    p.add_argument("--out-dir", type=Path, default=HERE)
    p.add_argument(
        "--marketing-tag",
        default="local-dev-0.0.1",
        help="MarketingTag only; never used as r5fms wire version",
    )
    p.add_argument(
        "--also-shell-names",
        action="store_true",
        default=True,
        help="Also write CHANNEL_MANIFEST.json + INSTALL_STATE.local.json for shell load paths",
    )
    p.add_argument(
        "--no-shell-names",
        action="store_true",
        help="Only emit .local / .sample names",
    )
    args = p.parse_args(argv)

    missing = [
        name
        for name, val in (
            ("--install-root or R5F_INSTALL_PATH", args.install_root),
            ("--basetypes or R5F_UNIFY_BASETYPES", args.basetypes),
            ("--client-meta or R5F_BUILDS_ROOT", args.client_meta),
            ("--server-meta or R5F_BUILDS_ROOT", args.server_meta),
        )
        if val is None
    ]
    if missing:
        raise SystemExit(
            "missing local paths (not stored in git):\n  " + "\n  ".join(missing)
        )

    sdk_version = read_sdk_version(args.basetypes)
    client_meta = load_json(args.client_meta)
    server_meta = load_json(args.server_meta)
    client_cat = (
        load_json(args.client_catalog)
        if args.client_catalog is not None and args.client_catalog.is_file()
        else None
    )
    server_cat = (
        load_json(args.server_catalog)
        if args.server_catalog is not None and args.server_catalog.is_file()
        else None
    )

    client = tip_from_meta(client_meta, client_cat, "client")
    server = tip_from_meta(server_meta, server_cat, "server")

    channel = build_channel(
        sdk_version,
        client,
        server,
        args.install_root,
        marketing_tag=args.marketing_tag,
    )
    install = build_install_state(sdk_version, client, server, args.install_root)

    out = args.out_dir
    write_json(out / "CHANNEL_MANIFEST.local.json", channel)
    write_json(out / "INSTALL_STATE.local.json", install)

    if args.also_shell_names and not args.no_shell_names:
        write_json(out / "CHANNEL_MANIFEST.json", channel)

    print(f"sdk_version={sdk_version}")
    print(f"client content_hash={client['content_hash']}")
    print(f"server content_hash={server['content_hash']}")
    print(f"install_root={args.install_root}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
