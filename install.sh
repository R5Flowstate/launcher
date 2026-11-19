#!/usr/bin/env bash
#
# install.sh — install the R5Flowstate Linux launcher for the current user.
#
# Windows players get R5FlowstateSetup.exe. On Linux there is no such program:
# the launcher is built from source here, so this script is the setup program.
# It publishes the launcher, puts it under ~/.local/opt, links it into
# ~/.local/bin, and asks the launcher itself to write its menu entries (which is
# also where the "EA App" entry comes from, once the prefix has EA Desktop).
#
# It also publishes the loadscreen art host (src/R5Flowstate.ArtHost, win-x64)
# into winhost/r5f-arthost/ beside the launcher. That is not optional decoration:
# retail loadscreens are Oodle-encoded and the decoder is a PE DLL a native Linux
# process cannot load, so this Windows exe is the only thing that can turn a map's
# art into pixels. Without it the Play tab's hero and map tiles are blank.
#
# Beside it goes the hosted console relay (src/R5Flowstate.ConsoleRelay, win-x64)
# in a folder of its own, winhost/r5f-relay/, and for the same reason: the game's
# console is two Windows named pipes, and only a process inside the prefix can own
# them. Without it the Console tab shows an empty pane — and the game opens console
# windows of its own instead. The two helpers get one folder each because both are
# self-contained *trimmed* publishes and the trimmer rewrites each app's framework
# differently; see the note above the relay's publish step for what sharing a
# folder cost.
#
#   ./install.sh                          # build and install for this user
#   ./install.sh --prefix ~/apps/r5f      # somewhere else
#   ./install.sh --no-shortcuts           # skip the menu entries
#   ./install.sh --no-warm                # skip the art prefix pre-warm
#   ./install.sh --uninstall              # remove the launcher and its entries
#
# It writes inside the install prefix, ~/.local/bin, the .desktop entry/icon
# directories, and the art host's own two: ~/.local/share/r5flowstate/art-prefix
# (its Proton prefix) and ~/.cache/r5flowstate/art (decoded pictures). It never
# touches the game install, the game's own Proton prefix, or anything belonging
# to EA.
set -euo pipefail

PROJECT="src/R5Flowstate.Shell.Linux"
ARTHOST="src/R5Flowstate.ArtHost"
RELAY="src/R5Flowstate.ConsoleRelay"
APP="r5flowstate"
FRIENDLY="R5Flowstate"

PREFIX="${HOME}/.local/opt/r5flowstate"
BIN_DIR="${HOME}/.local/bin"
DO_SHORTCUTS=1
DO_WARM=1
DO_UNINSTALL=0

# The staging directory is created only on the install path; the trap has to be
# harmless on the uninstall path, which never makes one.
STAGE=""
cleanup() { [ -n "$STAGE" ] && rm -rf "$STAGE"; return 0; }
trap cleanup EXIT

die() { printf 'error: %s\n' "$*" >&2; exit 1; }
note() { printf '  %s\n' "$*"; }

while [ $# -gt 0 ]; do
    case "$1" in
        --prefix)       [ $# -ge 2 ] || die "--prefix needs a directory"; PREFIX="$2"; shift 2 ;;
        --prefix=*)     PREFIX="${1#*=}"; shift ;;
        --bin-dir)      [ $# -ge 2 ] || die "--bin-dir needs a directory"; BIN_DIR="$2"; shift 2 ;;
        --bin-dir=*)    BIN_DIR="${1#*=}"; shift ;;
        --no-shortcuts) DO_SHORTCUTS=0; shift ;;
        --no-warm)      DO_WARM=0; shift ;;
        --uninstall)    DO_UNINSTALL=1; shift ;;
        -h|--help)      awk 'NR>1 && /^#/ { sub(/^# ?/, ""); print; next } NR>1 { exit }' "$0"; exit 0 ;;
        *)              die "unknown option: $1 (try --help)" ;;
    esac
done

