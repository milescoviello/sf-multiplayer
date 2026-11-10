# sf-online-patcher

The online game code, built on each player's PC from their own copy of the game.

Stick Fight Online needs changes to the game's networking code: plain UDP to the server instead of Steam P2P, plus the lobby and UI messages that go with it. That code lives in Landfall's `Assembly-CSharp.dll`, so this repo can't ship a modified copy of it. Instead:

- **`SFOnlinePatcher.dll`** is a BepInEx preloader patcher (it goes in `BepInEx/patchers/SFOnline/`). When the game is started for the server (it gets `-address`), it builds the online game code **in memory** from the player's own stock `Assembly-CSharp.dll`, before the game loads it. A normal launch is left alone: the patcher returns without touching anything.
- **`SFOnline.donor`** (next to it) holds what changes: the online build with every method it doesn't change stripped out. It has about 630 changed methods and 44 added ones, out of about 5,700. Everything else stays Landfall's original compiled code.
- **`Transplant.cs`** does the work with Mono.Cecil (the copy BepInEx ships). It copies each changed method's body into the stock assembly, along with the compiler-generated helper classes those methods use and the fields and methods the online build added. It also applies the visibility and static/instance changes the online build made.

Before it changes anything, the patcher:
- checks that the game file is the stock Steam version (SHA-256 in the donor);
- does a dry run on a separate copy;
- logs one line: `[SFOnline] Online build ready: ...`.

If anything is wrong, it logs why and leaves the game as it is.

A server uses the same patcher with `SF_ONLINE_FORCE=1` (`deploy/start-oracle-server.sh` sets it), since a server isn't started with `-address`.

## Building

- `refs/` needs `BepInEx.dll` and `Mono.Cecil.dll` from BepInEx 5.4's `core/` folder (the installer's bundled `BepInEx_win_x86_5.4.23.5.zip` has them). Then `dotnet build -c Release`.
- The patcher runs inside the game's Mono 2.0, so `tools/check-mono2-landmines.sh` covers this folder too.
- The donor is built by [`tools/sf-online-donor`](../tools/sf-online-donor) from the stock and the online game files. Keep those in a local `private/` folder, which git ignores. The tool runs the same `Transplant.cs` on a fresh stock copy and checks the result against the online build:
  - every method's fingerprint matches;
  - every reference resolves;
  - there are no static/instance mismatches, empty bodies or duplicate members.

  `1-click-install/build.sh` runs it when `private/` has both files and `SF_MANAGED_DIR` points at a game's `StickFight_Data/Managed`.

## Compatibility

The online build keeps the stock assembly name, `Assembly-CSharp`, so mods compiled against the stock game work (QOL Mod was tested). A plugin compiled against the old shipped online file, which was named `Assembly-CSharp.srv`, can't resolve its references: rebuild it against the stock file.
