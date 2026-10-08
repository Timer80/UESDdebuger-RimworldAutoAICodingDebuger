// 模组程序集来源与"全量 using"代价实测
// 由 _poc_roslyn 驱动在游戏进程内用 Roslyn 4.14 编译执行（本文件本身用纯反射，不依赖 Roslyn）
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Verse;

public static class ModRefProbe
{
    static string ManagedDir()
    {
        string data = UnityEngine.Application.dataPath;              // <game>/RimWorldWin64_Data
        string direct = Path.Combine(data, "Managed");
        if (Directory.Exists(direct)) return direct;
        string root = Path.GetDirectoryName(data);
        return Path.Combine(Path.Combine(root, "RimWorldWin64_Data"), "Managed");
    }

    /// <summary>问题 1：模组加载器能否汇报模组程序集（含工坊路径）？</summary>
    public static string LoaderReport()
    {
        var sb = new System.Text.StringBuilder();
        List<ModContentPack> mods = LoadedModManager.RunningModsListForReading;
        int withAsm = 0, asmTotal = 0, workshop = 0, local = 0, noLocation = 0;
        var detail = new System.Text.StringBuilder();
        foreach (ModContentPack m in mods)
        {
            // RimWorld 1.6：ModContentPack.assemblies 是 ModAssemblyHandler 字段，
            // 真正的程序集列表在 ModAssemblyHandler.loadedAssemblies（public List<Assembly>）
            var asms = m.assemblies == null ? null : m.assemblies.loadedAssemblies;
            if (asms == null || asms.Count == 0) continue;
            withAsm++;
            asmTotal += asms.Count;
            var names = new List<string>();
            foreach (Assembly a in asms)
            {
                names.Add(a.GetName().Name);
                try
                {
                    string loc = a.Location ?? "";
                    if (loc.Length == 0) noLocation++;
                    else if (loc.IndexOf("workshop", StringComparison.OrdinalIgnoreCase) >= 0) workshop++;
                    else local++;
                }
                catch { noLocation++; }
            }
            detail.Append("  " + m.PackageId + " n=" + asms.Count
                          + " [" + string.Join(",", names.ToArray()) + "]\n");
        }
        sb.Append("mods=" + mods.Count + " withAssemblies=" + withAsm + " modAssemblies=" + asmTotal
                  + " | path: workshop=" + workshop + " local=" + local + " noLocation=" + noLocation + "\n");
        sb.Append(detail);
        return sb.ToString();
    }

    /// <summary>引用集 A：仅已加载程序集。</summary>
    public static string[] RefsLoaded()
    {
        var list = new List<string>();
        foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
        {
            try { string loc = a.Location; if (!string.IsNullOrEmpty(loc) && File.Exists(loc)) list.Add(loc); }
            catch { }
        }
        return list.ToArray();
    }

    /// <summary>引用集 B：A + 本体 Managed 全集 + 加载器汇报的全部模组程序集。</summary>
    public static string[] RefsAll()
    {
        var list = new List<string>(RefsLoaded());
        var seen = new HashSet<string>(list, StringComparer.OrdinalIgnoreCase);
        string managed = ManagedDir();
        if (Directory.Exists(managed))
            foreach (string f in Directory.GetFiles(managed, "*.dll"))
                if (seen.Add(f)) list.Add(f);
        foreach (ModContentPack m in LoadedModManager.RunningModsListForReading)
        {
            var asms = m.assemblies == null ? null : m.assemblies.loadedAssemblies;
            if (asms == null) continue;
            foreach (Assembly a in asms)
            {
                try
                {
                    string loc = a.Location;
                    if (!string.IsNullOrEmpty(loc) && seen.Add(loc)) list.Add(loc);
                }
                catch { }
            }
        }
        return list.ToArray();
    }

    /// <summary>问题 2：把（模组/本体的）命名空间全量 using 会不会撞名？</summary>
    public static string Collisions()
    {
        var modNsTypes = new Dictionary<string, HashSet<string>>();
        var gameNsTypes = new Dictionary<string, HashSet<string>>();

        // 本体：已加载程序集里非模组来源的（用 Managed 目录判定）
        string managed = ManagedDir();
        var modLocs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ModContentPack m in LoadedModManager.RunningModsListForReading)
        {
            var asms = m.assemblies == null ? null : m.assemblies.loadedAssemblies;
            if (asms == null) continue;
            foreach (Assembly a in asms)
            {
                try { string loc = a.Location; if (!string.IsNullOrEmpty(loc)) modLocs.Add(loc); } catch { }
            }
        }

        foreach (string loc in RefsAll())
        {
            bool isMod = modLocs.Contains(loc);
            Dictionary<string, HashSet<string>> sink = isMod ? modNsTypes : gameNsTypes;
            Type[] types;
            try { types = Assembly.LoadFrom(loc).GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }
            catch { continue; }
            foreach (Type t in types)
            {
                if (string.IsNullOrEmpty(t.Namespace)) continue;
                if (!sink.ContainsKey(t.Namespace)) sink[t.Namespace] = new HashSet<string>();
                sink[t.Namespace].Add(t.Name);
            }
        }

        var sb = new System.Text.StringBuilder();
        sb.Append("gameNamespaces=" + gameNsTypes.Count + " modNamespaces=" + modNsTypes.Count + "\n");
        sb.Append("modNamespacesSample=[" + string.Join(", ",
            modNsTypes.Keys.OrderBy(k => k).Take(12).ToArray()) + "]\n");

        sb.Append(DupReport("仅模组之间", modNsTypes) + "\n");
        sb.Append(DupReport("本体+模组（默认 using 全量展开时）", Merge(gameNsTypes, modNsTypes)) + "\n");
        return sb.ToString();
    }

    static Dictionary<string, HashSet<string>> Merge(Dictionary<string, HashSet<string>> a,
                                                      Dictionary<string, HashSet<string>> b)
    {
        var r = new Dictionary<string, HashSet<string>>();
        foreach (var kv in a) r[kv.Key] = new HashSet<string>(kv.Value);
        foreach (var kv in b)
        {
            if (!r.ContainsKey(kv.Key)) r[kv.Key] = new HashSet<string>();
            foreach (string t in kv.Value) r[kv.Key].Add(t);
        }
        return r;
    }

    static string DupReport(string label, Dictionary<string, HashSet<string>> nsTypes)
    {
        var simpleToNs = new Dictionary<string, List<string>>();
        foreach (var kv in nsTypes)
            foreach (string tn in kv.Value)
            {
                if (!simpleToNs.ContainsKey(tn)) simpleToNs[tn] = new List<string>();
                simpleToNs[tn].Add(kv.Key);
            }
        var dups = simpleToNs.Where(kv => kv.Value.Count > 1).ToList();
        var worst = dups.OrderByDescending(kv => kv.Value.Count).Take(6)
            .Select(kv => kv.Key + "×" + kv.Value.Count).ToArray();
        return label + ": 命名空间数=" + nsTypes.Count + " 不同简单类型名=" + simpleToNs.Count
               + " 重名类型=" + dups.Count + " worst=[" + string.Join(", ", worst) + "]";
    }
}
