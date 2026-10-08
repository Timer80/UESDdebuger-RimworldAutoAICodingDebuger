// Feasibility harness: can Roslyn (modern C#) be loaded and used at runtime on Unity's Mono?
// Reflection-only driver so it needs no compile-time Roslyn reference.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

class Harness
{
    static Type tMetaRef, tTree, tCST, tComp, tParseOpts, tCompOpts, tOutputKind;
    static MethodInfo mParse, mRefCreate, mCreate, mEmit;
    static ConstructorInfo ctorOpts;

    static void Log(string s) { Console.WriteLine(s); }

    // Mono reports 0 for Process.WorkingSet64, so query the OS directly.
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct PMC
    {
        public uint cb; public uint PageFaultCount;
        public UIntPtr PeakWorkingSetSize; public UIntPtr WorkingSetSize;
        public UIntPtr QuotaPeakPagedPoolUsage; public UIntPtr QuotaPagedPoolUsage;
        public UIntPtr QuotaPeakNonPagedPoolUsage; public UIntPtr QuotaNonPagedPoolUsage;
        public UIntPtr PagefileUsage; public UIntPtr PeakPagefileUsage;
    }
    [System.Runtime.InteropServices.DllImport("psapi.dll", SetLastError = true)]
    static extern bool GetProcessMemoryInfo(IntPtr h, out PMC c, int size);

    static long WS()
    {
        try
        {
            PMC c = new PMC();
            c.cb = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(PMC));
            if (!GetProcessMemoryInfo(System.Diagnostics.Process.GetCurrentProcess().Handle, out c, (int)c.cb)) return -1;
            return (long)c.WorkingSetSize.ToUInt64();
        }
        catch { return -1; }
    }

    static string Mem(string tag)
    {
        return tag + ": gc=" + (GC.GetTotalMemory(false) / 1048576) + "MB ws=" + (WS() / 1048576) + "MB";
    }

    static void LoadAll(string dir)
    {
        // Order matters: System.Memory's typerefs into System.Numerics.Vectors must resolve first.
        string[] order = {
            "System.Numerics.Vectors.dll",
            "System.Runtime.CompilerServices.Unsafe.dll",
            "System.Threading.Tasks.Extensions.dll",
            "System.Memory.dll",
            "System.Collections.Immutable.dll",
            "System.Reflection.Metadata.dll",
            "Microsoft.CodeAnalysis.dll",
            "Microsoft.CodeAnalysis.CSharp.dll",
        };
        foreach (string f in order)
        {
            try
            {
                Assembly a = Assembly.LoadFrom(dir + f);
                Log("LOAD      " + a.GetName().Name + " " + a.GetName().Version);
            }
            catch (Exception e)
            {
                Log("LOADFAIL  " + f + " -> " + e.GetType().Name + ": " + e.Message);
            }
        }
    }

    static Type Pick(Assembly asm, string fullName)
    {
        Type t = null;
        try { t = asm.GetType(fullName, false); }
        catch (Exception e) { Log("          GetType(" + fullName + ") threw " + e.GetType().Name + ": " + e.Message); }
        Log("          type " + fullName + " = " + (t == null ? "NOT FOUND" : "ok"));
        if (t == null) throw new Exception("missing type " + fullName);
        return t;
    }

    static void InitRoslyn()
    {
        Assembly ca = Assembly.Load("Microsoft.CodeAnalysis");
        Assembly csc = Assembly.Load("Microsoft.CodeAnalysis.CSharp");
        Log("roslyn    CodeAnalysis=" + ca.GetName().Version + " CSharp=" + csc.GetName().Version);
        tMetaRef = Pick(ca, "Microsoft.CodeAnalysis.MetadataReference");
        tTree = Pick(ca, "Microsoft.CodeAnalysis.SyntaxTree");
        tCST = Pick(csc, "Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree");
        tComp = Pick(csc, "Microsoft.CodeAnalysis.CSharp.CSharpCompilation");
        tParseOpts = Pick(csc, "Microsoft.CodeAnalysis.CSharp.CSharpParseOptions");

        mParse = tCST.GetMethod("ParseText", new Type[] {
            typeof(string), tParseOpts, typeof(string), typeof(System.Text.Encoding), typeof(System.Threading.CancellationToken) });
        Log("          ParseText overload = " + (mParse == null ? "NOT FOUND" : "ok"));
        if (mParse == null)
        {
            foreach (MethodInfo mi in tCST.GetMethods())
                if (mi.Name == "ParseText") Log("            candidate: " + mi);
        }
        mRefCreate = PickMethod(tMetaRef, "CreateFromFile", typeof(string));
        mCreate = PickMethod(tComp, "Create", typeof(string));
        mEmit = PickMethod(tComp, "Emit", typeof(Stream));

        tCompOpts = Pick(csc, "Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions");
        tOutputKind = Pick(ca, "Microsoft.CodeAnalysis.OutputKind");
        foreach (ConstructorInfo c in tCompOpts.GetConstructors())
        {
            ParameterInfo[] ps = c.GetParameters();
            if (ps.Length == 0 || ps[0].ParameterType != tOutputKind) continue;
            if (ctorOpts == null || ps.Length < ctorOpts.GetParameters().Length) ctorOpts = c;
        }
        if (ctorOpts == null) throw new Exception("no CSharpCompilationOptions ctor");
        Log("          options ctor = " + ctorOpts);
    }

    static object MakeOptions()
    {
        object kind = Enum.Parse(tOutputKind, "DynamicallyLinkedLibrary");
        ParameterInfo[] ps = ctorOpts.GetParameters();
        object[] args = new object[ps.Length];
        args[0] = kind;
        for (int i = 1; i < ps.Length; i++)
            args[i] = ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null;
        return ctorOpts.Invoke(args);
    }

    // Roslyn exposes these with optional parameters, so match by name + first parameter instead
    // of by exact signature. Invoke() fills the omitted optional arguments.
    static MethodInfo PickMethod(Type t, string name, Type firstParam)
    {
        MethodInfo best = null;
        foreach (MethodInfo m in t.GetMethods())
        {
            if (m.Name != name) continue;
            ParameterInfo[] ps = m.GetParameters();
            if (ps.Length == 0 || ps[0].ParameterType != firstParam) continue;
            Log("          " + name + " -> " + m);
            if (best == null || ps.Length < best.GetParameters().Length) best = m;
        }
        if (best == null) throw new Exception("missing method " + name);
        return best;
    }

    // Mono's reflection does not fill omitted optional parameters, so pad them ourselves.
    static object Invoke(MethodInfo m, object target, params object[] provided)
    {
        ParameterInfo[] ps = m.GetParameters();
        if (provided.Length == ps.Length) return m.Invoke(target, provided);
        object[] args = new object[ps.Length];
        for (int i = 0; i < ps.Length; i++)
        {
            if (i < provided.Length) args[i] = provided[i];
            else args[i] = ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null;
        }
        return m.Invoke(target, args);
    }

    static object MakeTree(string code)
    {
        return mParse.Invoke(null, new object[] { code, null, "", null, default(System.Threading.CancellationToken) });
    }

    static void Compile(string name, string code, bool withShim)
    {
        IList trees = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(tTree));
        trees.Add(MakeTree(code));
        if (withShim)
            trees.Add(MakeTree("namespace System.Runtime.CompilerServices { internal static class IsExternalInit { } }"));

        IList refs = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(tMetaRef));
        refs.Add(Invoke(mRefCreate, null, typeof(object).Assembly.Location));
        refs.Add(Invoke(mRefCreate, null, typeof(System.Linq.Enumerable).Assembly.Location));

        object comp = Invoke(mCreate, null, name, trees, refs, MakeOptions());
        MemoryStream ms = new MemoryStream();
        object res = Invoke(mEmit, comp, ms);
        bool ok = (bool)res.GetType().GetProperty("Success").GetValue(res);

        Log("[" + name + "] shim=" + withShim + " success=" + ok + " bytes=" + ms.Length);
        foreach (object d in (IEnumerable)res.GetType().GetProperty("Diagnostics").GetValue(res))
        {
            string ds = d.ToString();
            if (ds.IndexOf("warning") < 0) Log("   DIAG " + ds);
        }
        if (!ok) return;

        Assembly asm = Assembly.Load(ms.ToArray());
        Log("   loaded=" + asm.FullName);
        Type tM = asm.GetType("Modern");
        if (tM != null)
        {
            Log("   Modern.F(1) = " + tM.GetMethod("F").Invoke(null, new object[] { 1 }));
            Log("   Modern.F(2) = " + tM.GetMethod("F").Invoke(null, new object[] { 2 }));
            Log("   Modern.F(9) = " + tM.GetMethod("F").Invoke(null, new object[] { 9 }));
            Log("   Modern.G()  = " + tM.GetMethod("G").Invoke(null, null));
            Log("   Modern.H(1) = " + tM.GetMethod("H").Invoke(null, new object[] { 1 }));
        }
        Type tR = asm.GetType("R");
        if (tR != null)
            Log("   new R(7,\"z\") = " + Activator.CreateInstance(tR, new object[] { 7, "z" }));
    }

    const string Modern = @"public record R(int A, string B);
