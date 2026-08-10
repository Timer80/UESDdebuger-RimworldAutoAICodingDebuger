using System.Threading;

// 兼容层：.NET Core/.NET 5+ 已移除 System.Runtime.Remoting.Messaging.AsyncResult，
// 且 Delegate.BeginInvoke/EndInvoke 在 .NET Core 上运行时不受支持（调用即抛
// PlatformNotSupportedException）。此 shim 仅用于让 vendored 的
// Mono.Debugger.Soft（VirtualMachineManager.cs 的 EndLaunch/EndListen/EndConnect）
// 通过编译——它只访问 AsyncDelegate 成员。
//
// 注意：后续实现 attach/launch 时不要走 Begin*/End* 这条路径（运行时不可用），
// 应改用 VirtualMachineManager.Connect(Connection, ...) 重载或直接构造 TcpConnection。
namespace System.Runtime.Remoting.Messaging
{
    public class AsyncResult : IAsyncResult
    {
        public Delegate AsyncDelegate { get; }

        public object AsyncState { get; }

        public WaitHandle AsyncWaitHandle { get; }

        public bool CompletedSynchronously { get; }

        public bool IsCompleted { get; }
    }
}

namespace Mono
{
    /// <summary>
    /// 兼容层：mono 构建中共享的 gettext 辅助类（Mono.Locale.GetText）。
    /// vendored StringMirror.cs 调用它做异常消息本地化；无 gettext 环境下原样返回消息文本。
    /// </summary>
    internal static class Locale
    {
        public static string GetText(string msg) => msg;
    }
}
