@echo off
chcp 936 >nul
title RimWorld MCP Server (Admin)

:: Get script directory
set "SCRIPT_DIR=%~dp0"
cd /d "%SCRIPT_DIR%"

:: Check if running as admin
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo Requesting admin privileges...
    powershell -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

echo ========================================
echo  RimWorld MCP Server (Admin Mode)
echo ========================================
echo.

:: Check if node is available
where node >nul 2>&1
if %errorLevel% neq 0 (
    echo [Error] Node.js not found. Please install Node.js and add it to PATH.
    pause
    exit /b 1
)

echo [Info] Starting MCP server with admin privileges...
echo [Info] Working directory: %cd%
echo [Info] Node version:
node --version
echo.

node index.js

if %errorLevel% neq 0 (
    echo.
    echo [Error] MCP server exited with error code: %errorLevel%
    pause
)
