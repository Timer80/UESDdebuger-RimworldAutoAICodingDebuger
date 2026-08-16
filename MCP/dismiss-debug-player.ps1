# ============================================================
# dismiss-debug-player.ps1 - 关闭目标进程的 Unity "Debug (Player)" 告知窗
#
# 自 MCP/index.js 的 dismissDebugPlayerWindow 内嵌脚本抽取（P3-MCP-5）：
# 通过 PowerShell Add-Type 内嵌 C# P/Invoke（Node 侧无 user32 绑定）。
# 匹配条件：窗口属于目标进程 + 标题精确等于 "Debug (Player)" + 可见，避免误伤其它窗口。
# 窗口在游戏启动后约 1~3s 才出现，此处轮询最多 TimeoutMs 并反复关闭可能重复弹出的窗口。
#
# 用法：
#   .\dismiss-debug-player.ps1 -TargetPid 1234              # 默认超时 20000ms
#   .\dismiss-debug-player.ps1 -TargetPid 1234 -TimeoutMs 30000
# 成功关闭过至少一次 → 输出 CLOSED；否则 → 输出 NONE
# ============================================================
param(
    [Parameter(Mandatory = $true)][int]$TargetPid,
    [Parameter(Mandatory = $false)][int]$TimeoutMs = 20000
)

$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class WinDismiss {
  public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lp);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
  public static IntPtr FindWindowByPid(uint target) {
    IntPtr hit = IntPtr.Zero;
    EnumWindows((h, l) => {
      uint p; GetWindowThreadProcessId(h, out p);
      if (p == target) {
        var t = new StringBuilder(256); GetWindowText(h, t, t.Capacity);
        if (IsWindowVisible(h) && t.ToString() == "Debug (Player)") { hit = h; return false; }
      }
      return true;
    }, IntPtr.Zero);
    return hit;
  }
}
"@

$deadline = (Get-Date).AddMilliseconds($TimeoutMs)
$closed = $false
while ((Get-Date) -lt $deadline) {
  $h = [WinDismiss]::FindWindowByPid($TargetPid)
  if ($h -ne [IntPtr]::Zero) {
    [void][WinDismiss]::PostMessage($h, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
    $closed = $true
  } elseif ($closed) { break }
  Start-Sleep -Milliseconds 400
}
if ($closed) { Write-Output "CLOSED" } else { Write-Output "NONE" }
