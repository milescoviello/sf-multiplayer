# Contributing to sf-multiplayer

Thanks for helping improve Stick Fight Online.

## Ground rules
- Be respectful: see the [Code of Conduct](CODE_OF_CONDUCT.md).
- `main` is protected: changes land through a pull request reviewed by the
  maintainer (@milescoviello).

## Project layout
The "For developers" section of the [README](../README.md#for-developers) maps
the folders. Current state and open work: [`NEXT_STEPS.md`](../NEXT_STEPS.md).

## Building
- **Plugins** (`sf-headless-host`, `sf-box-fix`, `sf-client-recon`,
  `sf-server-browser`): BepInEx 5 assemblies for the game's Unity 5.6 / Mono 2.0.
  Each plugin folder needs a `refs/` folder with `Assembly-CSharp.dll` and
  `UnityEngine.dll` (from your own `StickFight_Data/Managed/`) and `BepInEx.dll` +
  `0Harmony.dll` (from BepInEx 5.4). They aren't in the repo because the game
  files are Landfall's. Then `dotnet build -c Release` in the plugin folder.
  Run `tools/check-mono2-landmines.sh` before you ship: some C# compiles but
  breaks under Mono 2.0.
- **Router** (`sf-router/`): `go build ./... && go test -race ./...`.
- **Python tooling:** `python -m pytest test_serve_lobbies.py test_sf_monitor.py`.

CI runs the router and Python tests on every push and pull request. It can't
build the plugins (the reference DLLs can't be on a public runner).

## Pull requests
1. Branch off `main`.
2. Keep changes focused; match the surrounding code style.
3. If you touch the installer or the player plugins, **test on Windows**
   (install, play, uninstall).
4. Update the docs (`WHATS_NEW.md`, `NEXT_STEPS.md`, the README) when behavior changes.
5. Open the PR, fill out the template, and wait for review.

## Reporting
Use the issue templates for bugs and feature requests. For security problems,
don't open a public issue: see [SECURITY.md](SECURITY.md).
