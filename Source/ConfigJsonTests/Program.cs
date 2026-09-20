using System;
using System.Collections.Generic;
using UELoader;

namespace ConfigJsonTests
{
    /// <summary>
    /// config.json 嵌套对象往返测试（规格 §5 / §8 第 17-18 条）。
    ///
    /// 复现原 bug：AutoDeploy.WriteConfig 的手写拼接只认标量，嵌套 Dictionary 会落到
    /// ToString() 分支，被写成 "System.Collections.Generic.Dictionary`2[System.String,System.Object]"，
    /// 于是 MCP 侧 config.rimBridge 恒为字符串，rimBridge.requestTimeoutMs / longTask.* 全部读不到。
    ///
    /// 本测试断言「用 UELightJson.Serialize 写出的嵌套对象能被原样读回」——
    /// 预期在修改 AutoDeploy **之前就已通过**，这恰好证明 bug 不在序列化器，
    /// 而在 AutoDeploy 绕过了它。
    /// </summary>
    internal static class Program
    {
        static int failures;

        static void Check(bool cond, string label)
        {
            Console.WriteLine((cond ? "[PASS] " : "[FAIL] ") + label);
            if (!cond) failures++;
        }

        static int Main()
        {
            // 1) 嵌套对象往返：这是原 bug 的核心场景
            var cfg = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "gamePath", "C:/RimWorld/RimWorldWin64.exe" },
                { "startTimeout", 180000L },
                {
                    "timings", new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        { "rimapiProbeTimeoutMs", 5000L },
                        { "ueProbeTimeoutMs", 3000L },
                    }
                },
                {
                    "rimBridge", new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        { "requestTimeoutMs", 30000L },
                        { "timeoutRecoverThreshold", 3L },
                        {
                            "longTask", new Dictionary<string, object>(StringComparer.Ordinal)
                            {
                                { "maxSingleCallTimeoutMs", 1800000L },
                                { "playForOverheadMs", 10000L },
                                { "taskSampleIntervalMs", 2000L },
                                { "taskSnapshotIntervalMs", 30000L },
                                { "taskBurstSampleIntervalMs", 1000L },
                                { "taskBurstSnapshotIntervalMs", 10000L },
                                { "taskSampleKeep", 200L },
                                { "taskSnapshotKeep", 20L },
                                { "recoverRestorePause", false },
                                { "toolTimeouts", new Dictionary<string, object>(StringComparer.Ordinal) },
                            }
                        },
                    }
                },
            };

            string json = UELightJson.Serialize(cfg);
            Check(json.Contains("\"rimBridge\""), "序列化输出含 rimBridge 键");
            Check(!json.Contains("System.Collections.Generic.Dictionary"), "序列化输出不含被拼平的类型名");

            var back = UELightJson.ParseObject(json);
            Check(back != null, "输出可被 ParseObject 读回");
            Check(back["gamePath"] is string gp && gp == "C:/RimWorld/RimWorldWin64.exe", "标量字段往返一致");

            Check(back["rimBridge"] is Dictionary<string, object>, "rimBridge 读回仍是对象而非字符串");

            var rb = back["rimBridge"] as Dictionary<string, object>;
            Check(rb != null && rb.TryGetValue("requestTimeoutMs", out var rt) && Convert.ToInt64(rt) == 30000L,
                "rimBridge.requestTimeoutMs 数值可读");
            object ltObj = null;
            bool hasLongTask = rb != null && rb.TryGetValue("longTask", out ltObj);
            Check(hasLongTask && ltObj is Dictionary<string, object>,
                "rimBridge.longTask 二级嵌套仍是对象");
            var lt = ltObj as Dictionary<string, object>;
            Check(lt != null && lt.TryGetValue("maxSingleCallTimeoutMs", out var mx) && Convert.ToInt64(mx) == 1800000L,
                "longTask.maxSingleCallTimeoutMs 数值可读");
            Check(lt != null && lt.TryGetValue("toolTimeouts", out var tt) && tt is Dictionary<string, object>,
                "longTask.toolTimeouts 空对象仍是对象");
            Check(lt != null && lt.TryGetValue("recoverRestorePause", out var rr) && rr is bool b && b == false,
                "longTask.recoverRestorePause 布尔可读");

            var timings = back["timings"] as Dictionary<string, object>;
            Check(timings != null && timings.TryGetValue("ueProbeTimeoutMs", out var up) && Convert.ToInt64(up) == 3000L,
                "timings 子对象数值可读");

            // 2) 数组往返（toolTimeouts 未来可能换成数组形态；UELightJson 已声明支持）
            var arrCfg = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                { "list", new List<object> { 1L, "two", true } },
            };
            var arrBack = UELightJson.ParseObject(UELightJson.Serialize(arrCfg));
            Check(arrBack["list"] is List<object> l && l.Count == 3 && Convert.ToInt64(l[0]) == 1L,
                "数组往返一致");

            // 3) 反例守卫：证明「手写拼接」那种写法的确会坏（钉住 bug 的形态，防回归时被误判为测试太松）
            string broken = "\"rimBridge\": \"" + UELightJson.Escape(cfg["rimBridge"].ToString()) + "\"";
            var brokenBack = UELightJson.ParseObject("{" + broken + "}");
            Check(brokenBack != null && brokenBack["rimBridge"] is string,
                "反例：把对象按 ToString 写成字符串时，读回确实是 string（即原 bug 的形态）");

            Console.WriteLine();
            Console.WriteLine(failures == 0 ? "ALL PASS" : failures + " FAILED");
            return failures == 0 ? 0 : 1;
        }
    }
}
