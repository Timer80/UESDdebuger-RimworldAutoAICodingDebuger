using System;
using System.Collections.Generic;

// each feature is used, not merely declared, so a silent mis-parse still fails
class G03_tupleDeconstruct { public static int F() { var (a, b) = (1, 2); return a + b; } }
class G05_patternIsVar { public static bool F(object o) { if (o is int i) return i > 0; return false; } }
class G06_switchPatternStmt { public static int F(object o) { switch (o) { case int i: return i; default: return 0; } } }
class G07_refReturn { public static ref int F(int[] a) => ref a[0]; }
class G09_outDiscard { public static bool F(string s) { return int.TryParse(s, out _); } }
class G10_stackallocSpan { public static int F() { Span<int> s = stackalloc int[4]; return s.Length; } }
class G11_throwExpr { public static int F(int x) { return x > 0 ? x : throw new Exception(); } }
class G12_tupleNamedReturn { public static int F() { var t = F2(); return t.a + t.b; } static (int a, int b) F2() => (1, 2); }
class G14_unmanaged { public static void F<T>() where T : unmanaged { } }
class G15_switchExpr { public static int F(int x) { int y = x switch { 1 => 2, _ => 3 }; return y; } }
class G16_usingDecl { public static int F() { using var s = new System.IO.MemoryStream(); return (int)s.Length; } }
class G17_nullableRef { public static int F(string? s) { return s?.Length ?? 0; } }
class G18_nullCoalesceAssign { public static int F(string s) { s ??= "z"; return s.Length; } }
class G19_staticLocalFunc { public static int F() { static int G() => 1; return G(); } }
class G20_range { public static int F(int[] a) { var b = a[1..]; return b.Length; } }
class G21_defaultIfaceMemb { public interface I { int F() => 1; } public class C : I { } public static int G() { I i = new C(); return i.F(); } }
class G22_readonlyMember { public struct S { public readonly int F() => 1; } public static int G() { return new S().F(); } }
class G23_record { public record R(int X); public static int F() { var r = new R(5); return r.X; } }
class G24_initOnly { public int Y { get; init; } public static int F() { return new G24 { Y = 1 }.Y; } }
class G25_targetTypedNew { public static int F() { List<int> l = new(); return l.Count; } }
class G26_isNot { public static bool F(object o) { return o is not null; } }
class G27_patAnd { public static bool F(object o) { return o is int and > 3; } }
class G28_withExpr { public record R(int X); public static int F() { var r = new R(1); var r2 = r with { X = 5 }; return r2.X; } }
