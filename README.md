# R5Flowstate Launcher

Thin Windows shell for R5Flowstate. It is not the game.

Players install the shell with `R5FlowstateSetup.exe`, then pick a separate
folder for game content. The game never lives inside the launcher tree or
Program Files.

This repo is the shell only. No paks, no audio, no `r5apex.exe`.

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

## License

MIT. `tools/7za` is 7-Zip Extra (LGPL), see `tools/7za/COPYING` and
`tools/7za/7za-SOURCE.txt`.
