// sf-online-donor: build SFOnline.donor, the file the SFOnlinePatcher uses to
// turn a player's stock Assembly-CSharp into the online build in memory.
//
//   dotnet run -c Release -- STOCK.dll PATCHED.dll MANAGED_DIR OUT_DIR
//
// STOCK.dll is the game's own Assembly-CSharp (Steam version), PATCHED.dll the
// online build it should become, MANAGED_DIR the game's StickFight_Data/Managed
// (for UnityEngine & co). Both DLLs are Landfall's code and never go in the repo
// (keep them in private/, which git ignores).
//
// Steps: (1) classify every method: the same as stock (by what it references,
// its strings and constants, its compiler-generated helpers) or changed / added;
// (2) write the donor: the patched build with every unchanged method body
// stripped and the list of methods to transplant; (3) run the real transplant
// (Transplant.cs, the code the patcher runs in the game) on a fresh stock copy;
// (4) verify the result against the patched build: every method's fingerprint,
// every reference resolves, no static/instance mismatch, no empty bodies or
// duplicate members. Exit 1 if anything fails.
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Mono.Cecil;
using Mono.Cecil.Cil;
using SFOnlinePatcher;

static class DonorBuild
{
    static bool Gen(TypeDefinition t) => Transplant.Gen(t);
    static string Key(MethodReference m) => Transplant.Key(m);
    // ---------------------------------------------------------------- fingerprints
    static string Norm(string s) => Regex.Replace(s, @"<[^>]*>[a-z]__[A-Za-z]*[\d_]*|\bc__AnonStorey\d+|<>c__DisplayClass[\d_]+|<>c\b|<>9__[\d_]+|<>f__am\$cache\d+|\$this|<>\d__\w+|<>[0-9a-z]__[\w$]+|\$current|\$PC|\$disposing|\$locvar\d+|<[\w$]+>__\d+", "~");
    static IEnumerable<MethodDefinition> Helpers(MethodDefinition m, HashSet<MethodDefinition>? seen = null)
    {
        seen ??= new HashSet<MethodDefinition>();
        if (!seen.Add(m)) yield break;
        yield return m;
        if (!m.HasBody) yield break;
        foreach (var i in m.Body.Instructions)
        {
            TypeDefinition? gt = null;
            if (i.Operand is MethodReference mr) gt = mr.DeclaringType.Resolve();
            else if (i.Operand is FieldReference fr) gt = fr.DeclaringType.Resolve();
            else if (i.Operand is TypeReference tr) gt = tr.Resolve();
            if (i.Operand is MethodReference mr2 && mr2.Resolve() is MethodDefinition md && md.Module == m.Module && md.Name.Contains('<') && md.DeclaringType == m.DeclaringType)
                foreach (var x in Helpers(md, seen)) yield return x;
            if (gt != null && gt.Module == m.Module && Gen(gt) && gt != m.DeclaringType)
                foreach (var hm in gt.Methods) foreach (var x in Helpers(hm, seen)) yield return x;
        }
    }
    static List<string> Print(MethodDefinition m)
    {
        var l = new List<string>();
        foreach (var md in Helpers(m))
        {
            if (!md.HasBody) continue;
            foreach (var i in md.Body.Instructions)
            {
                switch (i.Operand)
                {
                    case FieldReference fr0: l.Add(Norm(fr0.FullName) + (fr0.Resolve()?.IsStatic == true ? "[static]" : "")); break;
                    case MethodReference mr0: l.Add(Norm(mr0.FullName) + (mr0.HasThis ? "" : "[static]")); break;
                    case MemberReference r: if (!(r is TypeReference t2 && Gen(t2.Resolve() ?? new TypeDefinition("", "", 0)))) l.Add(Norm(r.FullName)); break;
                    case string s: l.Add("\"" + s); break;
                    case int n: l.Add("#" + n); break;
                    case sbyte n: l.Add("#" + n); break;
                    case long n: l.Add("#" + n); break;
                    case float f: l.Add("#" + f.ToString("R")); break;
                    case double d: l.Add("#" + d.ToString("R")); break;
                }
                var oc = i.OpCode.Code;
                if (oc >= Code.Ldc_I4_M1 && oc <= Code.Ldc_I4_8) l.Add("#" + (oc - Code.Ldc_I4_0));
            }
        }
        l.Sort(StringComparer.Ordinal);
        return l;
    }

    static string Hex(byte[] b) => Convert.ToHexString(SHA256.HashData(b));

