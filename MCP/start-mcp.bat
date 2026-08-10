@echo off
rem ============================================================
rem start-mcp.bat - Start RimWorld MCP Server (SSE mode)
rem Double-click to run, or run from cmd.
rem
rem Delegates to start-mcp.ps1 which opens a new PowerShell
rem window running `node index.js` (logs to logs/mcp-*.log,
rem plus a window-side Tee copy mcp-window-*.log).
rem ============================================================
cd /d "%~dp0"

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0start-mcp.ps1"

echo.
echo Done. The MCP server window has been opened separately.
pause
