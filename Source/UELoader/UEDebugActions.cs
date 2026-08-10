using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using LudeonTK;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace UELoader
{
    /// <summary>
    /// DebugAction 菜单与 Map 结构浏览/执行（对应 MCP 的 7 个工具：
    /// list_debug_actions / get_debug_action_categories / search_debug_actions /
    /// get_debug_action_detail / execute_debug_action / get_map_structure / search_map_structure）。
    /// 所有方法必须在游戏主线程调用（经 UEHttpHandler.RunOnMain 投递）。
    /// 动作树复用游戏自身机制：LudeonTK.Dialog_Debug.TrySetupNodeGraph()（1.6 属性化 DebugAction 树）。
    /// ToolMap/ToolWorld 自动执行通过 Harmony 前缀补丁临时替换 UI.MouseCell()/GenWorld.MouseTile()。
    /// </summary>
    public static class UEDebugActions
    {
        const int MaxMembersPerDetail = 60;

        static readonly string[] CategoryOrder = { "Grid", "Manager", "Component", "Watcher", "Spawner", "Lister", "Path", "Draw", "Other" };

        // 供 Harmony 前缀补丁使用的共享状态（HTTP 处理在主线程串行执行，无并发）
        static IntVec3 patchedCell;
        static PlanetTile patchedWorldTile;
        static readonly Harmony HarmonyInstance = new Harmony("UESDdebuger.uedebugactions");

        // ---------------------------------------------------------------- 动作树

        // 混合惰性索引：不构建全量树（动态子菜单按 Def/实例展开可致主线程冻结、AppHangB1）。
        // 只按需惰性展开：静态节点（childGetter == null，子项在 DebugTabMenu.InitActions 时已急构建）
        // 在时间预算内逐请求展开并记入索引；动态菜单（childGetter != null，如返回 List<DebugActionNode>
        // 的方法）只记入索引不展开——搜索/分类将其作为叶子结果返回，需要内部时用 list parentPath
        // 按需展开该层（一次性原子调用，childrenSetup 缓存后毫秒级）。
        const long ExpandBudgetMs = 50;

        class IndexEntry
        {
            public DebugActionNode Node;
            public string Label;
            public string Category;
            public bool Dynamic;
        }

        static readonly Stack<DebugActionNode> indexExpandStack = new Stack<DebugActionNode>();
        static readonly List<IndexEntry> indexEntries = new List<IndexEntry>();
        static bool indexSeeded;
        static bool indexStatsLogged;
        static System.Diagnostics.Stopwatch indexBuildSw;

        /// <summary>静态部分是否已全部展开（dynamic 菜单不计入）。</summary>
        static bool IndexComplete => indexSeeded && indexExpandStack.Count == 0;

        /// <summary>时间预算内增量展开（必须在主线程调用）：预算内尽可能多展开，dynamic 菜单只索引不展开。</summary>
        static void ExpandBudgeted(System.Diagnostics.Stopwatch sw)
        {
            if (!indexSeeded)
            {
                indexBuildSw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    DebugActionNode root = GetRoot();
                    if (root != null)
                    {
                        root.TrySetupChildren();
                        if (root.children != null)
                            for (int i = root.children.Count - 1; i >= 0; i--)
                                indexExpandStack.Push(root.children[i]);
                    }
                }
                catch (Exception ex)
                {
                    UEHttpLog.Warning($"[UEHttp] Index seed error: {ex.Message}");
                }
                indexSeeded = true;
            }

            while (indexExpandStack.Count > 0 && sw.ElapsedMilliseconds < ExpandBudgetMs)
            {
                DebugActionNode node = indexExpandStack.Pop();
                try
                {
                    bool dyn = node.childGetter != null;
                    if (!dyn)
                    {
                        // 静态节点：子项已在 InitActions 急构建，TrySetupChildren 幂等（无 getter 时无操作）
                        node.TrySetupChildren();
                        if (node.children != null)
                            for (int i = node.children.Count - 1; i >= 0; i--)
                                indexExpandStack.Push(node.children[i]);
                    }
                    indexEntries.Add(new IndexEntry
                    {
                        Node = node,
                        Label = SafeString(node.LabelNow),
                        Category = node.category,
                        Dynamic = dyn
                    });
                }
                catch (Exception ex)
                {
                    UEHttpLog.Warning($"[UEHttp] Index expand error ({node.Path}): {ex.Message}");
                }
            }

            if (IndexComplete && !indexStatsLogged)
            {
                indexStatsLogged = true;
                int dynCount = 0;
                foreach (IndexEntry e in indexEntries)
                    if (e.Dynamic) dynCount++;
                UEHttpLog.Message($"[UEHttp] DebugAction index complete: entries={indexEntries.Count}, dynamicMenus={dynCount}, elapsedMs={indexBuildSw.ElapsedMilliseconds}");
            }
        }

        static DebugActionNode GetRoot()
        {
            if (Dialog_Debug.rootNode == null)
                Dialog_Debug.TrySetupNodeGraph();
            return Dialog_Debug.rootNode;
        }

        /// <summary>列表：空 parentPath 返回一级菜单；传 parentPath 返回其子项。category 过滤动作分类。</summary>
        public static Dictionary<string, object> ListActions(string category, string parentPath, bool includeHidden)
        {
            DebugActionNode root = GetRoot();
            if (root == null)
                return Err("动作树初始化失败", "TREE_INIT_FAILED");

            DebugActionNode parent = root;
            if (!string.IsNullOrEmpty(parentPath))
            {
                parent = Dialog_Debug.GetNode(parentPath);
                if (parent == null)
                    return Err("路径不存在: " + parentPath, "PATH_NOT_FOUND");
            }

            parent.TrySetupChildren();
            if (parent.children != null && parent.children.Count > 0)
                parent.TrySort();

            var items = new List<Dictionary<string, object>>();
            if (parent.children != null)
            {
                foreach (DebugActionNode child in parent.children)
                {
                    if (!includeHidden && !child.VisibleNow)
                        continue;
                    if (!string.IsNullOrEmpty(category) && !string.Equals(child.category, category, StringComparison.OrdinalIgnoreCase))
                        continue;
                    items.Add(new Dictionary<string, object>
                    {
                        { "path", child.Path },
                        { "label", child.LabelNow },
                        { "hasChildren", child.children != null && child.children.Count > 0 },
                        { "category", child.category },
                        { "actionType", child.actionType.ToString() }
                    });
                }
            }

            return Ok(new Dictionary<string, object>
            {
                { "actions", items },
                { "count", items.Count },
                { "parentPath", parentPath ?? "" }
            });
        }

        /// <summary>分类列表：统计已索引节点中带 category 的数量（按分类聚合）。dynamic 菜单按父节点计入。
        /// 时间预算内逐请求惰性展开；静态部分未展开完时返回 complete=false，由 MCP 侧循环续调直到 complete。</summary>
        public static Dictionary<string, object> GetCategories()
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            ExpandBudgeted(sw);

            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (IndexEntry e in indexEntries)
            {
                if (!string.IsNullOrEmpty(e.Category))
                    counts[e.Category] = counts.TryGetValue(e.Category, out int c) ? c + 1 : 1;
            }

            var list = counts
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => new Dictionary<string, object> { { "name", kv.Key }, { "count", kv.Value } })
                .ToList();

            return Ok(new Dictionary<string, object>
            {
                { "categories", list },
                { "count", list.Count },
                { "complete", IndexComplete },
                { "cursor", null }
            });
        }

        /// <summary>搜索：在已索引节点（路径/标签/分类）中匹配 query。命中动态菜单时直接返回菜单条目
        /// （dynamicMenu=true，提示用 list parentPath 展开），不展开内部。时间预算内逐请求惰性展开；
        /// 未展开完或结果达上限时 complete=false，由 MCP 侧循环续调。</summary>
        public static Dictionary<string, object> Search(string query, string category, int maxResults)
        {
            if (string.IsNullOrEmpty(query))
                return Err("query 必填", "MISSING_PARAMETER");

            var sw = System.Diagnostics.Stopwatch.StartNew();
            ExpandBudgeted(sw);

            string q = query.ToLowerInvariant();
            var results = new List<Dictionary<string, object>>();
            bool truncated = false;
            foreach (IndexEntry e in indexEntries)
            {
                if (results.Count >= maxResults)
                {
                    truncated = true;
                    break;
                }
                if (!string.IsNullOrEmpty(category) && !string.Equals(e.Category, category, StringComparison.OrdinalIgnoreCase))
                    continue;
                string path = e.Node.Path;
                string label = e.Label;
                if ((path != null && path.ToLowerInvariant().Contains(q))
                    || (label != null && label.ToLowerInvariant().Contains(q))
                    || (e.Category != null && e.Category.ToLowerInvariant().Contains(q)))
                {
                    var item = new Dictionary<string, object>
                    {
                        { "path", path },
                        { "label", label },
                        { "category", e.Category },
                        { "actionType", e.Node.actionType.ToString() }
                    };
                    if (e.Dynamic)
                        item["dynamicMenu"] = true;
                    results.Add(item);
                }
            }

            return Ok(new Dictionary<string, object>
            {
                { "results", results },
                { "count", results.Count },
                { "query", query },
                { "complete", IndexComplete && !truncated },
                { "truncated", truncated },
                { "cursor", null }
            });
        }

        /// <summary>详情：完整元数据（动作类型、分类、可见性、游戏状态要求、DLC 要求等）。</summary>
        public static Dictionary<string, object> GetDetail(string path)
        {
            if (string.IsNullOrEmpty(path))
                return Err("path 必填", "MISSING_PARAMETER");

            DebugActionNode node = Dialog_Debug.GetNode(path);
            if (node == null)
                return Err("路径不存在: " + path, "PATH_NOT_FOUND");

            var detail = new Dictionary<string, object>
            {
                { "path", node.Path },
                { "label", SafeString(node.LabelNow) },
                { "category", node.category },
                { "actionType", node.actionType.ToString() },
                { "visible", node.VisibleNow },
                { "active", node.ActiveNow },
                { "on", node.On },
                { "hasChildren", node.children != null && node.children.Count > 0 },
                { "displayPriority", node.displayPriority }
            };

            DebugActionAttribute attr = node.sourceAttribute;
            if (attr != null)
            {
                detail["allowedGameStates"] = attr.allowedGameStates.ToString();
                detail["requiresRoyalty"] = attr.requiresRoyalty;
                detail["requiresIdeology"] = attr.requiresIdeology;
                detail["requiresBiotech"] = attr.requiresBiotech;
                detail["requiresAnomaly"] = attr.requiresAnomaly;
                detail["hideInSubMenu"] = attr.hideInSubMenu;
            }

            return Ok(new Dictionary<string, object> { { "action", detail } });
        }

        /// <summary>执行 DebugAction。Action 直接执行；ToolMap 按 mapX/mapZ（或 pawnId 所在格）自动执行；
        /// ToolMapForPawns 按 pawnId 定位 Pawn 执行；ToolWorld 按 worldTile 执行。</summary>
        public static Dictionary<string, object> Execute(string path, int? mapX, int? mapZ, int? worldTile, int? pawnId)
        {
            if (string.IsNullOrEmpty(path))
                return Err("path 必填", "MISSING_PARAMETER");

            DebugActionNode node = Dialog_Debug.GetNode(path);
            if (node == null)
                return Err("路径不存在: " + path, "PATH_NOT_FOUND");

            node.TrySetupChildren();
            if (node.children != null && node.children.Count > 0)
                return Err("该路径是菜单节点而非可执行动作: " + path, "NOT_ACTION");

            switch (node.actionType)
            {
                case DebugActionType.Action:
                    if (node.action == null)
                        return Err("该动作无执行体（可能为占位节点）: " + path, "NO_ACTION_BODY");
                    return ExecuteGuarded(path, node.action);

                case DebugActionType.ToolMap:
                    IntVec3 cell;
                    if (mapX.HasValue && mapZ.HasValue)
                    {
                        cell = new IntVec3(mapX.Value, 0, mapZ.Value);
                    }
                    else if (pawnId.HasValue)
                    {
                        Pawn target = FindPawn(pawnId.Value);
                        if (target == null)
                            return Err("Pawn 不存在: " + pawnId, "PAWN_NOT_FOUND");
                        cell = target.Position;
                    }
                    else
                    {
                        return Err("ToolMap 需要 mapX/mapZ 或 pawnId", "MISSING_COORDINATES");
                    }
                    if (node.action == null)
                        return Err("该工具无执行体: " + path, "NO_ACTION_BODY");
                    return ExecuteWithPatchedMouse(path, cell, node.action);

                case DebugActionType.ToolMapForPawns:
                    if (!pawnId.HasValue)
                        return Err("ToolMapForPawns 需要 pawnId", "MISSING_PAWN");
                    if (node.pawnAction == null)
                        return Err("该工具无 Pawn 执行体: " + path, "NO_ACTION_BODY");
                    Pawn pawn = FindPawn(pawnId.Value);
                    if (pawn == null)
                        return Err("Pawn 不存在: " + pawnId, "PAWN_NOT_FOUND");
                    return ExecuteGuarded(path, () => node.pawnAction(pawn));

                case DebugActionType.ToolWorld:
                    if (!worldTile.HasValue)
                        return Err("ToolWorld 需要 worldTile", "MISSING_WORLD_TILE");
                    if (node.action == null)
                        return Err("该工具无执行体: " + path, "NO_ACTION_BODY");
                    return ExecuteWithPatchedWorldTile(path, worldTile.Value, node.action);

                default:
                    return Err("未知动作类型: " + node.actionType, "UNKNOWN_ACTION_TYPE");
            }
        }

        // ---------------------------------------------------------------- 执行辅助

        static Dictionary<string, object> ExecuteGuarded(string path, Action action)
        {
            try
            {
                action();
                return Ok(new Dictionary<string, object> { { "message", "已执行: " + path } });
            }
            catch (Exception ex)
            {
                UEHttpLog.Error($"[UEHttp] DebugAction execute failed ({path}): {ex}");
                return Err("执行失败: " + ex.Message, "EXECUTE_ERROR");
            }
        }

        /// <summary>ToolMap 自动执行：临时补丁 UI.MouseCell() 返回目标格，再调用动作体。</summary>
        static Dictionary<string, object> ExecuteWithPatchedMouse(string path, IntVec3 cell, Action action)
        {
            patchedCell = cell;
            MethodInfo original = AccessTools.Method(typeof(UI), "MouseCell");
            MethodInfo prefix = AccessTools.Method(typeof(UEDebugActions), nameof(MouseCellPrefix));
            HarmonyInstance.Patch(original, prefix: new HarmonyMethod(prefix));
            try
            {
                return ExecuteGuarded(path, action);
            }
            finally
            {
                HarmonyInstance.Unpatch(original, HarmonyPatchType.Prefix, HarmonyInstance.Id);
            }
        }

        static bool MouseCellPrefix(ref IntVec3 __result)
        {
            __result = patchedCell;
            return false;
        }

        /// <summary>ToolWorld 自动执行：临时补丁 GenWorld.MouseTile() 返回目标世界瓦片，再调用动作体。</summary>
        static Dictionary<string, object> ExecuteWithPatchedWorldTile(string path, int tileId, Action action)
        {
            patchedWorldTile = new PlanetTile(tileId);
            MethodInfo original = AccessTools.Method(typeof(GenWorld), "MouseTile", new[] { typeof(bool) });
            MethodInfo prefix = AccessTools.Method(typeof(UEDebugActions), nameof(WorldMouseTilePrefix));
            HarmonyInstance.Patch(original, prefix: new HarmonyMethod(prefix));
            try
            {
                return ExecuteGuarded(path, action);
            }
            finally
            {
                HarmonyInstance.Unpatch(original, HarmonyPatchType.Prefix, HarmonyInstance.Id);
            }
        }

        static bool WorldMouseTilePrefix(ref PlanetTile __result)
        {
            __result = patchedWorldTile;
            return false;
        }

        static Pawn FindPawn(int thingId)
        {
            Map map = Find.CurrentMap;
            if (map == null)
                return null;

            try
            {
                foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
                    if (p.thingIDNumber == thingId)
                        return p;
                foreach (Pawn p in map.mapPawns.AllPawns)
                    if (p.thingIDNumber == thingId)
                        return p;
            }
            catch { }

            try
            {
                foreach (Thing t in map.spawnedThings)
                    if (t is Pawn p2 && p2.thingIDNumber == thingId)
                        return p2;
            }
            catch { }
            return null;
        }

        // ---------------------------------------------------------------- Map 结构

        static List<MemberInfo> mapMembersCache;

        static List<MemberInfo> GetMapMembers()
        {
            if (mapMembersCache == null)
            {
                var list = new List<MemberInfo>();
                try
                {
                    foreach (FieldInfo f in typeof(Map).GetFields(BindingFlags.Public | BindingFlags.Instance))
                        list.Add(f);
                    foreach (PropertyInfo p in typeof(Map).GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (p.GetIndexParameters().Length == 0)
                            list.Add(p);
                    }
                }
                catch (Exception ex)
                {
                    UEHttpLog.Warning($"[UEHttp] GetMapMembers failed: {ex.Message}");
                }
                mapMembersCache = list;
            }
            return mapMembersCache;
        }

        static Type TypeOf(MemberInfo m)
        {
            if (m is FieldInfo f) return f.FieldType;
            if (m is PropertyInfo p) return p.PropertyType;
            return null;
        }

        static object GetMemberValue(MemberInfo m, object instance)
        {
            try
            {
                if (m is FieldInfo f) return f.GetValue(instance);
                if (m is PropertyInfo p && p.CanRead) return p.GetValue(instance, null);
            }
            catch { }
            return null;
        }

        static string CategoryOf(string name, Type type)
        {
            if (name.EndsWith("Grid", StringComparison.Ordinal)) return "Grid";
            if (name.IndexOf("Manager", StringComparison.Ordinal) >= 0 || name.EndsWith("Reservation", StringComparison.Ordinal)) return "Manager";
            if (name.EndsWith("Component", StringComparison.Ordinal) || (type != null && typeof(MapComponent).IsAssignableFrom(type))) return "Component";
            if (name.EndsWith("Watcher", StringComparison.Ordinal) || name.EndsWith("Decider", StringComparison.Ordinal) || name.EndsWith("Tracker", StringComparison.Ordinal)) return "Watcher";
            if (name.EndsWith("Spawner", StringComparison.Ordinal)) return "Spawner";
            if (name.StartsWith("lister", StringComparison.Ordinal) || name.EndsWith("Lister", StringComparison.Ordinal)) return "Lister";
            if (name.IndexOf("Path", StringComparison.Ordinal) >= 0 || name.EndsWith("Reachability", StringComparison.Ordinal)) return "Path";
            if (name.StartsWith("draw", StringComparison.Ordinal) || name.EndsWith("Drawer", StringComparison.Ordinal) || name.EndsWith("Draw", StringComparison.Ordinal) || name.EndsWith("Renderer", StringComparison.Ordinal)) return "Draw";
            return "Other";
        }

        /// <summary>Map 结构浏览：空路径→根分类；单段路径→分类下成员列表；完整路径→成员详情。</summary>
        public static Dictionary<string, object> GetMapStructure(string path)
        {
            Map map = Find.CurrentMap;
            if (map == null)
                return Err("当前未进入地图", "MAP_NOT_LOADED");

            List<MemberInfo> members = GetMapMembers();

            if (string.IsNullOrEmpty(path))
            {
                var cats = new List<Dictionary<string, object>>();
                foreach (string cat in CategoryOrder)
                {
                    int count = members.Count(m => CategoryOf(m.Name, TypeOf(m)) == cat);
                    cats.Add(new Dictionary<string, object>
                    {
                        { "path", cat },
                        { "label", cat },
                        { "category", cat },
                        { "count", count }
                    });
                }
                return Ok(new Dictionary<string, object> { { "nodes", cats }, { "count", cats.Count }, { "isRoot", true } });
            }

            string[] parts = path.Split(new[] { '\\' }, StringSplitOptions.None);

            if (parts.Length == 1)
            {
                string cat = parts[0];
                var items = new List<Dictionary<string, object>>();
                foreach (MemberInfo m in members)
                {
                    if (CategoryOf(m.Name, TypeOf(m)) != cat)
                        continue;
                    items.Add(new Dictionary<string, object>
                    {
                        { "path", cat + "\\" + m.Name },
                        { "label", m.Name },
                        { "category", cat },
                        { "type", TypeOf(m)?.Name ?? "?" },
                        { "memberType", m.MemberType.ToString() }
                    });
                }
                return Ok(new Dictionary<string, object> { { "nodes", items }, { "count", items.Count }, { "category", cat } });
            }

            // 完整路径：分类\成员（可继续深入成员的字段/属性）
            MemberInfo member = members.FirstOrDefault(m => m.Name == parts[1]);
            if (member == null)
                return Err("成员不存在: " + parts[1], "MEMBER_NOT_FOUND");

            object current = GetMemberValue(member, map);
            string curPath = parts[0] + "\\" + member.Name;
            for (int i = 2; i < parts.Length && current != null; i++)
            {
                MemberInfo sub = FindMember(current.GetType(), parts[i]);
                if (sub == null)
                    return Err("子成员不存在: " + string.Join("\\", parts, 0, i + 1), "MEMBER_NOT_FOUND");
                current = GetMemberValue(sub, current);
                curPath += "\\" + parts[i];
            }

            if (current == null)
                return Ok(new Dictionary<string, object> { { "path", curPath }, { "value", null }, { "type", "null" } });

            Type ct = current.GetType();
            var detail = new Dictionary<string, object>
            {
                { "path", curPath },
                { "type", ct.FullName },
                { "value", SafeToString(current) }
            };

            var fields = new List<Dictionary<string, object>>();
            var props = new List<Dictionary<string, object>>();
            var methods = new List<Dictionary<string, object>>();
            try
            {
                foreach (FieldInfo f in ct.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic))
                {
                    if (fields.Count >= MaxMembersPerDetail) break;
                    fields.Add(new Dictionary<string, object> { { "name", f.Name }, { "type", f.FieldType.Name } });
                }
                foreach (PropertyInfo p in ct.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic))
                {
                    if (props.Count >= MaxMembersPerDetail) break;
                    props.Add(new Dictionary<string, object> { { "name", p.Name }, { "type", p.PropertyType.Name } });
                }
                foreach (MethodInfo m2 in ct.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (methods.Count >= MaxMembersPerDetail) break;
                    if (m2.IsSpecialName) continue;
                    methods.Add(new Dictionary<string, object> { { "name", m2.Name }, { "return", m2.ReturnType.Name } });
                }
            }
            catch (Exception ex)
            {
                UEHttpLog.Warning($"[UEHttp] GetMapStructure detail failed: {ex.Message}");
            }
            detail["fields"] = fields;
            detail["properties"] = props;
            detail["methods"] = methods;

            return Ok(new Dictionary<string, object> { { "node", detail } });
        }

        static MemberInfo FindMember(Type type, string name)
        {
            try
            {
                FieldInfo f = type.GetField(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
                if (f != null) return f;
                PropertyInfo p = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
                if (p != null && p.GetIndexParameters().Length == 0) return p;
            }
            catch { }
            return null;
        }

        /// <summary>Map 结构搜索：在成员名、类型名、分类中匹配。</summary>
        public static Dictionary<string, object> SearchMapStructure(string query, string category)
        {
            Map map = Find.CurrentMap;
            if (map == null)
                return Err("当前未进入地图", "MAP_NOT_LOADED");
            if (string.IsNullOrEmpty(query))
                return Err("query 必填", "MISSING_PARAMETER");

            string q = query.ToLowerInvariant();
            var results = new List<Dictionary<string, object>>();
            foreach (MemberInfo m in GetMapMembers())
            {
                string cat = CategoryOf(m.Name, TypeOf(m));
                if (!string.IsNullOrEmpty(category) && !string.Equals(cat, category, StringComparison.OrdinalIgnoreCase))
                    continue;
                Type t = TypeOf(m);
                bool match = m.Name.ToLowerInvariant().Contains(q)
                             || (t != null && t.Name.ToLowerInvariant().Contains(q))
                             || cat.ToLowerInvariant().Contains(q);
                if (match)
                {
                    results.Add(new Dictionary<string, object>
                    {
                        { "path", cat + "\\" + m.Name },
                        { "label", m.Name },
                        { "category", cat },
                        { "type", t?.Name ?? "?" }
                    });
                }
            }

            return Ok(new Dictionary<string, object> { { "results", results }, { "count", results.Count }, { "query", query } });
        }

        // ---------------------------------------------------------------- 通用辅助

        static string SafeString(string s)
        {
            if (s == null) return null;
            return s.Length > 300 ? s.Substring(0, 300) + "..." : s;
        }

        static string SafeToString(object o)
        {
            if (o == null) return null;
            try
            {
                string s = o.ToString();
                return SafeString(s);
            }
            catch { return null; }
        }

        static Dictionary<string, object> Ok(Dictionary<string, object> data)
        {
            return new Dictionary<string, object> { { "success", true }, { "data", data } };
        }

        static Dictionary<string, object> Err(string error, string errorCode)
        {
            return new Dictionary<string, object>
            {
                { "success", false },
                { "error", error },
                { "errorCode", errorCode }
            };
        }
    }
}