    static int Main(string[] a)
    {
        string stockPath = a[0], patchedPath = a[1], managed = a[2], outDir = a[3];
        Directory.CreateDirectory(outDir);
        ReaderParameters RP() { var r = new DefaultAssemblyResolver(); r.AddSearchDirectory(managed); return new ReaderParameters { AssemblyResolver = r, ReadingMode = ReadingMode.Immediate }; }
        var S = ModuleDefinition.ReadModule(stockPath, RP());
        var P = ModuleDefinition.ReadModule(patchedPath, RP());

        // 1. classify
        var stockBy = new Dictionary<string, MethodDefinition>();
        foreach (var t in S.GetTypes()) { if (Gen(t)) continue; foreach (var m in t.Methods) stockBy.TryAdd(Transplant.FullKey(m), m); }
        var keys = new List<string>(); int same = 0, edited = 0, added = 0;
        var keep = new HashSet<MethodDefinition>();
        foreach (var t in P.GetTypes())
        {
            if (Gen(t)) continue;
            foreach (var m in t.Methods)
            {
                var k = Transplant.FullKey(m);
                if (stockBy.TryGetValue(k, out var sm))
                {
                    if (sm.IsStatic == m.IsStatic && sm.HasThis == m.HasThis && Print(sm).SequenceEqual(Print(m))) { same++; continue; }
                    edited++;
                }
                else added++;
                keys.Add(k);
                foreach (var h in Helpers(m)) keep.Add(h);
            }
        }
        Console.WriteLine($"classify: same {same}, edited {edited}, added {added}; bodies kept (with helpers) {keep.Count}");

        // 2. strip everything else
        var usedFields = new HashSet<FieldDefinition>();
        foreach (var m in keep) if (m.HasBody) foreach (var i in m.Body.Instructions) if (i.Operand is FieldReference fr && fr.Resolve() is FieldDefinition fd && fd.Module == P) usedFields.Add(fd);
        long ilKept = 0, ilStripped = 0; int stripped = 0, rvaDropped = 0;
        foreach (var t in P.GetTypes())
            foreach (var m in t.Methods)
            {
                if (!m.HasBody) continue;
                if (keep.Contains(m)) { ilKept += m.Body.CodeSize; continue; }
                ilStripped += m.Body.CodeSize; stripped++;
                var b = new MethodBody(m);
                b.Instructions.Add(Instruction.Create(OpCodes.Ldnull));
                b.Instructions.Add(Instruction.Create(OpCodes.Throw));
                m.Body = b;
            }
        foreach (var t in P.GetTypes())
            foreach (var f in t.Fields)
                if (f.InitialValue != null && f.InitialValue.Length > 0 && !usedFields.Contains(f)) { f.InitialValue = new byte[0]; f.Attributes &= ~FieldAttributes.HasFieldRVA; rvaDropped++; }
        P.Resources.Clear();
        P.Assembly.Name.Name = "SFOnline.Donor";
        P.Name = "SFOnline.Donor.dll";
        var stockBytes = File.ReadAllBytes(stockPath);
        var meta = "stock-sha256 " + Hex(stockBytes) + "\nkeys " + keys.Count + "\n";
        P.Resources.Add(new EmbeddedResource("SFOnline.transplant", ManifestResourceAttributes.Public, System.Text.Encoding.UTF8.GetBytes(string.Join("\n", keys))));
        P.Resources.Add(new EmbeddedResource("SFOnline.meta", ManifestResourceAttributes.Public, System.Text.Encoding.UTF8.GetBytes(meta)));
        var donorPath = Path.Combine(outDir, "SFOnline.donor");
        P.Write(donorPath);
        Console.WriteLine($"donor: {new FileInfo(donorPath).Length} bytes; IL kept {ilKept} of {ilKept + ilStripped} ({100.0 * ilKept / (ilKept + ilStripped):0.0}%), {stripped} bodies stripped, {rvaDropped} data tables dropped");

        // 3. self-test: the in-game code path, on a fresh stock copy
        var S2 = ModuleDefinition.ReadModule(stockPath, RP());
        var D = ModuleDefinition.ReadModule(donorPath, RP());
        var list = System.Text.Encoding.UTF8.GetString(((EmbeddedResource)D.Resources.First(r => r.Name == "SFOnline.transplant")).GetResourceData()).Split('\n');
        var tr = new Transplant(S2, D);
        tr.Run(list);
        var outPath = Path.Combine(outDir, "Assembly-CSharp.online-test.dll");
        S2.Write(outPath);
        File.WriteAllText(Path.Combine(outDir, "transplant-log.txt"), tr.Log.ToString());
        Console.WriteLine($"transplant: {tr.Transplanted} bodies, {tr.TypesCloned} types, {tr.MethodsCloned} methods, {tr.FieldsCloned} fields cloned");

        // 4. verify against the patched build
        var O = ModuleDefinition.ReadModule(outPath, RP());
        var P2 = ModuleDefinition.ReadModule(patchedPath, RP());
        int bad = 0, unres = 0, match = 0;
        foreach (var r in O.AssemblyReferences) if (r.Name.StartsWith("Assembly-CSharp") && r.Name != "Assembly-CSharp-firstpass" || r.Name.StartsWith("SFOnline")) { Console.WriteLine("BAD REFERENCE " + r.Name); bad++; }
        foreach (var t in O.GetTypes()) foreach (var m in t.Methods) if (m.HasBody) foreach (var i in m.Body.Instructions)
            if (i.Operand is MemberReference mr && !(mr is GenericParameter) && !(mr is MethodReference am && am.DeclaringType is ArrayType) && !(mr is TypeReference tt && (tt.IsGenericParameter || tt is ArrayType)))
            {
                object d = mr switch { MethodReference x => x.Resolve(), FieldReference x => x.Resolve(), TypeReference x => x.Resolve(), _ => mr };
                if (d == null) { unres++; if (unres < 10) Console.WriteLine($"  unresolved in {m.FullName}: {mr.FullName}"); }
            }
        var OB = new Dictionary<string, MethodDefinition>();
        foreach (var t in O.GetTypes()) { if (Gen(t)) continue; foreach (var m in t.Methods) OB.TryAdd(Transplant.FullKey(m), m); }
        foreach (var t in P2.GetTypes()) { if (Gen(t)) continue; foreach (var m in t.Methods)
        {
            if (!OB.TryGetValue(Transplant.FullKey(m), out var om)) { bad++; Console.WriteLine("  missing " + m.FullName); continue; }
            if (Print(om).SequenceEqual(Print(m))) match++; else { bad++; if (bad < 10) { Console.WriteLine("  differs " + m.FullName);
                var po = Print(om); var pp = Print(m); Console.WriteLine("    only in output: " + string.Join(" | ", po.Except(pp).Take(6))); Console.WriteLine("    only in patched: " + string.Join(" | ", pp.Except(po).Take(6)));
                var sk = stockBy.TryGetValue(Transplant.FullKey(m), out var smx) ? (Print(smx).SequenceEqual(pp) ? "stock==patched" : "stock!=patched") : "not in stock"; Console.WriteLine("    classified: " + sk + ", in keys: " + keys.Contains(Transplant.FullKey(m))); } }
        } }
        // IL sanity over every method in the output: static/instance must agree.
        int ilBad = 0;
        foreach (var t in O.GetTypes()) foreach (var m in t.Methods) if (m.HasBody) foreach (var i in m.Body.Instructions)
        {
            string why = null;
            if (i.Operand is FieldReference fr2 && fr2.Resolve() is FieldDefinition fd2)
            {
                bool sop = i.OpCode.Code == Code.Ldsfld || i.OpCode.Code == Code.Stsfld || i.OpCode.Code == Code.Ldsflda;
                bool iop = i.OpCode.Code == Code.Ldfld || i.OpCode.Code == Code.Stfld || i.OpCode.Code == Code.Ldflda;
                if (sop && !fd2.IsStatic || iop && fd2.IsStatic) why = "field static mismatch " + fd2.FullName;
            }
            else if (i.Operand is MethodReference mr2 && !(mr2.DeclaringType is ArrayType) && mr2.Resolve() is MethodDefinition md2)
                if (mr2.HasThis == md2.IsStatic) why = "method static mismatch " + md2.FullName;
            if (why != null) { ilBad++; if (ilBad < 10) Console.WriteLine($"  IL in {m.FullName}: {why}"); }
        }
        // Structure: no body-less concrete methods, no duplicate members.
        foreach (var t in O.GetTypes())
        {
            var seenM = new HashSet<string>(); var seenF = new HashSet<string>();
            foreach (var m in t.Methods)
            {
                if (!m.IsAbstract && !m.IsPInvokeImpl && !m.IsRuntime && !m.IsInternalCall && (!m.HasBody || m.Body.Instructions.Count == 0)) { ilBad++; if (ilBad < 10) Console.WriteLine("  no body: " + m.FullName); }
                if (!seenM.Add((m.IsStatic ? "s " : "i ") + Key(m))) { ilBad++; if (ilBad < 10) Console.WriteLine("  duplicate method: " + m.FullName); }
            }
            foreach (var f in t.Fields) if (!seenF.Add(f.Name)) { ilBad++; if (ilBad < 10) Console.WriteLine("  duplicate field: " + f.FullName); }
        }
        Console.WriteLine($"IL sanity: {ilBad} static/instance mismatches, empty bodies or duplicates");
        bad += unres + ilBad;
        Console.WriteLine($"verify: {match} methods match the patched build, {unres} unresolved, {bad} problems -> {(bad == 0 ? "OK" : "FAILED")}");
        return bad == 0 ? 0 : 1;
    }
}
