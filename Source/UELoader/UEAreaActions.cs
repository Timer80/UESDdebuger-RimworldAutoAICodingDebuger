using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace UELoader
{
    /// <summary>
    /// 手搓的 RimWorld Area 删除/清空实现（替代 RimBridgeServer 的有缺陷 GABP 工具）。
    ///
    /// 背景：RimBridgeServer 的 rimworld/delete_area / rimworld/clear_area 在 TCP 读线程上同步执行
    /// （ToolRegistry.CreateHandler 里 method.Invoke），却直接访问 Unity/RimWorld 主线程状态
    /// （Area.Delete/Clear、Designator_AreaAllowed.SelectedArea、RimWorldState.ReadStatus 读
    /// Find.CurrentMap/TickManager/ScreenFader 等），未走 RimBridgeMainThread.Invoke 派发 → 对真实
    /// Area 执行变更时读线程永久阻塞，该 GABP 连接读循环停滞、后续所有请求超时（详见
    /// MCP/docs/mcp-rimworld-debug-total-checklist.md §12）。
    ///
    /// 本实现完全由 UELoader 自研：所有 RimWorld 状态访问与变更都经 UEMainThreadDispatcher
    /// （自研主线程调度器，独立于 RimBridge）在主线程执行，天然线程安全，绝不死锁。
    /// 返回结构尽量与 RimBridge 的 DeleteAreaResponse/ClearAreaResponse 对齐
    /// （success / deletedArea|area / selectedAllowedArea / actualCellCount|previousCellCount / state）。
    ///
    /// 由 UEHttpServer 端点 /area/delete、/area/clear 暴露，MCP 侧把 rimworld.delete_area /
    /// rimworld.clear_area 拦截改走本实现。
    /// </summary>
    public static class UEAreaActions
    {
        /// <summary>
        /// 删除区域（主线程执行）。返回与 RimBridge DeleteAreaResponse 兼容的形状。
        /// </summary>
        public static Dictionary<string, object> DeleteArea(string areaId)
        {
            return RunOnMain(() =>
            {
                Map map = Find.CurrentMap;
                if (map == null || map.areaManager == null)
                    return Failure("当前地图不可用（未进入地图或无区域管理器）。");

                Area area = ResolveArea(map, areaId);
                if (area == null)
                    return Failure($"Could not find '{areaId}'.");

                if (!area.Mutable)
                    return Failure($"Area '{areaId}' is not mutable and cannot be deleted through this helper.");

                object deletedArea = DescribeArea(area);
                bool wasSelected = Designator_AreaAllowed.SelectedArea == area;
                area.Delete();
                if (wasSelected)
                {
                    try { Designator_AreaAllowed.ClearSelectedArea(); } catch { }
                }

                return new Dictionary<string, object>
                {
                    { "success", true },
                    { "deletedArea", deletedArea },
                    { "selectedAllowedArea", DescribeAreaNullable(Designator_AreaAllowed.SelectedArea) },
                    { "state", StateSnapshot() }
                };
            });
        }

        /// <summary>
        /// 清空区域（主线程执行）。返回与 RimBridge ClearAreaResponse 兼容的形状。
        /// </summary>
        public static Dictionary<string, object> ClearArea(string areaId)
        {
            return RunOnMain(() =>
            {
                Map map = Find.CurrentMap;
                if (map == null || map.areaManager == null)
                    return Failure("当前地图不可用（未进入地图或无区域管理器）。");

                Area area = ResolveArea(map, areaId);
                if (area == null)
                    return Failure($"Could not find '{areaId}'.");

                if (!area.Mutable)
                    return Failure($"Area '{areaId}' is not mutable and cannot be cleared through this helper.");

                int trueCount = area.TrueCount;
                area.Clear();

                return new Dictionary<string, object>
                {
                    { "success", true },
                    { "previousCellCount", trueCount },
                    { "area", DescribeArea(area) },
                    { "selectedAllowedArea", DescribeAreaNullable(Designator_AreaAllowed.SelectedArea) },
                    { "state", StateSnapshot() }
                };
            });
        }

        // ---------------------------------------------------------------- 内部

        static Area ResolveArea(Map map, string areaId)
        {
            if (string.IsNullOrEmpty(areaId) || map == null || map.areaManager == null)
                return null;

            // RimBridge TryResolveByIdentity：按 GetUniqueLoadID() 精确、RenamableLabel / Label 候选
            foreach (Area a in map.areaManager.AllAreas)
            {
                try
                {
                    if (a != null && (a.GetUniqueLoadID() == areaId
                        || a.RenamableLabel == areaId
                        || a.Label == areaId))
                        return a;
                }
                catch { }
            }
            return null;
        }

        static object DescribeArea(Area area)
        {
            if (area == null) return null;
            var d = new Dictionary<string, object>
            {
                { "id", area.GetUniqueLoadID() },
                { "label", DescribeAreaLabel(area) },
                { "baseLabel", DescribeAnySafe(() => area.BaseLabel) },
                { "renamableLabel", DescribeAnySafe(() => area.RenamableLabel) },
                { "className", area.GetType().FullName },
                { "cellCount", area.TrueCount },
                { "assignableAsAllowed", DescribeBoolSafe(() => area.AssignableAsAllowed()) }
            };
            AppendListPriority(area, d);
            return d;
        }

        static object DescribeAreaNullable(Area area)
        {
            return area == null ? null : DescribeArea(area);
        }

        static string DescribeAreaLabel(Area area)
        {
            // RimBridge 用的是动态 label（含已算的 label）。直接取 Label。
            return DescribeAnySafe(() => area.Label);
        }

        static string DescribeAnySafe(Func<string> fn)
        {
            try { return fn(); } catch { return null; }
        }

        static object DescribeBoolSafe(Func<bool> fn)
        {
            try { return fn(); } catch { return false; }
        }

        static void AppendListPriority(Area area, Dictionary<string, object> d)
        {
            try
            {
                var pi = area.GetType().GetProperty("ListPriority");
                if (pi != null)
                    d["listPriority"] = Convert.ToInt32(pi.GetValue(area));
            }
            catch { }
        }

        /// <summary>与 RimWorldState.ToolStateSnapshot 兼容的轻量状态快照（只读、捕获异常）。</summary>
        static object StateSnapshot()
        {
            var s = new Dictionary<string, object>();
            try { s["programState"] = Current.ProgramState.ToString(); } catch { s["programState"] = "Unknown"; }
            try { s["hasCurrentGame"] = Current.Game != null; } catch { s["hasCurrentGame"] = false; }
            try { s["currentMapId"] = Find.CurrentMap != null ? "Map_" + Find.CurrentMap.Index : null; } catch { s["currentMapId"] = null; }
            try { s["gameDataReady"] = !LongEventHandler.AnyEventNowOrWaiting; } catch { s["gameDataReady"] = false; }
            try { s["mapDataReady"] = Find.CurrentMap != null && Find.CurrentMap.info != null; } catch { s["mapDataReady"] = false; }
            try { s["playable"] = Current.Game != null && Find.CurrentMap != null; } catch { s["playable"] = false; }
            try
            {
                var tm = Current.Game != null ? Current.Game.tickManager : null;
                s["paused"] = tm != null && tm.Paused;
                s["timeSpeed"] = tm != null ? tm.CurTimeSpeed.ToString() : null;
            }
            catch { }
            return s;
        }

        static Dictionary<string, object> Failure(string message)
        {
            return new Dictionary<string, object>
            {
                { "success", false },
                { "message", message }
            };
        }

        /// <summary>在主线程执行并返回结果（复用 UEHttpHandler.RunOnMain 的语义，但独立实现，避免依赖）。</summary>
        static Dictionary<string, object> RunOnMain(Func<Dictionary<string, object>> fn)
        {
            return UEHttpHandler.RunOnMain(fn);
        }
    }
}
