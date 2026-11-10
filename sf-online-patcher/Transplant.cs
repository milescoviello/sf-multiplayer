// Transplant: copy methods (and whatever they need) from a donor module into
// the stock Assembly-CSharp, in memory, with Mono.Cecil.
//
// Shared by the BepInEx preloader patcher (runs inside the game: net35, Mono 2.0,
// so no lambdas, LINQ, yield, lock or tuples here) and by the offline donor
// builder, which runs the same code on a copy of the stock file to check the
// result before anything ships.
//
// Mapping is by name: a donor type / field / method that exists in stock maps to
// the stock one; one that doesn't is cloned into stock (compiler-generated
// helper classes, added fields and methods). The methods listed in the donor's
// transplant list get the donor's body; every other stock method keeps
// Landfall's original compiled code.
using System;
using System.Collections.Generic;

using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace SFOnlinePatcher
{
    public sealed class Transplant
    {
        readonly ModuleDefinition S, D;
        readonly Dictionary<TypeDefinition, TypeDefinition> typeMap = new Dictionary<TypeDefinition, TypeDefinition>();
        readonly Dictionary<MethodDefinition, MethodDefinition> methodMap = new Dictionary<MethodDefinition, MethodDefinition>();
        readonly Dictionary<FieldDefinition, FieldDefinition> fieldMap = new Dictionary<FieldDefinition, FieldDefinition>();
        readonly HashSet<TypeDefinition> clonedTypes = new HashSet<TypeDefinition>();
        readonly HashSet<MethodDefinition> clonedMethods = new HashSet<MethodDefinition>();
        readonly List<MethodDefinition> bodyPending = new List<MethodDefinition>();
        readonly HashSet<MethodDefinition> replaced = new HashSet<MethodDefinition>();
        readonly HashSet<object> staticChanged = new HashSet<object>();   // stock members that switched static <-> instance
        public int Neutralized;
        public readonly StringBuilder Log = new StringBuilder();
        public int Transplanted, TypesCloned, MethodsCloned, FieldsCloned;

        public Transplant(ModuleDefinition stock, ModuleDefinition donor) { S = stock; D = donor; }

        public static bool Gen(TypeDefinition t)
        {
            for (var x = t; x != null; x = x.DeclaringType) if (x.Name.IndexOf('<') >= 0) return true;
            return false;
        }

        public static string Key(MethodReference m)
        {
            var sb = new StringBuilder();
            sb.Append(m.ReturnType.FullName).Append(' ').Append(m.Name);
            if (m.HasGenericParameters) sb.Append('`').Append(m.GenericParameters.Count);
            sb.Append('(');
            for (int i = 0; i < m.Parameters.Count; i++) { if (i > 0) sb.Append(','); sb.Append(m.Parameters[i].ParameterType.FullName); }
            return sb.Append(')').ToString();
        }

        public static string FullKey(MethodDefinition m) { return m.DeclaringType.FullName + "::" + Key(m); }

        // ------------------------------------------------------------ apply
        // transplant: FullKey of every donor method whose body replaces stock's
        // (or that only exists in the donor).
        public void Run(ICollection<string> transplant)
        {
            var donorMethods = new Dictionary<string, MethodDefinition>();
            foreach (var t in D.GetTypes())
            {
                if (Gen(t)) continue;
                foreach (var m in t.Methods) { var k = FullKey(m); if (!donorMethods.ContainsKey(k)) donorMethods[k] = m; }
            }
            // Visibility changes on methods that are otherwise the same (the patched
            // build made some private methods public; transplanted code calls them).
            foreach (var t in D.GetTypes())
            {
                if (Gen(t)) continue;
                foreach (var dm in t.Methods)
                {
                    var st0 = MapTypeDef(t);
                    if (clonedTypes.Contains(st0)) continue;
                    var k0 = Key(dm);
                    foreach (var sm0 in st0.Methods)
                        if (!clonedMethods.Contains(sm0) && Key(sm0) == k0 && (sm0.Attributes & MethodAttributes.MemberAccessMask) != (dm.Attributes & MethodAttributes.MemberAccessMask))
                        {
                            Log.AppendLine("method access " + dm.FullName + ": " + (sm0.Attributes & MethodAttributes.MemberAccessMask) + " -> " + (dm.Attributes & MethodAttributes.MemberAccessMask));
                            sm0.Attributes = (sm0.Attributes & ~MethodAttributes.MemberAccessMask) | (dm.Attributes & MethodAttributes.MemberAccessMask);
                        }
                }
            }
            // Fields and types the patched build added (Unity finds fields by name).
            foreach (var t in D.GetTypes()) { if (Gen(t)) continue; foreach (var f in t.Fields) MapFieldDef(f); }
            foreach (var t in D.GetTypes()) if (!Gen(t)) MapTypeDef(t);

            foreach (var k in transplant)
            {
                MethodDefinition dm;
                if (!donorMethods.TryGetValue(k, out dm)) throw new Exception("donor has no method " + k);
                var sm = MapMethodDef(dm);
                if (clonedMethods.Contains(sm)) continue;          // added: body copied below
                if (sm.Attributes != dm.Attributes) Log.AppendLine("method attrs " + dm.FullName + ": " + sm.Attributes + " -> " + dm.Attributes);
                if (sm.IsStatic != dm.IsStatic) staticChanged.Add(sm);
                sm.Attributes = dm.Attributes;
                sm.ImplAttributes = dm.ImplAttributes;
                sm.HasThis = dm.HasThis;                 // static <-> instance changes too
                sm.ExplicitThis = dm.ExplicitThis;
                sm.CallingConvention = dm.CallingConvention;
                if (dm.HasBody) { CopyBody(dm, sm); replaced.Add(sm); Transplanted++; }
            }
            while (bodyPending.Count > 0)
            {
                var dm = bodyPending[bodyPending.Count - 1];
                bodyPending.RemoveAt(bodyPending.Count - 1);
                CopyBody(dm, methodMap[dm]);
            }
            // Stock code that wasn't replaced but uses a member that switched static
            // <-> instance is dead (only replaced code called it) and no longer valid
            // IL: make it throw instead, so nothing can ever run it half-broken.
            if (staticChanged.Count > 0)
                foreach (var t in S.GetTypes())
                    foreach (var m in t.Methods)
                    {
                        if (!m.HasBody || replaced.Contains(m) || clonedMethods.Contains(m)) continue;
                        bool uses = false;
                        foreach (var i in m.Body.Instructions) if (i.Operand != null && staticChanged.Contains(i.Operand)) { uses = true; break; }
                        if (!uses) continue;
                        var b = new MethodBody(m);
                        b.Instructions.Add(Instruction.Create(OpCodes.Ldnull));
                        b.Instructions.Add(Instruction.Create(OpCodes.Throw));
                        m.Body = b;
                        Neutralized++;
                        Log.AppendLine("neutralized dead stock method " + m.FullName);
                    }
            // Type-level changes on existing (non-generated) types.
            foreach (var kv in new List<KeyValuePair<TypeDefinition, TypeDefinition>>(typeMap))
            {
                TypeDefinition dt = kv.Key, st = kv.Value;
                if (clonedTypes.Contains(st) || Gen(st)) continue;
                if (st.Attributes != dt.Attributes) { Log.AppendLine("type attrs " + dt.FullName + ": " + st.Attributes + " -> " + dt.Attributes); st.Attributes = dt.Attributes; }
                if ((object)dt.BaseType != null && ((object)st.BaseType == null || st.BaseType.FullName != dt.BaseType.FullName)) { Log.AppendLine("base type " + dt.FullName); st.BaseType = MapType(dt.BaseType); }
                foreach (var ii in dt.Interfaces)
                {
                    bool has = false;
                    foreach (var si in st.Interfaces) if (si.InterfaceType.FullName == ii.InterfaceType.FullName) { has = true; break; }
                    if (!has) { Log.AppendLine("interface " + dt.FullName + " + " + ii.InterfaceType.FullName); st.Interfaces.Add(new InterfaceImplementation(MapType(ii.InterfaceType))); }
                }
            }
        }

        // ------------------------------------------------------------ types
        static readonly System.Reflection.ConstructorInfo posCtor = typeof(GenericParameter).GetConstructor(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null, new Type[] { typeof(int), typeof(GenericParameterType), typeof(ModuleDefinition) }, null);
        GenericParameter Pos(GenericParameter gp)
        {
            return (GenericParameter)posCtor.Invoke(new object[] { gp.Position, gp.Type == GenericParameterType.Method ? GenericParameterType.Method : GenericParameterType.Type, S });
        }

        bool InDonor(TypeReference t)
        {
            var e = t.GetElementType();
            var td = e as TypeDefinition;
            return td != null && td.Module == D;
        }

        TypeReference MapType(TypeReference t)
        {
            if ((object)t == null) return null;
            var gp = t as GenericParameter;
            if (gp != null) return Pos(gp);
            var at = t as ArrayType;
            if (at != null)
            {
                var na = new ArrayType(MapType(at.ElementType));
                if (!at.IsVector) { na.Dimensions.Clear(); foreach (var d in at.Dimensions) na.Dimensions.Add(new ArrayDimension(d.LowerBound, d.UpperBound)); }
                return na;
            }
            var br = t as ByReferenceType; if (br != null) return new ByReferenceType(MapType(br.ElementType));
            var pt = t as PointerType; if (pt != null) return new PointerType(MapType(pt.ElementType));
            var pn = t as PinnedType; if (pn != null) return new PinnedType(MapType(pn.ElementType));
            var rq = t as RequiredModifierType; if (rq != null) return new RequiredModifierType(MapType(rq.ModifierType), MapType(rq.ElementType));
            var op = t as OptionalModifierType; if (op != null) return new OptionalModifierType(MapType(op.ModifierType), MapType(op.ElementType));
            var gi = t as GenericInstanceType;
            if (gi != null)
            {
                var g = new GenericInstanceType(MapType(gi.ElementType));
                foreach (var a in gi.GenericArguments) g.GenericArguments.Add(MapType(a));
                return g;
            }
            var td = t as TypeDefinition;
            if (td != null && td.Module == D) return MapTypeDef(td);
            if (t.Module == S) return t;
            return S.ImportReference(t);
        }

        TypeDefinition Find(TypeDefinition dt)
        {
            if ((object)dt.DeclaringType != null)
            {
                var outer = MapTypeDef(dt.DeclaringType);
                foreach (var n in outer.NestedTypes) if (n.Name == dt.Name && !clonedTypes.Contains(n)) return n;
                return null;
            }
            foreach (var x in S.Types) if (x.Namespace == dt.Namespace && x.Name == dt.Name && !clonedTypes.Contains(x)) return x;
            return null;
        }

        TypeDefinition MapTypeDef(TypeDefinition dt)
        {
            TypeDefinition st;
            if (typeMap.TryGetValue(dt, out st)) return st;
            st = Find(dt);
            if (st != null) { typeMap[dt] = st; return st; }
            st = new TypeDefinition(dt.Namespace, dt.Name, dt.Attributes);
            // Only real layout info: Cecil reports 0 for "none", and a layout row
            // on an auto-layout class is invalid metadata.
            if (dt.HasLayoutInfo) { st.PackingSize = dt.PackingSize; st.ClassSize = dt.ClassSize; }
            typeMap[dt] = st;
            clonedTypes.Add(st);
            if ((object)dt.DeclaringType != null) MapTypeDef(dt.DeclaringType).NestedTypes.Add(st); else S.Types.Add(st);
            TypesCloned++;
            foreach (var g in dt.GenericParameters) st.GenericParameters.Add(new GenericParameter(g.Name, st) { Attributes = g.Attributes });
            for (int i = 0; i < dt.GenericParameters.Count; i++) foreach (var c in dt.GenericParameters[i].Constraints) st.GenericParameters[i].Constraints.Add(MapType(c));
            st.BaseType = MapType(dt.BaseType);
            foreach (var ii in dt.Interfaces) st.Interfaces.Add(new InterfaceImplementation(MapType(ii.InterfaceType)));
            foreach (var f in dt.Fields) MapFieldDef(f);
            foreach (var m in dt.Methods) MapMethodDef(m);
            foreach (var p in dt.Properties)
            {
                var sp = new PropertyDefinition(p.Name, p.Attributes, MapType(p.PropertyType));
                if ((object)p.GetMethod != null) sp.GetMethod = MapMethodDef(p.GetMethod);
                if ((object)p.SetMethod != null) sp.SetMethod = MapMethodDef(p.SetMethod);
                CopyAttrs(p, sp);
                st.Properties.Add(sp);
            }
            foreach (var e in dt.Events)
            {
                var se = new EventDefinition(e.Name, e.Attributes, MapType(e.EventType));
                if ((object)e.AddMethod != null) se.AddMethod = MapMethodDef(e.AddMethod);
                if ((object)e.RemoveMethod != null) se.RemoveMethod = MapMethodDef(e.RemoveMethod);
                st.Events.Add(se);
            }
            CopyAttrs(dt, st);
            foreach (var nt in dt.NestedTypes) MapTypeDef(nt);
            return st;
        }

        // ------------------------------------------------------------ members
        FieldDefinition MapFieldDef(FieldDefinition df)
        {
            FieldDefinition sf;
            if (fieldMap.TryGetValue(df, out sf)) return sf;
            var st = MapTypeDef(df.DeclaringType);
            if (fieldMap.TryGetValue(df, out sf)) return sf;
            foreach (var f in st.Fields) if (f.Name == df.Name) { sf = f; break; }
            if (sf != null)
            {
                fieldMap[df] = sf;
                if (sf.Attributes != df.Attributes && !Gen(st) && !clonedTypes.Contains(st)) { Log.AppendLine("field attrs " + df.FullName + ": " + sf.Attributes + " -> " + df.Attributes); if (sf.IsStatic != df.IsStatic) staticChanged.Add(sf); sf.Attributes = df.Attributes; }
                return sf;
            }
            sf = new FieldDefinition(df.Name, df.Attributes, MapType(df.FieldType));
            fieldMap[df] = sf;
            if (df.HasConstant) sf.Constant = df.Constant;
            if (df.InitialValue != null && df.InitialValue.Length > 0) sf.InitialValue = (byte[])df.InitialValue.Clone();
            if (df.HasLayoutInfo) sf.Offset = df.Offset;
            if (df.HasMarshalInfo) sf.MarshalInfo = df.MarshalInfo;
            st.Fields.Add(sf);
            CopyAttrs(df, sf);
            FieldsCloned++;
            if (!Gen(st)) Log.AppendLine("add field " + df.FullName);
            return sf;
        }

        MethodDefinition MapMethodDef(MethodDefinition dm)
        {
            MethodDefinition sm;
            if (methodMap.TryGetValue(dm, out sm)) return sm;
            var st = MapTypeDef(dm.DeclaringType);
            // Mapping the declaring type may have cloned this very method already
            // (a call into a helper class reaches its constructor first).
            if (methodMap.TryGetValue(dm, out sm)) return sm;
            var key = Key(dm);
            foreach (var m in st.Methods) if (!clonedMethods.Contains(m) && Key(m) == key) { sm = m; break; }
            if (sm != null) { methodMap[dm] = sm; return sm; }
            sm = new MethodDefinition(dm.Name, dm.Attributes, S.TypeSystem.Void)
            {
                ImplAttributes = dm.ImplAttributes, SemanticsAttributes = dm.SemanticsAttributes,
                HasThis = dm.HasThis, ExplicitThis = dm.ExplicitThis, CallingConvention = dm.CallingConvention,
            };
            methodMap[dm] = sm;
            clonedMethods.Add(sm);
            st.Methods.Add(sm);
            foreach (var g in dm.GenericParameters) sm.GenericParameters.Add(new GenericParameter(g.Name, sm) { Attributes = g.Attributes });
            for (int i = 0; i < dm.GenericParameters.Count; i++) foreach (var c in dm.GenericParameters[i].Constraints) sm.GenericParameters[i].Constraints.Add(MapType(c));
            sm.ReturnType = MapType(dm.ReturnType);
            foreach (var p in dm.Parameters)
            {
                var sp = new ParameterDefinition(p.Name, p.Attributes, MapType(p.ParameterType));
                if (p.HasConstant) sp.Constant = p.Constant;
                CopyAttrs(p, sp);
                sm.Parameters.Add(sp);
            }
            CopyAttrs(dm, sm);
            foreach (var o in dm.Overrides) sm.Overrides.Add(MapMethod(o));
            MethodsCloned++;
            if (!Gen(st)) Log.AppendLine("add method " + dm.FullName);
            if (dm.HasBody) bodyPending.Add(dm);
            return sm;
        }

        MethodReference MapMethod(MethodReference m)
        {
            var gim = m as GenericInstanceMethod;
            if (gim != null)
            {
                var g = new GenericInstanceMethod(MapMethod(gim.ElementMethod));
                foreach (var a in gim.GenericArguments) g.GenericArguments.Add(MapType(a));
                return g;
            }
            var md = m as MethodDefinition;
            if (md != null && md.Module == D) return MapMethodDef(md);
            if (InDonor(m.DeclaringType))
            {
                var def = m.Resolve();                    // a method on a generic instance of a donor type
                if (def != null) MapMethodDef(def);
            }
            var r = new MethodReference(m.Name, MapType(m.ReturnType), MapType(m.DeclaringType)) { HasThis = m.HasThis, ExplicitThis = m.ExplicitThis, CallingConvention = m.CallingConvention };
            foreach (var g in m.GenericParameters) r.GenericParameters.Add(new GenericParameter(g.Name, r));
            foreach (var p in m.Parameters) r.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, MapType(p.ParameterType)));
            return r;
        }

        FieldReference MapField(FieldReference f)
        {
            var fd = f as FieldDefinition;
            if (fd != null && fd.Module == D) return MapFieldDef(fd);
            if (InDonor(f.DeclaringType)) { var def = f.Resolve(); if (def != null) MapFieldDef(def); }
            return new FieldReference(f.Name, MapType(f.FieldType), MapType(f.DeclaringType));
        }

        void CopyAttrs(Mono.Cecil.ICustomAttributeProvider from, Mono.Cecil.ICustomAttributeProvider to)
        {
            if (!from.HasCustomAttributes) return;
            foreach (var ca in from.CustomAttributes) to.CustomAttributes.Add(new CustomAttribute(MapMethod(ca.Constructor), ca.GetBlob()));
        }

        // ------------------------------------------------------------ bodies
        void CopyBody(MethodDefinition dm, MethodDefinition sm)
        {
            var db = dm.Body;
            var sb = new MethodBody(sm) { InitLocals = db.InitLocals, MaxStackSize = db.MaxStackSize };
            sm.Body = sb;
            foreach (var v in db.Variables) sb.Variables.Add(new VariableDefinition(MapType(v.VariableType)));
            var map = new Dictionary<Instruction, Instruction>();
            foreach (var i in db.Instructions) { var n = Instruction.Create(OpCodes.Nop); n.OpCode = i.OpCode; map[i] = n; sb.Instructions.Add(n); }
            foreach (var i in db.Instructions)
            {
                var n = map[i];
                object o = i.Operand, r = o;
                if (o == null) r = null;
                else if (o is Instruction) r = map[(Instruction)o];
                else if (o is Instruction[])
                {
                    var src = (Instruction[])o; var dst = new Instruction[src.Length];
                    for (int k = 0; k < src.Length; k++) dst[k] = map[src[k]];
                    r = dst;
                }
                else if (o is VariableDefinition) r = sb.Variables[((VariableDefinition)o).Index];
                else if (o is ParameterDefinition) { var p = (ParameterDefinition)o; r = p == db.ThisParameter ? sb.ThisParameter : sm.Parameters[p.Index]; }
                else if (o is MethodReference) r = MapMethod((MethodReference)o);
                else if (o is FieldReference) r = MapField((FieldReference)o);
                else if (o is TypeReference) r = MapType((TypeReference)o);
                else if (o is CallSite) throw new Exception("calli in " + dm.FullName);
                n.Operand = r;
            }
            foreach (var h in db.ExceptionHandlers)
            {
                var nh = new ExceptionHandler(h.HandlerType);
                nh.TryStart = map[h.TryStart];
                nh.TryEnd = h.TryEnd == null ? null : map[h.TryEnd];
                nh.HandlerStart = map[h.HandlerStart];
                nh.HandlerEnd = h.HandlerEnd == null ? null : map[h.HandlerEnd];
                nh.FilterStart = h.FilterStart == null ? null : map[h.FilterStart];
                nh.CatchType = (object)h.CatchType == null ? null : MapType(h.CatchType);
                sb.ExceptionHandlers.Add(nh);
            }
        }
    }
}
