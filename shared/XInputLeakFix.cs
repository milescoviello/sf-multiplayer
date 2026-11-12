using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using HarmonyLib;

namespace SFShared
{
    // Stock memory leak, on every machine that runs the game: XInputDotNetPure's
    // GamePad.GetState allocates its 16-byte state buffer with Marshal.AllocHGlobal
    // on every call and never frees it. InControl's XInput thread calls it for all
    // four pad slots every 16 ms (250 times a second), so the game grows ~28 MB an
    // hour, connected pads or not. Found 11-12 on the server: 249 leaked 32-byte heap
    // blocks a second in every lobby, 2.95 M blocks (94 MB) after 3 h; the stock game
    // with no mods leaked at the same rate.
    //
    // Fix: a transpiler that frees the buffer before every return of GetState. The
    // state is copied out with PtrToStructure before that, so nothing reads the
    // buffer afterwards. Shared by the server and the player client.
    internal static class XInputLeakFix
    {
        private static bool _applied;

        internal static string Install(Harmony harmony)
        {
            try
            {
                var gamePad = AccessTools.TypeByName("XInputDotNetPure.GamePad");
                if ((object)gamePad == null) return "XInputDotNetPure.GamePad not found (nothing to fix)";
                int patched = 0;
                foreach (var m in gamePad.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (m.Name != "GetState") continue;
                    _applied = false;
                    harmony.Patch(m, transpiler: new HarmonyMethod(typeof(XInputLeakFix), nameof(Transpiler)));
                    if (_applied) patched++;
                }
                return patched > 0
                    ? "GamePad.GetState frees its buffer now (stock leak: 16 bytes per pad read, ~28 MB an hour)."
                    : "GamePad.GetState already frees its buffer or has none (left as is).";
            }
            catch (Exception e) { return "install failed: " + e.Message; }
        }

        private static bool IsCall(CodeInstruction c, string type, string name)
        {
            if (c.opcode.Value != OpCodes.Call.Value) return false;
            var mi = c.operand as MethodInfo;
            return (object)mi != null && mi.Name == name && (object)mi.DeclaringType != null && mi.DeclaringType.FullName == type;
        }

        // ldloc for the local a stloc writes (the buffer pointer); null if c isn't a stloc.
        private static CodeInstruction LoadFor(CodeInstruction c)
        {
            short v = c.opcode.Value;
            if (v == OpCodes.Stloc_0.Value) return new CodeInstruction(OpCodes.Ldloc_0);
            if (v == OpCodes.Stloc_1.Value) return new CodeInstruction(OpCodes.Ldloc_1);
            if (v == OpCodes.Stloc_2.Value) return new CodeInstruction(OpCodes.Ldloc_2);
            if (v == OpCodes.Stloc_3.Value) return new CodeInstruction(OpCodes.Ldloc_3);
            if (v == OpCodes.Stloc_S.Value) return new CodeInstruction(OpCodes.Ldloc_S, c.operand);
            if (v == OpCodes.Stloc.Value) return new CodeInstruction(OpCodes.Ldloc, c.operand);
            return null;
        }

        // Returns a plain List (no yield: an iterator would need
        // Environment.CurrentManagedThreadId, which the game's Mono 2.0 lacks).
        internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            // Already frees (another copy of this fix ran first, or a game update
            // fixed it): leave it, a second free would crash.
            foreach (var c in codes)
                if (IsCall(c, "System.Runtime.InteropServices.Marshal", "FreeHGlobal")) return codes;
            CodeInstruction load = null;
            for (int i = 0; i + 1 < codes.Count && load == null; i++)
                if (IsCall(codes[i], "System.Runtime.InteropServices.Marshal", "AllocHGlobal"))
                    load = LoadFor(codes[i + 1]);
            if (load == null) return codes;
            var free = AccessTools.Method(typeof(Marshal), "FreeHGlobal", new[] { typeof(IntPtr) });
            if ((object)free == null) return codes;
            var outList = new List<CodeInstruction>(codes.Count + 4);
            foreach (var c in codes)
            {
                if (c.opcode.Value == OpCodes.Ret.Value)
                {
                    // ldloc ptr; call FreeHGlobal; ret: stack-neutral, the return value stays
                    // on the stack. Branches to the ret now land on the ldloc.
                    var ld = new CodeInstruction(load.opcode, load.operand);
                    ld.labels.AddRange(c.labels);
                    c.labels.Clear();
                    outList.Add(ld);
                    outList.Add(new CodeInstruction(OpCodes.Call, free));
                }
                outList.Add(c);
            }
            _applied = true;
            return outList;
        }
    }
}
