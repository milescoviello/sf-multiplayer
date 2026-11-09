using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace SFClientRecon
{
    // Server chat commands with QOL Mod installed (it's allowed in DSF comp).
    // QOL prefixes ChatManager.SendChatMessage and treats EVERY "/" message as
    // its own command: an unknown one ("/score", "/discount") prints "command
    // not found" and never reaches the server. This prefix runs first and sends
    // our server's commands the stock way (NetworkPlayer.OnTalked, what
    // SendChatMessage itself does), then blanks the message so QOL ignores it.
    // Commands QOL also has (/help, /ping, /maps, /weapons) are left to QOL.
    // Without QOL nothing changes: the stock path would send them the same way.
    public partial class Plugin
    {
        private static readonly HashSet<string> ServerChatCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "score", "discount", "code", "room", "start", "restart", "next", "map", "listmaps",
            "players", "lobbies", "admin", "kick", "anticheat", "tickrate", "tick", "version",
            "replays", "list", "play", "watch",
        };
        private static FieldInfo _chatNpField, _npHasLocalCtlChatF;
        private static MethodInfo _npOnTalked;

        private void InstallServerChatCommandPatch()
        {
            try
            {
                var cmType = CachedType("ChatManager");
                var send = RefOk(cmType) ? AccessTools.Method(cmType, "SendChatMessage") : null;
                if (!RefOk(send)) { Log.LogWarning("[chat] ChatManager.SendChatMessage not found; server commands rely on the stock path."); return; }
                _chatNpField = AccessTools.Field(cmType, "m_NetworkPlayer");
                var npType = CachedType("NetworkPlayer");
                if (RefOk(npType))
                {
                    _npOnTalked = AccessTools.Method(npType, "OnTalked");
                    _npHasLocalCtlChatF = AccessTools.Field(npType, "mHasLocalControl");
                }
                var prefix = new HarmonyMethod(AccessTools.Method(typeof(Plugin), nameof(SendChatMessage_ServerCommandPrefix))) { priority = Priority.First };
                new Harmony(PluginGuid + ".server-chat").Patch(send, prefix: prefix);
                Log.LogInfo("[chat] Patched ChatManager.SendChatMessage (server commands reach the server with QOL Mod installed).");
            }
            catch (Exception e) { Log.LogWarning($"[chat] patch failed: {e.Message}"); }
        }

        internal static bool SendChatMessage_ServerCommandPrefix(object __instance, ref string message)
        {
            try
            {
                if (string.IsNullOrEmpty(message) || message[0] != '/' || !InitializedFromServer()) return true;
                int sp = message.IndexOf(' ');
                string cmd = (sp < 0 ? message.Substring(1) : message.Substring(1, sp - 1)).Trim();
                if (!ServerChatCommands.Contains(cmd)) return true;
                var np = RefOk(_chatNpField) ? _chatNpField.GetValue(__instance) : null;
                if (!RefOk(np) || !RefOk(_npOnTalked)) return true;
                if (RefOk(_npHasLocalCtlChatF) && !(bool)_npHasLocalCtlChatF.GetValue(np)) return true;
                _npOnTalked.Invoke(np, new object[] { message });
                message = "";      // anything later in the chain (QOL) sees an empty message
                return false;      // the stock send already happened above
            }
            catch (Exception e) { Log.LogWarning($"[chat] {e.Message}"); return true; }
        }
    }
}
