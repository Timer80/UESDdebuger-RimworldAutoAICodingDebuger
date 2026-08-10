@echo off
rem ============================================================
rem start-aggregator.bat - Start RimWorld MCP Aggregator (SSE mode)
rem Double-click to run, or run from cmd.
rem
rem Delegates to start-aggregator.ps1 which opens a new PowerShell
rem window running `node index.js` (logs to logs/mcp-*.log,
rem plus a window-side Tee copy mcp-window-*.log).
rem Aggregator listens on http://127.0.0.1:3100 (SSE: /sse, Health: /health).
rem The upstream server on port 3000 must be started first.
rem ============================================================
cd /d "%~dp0"

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0start-aggregator.ps1"

echo.
echo Done. The aggregator window has been opened separately.
pause
