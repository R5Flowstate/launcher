# R5Flowstate Launcher — Linux (native)

Native Linux launcher for R5Flowstate. It is not the game.

The launcher itself runs natively on Linux — only the game and the EA App
run under Proton, in the game's prefix.

Players install the launcher with `./install.sh`, then pick a separate
folder for game content. The game never lives inside the launcher tree.
(On Windows, the same source builds the Windows shell and installs with
`R5FlowstateSetup.exe`.)

This repo is the launcher only. No paks, no audio, no `r5apex.exe`.

## Build

Needs the .NET 8 SDK.

```powershell
.\scripts\dev_build.ps1
.\scripts\dev_run.ps1
```

## Pack the installer

```powershell
.\scripts\pack_velopack.ps1
```

Writes the player wizard and the Velopack payload under `artifacts`. Does not
publish. Needs Inno Setup 6; the script downloads it if missing.

## Build and install (Linux)

Needs the .NET 8 SDK. There is no setup program on Linux, so `install.sh` is it:

```bash
./install.sh              # publish, install to ~/.local/opt, write menu entries
./install.sh --uninstall  # remove the launcher and its entries
```

It installs the launcher only — plus the two Windows helpers under `winhost/` (the art
host in `winhost/r5f-arthost/` and the console relay in `winhost/r5f-relay/`; see *One
runtime* below), which are what make map art and the Console tab work. They get a folder
each because both are self-contained *trimmed* publishes, and one folder for two of those
is a mixture that breaks whichever published first. The launcher then installs the game files into a folder you pick, and
sets up the EA App when you press Install on the EA row.

## One runtime: Proton

The game and the EA App both run under **Proton, in the game's prefix** — one Wine
prefix, so the two can see each other, which is what the game needs to prove its
account to a master server. There is no Wine setting any more: the split runtime (the
EA App under your system Wine in a prefix of its own) is gone, and a settings file that
still names it loads with a one-line notice and drops the keys on the next save. See
`docs/LINUX_FIXUPS.md` §24.

Two Windows helpers run through the same Proton build, because neither can be a native
Linux process: the **loadscreen art host** (`winhost/r5f-arthost/r5f-arthost.exe`, the
Oodle decoder, in a prefix of its own) and the **console relay**
(`winhost/r5f-relay/r5f-relay.exe`, which owns the game's two console pipes and bridges
them to the Console tab over loopback). `install.sh` publishes both, one folder each.

Proton is what runs the game, so the launcher needs a Proton build; if none is
found it can download **Proton-GE** for you (on by default, and switchable off in
Settings — the *Download Proton-GE* button is always there). Proton's own bundled Wine
(and only that one) is used to prepare the prefix with winetricks, so the EA App gets the
fonts and the VC++ redistributable it expects.

## License

MIT. `tools/7za` is 7-Zip Extra (LGPL), see `tools/7za/COPYING` and
`tools/7za/7za-SOURCE.txt`. `tools/oo2core` is RAD Game Tools Oodle;
see `tools/oo2core/NOTICE.txt`.
