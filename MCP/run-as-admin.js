#!/usr/bin/env node
/**
 * RimWorld MCP 服务器管理员权限启动器
 * 此脚本会先检查管理员权限，如果没有则重新以管理员身份启动
 */

import { spawn, execSync } from 'child_process';
import { fileURLToPath } from 'url';
import path from 'path';

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);

// 检查是否以管理员身份运行
function isAdmin() {
  try {
    // Windows 下检查管理员权限
    execSync('net session', { stdio: 'ignore' });
    return true;
  } catch {
    return false;
  }
}

// 以管理员身份重新启动
function restartAsAdmin() {
  const indexPath = path.join(__dirname, 'index.js');
  
  console.log('需要管理员权限，正在提升权限...');
  
  // 使用 PowerShell 以管理员身份启动
  const psCommand = `Start-Process node -ArgumentList '"${indexPath}"' -Verb RunAs -Wait`;
  
  try {
    execSync(`powershell -ExecutionPolicy Bypass -Command "${psCommand}"`, {
      stdio: 'inherit'
    });
  } catch (error) {
    console.error('提升权限失败:', error.message);
    process.exit(1);
  }
}

// 主函数
function main() {
  if (!isAdmin()) {
    restartAsAdmin();
    return;
  }
  
  // 已经是管理员，直接启动 MCP 服务器
  console.log('========================================');
  console.log('  RimWorld MCP 服务器 (管理员模式)');
  console.log('========================================');
  
  const indexPath = path.join(__dirname, 'index.js');
  
  // 使用 spawn 启动真正的 MCP 服务器
  const child = spawn('node', [indexPath], {
    stdio: 'inherit',
    cwd: __dirname
  });
  
  child.on('exit', (code) => {
    process.exit(code);
  });
}

main();
