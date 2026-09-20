using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Verse;

namespace UELoader
{
    /// <summary>
    /// x64 detour 跳板：把旧方法入口改写为无条件跳转到新方法体（Reloader 同款，
    /// 见 pardeike/Reloader 的 Memory.cs）。仅支持 x64 Windows（RimWorldWin64）。
    /// 跳板字节：48 B8 <8B 目标地址> FF E0（mov rax, imm64; jmp rax）。
    /// 只允许在"未被 Harmony patch"的方法上使用（patch 会改写入口，叠加会破坏跳转链，
    /// 由调用方 HotReloadManager 保证）。
    /// </summary>
    public static class HotReloadDetour
    {
        [Flags]
        enum Protection : uint
        {
            PAGE_EXECUTE_READWRITE = 0x40
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool VirtualProtect(IntPtr lpAddress, UIntPtr dwSize,
            Protection flNewProtect, out Protection lpflOldProtect);

        /// <summary>对旧方法打跳板跳转到新方法。成功返回 null，失败返回错误信息。</summary>
        public static string Detour(MethodBase original, MethodBase replacement)
        {
            try
            {
                if (IntPtr.Size != 8)
                    return "仅支持 x64（RimWorldWin64）";
                long from = GetMethodStart(original);
                long to = GetMethodStart(replacement);
                if (from == 0 || to == 0)
                    return "无法获取方法入口（PrepareMethod/GetFunctionPointer 失败）";
                return WriteJump(from, to);
            }
            catch (Exception ex)
            {
                return ex.GetType().Name + ": " + ex.Message;
            }
        }

        static long GetMethodStart(MethodBase method)
        {
            RuntimeMethodHandle handle = method.MethodHandle;
            try { RuntimeHelpers.PrepareMethod(handle); } catch { }
            try { return handle.GetFunctionPointer().ToInt64(); }
            catch { return 0; }
        }

        static string WriteJump(long memory, long destination)
        {
            if (!VirtualProtect(new IntPtr(memory), new UIntPtr(12),
                Protection.PAGE_EXECUTE_READWRITE, out _))
                return "VirtualProtect 失败: " + Marshal.GetLastWin32Error();
            unsafe
            {
                byte* p = (byte*)memory;
                // 48 B8 <imm64> FF E0
                p[0] = 0x48; p[1] = 0xB8;
                *(long*)(p + 2) = destination;
                p[10] = 0xFF; p[11] = 0xE0;
            }
            return null;
        }
    }
}