# Expand a leading ~ so --prefix '~/x' behaves the way a shell user expects.
case "$PREFIX" in "~"*) PREFIX="${HOME}${PREFIX#\~}" ;; esac
case "$BIN_DIR" in "~"*) BIN_DIR="${HOME}${BIN_DIR#\~}" ;; esac

LINK="${BIN_DIR}/${APP}"

# ---------------------------------------------------------------- uninstall
if [ "$DO_UNINSTALL" = 1 ]; then
    echo "Uninstalling ${FRIENDLY} from ${PREFIX}"
    # The entries come off through the launcher that wrote them, before the
    # binary that would do it is deleted. A launcher that is already gone still
    # leaves the entries removable by hand, and --no-shortcuts runs skip this.
    if [ -x "${PREFIX}/R5Flowstate.Shell.Linux" ]; then
        if ! "${PREFIX}/R5Flowstate.Shell.Linux" --install-shortcuts --uninstall; then
            note "the launcher could not remove its menu entries. Remove"
            note "${HOME}/.local/share/applications/r5flowstate*.desktop by hand."
        fi
    else
        note "no launcher at ${PREFIX}: nothing to ask about the menu entries."
    fi
    [ -L "$LINK" ] && rm -f "$LINK" && note "removed ${LINK}"
    if [ -d "$PREFIX" ]; then
        rm -rf "$PREFIX"
        note "removed ${PREFIX}"
    fi
    # Named rather than removed. The art cache and the art host's prefix are the
    # launcher's own, and leaving them means a reinstall has art on the first Play
    # press instead of decoding it again — but a silent leftover is exactly what an
    # uninstall is not allowed to be.
    if [ -d "${HOME}/.cache/r5flowstate/art" ]; then
        note "left in place: ${HOME}/.cache/r5flowstate/art (decoded loadscreens)"
    fi
    if [ -d "${HOME}/.local/share/r5flowstate" ]; then
        note "left in place: ${HOME}/.local/share/r5flowstate (the art host's own prefix)"
    fi
    note "game files, the game's Proton prefix, your settings and EA are untouched."
    exit 0
fi

# ------------------------------------------------------------------- check
command -v dotnet >/dev/null 2>&1 || \
    die "the .NET 8 SDK is required to build the launcher (dotnet not found)."
[ -d "$PROJECT" ] || die "run this from the repository root (no ${PROJECT})."
[ -d "$ARTHOST" ] || die "run this from the repository root (no ${ARTHOST})."
[ -d "$RELAY" ] || die "run this from the repository root (no ${RELAY})."

# ------------------------------------------------------------------- build
STAGE="$(mktemp -d "${TMPDIR:-/tmp}/r5f-install.XXXXXX")"

echo "Building ${FRIENDLY} (Release)…"
dotnet publish "$PROJECT" -c Release -o "$STAGE" --nologo -v quiet
[ -x "${STAGE}/R5Flowstate.Shell.Linux" ] || \
    die "the publish step produced no launcher binary."

# The art host, into its own folder under winhost/ — the tree WinHost.Find looks
# in first. It is a win-x64 publish, so it lands as a folder of ~23 MB next to a
# ~40 MB launcher; the whole point is that it is the one process where Oodle loads.
echo "Building the loadscreen art host (win-x64)…"
dotnet publish "$ARTHOST" -c Release -o "${STAGE}/winhost/r5f-arthost" --nologo -v quiet
[ -f "${STAGE}/winhost/r5f-arthost/r5f-arthost.exe" ] || \
    die "the art host publish produced no r5f-arthost.exe."
# Not fatal: without the DLL only uncompressed loadscreens decode, which is a
# launcher that mostly works rather than one that cannot start. Said out loud
# because the failure it causes (blank map art) looks nothing like its cause.
if [ ! -f "${STAGE}/winhost/r5f-arthost/oo2core_8_win64.dll" ]; then
    note "warning: oo2core_8_win64.dll is not beside the art host, so only"
    note "uncompressed loadscreens will decode. Put it in tools/oo2core/ and"
    note "re-run, or copy it into ${PREFIX}/winhost/r5f-arthost/ by hand."
