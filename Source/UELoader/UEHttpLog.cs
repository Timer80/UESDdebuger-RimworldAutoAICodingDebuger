using UnityEngine;
using Verse;

namespace UELoader
{
    /// <summary>
    /// 线程安全的 [UEHttp] 日志桥接。
    /// UEHttpServer 的请求/结果日志在 HTTP 线程产生，而 Verse.Log 的消息队列会被游戏内
    /// EditWindow_Log（日志窗口）在主线程枚举；若从非主线程直接写 Verse.Log，会导致
    /// "Collection was modified; enumeration operation may not execute" 竞态异常。
    ///
    /// 因此：
    ///  - 主线程调度器就绪时（进入世界后）：投递到主线程执行 Verse.Log.*（与 UI 枚举同线程，无竞态）
    ///  - 调度器未就绪（Mod 构造/主菜单）：回落 UnityEngine.Debug.*（Unity 内部线程安全，
    ///    同样写入 Player.log；不进 RimWorld 日志队列，故无枚举冲突）
    /// </summary>
    public static class UEHttpLog
    {
        /// <summary>交流报告/热路径日志开关（P2-MD-1.1/P3-CS-3），默认关。Warning/Error 不受影响。</summary>
        public static bool DiagnosticEnabled { get; set; } = false;

        public static void Message(string msg)
        {
            if (!DiagnosticEnabled) return;   // P3-CS-3：Message 级交流日志默认关
            Write("Message", msg);
        }
        public static void Warning(string msg) => Write("Warning", msg);
        public static void Error(string msg) => Write("Error", msg);

        static void Write(string level, string msg)
        {
            try
            {
                if (UEMainThreadDispatcher.IsReady)
                {
                    // 投递主线程执行 Verse.Log（与 UI 日志窗口枚举同线程）
                    UEMainThreadDispatcher.Enqueue(() => VerseLog(level, msg));
                }
                else
                {
                    // 未就绪：Unity Debug 线程安全（输出 Player.log，不进 RimWorld 队列）
                    UnityLog(level, msg);
                }
            }
            catch
            {
                // 日志桥接本身失败不影响业务
            }
        }

        static void VerseLog(string level, string msg)
        {
            switch (level)
            {
                case "Error": Log.Error(msg); break;
                case "Warning": Log.Warning(msg); break;
                default: Log.Message(msg); break;
            }
        }

        static void UnityLog(string level, string msg)
        {
            switch (level)
            {
                case "Error": Debug.LogError(msg); break;
                case "Warning": Debug.LogWarning(msg); break;
                default: Debug.Log(msg); break;
            }
        }
    }
}
