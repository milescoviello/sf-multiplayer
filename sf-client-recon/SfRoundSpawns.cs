using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace SFClientRecon
{
    // COMP random spawns. Stock StartMapSequence moves player #k (controller
    // order) to currentMapInfo.spawnPoints[k] through GameManager.MovePlayer, so
    // the same player always gets the same side. In the COMP lobby the server
    // sends msgType 44 before each MapChange: [u8 count] + count x [u8 slot,
    // u8 spawnIndex]. Every client applies the same assignment to every player,
    // so all screens agree on who spawns where.
    public partial class Plugin
    {
        private const byte PktRoundSpawns = 44;
        private static volatile Dictionary<int, int> _roundSpawns;   // slot -> spawn index (set on the RX thread)
        private static DateTime _roundSpawnsAtUtc;
        private static Type _gmTypeSp, _npTypeSp, _ctrlTypeSp;
        private static int _spawnLogs;

        // RX thread: parse only, no Unity calls.
        private static void HandleRoundSpawnsPacket(byte[] pkt)
        {
            int off = 5, len = pkt.Length - 14;
            if (len < 1) return;
            int count = pkt[off];
            if (count <= 0 || count > 4 || len < 1 + count * 2) return;
            var map = new Dictionary<int, int>();
            for (int i = 0; i < count; i++) map[pkt[off + 1 + i * 2]] = pkt[off + 2 + i * 2];
            _roundSpawnsAtUtc = DateTime.UtcNow;
            _roundSpawns = map;
        }

        private void InstallRoundSpawnPatch()
        {
            try
            {
                _gmTypeSp = CachedType("GameManager");
                _npTypeSp = CachedType("NetworkPlayer");
                _ctrlTypeSp = CachedType("Controller");
                var move = RefOk(_gmTypeSp) ? AccessTools.Method(_gmTypeSp, "MovePlayer") : null;
                if (!RefOk(move)) { Log.LogWarning("[spawns] GameManager.MovePlayer not found; COMP spawn shuffle off."); return; }
                new Harmony(PluginGuid + ".round-spawns").Patch(move,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(Plugin), nameof(MovePlayer_RoundSpawnPrefix))));
                Log.LogInfo("[spawns] Patched GameManager.MovePlayer (COMP spawn shuffle when the server sends one).");
            }
            catch (Exception e) { Log.LogWarning($"[spawns] patch failed: {e.Message}"); }
        }

        // GameManager.MovePlayer(Rigidbody player, Vector3 targetPosition) is an
        // iterator: a prefix that rewrites the argument runs before the state
        // machine captures it.
        internal static void MovePlayer_RoundSpawnPrefix(object __instance, Rigidbody player, ref Vector3 targetPosition)
        {
            try
            {
                var map = _roundSpawns;
                if (map == null || player == null || (DateTime.UtcNow - _roundSpawnsAtUtc).TotalSeconds > 15) return;
                var np = RefOk(_npTypeSp) ? player.transform.root.GetComponentInChildren(_npTypeSp) : null;
                if (np == null) return;
                var idP = AccessTools.Property(_npTypeSp, "NetworkSpawnID");
                if (!RefOk(idP)) return;
                int slot = Convert.ToInt32(idP.GetValue(np, null));
                int idx;
                if (!map.TryGetValue(slot, out idx)) return;
                var mapInfo = AccessTools.Field(_gmTypeSp, "currentMapInfo")?.GetValue(__instance);
                var spawns = RefOk(mapInfo) ? AccessTools.Field(mapInfo.GetType(), "spawnPoints")?.GetValue(mapInfo) as Transform[] : null;
                if (spawns == null || idx < 0 || idx >= spawns.Length || spawns[idx] == null) return;
                targetPosition = spawns[idx].localPosition;
                if (_spawnLogs++ < 12) Log.LogInfo($"[spawns] slot {slot} -> spawn {idx} at {targetPosition}");
            }
            catch (Exception e) { Log.LogWarning($"[spawns] {e.Message}"); }
        }
    }
}
