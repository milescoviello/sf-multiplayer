using System;
using HarmonyLib;
using UnityEngine;

namespace SFClientRecon
{
    // Where do snake targets go? The server's snake target is a 3-byte
    // ObjectUpdate ([u16 object][u8 slot], msgType 26, channel 10; objects get
    // it with the 9-byte packet trailer, 12 bytes). Counted as
    // it arrives (receive thread) and as SyncableObjectManager routes it to an
    // object (main thread); the first few routings are logged with what they
    // found, so a lost target shows the step it was lost at.
    public partial class Plugin
    {
        private static long _snakeMsgsRx;
        private static int _snakeRouteLogs;
        private static long _snakeMsgsLogged;
        private static float _snakeDiagAt;
        private void TickSnakeDiag()
        {
            if (Time.realtimeSinceStartup < _snakeDiagAt) return;
            _snakeDiagAt = Time.realtimeSinceStartup + 10f;
            long n = System.Threading.Interlocked.Read(ref _snakeMsgsRx);
            if (n != _snakeMsgsLogged) { Log.LogInfo($"[snake-diag] snake target packets received: {n}"); _snakeMsgsLogged = n; }
        }

        internal static void NoteSnakeTargetPacket(byte[] pkt, int n)
        {
            if (n == 17 && pkt[4] == 26) System.Threading.Interlocked.Increment(ref _snakeMsgsRx);
        }

        private void InstallSnakeDiag()
        {
            try
            {
                var somType = CachedType("SyncableObjectManager");
                var m = (object)somType != null ? AccessTools.Method(somType, "ReceivedPackage") : null;
                if ((object)m == null) { Log.LogWarning("[snake-diag] SyncableObjectManager.ReceivedPackage not found."); return; }
                new Harmony(PluginGuid + ".snake-diag").Patch(m, prefix: new HarmonyMethod(AccessTools.Method(typeof(Plugin), nameof(SomReceivedPackage_DiagPrefix))));
            }
            catch (Exception e) { Log.LogWarning($"[snake-diag] install: {e.Message}"); }
        }

        internal static void SomReceivedPackage_DiagPrefix(object msgType, byte[] data)
        {
            try
            {
                if (data == null || (data.Length != 3 && data.Length != 12) || Convert.ToInt32(msgType) != 26 || _snakeRouteLogs >= 8) return;
                _snakeRouteLogs++;
                ushort idx = (ushort)(data[0] | (data[1] << 8));
                var mmType = CachedType("MultiplayerManager");
                var mm = (object)mmType != null ? UnityEngine.Object.FindObjectOfType(mmType) : null;
                var get = (object)mmType != null ? AccessTools.Method(mmType, "GetSyncedObject") : null;
                var nso = (object)mm != null && (object)get != null ? get.Invoke(mm, new object[] { idx }) as Behaviour : null;
                string what = (object)nso == null ? "no object" : nso.name + " active=" + nso.isActiveAndEnabled
                    + " listening=" + AccessTools.Property(nso.GetType(), "ListeningForPackages").GetValue(nso, null)
                    + " snakeAI=" + ((object)CachedType("SnakeAI") != null && nso.GetComponent(CachedType("SnakeAI")) != null);
                Log.LogInfo($"[snake-diag] target packet for object {idx} slot {data[2]}: {what} (received {System.Threading.Interlocked.Read(ref _snakeMsgsRx)} so far)");
            }
            catch (Exception e) { Log.LogWarning($"[snake-diag] {e.Message}"); }
        }
    }
}
