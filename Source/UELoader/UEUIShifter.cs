using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityExplorer.UI;
using UnityEngine;
using Verse;

namespace UELoader
{
    /// <summary>
    /// 进入地图后把 RimWorld 顶部 UI 整体下移一段距离，避开 UnityExplorer 顶部菜单栏
    /// （UE 主导航栏，默认 Main Navbar Anchor = Top，尺寸 1020x35，锚定屏幕顶端正中）：
    ///  1) 开发模式调试工具栏 8 个按钮（Logs / Tweakvalues / Debug actions / View settings /
    ///     Debug logging / Inspector / Dev palette / God mode）——原绘制于 y=3f 的顶部居中即时窗口；
    ///  2) 殖民地 Pawn 头像条（ColonistBar，原 MarginTop=21f）。
    /// 仅当「游戏中 + UE 菜单显示 + UE 导航栏锚定在顶部」时下移，避免把顶栏让给不存在的东西；
    /// UE 隐藏或锚定底部时恢复原位置。全部补丁失败仅告警，不影响游戏。
    ///
    /// 调试工具栏不再是脆弱的 IL 转译（"替换第一个 3f 常量"），改为在 WindowStack.ImmediateWindow
    /// 的 Harmony 前缀里按窗口 ID 精确命中工具栏并给 rect.y 加偏移（P3-CS-2 彻底改造）：不解析 IL，
    /// 只依赖工具栏窗口的稳定 ID（1593759361），对游戏版本更鲁棒、也不误伤其它 3f/窗口。
    /// </summary>
    public static class UEUIShifter
    {
        /// <summary>调试工具栏下移距离：UE 导航栏高 35px + 5px 间隔。</summary>
        const float UiOffset = 40f;

        /// <summary>殖民地头像条在 UiOffset 基础上再额外下移的距离（避让调试工具栏）。</summary>
        const float ColonistBarExtraOffset = 10f;

        /// <summary>开发模式调试工具栏即时窗口的稳定 ID（原版 DevToolStarterOnGUI 中写死）。</summary>
        const int DevToolbarWindowId = 1593759361;

        static bool installed;

        public static void Install()
        {
            if (installed)
                return;
            installed = true;

            try
            {
                var harmony = new Harmony("UESDdebuger.ui.shifter");

                // 调试工具栏：Hook WindowStack.ImmediateWindow，按窗口 ID 精确命中工具栏并偏移 y。
                // 替代原先对 DebugWindowsOpener.DevToolStarterOnGUI 的 IL 转译（第一个 3f 常量，脆弱）。
                MethodInfo immediateWindow = AccessTools.Method(typeof(WindowStack), nameof(WindowStack.ImmediateWindow));
                if (immediateWindow != null)
                {
                    MethodInfo prefix = AccessTools.Method(typeof(UEUIShifter), nameof(ImmediateWindow_Prefix));
                    harmony.Patch(immediateWindow, prefix: new HarmonyMethod(prefix));
                }
                else
                {
                    UEHttpLog.Warning("[UEUIShifter] 未找到 WindowStack.ImmediateWindow，调试工具栏无法下移");
                }

                // 殖民地头像条：在绘制位置计算完成后，把每个头像的绘制点整体下移。
                // 该类有重载（private CalculateDrawLocs(…, float scale, …)），必须用参数类型消歧：
                // public void CalculateDrawLocs(List<Vector2> outDrawLocs, out float scale, int groupsCount)
                MethodInfo colonistLocs = AccessTools.Method(typeof(ColonistBarDrawLocsFinder), "CalculateDrawLocs",
                    new[] { typeof(List<Vector2>), typeof(float).MakeByRefType(), typeof(int) });
                if (colonistLocs != null)
                {
                    MethodInfo postfix = AccessTools.Method(typeof(UEUIShifter), nameof(CalculateDrawLocs_Postfix));
                    harmony.Patch(colonistLocs, postfix: new HarmonyMethod(postfix));
                }
                else
                {
                    UEHttpLog.Warning("[UEUIShifter] 未找到 ColonistBarDrawLocsFinder.CalculateDrawLocs，殖民地头像条无法下移");
                }

                Log.Message("[UELoader] UEUIShifter installed: debug toolbar & colonist bar shift down to avoid the UE navbar.");
            }
            catch (Exception ex)
            {
                UEHttpLog.Warning($"[UEUIShifter] 安装补丁失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>当前应下移的距离：游戏中且 UE 菜单显示且导航栏锚定在顶部时为 UiOffset，否则 0。</summary>
        static float CurrentOffset()
        {
            try
            {
                if (Current.ProgramState != ProgramState.Playing)
                    return 0f;
                if (!UIManager.ShowMenu)
                    return 0f;
                if (UIManager.NavbarAnchor != UIManager.VerticalAnchor.Top)
                    return 0f;
                return UiOffset;
            }
            catch
            {
                return 0f;
            }
        }

        /// <summary>
        /// WindowStack.ImmediateWindow 前缀：对开发模式调试工具栏的即时窗口，将其绘制矩形 y 下移 offset。
        /// 按窗口 ID（1593759361，原版 DevToolStarterOnGUI 写死）精确命中，不解析 IL、不受其它 3f 影响。
        /// </summary>
        static void ImmediateWindow_Prefix(int ID, ref Rect rect)
        {
            try
            {
                if (ID != DevToolbarWindowId)
                    return; // 只命中调试工具栏即时窗口
                float offset = CurrentOffset();
                if (offset > 0f)
                    rect.y += offset;
            }
            catch (Exception ex)
            {
                UEHttpLog.Warning($"[UEUIShifter] ImmediateWindow_Prefix 失败：{ex.Message}");
            }
        }

        /// <summary>绘制点计算完成后把每个头像位置下移（头像条 = 工具栏下移量 + 额外 10px）。</summary>
        static void CalculateDrawLocs_Postfix(List<Vector2> outDrawLocs)
        {
            float offset = CurrentOffset();
            if (offset == 0f || outDrawLocs == null || outDrawLocs.Count == 0)
                return;
            offset += ColonistBarExtraOffset;
            for (int i = 0; i < outDrawLocs.Count; i++)
                outDrawLocs[i] = new Vector2(outDrawLocs[i].x, outDrawLocs[i].y + offset);
        }
    }
}
