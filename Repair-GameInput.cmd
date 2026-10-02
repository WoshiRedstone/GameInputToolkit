@echo off
REM ============================================================
REM  GameInput 组件存储不一致 —— 定向修复（自动提权）
REM
REM  双击            -> Diagnose（只诊断，不改动任何东西）
REM  右键以管理员身份运行 -> 同样先诊断，然后按提示修复
REM
REM  用法：
REM    Repair-GameInput.cmd            诊断
REM    Repair-GameInput.cmd Repair     执行修复
REM    Repair-GameInput.cmd Deep       修复 + DISM 复查
REM    Repair-GameInput.cmd Repair -UseSfc   改用 sfc /scannow 恢复
REM ============================================================
setlocal EnableExtensions

set "MODE=%~1"
if "%MODE%"=="" set "MODE=Diagnose"

REM 收集第 2 个及之后的参数原样转发
set "EXTRA="
shift
:collect
if "%~1"=="" goto :collected
set "EXTRA=%EXTRA% %~1"
shift
goto :collect
:collected

REM 用完整性级别判断是否已提权（net session 在 Server 服务停用时不可靠）
whoami /groups 2>nul | findstr /c:"S-1-16-12288" >nul 2>&1
if %errorlevel% equ 0 goto :elevated

REM 未提权：诊断模式不需要管理员，直接跑
if /i "%MODE%"=="Diagnose" (
  pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0Repair-GameInputComponents.ps1" -Mode Diagnose
  set "RC=%errorlevel%"
  echo.
  pause
  exit /b %RC%
)

echo.
echo   修复需要管理员权限，正在请求 UAC 提权...
echo   请在弹窗中点击"是"。
echo.
pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0Invoke-Elevated.ps1" -Script Repair-GameInputComponents.ps1 -Mode %MODE%%EXTRA%
set "RC=%errorlevel%"
if %RC% neq 0 (
  echo.
  echo   [提示] 未以管理员权限完成 ^(exit %RC%^)。
  echo   可改为：右键本文件 -^> 以管理员身份运行。
  echo.
  pause
)
exit /b %RC%

:elevated
echo.
echo   已具备管理员权限，以 %MODE% 模式启动...
echo.
pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0Repair-GameInputComponents.ps1" -Mode %MODE%%EXTRA%
set "RC=%errorlevel%"
echo.
pause
exit /b %RC%
