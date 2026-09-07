@echo off
chcp 65001 >nul
title 有道 Cookie 一键获取（YNMT 模式）
echo.
echo 正在启动（需要能联网访问 fanyi.youdao.com）...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0获取Cookie一键脚本.ps1"
