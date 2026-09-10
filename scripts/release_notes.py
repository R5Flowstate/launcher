"""Print the LAUNCHER_NOTES entry for a version as the GitHub release body."""
import json
import pathlib
import sys

NOTES = pathlib.Path(__file__).resolve().parent.parent / "src" / "R5Flowstate.Shell" / "Notes" / "LAUNCHER_NOTES.json"


def main() -> int:
    if len(sys.argv) != 2:
        print("usage: release_notes.py <version>", file=sys.stderr)
        return 2

    version = sys.argv[1]
    entries = json.loads(NOTES.read_text(encoding="utf-8")).get("entries", [])
    entry = next((e for e in entries if version in e.get("title", "")), None)
    if entry is None:
        print(f"no LAUNCHER_NOTES entry titled for {version}", file=sys.stderr)
        return 1

    print(f"### {entry.get('title', version)}\n")
    for item in entry.get("items", []):
        print(f"- {item}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
