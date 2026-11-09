using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace SFHeadlessHost
{
    // Crate diagnostics. "Stacks collapse as soon as players fight on them":
    // this says WHY, per jolt. Every FixedUpdate (only while rigs exist) each
    // pushable crate's velocity is compared with the previous step's; a change
    // well beyond gravity is a jolt and gets logged (rate-limited) with what
    // touched it: a server player rig overlapping the crate, a server bullet
    // kick, a server blast, or nothing we caused (crate-on-crate). The bridge
    // command {"cmd":"rigdiag"} reports how each server rig's body sits
    // relative to the position its client reports.
    public partial class Plugin
    {
        private const float CrateJoltDv = 2f;   // m/s change in one physics step, gravity removed
        private readonly Dictionary<int, Vector3> _cdPrevVel = new Dictionary<int, Vector3>();
        private readonly Dictionary<int, float> _cdKickAt = new Dictionary<int, float>();
        private float _cdBlastAt = -10f;
        private int _cdLogged, _cdSeen, _cdByRig, _cdByKick, _cdByBlast, _cdByNone;
        private float _cdSummaryAt;

        private void NoteCrateKick(Rigidbody rb) { if ((object)rb != null) _cdKickAt[rb.GetInstanceID()] = Time.realtimeSinceStartup; }
        private void NoteBlast() { _cdBlastAt = Time.realtimeSinceStartup; }

        private void TickCrateJoltDiag()
        {
            if (SlotToRig.Count == 0) { if (_cdPrevVel.Count > 0) _cdPrevVel.Clear(); return; }
            EnsureNsoSrvCache();
            if (_nsoSrvEntries.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            Vector3 g = Physics.gravity * Time.fixedDeltaTime;
            foreach (var ent in _nsoSrvEntries)
            {
                if (ent.Weapon) continue;   // any free networked prop (Desert5's crates have no DestructiblePiece)
                var rb = ent.Rb;
                if ((object)rb == null) continue;
                int id;
                Vector3 v;
                try { id = rb.GetInstanceID(); v = rb.isKinematic ? Vector3.zero : rb.velocity; }
                catch { continue; }
                Vector3 pv;
                if (_cdPrevVel.TryGetValue(id, out pv))
                {
                    Vector3 dv = v - pv - g;
                    if (dv.sqrMagnitude > CrateJoltDv * CrateJoltDv && !rb.isKinematic)
                        ReportCrateJolt(ent, rb, dv, v, now);
                }
                _cdPrevVel[id] = v;
            }
            if (now - _cdSummaryAt > 30f && _cdSeen > 0)
            {
                _cdSummaryAt = now;
                Log.LogInfo($"[CRATE-JOLT] 30s: {_cdSeen} jolts (rig contact {_cdByRig}, bullet kick {_cdByKick}, blast {_cdByBlast}, other {_cdByNone})");
                _cdSeen = _cdByRig = _cdByKick = _cdByBlast = _cdByNone = 0;
            }
        }

        private void ReportCrateJolt(NsoSrvEntry ent, Rigidbody rb, Vector3 dv, Vector3 v, float now)
        {
            _cdSeen++;
            string cause;
            float kickAt;
            int rigSlot = RigTouchingCrate(rb);
            if (rigSlot >= 0) { cause = "rig slot " + rigSlot; _cdByRig++; }
            else if (_cdKickAt.TryGetValue(rb.GetInstanceID(), out kickAt) && now - kickAt < 0.1f) { cause = "bullet kick"; _cdByKick++; }
            else if (now - _cdBlastAt < 0.1f) { cause = "blast"; _cdByBlast++; }
            else { cause = "other"; _cdByNone++; }
            if (_cdLogged++ < 60 || _cdLogged % 50 == 0)
                Log.LogInfo($"[CRATE-JOLT] #{_cdLogged} id={ent.Id} '{rb.name}' dv={dv.magnitude:0.0} ({dv.y:0.0},{dv.z:0.0}) v={v.magnitude:0.0} pos=({rb.position.y:0.00},{rb.position.z:0.00}) mass={rb.mass:0} cause={cause}");
        }

        // A server rig whose collider bounds overlap the crate's (slightly
        // grown). -1 if none.
        private static int RigTouchingCrate(Rigidbody crate)
        {
            Bounds cb;
            if (!CrateBounds(crate, out cb)) return -1;
            cb.Expand(0.15f);
            foreach (var kv in SlotToRig)
            {
                var rig = kv.Value;
                if ((object)rig == null) continue;
                foreach (var col in rig.GetComponentsInChildren<Collider>())
                {
                    if ((object)col == null || !col.enabled || col.isTrigger) continue;
                    if (col.bounds.Intersects(cb)) return kv.Key;
                }
            }
            return -1;
        }

        private static bool CrateBounds(Rigidbody rb, out Bounds b)
        {
            b = new Bounds();
            bool any = false;
            foreach (var col in rb.GetComponentsInChildren<Collider>())
            {
                if ((object)col == null || col.isTrigger || col.attachedRigidbody != rb) continue;
                if (!any) { b = col.bounds; any = true; } else b.Encapsulate(col.bounds);
            }
            return any;
        }

        // Bridge "rigdiag": per rig, root vs body average (what clients report
        // is the body average), the body's lowest collider point, kinematic
        // body count, and the latest reported position.
        private string RigDiag()
        {
            var sb = new StringBuilder(512);
            foreach (var kv in SlotToRig)
            {
                var rig = kv.Value;
                if ((object)rig == null) continue;
                var rbs = rig.GetComponentsInChildren<Rigidbody>();
                Vector3 avg = Vector3.zero; int n = 0, kin = 0; float mass = 0f;
                foreach (var rb in rbs) { if ((object)rb == null) continue; avg += rb.position; n++; if (rb.isKinematic) kin++; mass += rb.mass; }
                if (n > 0) avg /= n;
                float minY = float.MaxValue; int cols = 0;
                foreach (var col in rig.GetComponentsInChildren<Collider>())
                {
                    if ((object)col == null || !col.enabled || col.isTrigger) continue;
                    cols++;
                    if (col.bounds.min.y < minY) minY = col.bounds.min.y;
                }
                Vector3 rep;
                _reportedPos.TryGetValue(kv.Key, out rep);
                Vector3 root = rig.transform.position;
                sb.Append($"slot {kv.Key}: root=({root.y:0.00},{root.z:0.00}) bodyAvg=({avg.y:0.00},{avg.z:0.00}) reported=({rep.y:0.00},{rep.z:0.00}) bodyMinY={minY:0.00} rbs={n} kinematic={kin} mass={mass:0.0} colliders={cols} | ");
            }
            foreach (var kv in SlotToRig)
            {
                if ((object)kv.Value == null) continue;
                var types = new Dictionary<string, int>();
                foreach (var mb in kv.Value.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if ((object)mb == null) continue;
                    string k = mb.GetType().Name + (mb.enabled ? "" : "(off)");
                    int c; types.TryGetValue(k, out c); types[k] = c + 1;
                }
                sb.Append("scripts: ");
                foreach (var t in types) sb.Append(t.Key).Append(t.Value > 1 ? "x" + t.Value : "").Append(' ');
                sb.Append("| weapons: ");
                var wType = CachedType("Weapon");
                var pcType = CachedType("ProjectileCollision");
                if ((object)wType != null && (object)pcType != null)
                {
                    var projF = HarmonyLib.AccessTools.Field(wType, "projectile");
                    var forceF = HarmonyLib.AccessTools.Field(pcType, "force");
                    var dmgF = HarmonyLib.AccessTools.Field(pcType, "damage");
                    int wi = 0;
                    foreach (var w in kv.Value.GetComponentsInChildren(wType, true))
                    {
                        var proj = (object)projF != null ? projF.GetValue(w) as GameObject : null;
                        var pc = (object)proj != null ? proj.GetComponentInChildren(pcType) : null;
                        if ((object)pc != null)
                            sb.Append(wi).Append(':').Append(((Component)w).name).Append(" f=").Append(forceF.GetValue(pc)).Append(" d=").Append(dmgF.GetValue(pc)).Append("; ");
                        wi++;
                    }
                }
                break;   // one rig is enough
            }
            return sb.Length == 0 ? "no rigs" : sb.ToString();
        }
    }
}