fi

# The console relay, into a folder of its own — NOT the art host's, and that is not
# tidiness. Both are self-contained trimmed publishes: the trimmer rewrites each
# app's copy of the framework it uses, so the same file name holds different bytes
# in the two outputs. Publishing the relay into the host's folder put the relay's
# System.Console.dll on top of the host's, and the host then died at its first
# Console.WriteLine (MissingMethodException, exit 82) before writing a single
# record — which reached the player as "the art host did not report this one" for
# every map, with no art anywhere and no decode ever attempted. Two trimmed
# self-contained apps need two folders; anything else is a silent mixture.
echo "Building the hosted console relay (win-x64)…"
dotnet publish "$RELAY" -c Release -o "${STAGE}/winhost/r5f-relay" --nologo -v quiet
[ -f "${STAGE}/winhost/r5f-relay/r5f-relay.exe" ] || \
    die "the console relay publish produced no r5f-relay.exe."

note "winhost: $(du -sh "${STAGE}/winhost" | cut -f1)"

# --------------------------------------------------------------- install
echo "Installing to ${PREFIX}"
mkdir -p "$PREFIX" "$BIN_DIR"
# Publish into place, then swap: a failure part-way through leaves the previous
# install intact rather than a half-copied one.
NEW="${PREFIX}.new"
rm -rf "$NEW"
mkdir -p "$NEW"
cp -a "${STAGE}/." "$NEW/"
rm -rf "${PREFIX}.old"
if [ -d "$PREFIX" ]; then mv "$PREFIX" "${PREFIX}.old"; fi
mv "$NEW" "$PREFIX"
rm -rf "${PREFIX}.old"
note "$PREFIX/R5Flowstate.Shell.Linux"

ln -sfn "${PREFIX}/R5Flowstate.Shell.Linux" "$LINK"
note "linked ${LINK}"

# ------------------------------------------------------------- menu entries
if [ "$DO_SHORTCUTS" = 1 ]; then
    echo "Writing menu entries"
    # The entry points at the symlink in ~/.local/bin, not at the publish output
    # inside the prefix: the symlink is the stable name, and it survives the next
    # install replacing the directory underneath it.
    if ! "$LINK" --install-shortcuts --exec "$LINK"; then
        note "the entries could not be written here. The launcher's Settings panel"
        note "has a Create menu shortcuts button that does the same thing."
    fi
else
    note "skipped the menu entries (--no-shortcuts). The first run writes them."
fi

# ------------------------------------------------------------- art warm-up
# The art host runs in a Proton prefix of its own, and the first decode in a fresh
# one pays Proton's prefix creation before it decodes anything. Doing it here means
# the first Play press does not show a blank hero pane while a prefix is built.
#
# Best-effort in three ways at once: no install configured yet, no Proton build,
# and a decode that simply fails are all "carry on with the install" — the launcher
# sets the prefix up on its own the first time it needs art.
if [ "$DO_WARM" = 1 ] && [ -x "$LINK" ]; then
    echo "Pre-warming the loadscreen art host (best effort)"
    if "$LINK" --art-probe >/dev/null 2>&1; then
        note "the art host's prefix is ready and one map is decoded"
    else
        note "skipped — no game install configured yet, or no Proton build found."
        note "The first Play press sets the art prefix up instead (a one-time pause)."
    fi
fi

# ------------------------------------------------------------------- done
echo
echo "${FRIENDLY} is installed. Start it with:"
echo "    ${APP}"
case ":${PATH}:" in
    *":${BIN_DIR}:"*) ;;
    *)
        echo
        echo "Note: ${BIN_DIR} is not in your PATH, so ${APP} will not resolve"
        echo "until you add it (fish: 'fish_add_path ${BIN_DIR}'). The menu entry"
        echo "works either way — it uses the full path."
        ;;
esac