public class Modern {
  public static string F(int x) => x switch { 1 => ""one"", 2 => ""two"", _ => ""many"" };
  public static int G() { R r = new(1, ""a""); R r2 = r with { A = 5 }; return r2.A + (r2 is { B: ""a"" } ? 100 : 0); }
  public static string H(object o) => o is not null ? ""set"" : ""null"";
}";

    static int Main(string[] args)
    {
        string dir = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory() + Path.DirectorySeparatorChar;
        Log("mono=" + Environment.Version + " clr=" + typeof(object).Assembly.GetName().Name + " " +
            typeof(object).Assembly.GetName().Version + " loc=" + typeof(object).Assembly.Location);
        Log("span=" + typeof(System.Span<int>).Assembly.GetName().Name +
            " index=" + (Type.GetType("System.Index, mscorlib") != null ? "present" : "missing") +
            " isExternalInit=" + (Type.GetType("System.Runtime.CompilerServices.IsExternalInit, mscorlib") != null ? "present" : "missing"));
        Log(Mem("mem before load"));
        LoadAll(dir);
        Log(Mem("mem after load "));
        try
        {
            InitRoslyn();
            Compile("NoShim", Modern, false);
            Compile("WithShim", Modern, true);
            Log(Mem("mem after use  "));
        }
        catch (Exception e)
        {
            Log("ROSLYN FAILED: " + e.GetType().Name + ": " + e.Message);
            Log("   stack: " + e.StackTrace);
            Exception inner = e.InnerException;
            while (inner != null) { Log("   inner: " + inner.GetType().Name + ": " + inner.Message); inner = inner.InnerException; }
            return 1;
        }
        return 0;
    }
}
