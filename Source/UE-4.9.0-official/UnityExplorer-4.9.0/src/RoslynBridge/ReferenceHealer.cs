using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace UnityExplorer.RoslynBridge
{
    /// <summary>
    /// 把"缺程序集/缺命名空间"解析成具体 dll 路径。只读元数据（Roslyn <c>MetadataReference</c> /
    /// <c>AssemblyName.GetAssemblyName</c>），不用 Assembly.LoadFrom（避免真加载本体 dll，§3.1 老教训）。
    /// 任务 13 定稿三段顺序：①CS0433/CS1704 响亮拒绝 → ②身份通道（已加载程序集全身份优先 →
    /// 退简单名必打降级结构化日志 → Location 空按身份找文件 → 仍无响亮建议）→ ③符号通道
    /// （池 = 加载件文件 + 固定目录 + modPaths(ON)；OFF ⇒ 模组路径作排除集）。
    /// C.5：载荷类型只进方法体不进字段；全程无 lambda/迭代器（闭包/缓存委托都会生成字段）。
    /// </summary>
    public sealed class ReferenceHealer
    {
        readonly List<string> s_dirs;
        // 候选池结果缓存：正结果键 = 前缀名（值=路径，命中时再验 File.Exists/未引用）；
        // 负结果键 = 前缀名 + "#" + 池代际（refs 数量/开关/模组路径数变化即失效 —— 池每次 Resolve 现算，负缓存不许跨代）。
        readonly Dictionary<string, string> s_nsCache = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>已成功补进引用集的 dll 路径（"计数 &gt; 0 才证明分支真被执行"的观测点）。</summary>
        public List<string> Healed { get; } = new List<string>();

        /// <summary>模组程序集候选来源（每次 Resolve 现算，禁止构造期快照；§4.1）。宿主用方法组注入。</summary>
        public Func<List<string>> ModPathsProvider { get; set; }

        /// <summary>响亮日志出口（拒绝/降级/到顶建议）。宿主用方法组注入；null = 静默（离线部分用例）。</summary>
        public Action<string> ReportSink { get; set; }

        /// <summary>
        /// 开关 <c>CSharp_Console_Allow_Mod_Type_Resolution</c> 的落位（默认 ON = 配置默认值）。
        /// ON ⇒ 模组路径进候选池、仅诊断触发（永不预加）；OFF ⇒ 模组路径作**排除集**
        /// （否则候选池"已加载程序集 Location"通道会绕过开关），失败日志注明开关。
        /// </summary>
        public bool AllowModTypeResolution { get; set; } = true;

        public ReferenceHealer(IEnumerable<string> searchDirs)
        {
            s_dirs = new List<string>();
            if (searchDirs == null) return;
            foreach (string d in searchDirs)
            {
                if (string.IsNullOrEmpty(d)) continue;
                try { if (Directory.Exists(d) && !s_dirs.Contains(d)) s_dirs.Add(d); } catch { }
            }
        }

        void Report(string message)
        {
            Action<string> sink = ReportSink;
            if (sink != null) sink(message);
        }

        /// <summary>返回应该补进引用集的 dll 路径；无法解析返回 null（此时响亮原因已在 ReportSink/内部日志）。</summary>
        public string Resolve(string diagnostics, ICollection<string> currentRefs)
        {
            if (string.IsNullOrEmpty(diagnostics)) return null;

            // ① CS0433（类型同时存在于两个程序集）/ CS1704（重复的程序集标识）= "重复定义"不是"缺引用"：
            //    再加引用只会让冲突更糟 —— 响亮拒绝（§4.6 保留项）。
            bool has433 = diagnostics.IndexOf("CS0433", StringComparison.Ordinal) >= 0;
            bool has1704 = diagnostics.IndexOf("CS1704", StringComparison.Ordinal) >= 0;
            if (has433 || has1704)
            {
                Report("拒绝自愈：诊断含 "
                    + (has433 && has1704 ? "CS0433/CS1704" : (has433 ? "CS0433" : "CS1704"))
                    + "（同名类型重复定义/程序集标识重复）——同身份两份时再加引用只会更糟，已按设计拒绝自愈。");
                return null;
            }

            if (currentRefs == null) currentRefs = new List<string>();

            // 模组路径每次 Resolve 现算（§4.1：中途加载的程序集必须能被看见）
            List<string> modPaths = null;
            Func<List<string>> provider = ModPathsProvider;
            if (provider != null)
            {
                try { modPaths = provider(); } catch (Exception ex) { Report("模组候选路径获取失败：" + ex.Message); }
            }
            bool allowMod = AllowModTypeResolution;
            bool blockedSwitch = false;

            // ② 身份通道（诊断自带 Version/Culture/PKT 时）
            string idName, idVer, idCulture, idPkt;
            bool hasIdentity = MissingReferenceParser.TryAssemblyIdentity(diagnostics,
                out idName, out idVer, out idCulture, out idPkt);
            if (hasIdentity)
            {
                bool refsOnly;
                string p = ResolveByAssemblyIdentity(idName, idVer, idCulture, idPkt,
                    currentRefs, modPaths, allowMod, out blockedSwitch, out refsOnly);
                if (p != null) { Healed.Add(p); return p; }
            }

            // ③ 符号通道（CS0234 组合命名空间优先，其次 CS0246/CS0103 全名；深前缀匹配）
            string sym = MissingReferenceParser.SymbolFromCs0234(diagnostics)
                       ?? MissingReferenceParser.SymbolFromCs0246(diagnostics)
                       ?? MissingReferenceParser.SymbolFromCs0103(diagnostics);
            bool symBlocked = false;
            if (sym != null)
            {
                string p = ResolveByNamespace(sym, currentRefs, modPaths, allowMod, out symBlocked);
                if (p != null) { Healed.Add(p); return p; }
            }
            blockedSwitch = blockedSwitch || symBlocked;

            // 解析失败的响亮出口（禁静默）
            if (blockedSwitch)
                Report("无法自愈：已排除模组程序集候选（配置 CSharp_Console_Allow_Mod_Type_Resolution=OFF"
                    + " ⇒ 模组类型按设计不可解析）；本次失败若涉及模组类型即因此，需要时请开启该配置。");
            if (hasIdentity && !blockedSwitch)
                Report("无法自愈：未找到身份 '" + idName + ", Version=" + (idVer ?? "?")
                    + ", Culture=" + (idCulture ?? "?") + ", PublicKeyToken=" + (idPkt ?? "?")
                    + "' 的程序集文件（含内存加载 Location 为空的情形）。可执行建议：把该 dll 放进模组目录随模组加载，"
                    + "或改用 Assembly.LoadFrom(完整路径) 加载后重试。");
            return null;
        }

        // ───────────────────────── ② 身份通道 ─────────────────────────

        string ResolveByAssemblyIdentity(string simple, string version, string culture, string pkt,
            ICollection<string> currentRefs, List<string> modPaths, bool allowMod,
            out bool blockedSwitch, out bool refsOnly)
        {
            blockedSwitch = false;
            refsOnly = false;
            bool hasVersionReq = version != null;

            var exactPaths = new List<string>();
            var simplePaths = new List<string>();
            var simpleVersions = new List<string>();
            bool exactAny = false, simpleAny = false, anyLocEmpty = false;
            string emptyLocSimpleVersion = null;

            Assembly[] loaded = null;
            try { loaded = AppDomain.CurrentDomain.GetAssemblies(); } catch { }
            if (loaded != null)
            {
                foreach (Assembly a in loaded)
                {
                    AssemblyName an;
                    try { an = a.GetName(); } catch { continue; }
                    if (an == null || an.Name == null
                        || !string.Equals(an.Name, simple, StringComparison.OrdinalIgnoreCase)) continue;
                    bool exact = IdentityEquals(an, version, culture, pkt);
                    string loc = null;
                    try { loc = a.Location; } catch { loc = null; }
                    bool locOk = !string.IsNullOrEmpty(loc);
                    try { locOk = locOk && File.Exists(loc); } catch { locOk = false; }
                    if (exact)
                    {
                        exactAny = true;
                        if (locOk) AddUnique(exactPaths, loc);
                        else anyLocEmpty = true;
                    }
                    else
                    {
                        simpleAny = true;
                        if (locOk) { AddUnique(simplePaths, loc); simpleVersions.Add(AnVersion(an)); }
                        else { anyLocEmpty = true; if (emptyLocSimpleVersion == null) emptyLocSimpleVersion = AnVersion(an); }
                    }
                }
            }

            var candidates = new List<string>();
            if (exactAny && exactPaths.Count > 0)
            {
                candidates.AddRange(exactPaths);
            }
            else
            {
                // 完全匹配失败 → 退简单名：**必须**打降级结构化日志（§4.10：名字/旧版本/新版本/采用/原因）
                if (simpleAny)
                {
                    string adopted = simplePaths.Count > 0 ? simplePaths[0] : null;
                    string matchedVer = simplePaths.Count > 0 ? simpleVersions[0] : emptyLocSimpleVersion;
                    Report("身份降级：名字=" + simple
                        + " 旧版本=" + (version ?? "<诊断未给>")
                        + " 新版本=" + (matchedVer ?? "<未知>")
                        + " 采用=" + (adopted ?? "<无 Location，转按身份找文件>")
                        + " 原因=完整身份未命中，按简单名匹配（身份不完全一致，禁止把简单名当身份）");
                    candidates.AddRange(simplePaths);
                }
                if (candidates.Count == 0 || anyLocEmpty)
                {
                    // Location 为空（内存加载）/无已加载匹配 → 按身份在候选目录找同身份文件（§4.8）
                    List<string> dirs = BuildCandidateDirs(modPaths, allowMod);
                    foreach (string dir in dirs)
                    {
                        string p;
                        try { p = Path.Combine(dir, simple + ".dll"); } catch { continue; }
                        bool exists;
                        try { exists = File.Exists(p); } catch { exists = false; }
                        if (!exists) continue;
                        AssemblyName fileAn = null;
                        try { fileAn = AssemblyName.GetAssemblyName(p); } catch { fileAn = null; }
                        if (fileAn == null) continue;
                        if (hasVersionReq && !IdentityEquals(fileAn, version, culture, pkt))
                        {
                            // 同简单名不同身份：结构化留痕（§4.10 禁静默），跳过该文件
                            Report("身份不符：名字=" + simple
                                + " 旧版本=" + (version ?? "<诊断未给>")
                                + " 新版本=" + AnVersionOf(fileAn)
                                + " 采用=无"
                                + " 原因=候选文件身份与诊断要求不符，已跳过");
                            continue;
                        }
                        AddUnique(candidates, p);
                    }
                }
            }

            foreach (string p in candidates)
            {
                if (ContainsPath(currentRefs, p)) { refsOnly = true; continue; }
                if (!allowMod && IsModPath(p, modPaths)) { blockedSwitch = true; continue; }
                return p;
            }
            if (candidates.Count == 0 && ContainsStem(currentRefs, simple)) refsOnly = true;
            return null;
        }

        // ───────────────────────── ③ 符号通道 ─────────────────────────

        string ResolveByNamespace(string name, ICollection<string> currentRefs,
            List<string> modPaths, bool allowMod, out bool blockedSwitch)
        {
            blockedSwitch = false;
            if (string.IsNullOrEmpty(name)) return null;

            // 深前缀候选：A.B.C → [A.B.C, A.B, A]（深的先试，避免短前缀误配 —— 简报首段截断会误配 System.Data）。
            // 缺名是"命名空间段"还是"类型"从诊断文本分不出（'CSharp' 是段、'ConfigurationManager' 是类型），
            // 故一律**先试全名、再退逐级父前缀**；退前缀的合法性由下一层判据守：某个前缀一旦"匹配到候选但被
            // 开关排除"就终止（见循环内 blockedAtPrefix），绝不拿更浅前缀的程序集顶替。
            var prefixes = new List<string>();
            string cur = name;
            for (; ; )
            {
                if (!prefixes.Contains(cur)) prefixes.Add(cur);
                int dot = cur.LastIndexOf('.');
                if (dot < 0) break;
                cur = cur.Substring(0, dot);
            }

            // 池每次现算（§4.1）：① 已加载程序集文件 ② 固定目录 ③ modPaths（ON 才入池）
            var pool = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Assembly[] loaded = null;
            try { loaded = AppDomain.CurrentDomain.GetAssemblies(); } catch { }
            if (loaded != null)
            {
                foreach (Assembly a in loaded)
                {
                    string loc = null;
                    try { loc = a.Location; } catch { loc = null; }
                    if (string.IsNullOrEmpty(loc)) continue;
                    try { if (!File.Exists(loc)) continue; } catch { continue; }
                    if (seen.Add(loc)) pool.Add(loc);
                }
            }
            foreach (string dir in s_dirs)
            {
                string[] files = null;
                try { files = Directory.GetFiles(dir, "*.dll"); } catch { }
                if (files == null) continue;
                foreach (string f in files)
                    if (seen.Add(f)) pool.Add(f);
            }
            if (allowMod && modPaths != null)
                foreach (string p in modPaths)
                {
                    if (string.IsNullOrEmpty(p)) continue;
                    try { if (!File.Exists(p)) continue; } catch { continue; }
                    if (seen.Add(p)) pool.Add(p);
                }

            // 缓存键 = **池内容指纹**（不是计数！）：微审实测指出"计数相同但内容不同"（卸 ModA 装 ModB、
            // 引用集数量不变而内容换血）会命中上一代的解 —— 正向静默返回无关程序集、负向把本次已有解判成无解。
            // 指纹取"本代际现算池的全部路径 + 开关 + 引用集数"的滚动哈希；命中分支另有 pool.Contains 成员校验。
            int gen = currentRefs.Count * 7919 + (allowMod ? 131 : 0) + (modPaths == null ? 0 : modPaths.Count);
            int ph = 17;
            foreach (string p in pool)
            {
                ph = ph * 31 + (p == null ? 0 : p.GetHashCode());
            }
            gen = gen * 31 + ph;
            string genKey = "#" + gen + "|";

            foreach (string prefix in prefixes)
            {
                // 终止条件一：本前缀存在"文件名=命名空间"的**同名程序集**（stem 命中，无论是否被开关排除）⇒ 不再用
                // pass2 的"其它含该命名空间的程序集"顶替。实测必要性：`Microsoft.CSharp` 命名空间同时存在于
                // Microsoft.CSharp.dll（RuntimeBinder）与 System.dll（CSharpCodeProvider）⇒ 纯命名空间判据会挑到
                // System.dll；而该诊断要的正是**同名程序集**。stem 命中全被开关排除时（OFF 语义）响亮失败，不顶替。
                bool stemDeclares = false;
                // 终止条件二：本前缀"匹配到候选但被开关排除" ⇒ 不许退更浅前缀另找
                //（'Microsoft.CSharp' 退到 'Microsoft' 会命中 System.dll——它只借 Microsoft.Win32 顺带存在该命名空间）。
                bool blockedAtPrefix = false;

                // 正缓存与本代际负缓存同键（genKey+prefix）：池按 §4.1 每次现算，跨代际的正缓存会在池变化后
                // 返回"上一代池里的最佳解"（同样会顶替成无关程序集）。
                string cached;
                bool cachedFound = s_nsCache.TryGetValue(genKey + prefix, out cached);
                if (cachedFound && !string.IsNullOrEmpty(cached))
                {
                    bool ok;
                    try { ok = File.Exists(cached); } catch { ok = false; }
                    // pool.Contains 是"仍是本代际候选"的证据（微审指出：File.Exists/GatePass 只证明缓存值自身，
                    // 文件还在但已不是正确解正好落在盲区）。
                    if (ok && pool.Contains(cached) && !ContainsPath(currentRefs, cached)
                        && GatePass(cached, modPaths, allowMod))
                        return cached;
                }
                else if (cachedFound)
                {
                    continue;   // 本代际已判无解
                }

                // pass1：文件名 == 前缀（把 System.Xml.dll 排在 System.Xml.Linq.dll 前，避免垃圾引用）
                foreach (string p in pool)
                {
                    if (ContainsPath(currentRefs, p)) continue;
                    if (!string.Equals(FileNameStem(p), prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!DefinesNamespace(p, prefix)) continue;
                    stemDeclares = true;
                    if (!allowMod && IsModPath(p, modPaths)) { blockedAtPrefix = true; continue; }
                    ReportSameStemChoices(p, pool);
                    s_nsCache[genKey + prefix] = p;
                    return p;
                }
                // pass2：Roslyn 只读元数据判命名空间（**仅当无同名程序集时**才允许，见 stemDeclares 说明）
                if (!stemDeclares)
                {
                    foreach (string p in pool)
                    {
                        if (ContainsPath(currentRefs, p)) continue;
                        if (!DefinesNamespace(p, prefix)) continue;
                        if (!allowMod && IsModPath(p, modPaths)) { blockedAtPrefix = true; continue; }
                        ReportSameStemChoices(p, pool);
                        s_nsCache[genKey + prefix] = p;
                        return p;
                    }
                }
                s_nsCache[genKey + prefix] = "";
                if (blockedAtPrefix)
                {
                    blockedSwitch = true;
                    return null;   // 响亮失败：目标命名空间只有被排除的模组程序集提供
                }
            }
            return null;
        }

        bool GatePass(string path, List<string> modPaths, bool allowMod)
        {
            if (allowMod) return true;
            return !IsModPath(path, modPaths);
        }

        List<string> BuildCandidateDirs(List<string> modPaths, bool allowMod)
        {
            var dirs = new List<string>(s_dirs);
            if (allowMod && modPaths != null)
                foreach (string p in modPaths)
                {
                    if (string.IsNullOrEmpty(p)) continue;
                    string d = null;
                    try { d = Path.GetDirectoryName(p); } catch { d = null; }
                    if (!string.IsNullOrEmpty(d) && !dirs.Contains(d)) dirs.Add(d);
                }
            return dirs;
        }

        /// <summary>只读元数据判断该 dll 是否**声明**了命名空间 ns（Roslyn 读程序集符号，不加载程序集）。
        /// 判据 = 命名空间节点存在（<b>不</b>校验"该命名空间下至少有一个由本程序集定义的类型"）。
        /// 为什么不做更严的校验：①实测 `System.dll` **真声明** `Microsoft.CSharp.CSharpCodeProvider`
        /// （不是类型转发），所以"含该命名空间"本就有多个合法 provider，严校验也区分不出"诊断要哪一个"；
        /// ②曾按"须有本程序集定义的类型"（`SymbolEqualityComparer`）实现，实测把**合法匹配也判否**
        /// （`Microsoft.CSharp.dll` 被拒 ⇒ 测试 ④ ON 反而失败）⇒ 已回退。
        /// 真正的防线在上层：**同名程序集优先**（`stemDeclares`，文件名==命名空间者不被其它 provider 顶替）
        /// + **无效自愈回滚**（加入后诊断无变化即撤回，见 `ReplSession.Submit`/`CompileWithHealing`）。
        /// 载荷类型只作局部，无 lambda/迭代器捕获（C.5）。</summary>
        static bool DefinesNamespace(string dllPath, string ns)
        {
            try
            {
                MetadataReference mr = MetadataReference.CreateFromFile(dllPath);
                CSharpCompilation probe = CSharpCompilation.Create("probe", null, new[] { mr });
                if (!(probe.GetAssemblyOrModuleSymbol(mr) is IAssemblySymbol asm)) return false;
                INamespaceSymbol cur2 = asm.GlobalNamespace;
                string[] parts = ns.Split('.');
                for (int i = 0; i < parts.Length; i++)
                {
                    INamespaceSymbol next = null;
                    foreach (INamespaceSymbol child in cur2.GetNamespaceMembers())
                    {
                        if (child.Name == parts[i]) { next = child; break; }
                    }
                    if (next == null) return false;
                    cur2 = next;
                }
                return true;   // 命名空间节点存在即算"定义"（转发/同名多provider 的区分交给上层"同名程序集优先"规则）
            }
            catch { return false; }
        }

        // ───────────────────────── 同简单名多份的**采用披露**（§4.10-2） ─────────────────────────

        void ReportSameStemChoices(string chosen, List<string> pool)
        {
            string msg = DescribeSameStemChoices(chosen, pool);
            if (msg != null) Report(msg);
        }

        /// <summary>**命名空间通道的采用披露**（上机 13-10 抓到的缺口）：命名空间通道只按池内**首个**命中
        /// 取件，若池内还有**同简单名但身份不同**的候选（典型=同一个 dll 的两个版本），必须说清
        /// "采用谁 / 另有哪些 / 为什么" —— 否则日志里的"补上了"根本没告诉你补的是哪一版，
        /// 这正是 dll 热重载场景"新版静默不生效"的病根（§4.10）。
        /// 返回 null = 无同简单名多身份（**常态，零噪声**：同身份副本不报）。公开静态 ⇒ 可离线断言。</summary>
        public static string DescribeSameStemChoices(string chosen, IList<string> pool)
        {
            if (string.IsNullOrEmpty(chosen) || pool == null) return null;
            string stem = FileNameStem(chosen);
            if (string.IsNullOrEmpty(stem)) return null;
            string chosenId = IdentityOf(chosen);
            List<string> others = null;
            foreach (string q in pool)
            {
                if (string.IsNullOrEmpty(q) || SamePath(q, chosen)) continue;
                if (!string.Equals(FileNameStem(q), stem, StringComparison.OrdinalIgnoreCase)) continue;
                string id = IdentityOf(q);
                if (string.Equals(id, chosenId, StringComparison.OrdinalIgnoreCase)) continue;   // 同身份副本：无歧义
                if (others == null) others = new List<string>();
                others.Add(q + "（身份=" + id + "）");
            }
            if (others == null) return null;
            return "命名空间通道采用披露：候选池里同简单名 '" + stem + "' 有 **身份不同** 的多份 ⇒ 采用="
                + chosen + "（身份=" + chosenId + "）；未采用 " + others.Count + " 份："
                + string.Join("；", others.ToArray())
                + "。原因=命名空间通道按池内首个命中取件（同一编译内同简单名只能引用一份，故不把多版本当等价）";
        }

        /// <summary>只读元数据取身份（版本+公钥标记）；取不到 → "&lt;未知&gt;"。**只读，不加载程序集**。</summary>
        static string IdentityOf(string path)
        {
            if (string.IsNullOrEmpty(path)) return "<未知>";
            try
            {
                AssemblyName an = AssemblyName.GetAssemblyName(path);
                if (an == null) return "<未知>";
                string v = an.Version == null ? "<无版本>" : an.Version.ToString();
                byte[] pkt = an.GetPublicKeyToken();
                string token = "<无标记>";
                if (pkt != null && pkt.Length > 0)
                {
                    var sb = new StringBuilder(pkt.Length * 2);
                    for (int i = 0; i < pkt.Length; i++) sb.Append(pkt[i].ToString("x2"));
                    token = sb.ToString();
                }
                return "Version=" + v + ", PKT=" + token;
            }
            catch { return "<未知>"; }
        }

        static bool SamePath(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
            catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        }

        // ───────────────────────── 身份/路径小工具 ─────────────────────────

        static bool IdentityEquals(AssemblyName an, string version, string culture, string pkt)
        {
            // version == null ⇒ 诊断未给版本要求，只按名字+文化+标记（有给则必须逐项相等）
            if (version != null)
            {
                if (an.Version == null) return false;
                if (!string.Equals(an.Version.ToString(), version.Trim(), StringComparison.Ordinal)) return false;
            }
            string reqCulture = NormalizeCulture(culture);
            string gotCulture = NormalizeCulture(an.CultureName);
            if (!string.Equals(reqCulture, gotCulture, StringComparison.OrdinalIgnoreCase)) return false;
            if (pkt != null && pkt.Length > 0)
            {
                string req = pkt.Trim();
                string got = TokenHex(an);
                if (req.Equals("null", StringComparison.OrdinalIgnoreCase))
                    return got.Length == 0;
                return string.Equals(req, got, StringComparison.OrdinalIgnoreCase);
            }
            return true;
        }

        static string NormalizeCulture(string culture)
        {
            if (string.IsNullOrEmpty(culture)) return "neutral";
            string c = culture.Trim();
            return c.Length == 0 || c.Equals("neutral", StringComparison.OrdinalIgnoreCase) ? "neutral" : c;
        }

        static string TokenHex(AssemblyName an)
        {
            try
            {
                byte[] tok = an.GetPublicKeyToken();
                if (tok == null || tok.Length == 0) return "";
                var sb = new System.Text.StringBuilder(tok.Length * 2);
                foreach (byte b in tok) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
            catch { return ""; }
        }

        static string AnVersion(AssemblyName an)
        {
            try { return an.Version == null ? null : an.Version.ToString(); } catch { return null; }
        }

        static string AnVersionOf(AssemblyName an) => AnVersion(an) ?? "<未知>";

        static bool IsModPath(string path, List<string> modPaths)
        {
            if (modPaths == null || modPaths.Count == 0 || string.IsNullOrEmpty(path)) return false;
            string full = null;
            try { full = Path.GetFullPath(path); } catch { full = path; }
            foreach (string m in modPaths)
            {
                if (string.IsNullOrEmpty(m)) continue;
                string mf = null;
                try { mf = Path.GetFullPath(m); } catch { mf = m; }
                if (string.Equals(full, mf, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        static bool ContainsPath(ICollection<string> refs, string path)
        {
            if (refs == null || string.IsNullOrEmpty(path)) return false;
            foreach (string r in refs)
            {
                if (string.IsNullOrEmpty(r)) continue;
                if (string.Equals(r, path, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        static bool ContainsStem(ICollection<string> refs, string simple)
        {
            if (refs == null || string.IsNullOrEmpty(simple)) return false;
            foreach (string r in refs)
            {
                if (string.IsNullOrEmpty(r)) continue;
                if (string.Equals(FileNameStem(r), simple, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        static string FileNameStem(string path)
        {
            try { return Path.GetFileNameWithoutExtension(path); } catch { return ""; }
        }

        static void AddUnique(List<string> list, string path)
        {
            foreach (string p in list)
                if (string.Equals(p, path, StringComparison.OrdinalIgnoreCase)) return;
            list.Add(path);
        }
    }
}
