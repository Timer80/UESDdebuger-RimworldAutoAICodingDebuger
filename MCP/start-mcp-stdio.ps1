# start-mcp-stdio.ps1 —— MCP 以 stdio 传输单实例启动（P1-MCP-4：避免 HTTP 多实例共享全局状态互踩/多实例争启）
$root = Split-Path -Parent $PSScriptRoot
node "$root/index.js" --stdio
