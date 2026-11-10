using System;
using System.Collections;
using HarmonyLib;
using UnityEngine;

namespace SFClientRecon
{
    // Snakes never bit anyone. The server picks a snake's target and sends its
    // SLOT (NewSnakeTarget: [u16 object][u8 target]); the game applies it as
    // PlayerControllers[target], an index into this client's own player list,
    // which is in join order, not slot order (we add the other players' copies
    // before our own player). So on many clients the snake chased someone
    // else, often the shooter, and since a bite only counts on the target's
    // own client, no snake ever did damage. Apply the target by slot instead
    // (Controller.playerID is the slot on every player object), and ignore the
    // position updates the server used to send for snakes (read as targets).
    public partial class Plugin
    {
        private static Type _snakeAiType;
        private static System.Reflection.FieldInfo _nsoSnakeCompF, _nsoNetMgrF;
        private static System.Reflection.MethodInfo _snakeForceTargetM;
        private static System.Reflection.PropertyInfo _mmPlayerCtlsP;
        private static int _snakeTargetsApplied;

        private void InstallSnakeTargetFix()
        {
            try
            {
                var nsoType = CachedType("NetworkSyncableObject");
                _snakeAiType = CachedType("SnakeAI");
                var mmType = CachedType("MultiplayerManager");
                var sync = (object)nsoType != null ? AccessTools.Method(nsoType, "SyncObjectState") : null;
                _nsoSnakeCompF = (object)nsoType != null ? AccessTools.Field(nsoType, "mSnakeComponent") : null;
                _nsoNetMgrF = (object)nsoType != null ? AccessTools.Field(nsoType, "mNetworkManager") : null;
                _snakeForceTargetM = (object)_snakeAiType != null ? AccessTools.Method(_snakeAiType, "NetworkForceNewTarget") : null;
                _mmPlayerCtlsP = (object)mmType != null ? AccessTools.Property(mmType, "PlayerControllers") : null;
                if ((object)sync == null || (object)_nsoSnakeCompF == null || (object)_nsoNetMgrF == null || (object)_snakeForceTargetM == null || (object)_mmPlayerCtlsP == null)
                { Log.LogWarning("[snake] target fix not installed (game types not found)."); return; }
                new Harmony(PluginGuid + ".snake-target").Patch(sync,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(Plugin), nameof(SyncObjectState_SnakePrefix))));
                Log.LogInfo("[snake] Patched NetworkSyncableObject.SyncObjectState (snake targets by slot).");
            }
            catch (Exception e) { Log.LogWarning($"[snake] install: {e.Message}"); }
        }

        internal static bool SyncObjectState_SnakePrefix(object __instance, byte[] data)
        {
            try
            {
                if (data == null) return true;
                var comp = __instance as Component;
                if ((object)comp == null || comp.GetComponent(_snakeAiType) == null) return true;
                // A snake's only message is its target ([u16 object][u8 slot]).
                // This build hands objects the body plus the packet's 9-byte
                // trailer ([u64 sender][u8 channel]), so a target arrives as 12
                // bytes (3 without it). Anything else is a position update the
                // server shouldn't send for a snake; the game would read a
                // position byte as the target (often 255 = nobody), so ignore it.
                // (This check once required exactly 3 bytes and so dropped every
                // real target: snakes never chased anyone on clients.)
                if (data.Length != 3 && data.Length != 3 + 9) return false;
                var snake = _nsoSnakeCompF.GetValue(__instance);
                var mm = _nsoNetMgrF.GetValue(__instance);
                if (snake == null || mm == null) return true;
                byte slot = data[2];
                Component target = null;
                var players = _mmPlayerCtlsP.GetValue(mm, null) as IList;
                if (players != null && slot != byte.MaxValue)
                    foreach (var p in players)
                    {
                        var c = p as Component;
                        if ((object)c == null || !c) continue;
                        var idF = AccessTools.Field(c.GetType(), "playerID");
                        if ((object)idF != null && (int)idF.GetValue(c) == slot) { target = c; break; }
                    }
                _snakeForceTargetM.Invoke(snake, new object[] { target });
                if (_snakeTargetsApplied++ < 5) Log.LogInfo($"[snake] target slot {slot} → {(target != null ? target.name : "none")}");
                return false;
            }
            catch (Exception e) { Log.LogWarning($"[snake] target: {e.Message}"); return true; }
        }
    }
}
