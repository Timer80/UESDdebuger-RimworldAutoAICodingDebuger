using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace UnityExplorer.RoslynBridge
{
    /// <summary>
    /// 任务 15d：**同简单名多份**时按"编译器策略"取舍（人类裁定原话）：
    /// "有 Roslyn 的情况下用全版（较新），没有 Roslyn 只有 mcs 用旧版"。
    /// 即：Roslyn 路 ⇒ 程序集版本号**更高**者优先；mcs 路 ⇒ 版本号**更低**者优先（旧编译器配旧库）。
    /// 单份、以及**同身份副本**（不同路径同一 Version+PKT）⇒ 结论与"池内首个命中"逐字一致
    /// （不引入任何新决定，披露也**不报警 = 零噪声**）。
    /// 纯逻辑：零 Roslyn/UE/Unity 依赖（只用 System.*），net35 安全
    /// （不用 <c>Array.Empty</c> 等 net40+ API、无 lambda/闭包捕获、无 C#8+ 语法）——
    /// 故本文件进全部 9 个配置，并可被测试工程 link 做离线断言。
    /// </summary>
    public enum SameStemPreference
    {
        /// <summary>程序集版本号更高者优先（Roslyn 路：有 Roslyn 用全版）。</summary>
        Newest = 0,
        /// <summary>程序集版本号更低者优先（mcs 路：旧编译器配旧库）。</summary>
        Oldest = 1,
    }

    /// <summary>
    /// 任务 15d：一个候选 = 路径 + 简单名 + 身份串（Version+PKT）+ 版本号。
    /// 只用只读元数据（<see cref="AssemblyName"/>）取身份，**不加载程序集**。
    /// </summary>
    public sealed class SameStemCandidate
    {
        /// <summary>候选路径（已加载件无 <c>Location</c> 时 = 简单名 + 说明；不会是 null）。</summary>
        public string Path;
        /// <summary>分组口径：Roslyn 路 = 文件名 stem（与命名空间通道的 stem 判据同源）；mcs 路 = 程序集简单名。</summary>
        public string SimpleName;
        /// <summary>身份串，形如 <c>Version=1.2.3.4, PKT=30ad4fe6b2a6aeed</c>（取不到 = <c>&lt;未知&gt;</c>）。</summary>
        public string Identity;
        /// <summary>版本号（排序键；取不到 = null，视为最低版本）。</summary>
        public Version Version;
        /// <summary>来源对象（mcs 路 = <see cref="Assembly"/>）——供调用方把选定项回找成实例。</summary>
        public object Tag;
    }

    /// <summary>
    /// 任务 15d：同简单名多份的**挑选 + 披露**纯逻辑（两路共用：Roslyn 路传 <see cref="SameStemPreference.Newest"/>，
    /// mcs 路传 <see cref="SameStemPreference.Oldest"/>）。披露文案两路同款，且**明写原因**，
    /// 免掉"补上了但补的是哪一版没人说得清"（dll 热重载"新版静默不生效"的病根）。
    /// </summary>
    public static class SameStemChoice
    {
        /// <summary>披露里"原因="的 Roslyn 版文案。</summary>
        public const string NewestReason = "按编译器策略：Roslyn 选较新";
        /// <summary>披露里"原因="的 mcs 版文案。</summary>
        public const string OldestReason = "按编译器策略：mcs 选较旧";
        /// <summary>身份取不到时的占位（与 <see cref="ReferenceHealer"/> 既有措辞同源）。</summary>
        public const string Unknown = "<未知>";

        /// <summary>偏好 ⇒ 披露原因文案。</summary>
        public static string ReasonOf(SameStemPreference pref)
        {
            return pref == SameStemPreference.Oldest ? OldestReason : NewestReason;
        }

        // ───────────────────────── 候选构造 ─────────────────────────

        /// <summary>池里的**文件路径** ⇒ 候选（简单名 = 文件名 stem，身份 = 该文件程序集元数据）。</summary>
        public static IList<SameStemCandidate> FromPaths(IList<string> paths)
        {
            var list = new List<SameStemCandidate>();
            if (paths == null) return list;
            for (int i = 0; i < paths.Count; i++)
            {
                string p = paths[i];
                if (string.IsNullOrEmpty(p)) continue;
                list.Add(FromPath(p));
            }
            return list;
        }

        /// <summary>单个文件路径 ⇒ 候选。</summary>
        public static SameStemCandidate FromPath(string path)
        {
            var c = new SameStemCandidate();
            c.Path = path;
            c.SimpleName = StemOf(path);
            AssemblyName an = ReadAssemblyName(path);
            c.Version = an == null ? null : an.Version;
            c.Identity = an == null ? Unknown : IdentityText(an);
            return c;
        }

        /// <summary>**已加载件** ⇒ 候选（简单名 = 程序集简单名，`Tag` 回填该 <see cref="Assembly"/>）。</summary>
        public static SameStemCandidate FromAssembly(Assembly asm)
        {
            var c = new SameStemCandidate();
            c.Tag = asm;
            c.SimpleName = SimpleNameOf(asm);
            c.Path = PathOf(asm);
            AssemblyName an = null;
            try { if (asm != null) an = asm.GetName(); }
            catch { an = null; }
            c.Version = an == null ? null : an.Version;
            c.Identity = an == null ? Unknown : IdentityText(an);
            return c;
        }

        /// <summary>已加载件列表 ⇒ 候选表（**逐项一一对应**，含 null 项也占位，便于按下标回找）。</summary>
        public static IList<SameStemCandidate> FromAssemblies(IList<Assembly> asms)
        {
            var list = new List<SameStemCandidate>();
            if (asms == null) return list;
            for (int i = 0; i < asms.Count; i++)
            {
                if (asms[i] == null) { list.Add(new SameStemCandidate()); continue; }
                list.Add(FromAssembly(asms[i]));
            }
            return list;
        }

        // ───────────────────────── 挑选 ─────────────────────────

        /// <summary>按偏好选定一项；无有效候选 ⇒ null。</summary>
        public static SameStemCandidate Pick(IList<SameStemCandidate> candidates, SameStemPreference pref)
        {
            int i = PickIndex(candidates, pref);
            return i < 0 || candidates == null ? null : candidates[i];
        }

        /// <summary>
        /// 选定项的**下标**（-1 = 空/无有效候选）。
        /// 判据：只把"身份不同"的候选算作歧义 —— 同身份副本（同一份 dll 的两个路径）一律取**首个**
        /// （与 15c 的"池内首个命中"逐字一致，零噪声）；身份不同时按**版本号**取舍
        /// （<see cref="SameStemPreference.Newest"/> 取更高、<see cref="SameStemPreference.Oldest"/> 取更低）；
        /// 版本号相同而身份不同（同版本不同 PKT）⇒ 仍取**首个**（结论只随输入顺序，不引入随机性）。
        /// </summary>
        public static int PickIndex(IList<SameStemCandidate> candidates, SameStemPreference pref)
        {
            if (candidates == null) return -1;
            int best = -1;
            for (int i = 0; i < candidates.Count; i++)
            {
                SameStemCandidate c = candidates[i];
                if (c == null) continue;
                if (string.IsNullOrEmpty(c.Path) && string.IsNullOrEmpty(c.SimpleName)) continue;
                if (best < 0) { best = i; continue; }
                SameStemCandidate b = candidates[best];
                if (SameIdentity(c, b)) continue;
                int cmp = CompareVersion(c.Version, b.Version);
                if (pref == SameStemPreference.Oldest ? cmp < 0 : cmp > 0) best = i;
            }
            return best;
        }

        // ───────────────────────── 披露 ─────────────────────────

        /// <summary>采用披露（通道名默认"同简单名多份"）。</summary>
        public static string Describe(SameStemCandidate chosen, IList<SameStemCandidate> candidates, SameStemPreference pref)
        {
            return Describe(chosen, candidates, pref, null);
        }

        /// <summary>
        /// **采用披露**（任务 15d：两路统一文案，格式 = 采用=路径（身份 Version+PKT）；
        /// 未采用 N 份=各路径与身份；原因=按编译器策略：Roslyn 选较新 / mcs 选较旧）。
        /// 返回 **null** = 没有"同简单名且身份不同"的另一份 —— 单份、同身份副本都不报（**常态，零噪声**）。
        /// 公开静态 ⇒ 可离线断言。
        /// </summary>
        public static string Describe(SameStemCandidate chosen, IList<SameStemCandidate> candidates,
            SameStemPreference pref, string channelLabel)
        {
            if (chosen == null || candidates == null) return null;
            string name = chosen.SimpleName;
            if (string.IsNullOrEmpty(name)) return null;
            string chosenId = chosen.Identity;
            List<string> others = null;
            for (int i = 0; i < candidates.Count; i++)
            {
                SameStemCandidate c = candidates[i];
                if (c == null) continue;
                if (SameCandidate(c, chosen)) continue;                       // 就是采用的那一份
                if (!string.Equals(c.SimpleName, name, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(c.Identity, chosenId, StringComparison.OrdinalIgnoreCase)) continue;  // 同身份副本：无歧义
                if (others == null) others = new List<string>();
                others.Add(c.Path + "（身份=" + c.Identity + "）");
            }
            if (others == null) return null;
            return (string.IsNullOrEmpty(channelLabel) ? "同简单名多份" : channelLabel)
                + "采用披露：候选池里同简单名 '" + name + "' 有 **身份不同** 的多份 ⇒ 采用="
                + chosen.Path + "（身份=" + chosenId + "）；未采用 " + others.Count + " 份："
                + string.Join("；", others.ToArray())
                + "。原因=" + ReasonOf(pref);
        }

        /// <summary>按**路径**披露（调用方只有"采用的那份路径"时用；池里找不到该项就现读元数据）。</summary>
        public static string Describe(string chosenPath, IList<SameStemCandidate> candidates,
            SameStemPreference pref, string channelLabel)
        {
            if (string.IsNullOrEmpty(chosenPath) || candidates == null) return null;
            SameStemCandidate c = FindByPath(candidates, chosenPath);
            if (c == null) c = FromPath(chosenPath);
            return Describe(c, candidates, pref, channelLabel);
        }

        /// <summary>按**已加载件**披露（mcs 路用）。</summary>
        public static string DescribeAssembly(Assembly chosen, IList<Assembly> all,
            SameStemPreference pref, string channelLabel)
        {
            if (chosen == null || all == null) return null;
            IList<SameStemCandidate> cands = FromAssemblies(all);
            SameStemCandidate c = FindByAssembly(cands, chosen);
            if (c == null) return null;
            return Describe(c, cands, pref, channelLabel);
        }

        // ───────────────────────── 查找/比较小工具 ─────────────────────────

        /// <summary>按路径找候选（全路径忽略大小写；找不到 ⇒ null）。</summary>
        public static SameStemCandidate FindByPath(IList<SameStemCandidate> candidates, string path)
        {
            if (candidates == null || string.IsNullOrEmpty(path)) return null;
            for (int i = 0; i < candidates.Count; i++)
            {
                SameStemCandidate c = candidates[i];
                if (c != null && SamePath(c.Path, path)) return c;
            }
            return null;
        }

        /// <summary>按已加载件找候选（先认 `Tag` 实例，再退"路径 + 身份"）。</summary>
        public static SameStemCandidate FindByAssembly(IList<SameStemCandidate> candidates, Assembly asm)
        {
            if (candidates == null || asm == null) return null;
            for (int i = 0; i < candidates.Count; i++)
            {
                SameStemCandidate c = candidates[i];
                if (c != null && ReferenceEquals(c.Tag, asm)) return c;
            }
            string p = PathOf(asm);
            string id = IdentityOf(asm);
            for (int i = 0; i < candidates.Count; i++)
            {
                SameStemCandidate c = candidates[i];
                if (c == null) continue;
                if (SamePath(c.Path, p) && string.Equals(c.Identity, id, StringComparison.OrdinalIgnoreCase))
                    return c;
            }
            return null;
        }

        /// <summary>两条候选是否为"同一份"（同一实例 / 同一路径且同一身份）。
        /// 路径相同还要求身份相同：已加载件无 `Location` 时路径退化成"简单名 + 说明"，
        /// 只比路径会把同一简单名的两个版本误判成同一份（披露于是静默消失）。</summary>
        static bool SameCandidate(SameStemCandidate a, SameStemCandidate b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a.Tag != null && ReferenceEquals(a.Tag, b.Tag)) return true;
            return SamePath(a.Path, b.Path) && SameIdentity(a, b);
        }

        /// <summary>身份串相等 ⇒ 同一份的副本（无歧义）。</summary>
        public static bool SameIdentity(SameStemCandidate a, SameStemCandidate b)
        {
            if (a == null || b == null) return false;
            return string.Equals(a.Identity, b.Identity, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>全路径比较（取不到全路径就逐字比）。</summary>
        public static bool SamePath(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
            catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        }

        static int CompareVersion(Version a, Version b)
        {
            if (a == null && b == null) return 0;
            if (a == null) return -1;
            if (b == null) return 1;
            return a.CompareTo(b);
        }

        // ───────────────────────── 元数据（只读，不加载程序集） ─────────────────────────

        /// <summary>文件的程序集身份（Version + PKT）；读不出（非托管 dll / 文件没了）⇒ <see cref="Unknown"/>。</summary>
        public static string IdentityOfFile(string path)
        {
            AssemblyName an = ReadAssemblyName(path);
            return an == null ? Unknown : IdentityText(an);
        }

        /// <summary>已加载件的身份（Version + PKT）；取不到 ⇒ <see cref="Unknown"/>。</summary>
        public static string IdentityOf(Assembly asm)
        {
            if (asm == null) return Unknown;
            try { return IdentityText(asm.GetName()); }
            catch { return Unknown; }
        }

        /// <summary>已加载件的展示路径：真 <c>Location</c>；无（内存加载件/动态件）⇒ 简单名 + 说明。</summary>
        public static string PathOf(Assembly asm)
        {
            if (asm == null) return Unknown;
            string loc = null;
            try { loc = asm.Location; } catch { loc = null; }
            if (!string.IsNullOrEmpty(loc)) return loc;
            string name = SimpleNameOf(asm);
            return string.IsNullOrEmpty(name) ? Unknown : name + "（无 Location：内存加载件）";
        }

        /// <summary>程序集简单名（取不到 ⇒ null；不抛）。</summary>
        public static string SimpleNameOf(Assembly asm)
        {
            if (asm == null) return null;
            try { return asm.GetName().Name; }
            catch { return null; }
        }

        /// <summary>文件名 stem（`C:\a\B.dll` ⇒ `B`；取不到 ⇒ ""）。</summary>
        public static string StemOf(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            try { return Path.GetFileNameWithoutExtension(path); }
            catch { return ""; }
        }

        static AssemblyName ReadAssemblyName(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            try { return AssemblyName.GetAssemblyName(path); }
            catch { return null; }
        }

        static string IdentityText(AssemblyName an)
        {
            if (an == null) return Unknown;
            string v = an.Version == null ? "<无版本>" : an.Version.ToString();
            return "Version=" + v + ", PKT=" + TokenOf(an);
        }

        static string TokenOf(AssemblyName an)
        {
            byte[] pkt = null;
            try { pkt = an.GetPublicKeyToken(); }
            catch { pkt = null; }
            if (pkt == null || pkt.Length == 0) return "<无标记>";
            var sb = new StringBuilder(pkt.Length * 2);
            for (int i = 0; i < pkt.Length; i++) sb.Append(pkt[i].ToString("x2"));
            return sb.ToString();
        }
    }
}
