using System;
using System.Collections;
using HarmonyLib;
using UnityEngine;

namespace SFClientRecon
{
    // The game indexes its player list (MultiplayerManager.m_Players /
    // PlayerControllers) with SLOT numbers from the network: the round winner
    // (crown, win text, win counter), a player falling out, a player leaving,
    // a snake's target. In stock the list is in slot order because players
    // join in slot order; we add the other players' copies before our own, so
    // on many clients index != slot and the win, the fall-out or the removal
    // landed on the wrong player. Keep the list sorted by Controller.playerID
    // (= slot) after every spawn, and translate the winner by slot so a gap
    // (a player who left) can't shift it either.
    public partial class Plugin
    {
        private static System.Reflection.FieldInfo _mmPlayersListF;

        private void InstallPlayerOrderFix()
        {
            try
            {
                var mmType = CachedType("MultiplayerManager");
                var gmType = CachedType("GameManager");
                _mmPlayersListF = (object)mmType != null ? AccessTools.Field(mmType, "m_Players") : null;
                if ((object)_mmPlayersListF == null) { Log.LogWarning("[players] m_Players not found: order fix off."); return; }
                var h = new Harmony(PluginGuid + ".player-order");
                var post = new HarmonyMethod(AccessTools.Method(typeof(Plugin), nameof(SortPlayersBySlot_Postfix)));
                foreach (var name in new[] { "SpawnPlayerDummy", "OnPlayerSpawned" })
                {
                    var m = AccessTools.Method(mmType, name);
                    if ((object)m != null) h.Patch(m, postfix: post);
                }
                var died = (object)gmType != null ? AccessTools.Method(gmType, "NetworkAllPlayersDiedButOne") : null;
                if ((object)died != null)
                    h.Patch(died, prefix: new HarmonyMethod(AccessTools.Method(typeof(Plugin), nameof(WinnerBySlot_Prefix))));
                Log.LogInfo("[players] player list kept in slot order; round winner applied by slot.");
            }
            catch (Exception e) { Log.LogWarning($"[players] install: {e.Message}"); }
        }

        private static int SlotOf(object controller)
        {
            var c = controller as Component;
            if ((object)c == null || !c) return int.MaxValue;
            var f = AccessTools.Field(c.GetType(), "playerID");
            return (object)f != null ? (int)f.GetValue(c) : int.MaxValue;
        }

        internal static void SortPlayersBySlot_Postfix(object __instance)
        {
            try
            {
                var list = _mmPlayersListF.GetValue(__instance) as IList;
                if (list == null || list.Count < 2) return;
                // insertion sort (a handful of players), stable
                for (int i = 1; i < list.Count; i++)
                {
                    object cur = list[i];
                    int k = SlotOf(cur), j = i - 1;
                    while (j >= 0 && SlotOf(list[j]) > k) { list[j + 1] = list[j]; j--; }
                    list[j + 1] = cur;
                }
            }
            catch (Exception e) { Log.LogWarning($"[players] sort: {e.Message}"); }
        }

        internal static void WinnerBySlot_Prefix(ref byte winner)
        {
            try
            {
                if (winner == byte.MaxValue) return;
                var mmType = CachedType("MultiplayerManager");
                var mm = (object)mmType != null ? UnityEngine.Object.FindObjectOfType(mmType) : null;
                if (mm == null) return;
                var list = _mmPlayersListF.GetValue(mm) as IList;
                if (list == null) return;
                for (int i = 0; i < list.Count; i++)
                    if (SlotOf(list[i]) == winner) { winner = (byte)i; return; }
            }
            catch { }
        }
    }
}
