// 外部模组参数盘点：TEXT20（text20.flightastar）
// 由 _poc_roslyn 驱动在游戏进程内用 Roslyn 4.14 编译执行（C# 13 语法可用）。
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Verse;
using FlightPawn2Framework.PathPlan.FlightAStar;

public static class Text20Defs
{
    public static string All()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("vanillaThingDefs=" + DefDatabase<ThingDef>.AllDefsListForReading.Count);

        Assembly asm = typeof(FlightPawnConfig).Assembly;
        Type[] ts;
        try { ts = asm.GetTypes(); }
        catch (Exception e) { return sb.ToString() + " GetTypes FAILED " + e.GetType().Name; }

        var defTypes = ts.Where(t => typeof(Def).IsAssignableFrom(t) && t != typeof(Def) && !t.IsAbstract).ToList();
        sb.Append(" TEXT20DefTypes=" + defTypes.Count + " [" + string.Join("|", defTypes.Select(t => t.Name).ToArray()) + "]");

        foreach (Type t in defTypes)
        {
            PropertyInfo prop = typeof(DefDatabase<>).MakeGenericType(t).GetProperty("AllDefsListForReading");
            var list = (IEnumerable)prop.GetValue(null);
            int count = 0;
            var names = new System.Text.StringBuilder();
            foreach (object o in list)
            {
                count++;
                if (count <= 4) names.Append(((Def)o).defName + ",");
            }
            sb.Append("\n  " + t.Name + "=" + count + " [" + names + "]");

            if (count > 0)
            {
                Def first = list.Cast<Def>().First();
                sb.Append("\n    first=" + first.defName + " label=" + first.label);
                int k = 0;
                foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
                {
                    k++;
                    if (k > 16) break;
                    object v = null;
                    try { v = f.GetValue(first); } catch { v = "<err>"; }
                    string vs = v == null ? "null" : v.ToString();
                    if (vs.Length > 70) vs = vs.Substring(0, 70) + "...";
                    sb.Append("\n      " + f.Name + " = " + vs);
                }
            }
        }
        return sb.ToString();
    }

    // 现代 C# 语法就位性检查（旧 mcs 控制台无法编译以下任何一行）
    public static string ModernSyntax()
    {
        var rows = new List<string>();
        int SwitchExpr(int x) => x switch { 1 => 10, 2 => 20, _ => 0 };
        rows.Add("switchExpr(2)=" + SwitchExpr(2));

        using var mem = new System.IO.MemoryStream();
        rows.Add("usingDecl.ok=" + (mem != null));

        string? maybe = null;
        rows.Add("nullableRef.isEmpty=" + (maybe is null));

        var rec = new ProbeRec(3, "wing");
        var bumped = rec with { A = 9 };
        rows.Add("record.with=" + bumped);

        return string.Join(" | ", rows.ToArray());
    }

    public record ProbeRec(int A, string B);
}
