using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SFHeadlessHost
{
    // Bullet hits on crates, the vanilla way. The shooter's own game sends
    // PlayerForceAdded on channel 11 (NetworkSyncableObject.ForceAdded:
    // [u16 nso][u8 rigidbody][s8 y/100][s8 z/100][u8 ForceMode]) and the stock
    // host applies it to its copy (NetworkSyncableObject.SyncObjectForce).
    // A pistol hit is an impulse of 5000 (3.3 m/s on a 1500-mass crate), a
    // sniper 12000. The server used to drop these and shove "pushable" crates
    // a flat 2.5 m/s per shot from its own straight-line bullet instead, and
    // plain crates (Desert5's) not at all, while every client's local copy did
    // move: the two drifted apart. SFHEADLESS_CLIENT_CRATE_FORCE=0 restores it.
    // Checks: participants only, ForceMode.Impulse only (what bullets send; a
    // VelocityChange would be unbounded), the wire caps each axis at 12700,
    // and at most 40 per second per player.
    public partial class Plugin
    {
        private const byte ObjectForceChannel = 11;
        private const int ObjectForceBurst = 40;
        private static bool? _clientCrateForceCache;
        private static bool ClientCrateForce
        {
            get
            {
                if (_clientCrateForceCache == null)
                    _clientCrateForceCache = Environment.GetEnvironmentVariable("SFHEADLESS_CLIENT_CRATE_FORCE") != "0";
                return _clientCrateForceCache.Value;
            }
        }
        private static MethodInfo _nsoSyncForceM;
        private readonly Dictionary<int, float> _objForceTokens = new Dictionary<int, float>();
        private readonly Dictionary<int, float> _objForceTokensAt = new Dictionary<int, float>();
        private int _objForceApplied, _objForceDropped;

        private void ApplyClientObjectForce(SfClient cli, byte[] data, int off, int len, byte channel)
        {
            if (!ClientCrateForce || channel != ObjectForceChannel || !IsParticipant(cli) || len < 6) return;
            byte mode = data[off + 5];
            if (mode != (byte)ForceMode.Impulse) { DropObjectForce(cli, "mode " + mode); return; }
            float now = Time.realtimeSinceStartup;
            float tok, at;
            if (!_objForceTokens.TryGetValue(cli.Slot, out tok)) tok = ObjectForceBurst;
            if (_objForceTokensAt.TryGetValue(cli.Slot, out at)) tok = Mathf.Min(ObjectForceBurst, tok + (now - at) * ObjectForceBurst);
            _objForceTokensAt[cli.Slot] = now;
            if (tok < 1f) { _objForceTokens[cli.Slot] = tok; DropObjectForce(cli, "rate"); return; }
            _objForceTokens[cli.Slot] = tok - 1f;

            ushort index = (ushort)(data[off] | (data[off + 1] << 8));
            EnsureNsoSrvCache();
            Component nso;
            if (!_nsoByIndexCache.TryGetValue(index, out nso) || (object)nso == null) { DropObjectForce(cli, "no object " + index); return; }
            if (IsWeaponNsoRoot(nso.gameObject)) { DropObjectForce(cli, "weapon"); return; }
            if ((object)_nsoSyncForceM == null)
            {
                _nsoSyncForceM = AccessTools.Method(nso.GetType(), "SyncObjectForce");
                if ((object)_nsoSyncForceM == null) { DropObjectForce(cli, "SyncObjectForce missing"); return; }
            }
            int rbCount = nso.GetComponentsInChildren<Rigidbody>().Length;
            if (data[off + 2] >= rbCount) { DropObjectForce(cli, "rigidbody " + data[off + 2]); return; }
            byte[] body = new byte[6];
            Buffer.BlockCopy(data, off, body, 0, 6);
            try
            {
                _nsoSyncForceM.Invoke(nso, new object[] { body });
                _objForceApplied++;
                if (_objForceApplied <= 5 || _objForceApplied % 100 == 0)
                    Log.LogInfo($"[crate-force] #{_objForceApplied} slot {cli.Slot} → object {index} rb {data[off + 2]} impulse=({(sbyte)data[off + 3] * 100},{(sbyte)data[off + 4] * 100})");
            }
            catch (Exception e) { DropObjectForce(cli, e.InnerException != null ? e.InnerException.Message : e.Message); }
        }

        private void DropObjectForce(SfClient cli, string why)
        {
            _objForceDropped++;
            if (_objForceDropped <= 5 || _objForceDropped % 100 == 0)
                Log.LogInfo($"[crate-force] dropped #{_objForceDropped} from slot {cli.Slot}: {why}");
        }
    }
}
