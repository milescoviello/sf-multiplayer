===================================================================
  Stick Fight Online - install (Windows)
===================================================================

INSTALL (once):
  1) Extract this zip anywhere.
  2) Double-click  INSTALL-sf-multiplayer.bat  and accept the admin prompt.
  3) When it says INSTALL COMPLETE you're done.

PLAY:
  Use the "Stick Fight Online" shortcut (desktop or Start menu). Each start
  it checks for a mod update (a few KB), installs it if there is one, then
  starts the game on the server with the lobby list open (F2 opens it again).
  Updates are automatic and never need admin.

  Starting Stick Fight from Steam is the normal game, as before.

WHAT IT CHANGES:
  - Nothing in the game's own files. (If an older version of this mod had
    patched the game file, the original is put back.)
  - Installs BepInEx 5.4.23.5 if you don't have it (other mods are kept).
  - Adds the SFClientRecon and SFServerBrowser plugins. They do nothing
    unless the game was started with the "Stick Fight Online" shortcut.
  - Adds <game>\sf-online\ (the launcher and the online game file it uses),
    a Windows Firewall rule for Stick Fight, and the two shortcuts.

UNDO:
  Double-click  UNINSTALL-sf-multiplayer.bat : removes only this mod (and
  BepInEx, if this installer added it and no other mods use it), the
  launcher, the firewall rule and the shortcuts.

Problems: <game>\sf-online\launch.log says what the launcher did.
BepInEx: https://github.com/BepInEx/BepInEx (LGPL-2.1), bundled unmodified.
