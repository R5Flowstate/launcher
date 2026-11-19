"""Stage the Microsoft runtimes the game needs on a fresh Windows install.

Output (tools/redist, gitignored):
  dx/         trimmed DirectX June 2010 redist: DXSETUP + XInput 1.3 + D3DCompiler_43
  vc_redist.x64.exe

The DirectX redist may only be redistributed through DXSETUP, which accepts a
folder holding any subset of its cabs plus the setup files. XInput 1.3 is the
APR2007 cab; there is no Jun2010 XInput cab.

Usage: py -3 tools/fetch_redist.py [--force]
"""

import argparse
import hashlib
import shutil
import subprocess
import sys
import tempfile
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "tools" / "redist"

DOWNLOADS = {
	"directx_Jun2010_redist.exe": (
		"https://download.microsoft.com/download/8/4/A/84A35BF1-DAFE-4AE8-82AF-AD2AE20B6B14/directx_Jun2010_redist.exe",
		"053f76dcbb28802e23341b6a787e3b0791c0fa5c8d4d011b1044172dbf89c73b",
	),
	# Versioned link behind aka.ms/vs/17/release/vc_redist.x64.exe (14.44.35211).
	"vc_redist.x64.exe": (
		"https://download.visualstudio.microsoft.com/download/pr/990c720a-46ce-4dc7-87b9-c2eb615d34b9/CC0FF0EB1DC3F5188AE6300FAEF32BF5BEEBA4BDD6E8E445A9184072096B713B/VC_redist.x64.exe",
		"cc0ff0eb1dc3f5188ae6300faef32bf5beeba4bdd6e8e445a9184072096b713b",
	),
}

DX_FILES = (
	"DXSETUP.exe",
	"DSETUP.dll",
	"dsetup32.dll",
	"dxupdate.cab",
	"dxdllreg_x86.cab",
	"APR2007_xinput_x64.cab",
	"APR2007_xinput_x86.cab",
	"Jun2010_D3DCompiler_43_x64.cab",
	"Jun2010_D3DCompiler_43_x86.cab",
)


def sha256(path: Path) -> str:
	h = hashlib.sha256()
	with path.open("rb") as f:
		for chunk in iter(lambda: f.read(1 << 20), b""):
			h.update(chunk)
	return h.hexdigest()


def fetch(name: str, dest: Path) -> Path:
	url, want = DOWNLOADS[name]
	path = dest / name
	print(f"download {name}")
	with urllib.request.urlopen(url, timeout=120) as r, path.open("wb") as f:
		shutil.copyfileobj(r, f)
	got = sha256(path)
	if got != want:
		sys.exit(f"ERROR: {name} sha256 {got} != pinned {want}; verify the new file's "
			f"Microsoft signature before updating the pin")
	return path


def staged() -> bool:
	return (OUT / "vc_redist.x64.exe").is_file() and all((OUT / "dx" / n).is_file() for n in DX_FILES)


def main() -> int:
	ap = argparse.ArgumentParser()
	ap.add_argument("--force", action="store_true", help="re-download even if staged")
	args = ap.parse_args()

	if staged() and not args.force:
		print(f"redist already staged in {OUT}")
		return 0

	if sys.platform != "win32":
		sys.exit("ERROR: the DirectX redist is a Windows self-extractor; run this on Windows")

	with tempfile.TemporaryDirectory(prefix="r5f_redist_") as tmp:
		tmp = Path(tmp)
		dx_sfx = fetch("directx_Jun2010_redist.exe", tmp)
		vc = fetch("vc_redist.x64.exe", tmp)

		extract = tmp / "dx"
		extract.mkdir()
		rc = subprocess.run([str(dx_sfx), "/Q", f"/T:{extract}"], check=False).returncode
		if rc != 0:
			sys.exit(f"ERROR: DirectX redist extract failed (exit {rc})")

		lower = {p.name.lower(): p for p in extract.iterdir()}
		missing = [n for n in DX_FILES if n.lower() not in lower]
		if missing:
			sys.exit(f"ERROR: DirectX redist is missing {missing}")

		if OUT.exists():
			shutil.rmtree(OUT)
		(OUT / "dx").mkdir(parents=True)
		for n in DX_FILES:
			shutil.copy2(lower[n.lower()], OUT / "dx" / n)
		shutil.copy2(vc, OUT / "vc_redist.x64.exe")

	total = sum(p.stat().st_size for p in OUT.rglob("*") if p.is_file())
	print(f"staged {len(DX_FILES) + 1} files ({total / 1e6:.1f} MB) in {OUT}")
	return 0


if __name__ == "__main__":
	sys.exit(main())
