===================================================================
  Stick Fight Online (sf-multiplayer) - install
===================================================================

INSTALL (once):
  1) Extract this zip anywhere.
  2) Double-click  INSTALL-sf-multiplayer.bat  and accept the admin prompt.
  3) When it says INSTALL COMPLETE you're done.

PLAY:
  Use the "Stick Fight Online" shortcut (desktop or Start menu).
  Each start it checks for a mod update (a few KB), installs it if there is
  one, then starts the game on the server. Nothing else to do: updates are
  automatic. In game: PLAY ONLINE, or F2 for the lobby list.

  (Starting Stick Fight from Steam still runs the mod, but skips the update
  check and needs the launch options "-address <server> -port <port>".)

WHAT IT CHANGES:
  - Backs up your Assembly-CSharp.dll (once) and installs the patched one.
  - Installs BepInEx 5.4.23.5 if you don't have it (other mods are kept).
  - Adds the SFClientRecon and SFServerBrowser plugins.
  - Adds <game>\sf-online\ (the launcher) and the two shortcuts.

UNDO:
  Double-click  UNINSTALL-sf-multiplayer.bat : restores your original game
  file, removes only this mod (and BepInEx, if this installer added it and no
  other mods use it), the launcher and the shortcuts.

Problems: <game>\sf-online\launch.log says what the launcher did.
BepInEx: https://github.com/BepInEx/BepInEx (LGPL-2.1), bundled unmodified.
