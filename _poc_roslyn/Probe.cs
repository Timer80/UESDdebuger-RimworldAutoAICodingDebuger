using System;
using System.Collections.Generic;

class F01_localFunc_expr { public static int F() { int G() => 1; return G(); } }
class F02_localFunc_stmt { public static int F() { int G() { return 1; } return G(); } }
class F03_tupleDeconstruct { public static int F() { var (a, b) = (1, 2); return a + b; } }
class F04_readonlyStruct { public readonly struct S { public readonly int X; public S(int x) { X = x; } } }
class F05_patternIsVar { public static bool F(object o) { if (o is int i) return i > 0; return false; } }
class F06_switchPatternStmt { public static int F(object o) { switch (o) { case int i: return i; default: return 0; } } }
class F07_refReturn { public static ref int F(int[] a) => ref a[0]; }
class F08_stringInterp { public static string F(int x) { return $"v={x}"; } }
class F09_outDiscard { public static bool F(string s) { return int.TryParse(s, out _); } }
class F10_stackallocSpan { public static int F() { Span<int> s = stackalloc int[4]; return s.Length; } }
class F11_throwExpr { public static int F() => throw new Exception(); }
class F12_tupleNamedReturn { public static (int a, int b) F() => (1, 2); }
class F13_privProtected { private protected int X; }
class F14_unmanaged { public static void F<T>() where T : unmanaged { } }
class F15_switchExpr { public static int F(int x) => x switch { 1 => 2, _ => 3 }; }
class F16_usingDecl { public static void F() { using var s = new System.IO.MemoryStream(); } }
class F17_nullableRef { public static string? F(string? s) => s; }
class F18_nullCoalesceAssign { public static string F(string s) { s ??= string.Empty; return s; } }
class F19_staticLocalFunc { public static int F() { static int G() => 1; return G(); } }
class F20_range { public static int[] F(int[] a) => a[1..]; }
class F21_defaultIfaceMemb { public interface I { int F() => 1; } }
class F22_readonlyMember { public struct S { public readonly int F() => 1; } }
class F23_record { public record R(int X); }
class F24_initOnly { public int X { get; init; } }
class F25_targetTypedNew { public static List<int> F() => new(); }
class F26_isNot { public static bool F(object o) => o is not null; }
class F27_patAnd { public static bool F(object o) => o is int and > 3; }
