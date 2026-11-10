// SFOnlinePatcher: a BepInEx preloader patcher that turns the player's own stock
// Assembly-CSharp into the online build, in memory, when the game is started
// for the server (the "Stick Fight Online" launcher passes -address). A normal
// launch is left alone: the patcher returns without touching anything.
//
// The online changes come from SFOnline.donor next to this file: the online
// build with every method it doesn't change stripped out. Transplant.cs copies
// the changed methods (and what they need) into the stock assembly; everything
// else stays Landfall's original code. No modified game file is shipped or
// written to disk.
//
// Runs inside the game's Mono 2.0: no lambdas, LINQ, yield or lock here.
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using Mono.Cecil;

namespace SFOnlinePatcher
{
    public static class Patcher
    {
        public static IEnumerable<string> TargetDLLs { get { return new string[] { "Assembly-CSharp.dll" }; } }

        static readonly ManualLogSource Log = Logger.CreateLogSource("SFOnline");

        static bool OnlineLaunch()
        {
            if (Environment.GetEnvironmentVariable("SF_ONLINE_FORCE") == "1") return true;
            foreach (var a in Environment.GetCommandLineArgs())
                if (string.Equals(a, "-address", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static string Resource(ModuleDefinition m, string name)
        {
            foreach (var r in m.Resources)
            {
                var er = r as EmbeddedResource;
                if (er != null && er.Name == name) return Encoding.UTF8.GetString(er.GetResourceData());
            }
            return null;
        }

        static string Sha256(string path)
        {
            using (var h = SHA256.Create())
            {
                var b = h.ComputeHash(File.ReadAllBytes(path));
                var sb = new StringBuilder(64);
                foreach (var x in b) sb.Append(x.ToString("X2"));
                return sb.ToString();
            }
        }

        public static void Patch(AssemblyDefinition assembly)
        {
            try
            {
                if (!OnlineLaunch()) { Log.LogInfo("Normal launch: game code left as it is."); return; }
                var stock = assembly.MainModule;
                if ((object)stock.GetType("P2PPackageHandler/State") != null) { Log.LogInfo("The game file is already the online build: nothing to do."); return; }

                string dir = Path.GetDirectoryName(typeof(Patcher).Assembly.Location);
                string donorPath = Path.Combine(dir, "SFOnline.donor");
                if (!File.Exists(donorPath)) { Log.LogError("SFOnline.donor is missing next to SFOnlinePatcher.dll: start Stick Fight Online again to repair it."); return; }
                var res = new DefaultAssemblyResolver();
                res.AddSearchDirectory(Paths.ManagedPath);
                var rp = new ReaderParameters { AssemblyResolver = res, ReadingMode = ReadingMode.Immediate };
                var donor = ModuleDefinition.ReadModule(donorPath, rp);

                // The donor lists every stock build it applies to (the first is the one it
                // was made from; later ones, e.g. the 64-bit Steam build, have their own
                // transplant list that keeps that build's own changes).
                string meta = Resource(donor, "SFOnline.meta") ?? "";
                var known = new List<string>();
                foreach (var line in meta.Split('\n')) if (line.StartsWith("stock-sha256 ")) known.Add(line.Substring(13).Trim().ToUpperInvariant());
                string gameFile = Path.Combine(Paths.ManagedPath, "Assembly-CSharp.dll");
                string have = Sha256(gameFile);
                int which = known.IndexOf(have.ToUpperInvariant());
                if (which < 0)
                {
                    Log.LogError("Your StickFight_Data/Managed/Assembly-CSharp.dll isn't a stock Steam version this mod knows (sha256 " + have + "), so the online build can't be made from it. In Steam: Stick Fight > Properties > Installed Files > Verify integrity, then start Stick Fight Online again. If that doesn't help, the game had an update: report it on GitHub.");
                    return;
                }
                string list = Resource(donor, which == 0 ? "SFOnline.transplant" : "SFOnline.transplant." + known[which]);
                if (list == null) { Log.LogError("SFOnline.donor has no transplant list for this game build."); return; }
                var keys = new List<string>();
                foreach (var k in list.Split('\n')) if (k.Length > 0) keys.Add(k);

                var t0 = DateTime.UtcNow;
                // Dry run on a separate copy first: a failure must not leave the
                // real assembly half-changed.
                var trial = ModuleDefinition.ReadModule(gameFile, rp);
                new Transplant(trial, donor).Run(keys);
                var t = new Transplant(stock, donor);
                t.Run(keys);
                Log.LogInfo(string.Format("Online build ready: {0} methods from the online build, {1} helper types, {2} added methods, {3} added fields ({4:0} ms, game build {5}).",
                    t.Transplanted, t.TypesCloned, t.MethodsCloned, t.FieldsCloned, (DateTime.UtcNow - t0).TotalMilliseconds, have.Substring(0, 8)));
            }
            catch (Exception e)
            {
                Log.LogError("Couldn't make the online build (the game is left as it is): " + e);
            }
        }
    }
}
