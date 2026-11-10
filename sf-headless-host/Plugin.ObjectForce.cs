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
        private static MethodInfo _nsoSyncForceM, _nsoInitRbsM;
        private static FieldInfo _nsoAllRbsF;
        private readonly Dictionary<int, float> _objForceTokens = new Dictionary<int, float>();
        private readonly Dictionary<int, float> _objForceTokensAt = new Dictionary<int, float>();
        private int _objForceApplied, _objForceDropped;

        private void ApplyClientObjectForce(SfClient cli, byte[] data, int off, int len, byte channel)
        {
            if (!ClientCrateForce) return;
            if (channel != ObjectForceChannel) return;   // a push on a player (their own channel): relayed as stock
            if (!IsParticipant(cli)) { DropObjectForce(cli, "not a participant"); return; }
            if (len < 6) { DropObjectForce(cli, "body " + len + " bytes"); return; }
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
            // The server never runs NetworkSyncableObject.Init for map objects
            // (stock Init throws on the server; our boot-time setup fills the
            // network fields by hand), so the rigidbody list SyncObjectForce
            // indexes was null and EVERY client push threw: bullets never moved
            // a crate on the server. Build it the stock way on first use.
            if ((object)_nsoAllRbsF == null) _nsoAllRbsF = AccessTools.Field(nso.GetType(), "mAllRigidBodies");
            if ((object)_nsoAllRbsF != null && _nsoAllRbsF.GetValue(nso) == null)
            {
                if ((object)_nsoInitRbsM == null) _nsoInitRbsM = AccessTools.Method(nso.GetType(), "InitRigidBodies");
                if ((object)_nsoInitRbsM == null) { DropObjectForce(cli, "InitRigidBodies missing"); return; }
                _nsoInitRbsM.Invoke(nso, null);
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

        private readonly Dictionary<string, int> _objForceDropsByReason = new Dictionary<string, int>();
        private void DropObjectForce(SfClient cli, string why)
        {
            _objForceDropped++;
            string kind = why.Split(' ')[0];
            int n; _objForceDropsByReason.TryGetValue(kind, out n); _objForceDropsByReason[kind] = ++n;
            if (n <= 5 || n % 100 == 0)
                Log.LogInfo($"[crate-force] dropped #{_objForceDropped} from slot {cli.Slot}: {why}");
        }

        // Bridge "nso": one object's server state (test tool).
        private string NsoInfo(int id)
        {
            EnsureNsoSrvCache();
            Component c;
            if (!_nsoByIndexCache.TryGetValue((ushort)id, out c) || !c) return "no object " + id;
            var rb = c.GetComponentInChildren<Rigidbody>();
            if (!rb) return "object " + id + " has no rigidbody";
            int copies = 0; var sb = new System.Text.StringBuilder();
            var nsoType = c.GetType();
            var idxP = AccessTools.Property(nsoType, "Index");
            foreach (var o in UnityEngine.Object.FindObjectsOfType(nsoType))
            {
                var oc = o as Component;
                if (!oc || (object)idxP == null || (ushort)idxP.GetValue(o, null) != (ushort)id) continue;
                copies++;
                var orb = oc.GetComponentInChildren<Rigidbody>();
                sb.Append(" | copy scene=").Append(oc.gameObject.scene.name).Append(" active=").Append(oc.gameObject.activeInHierarchy)
                  .Append(" iid=").Append(oc.GetInstanceID()).Append(orb ? " pos=" + orb.position.ToString("0.00") : "");
            }
            return "id=" + id + " name=" + c.name + " cachedIid=" + c.GetInstanceID() + " scene=" + c.gameObject.scene.name + " active=" + c.gameObject.activeInHierarchy
                + " pos=" + rb.position.ToString("0.00") + " vel=" + rb.velocity.ToString("0.00")
                + " mass=" + rb.mass.ToString("0") + " kin=" + rb.isKinematic + " sleeping=" + rb.IsSleeping() + " constraints=" + (int)rb.constraints
                + " ccd=" + rb.collisionDetectionMode + " drag=" + rb.drag + " angDrag=" + rb.angularDrag + " interp=" + rb.interpolation
                + " solverIters=" + rb.solverIterations + " sleepThr=" + rb.sleepThreshold
                + " | physics: fixedDt=" + Time.fixedDeltaTime + " gravity=" + Physics.gravity.ToString("0.0") + " defSolver=" + Physics.defaultSolverIterations
                + " sleepThr=" + Physics.sleepThreshold + " contactOffset=" + Physics.defaultContactOffset + " bounceThr=" + Physics.bounceThreshold
                + " copies=" + copies + " timeScale=" + Time.timeScale.ToString("0.00") + " fixedDt=" + Time.fixedDeltaTime.ToString("0.000") + " matchStarted=" + _matchStarted + sb;
        }

        // Bridge "nsolist": the network indices the server knows for this map.
        private string NsoList()
        {
            EnsureNsoSrvCache();
            var sb = new System.Text.StringBuilder();
            int pushable = 0, n = 0;
            var ids = new List<int>();
            foreach (var e in _nsoSrvEntries) { n++; if (e.Pushable) pushable++; if (!e.Weapon) ids.Add(e.Id); }
            ids.Sort();
            sb.Append("scene=").Append(_currentSceneIndex).Append(" nsos=").Append(n).Append(" pushable=").Append(pushable).Append(" ids=");
            for (int i = 0; i < ids.Count && i < 60; i++) { if (i > 0) sb.Append(','); sb.Append(ids[i]); }
            if (ids.Count > 60) sb.Append(",... max=").Append(ids[ids.Count - 1]);
            return sb.ToString();
        }
    }
}
