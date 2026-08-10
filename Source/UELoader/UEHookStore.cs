using System;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace UELoader
{
    /// <summary>
    /// 通过 UE HTTP 服务创建的 Hook 存储（管理 HookInstance 生命周期与启停状态）。
    /// 自 RimworldMCPDebug/MCPNotifier/HookStorage.cs 精简移植（只保留 MCP 需要的功能）。
    /// </summary>
    public static class UEHookStore
    {
        public sealed class HookEntry
        {
            public string HookId;
            public object Instance;          // UnityExplorer.Hooks.HookInstance
            public string TargetMethod;
            public string DeclaringType;
            public string MethodName;
            public string PatchType;
            public DateTime CreatedAt;
            public string PatchCode;
            public bool Enabled;
        }

        static readonly Dictionary<string, HookEntry> Hooks = new Dictionary<string, HookEntry>(StringComparer.Ordinal);
        static readonly object Lock = new object();
        static int nextId = 1;

        public static string Store(object hookInstance, string targetMethod, string declaringType,
            string methodName, string patchType, string patchCode)
        {
            lock (Lock)
            {
                string hookId = $"hook_{nextId++:X8}";
                Hooks[hookId] = new HookEntry
                {
                    HookId = hookId,
                    Instance = hookInstance,
                    TargetMethod = targetMethod,
                    DeclaringType = declaringType,
                    MethodName = methodName,
                    PatchType = patchType,
                    CreatedAt = DateTime.UtcNow,
                    PatchCode = patchCode,
                    Enabled = true
                };
                Log.Message($"[UEHttp] Hook stored: {hookId} -> {targetMethod}");
                return hookId;
            }
        }

        public static HookEntry Get(string hookId)
        {
            lock (Lock)
                return Hooks.TryGetValue(hookId, out var entry) ? entry : null;
        }

        public static bool Remove(string hookId)
        {
            lock (Lock)
            {
                if (!Hooks.TryGetValue(hookId, out var entry))
                    return false;

                try
                {
                    entry.Instance?.GetType().GetMethod("Unpatch")?.Invoke(entry.Instance, null);
                }
                catch (Exception ex)
                {
                    Log.Warning($"[UEHttp] Error unpatching hook {hookId}: {ex.Message}");
                }

                Hooks.Remove(hookId);
                Log.Message($"[UEHttp] Hook removed: {hookId}");
                return true;
            }
        }

        public static List<HookEntry> GetAll()
        {
            lock (Lock)
                return Hooks.Values.ToList();
        }

        public static int Count
        {
            get { lock (Lock) return Hooks.Count; }
        }
    }
}
