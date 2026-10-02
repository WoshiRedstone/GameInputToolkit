@echo off
REM ============================================================
REM  GameInput 处置工具 - 自动提权启动器
REM
REM  用法：
REM    双击                     -> Full 模式（服务层 + 删除 DLL）
REM    Run-Elevated.cmd Audit   -> 只检测
REM    Run-Elevated.cmd Soft    -> 只停用+摘触发器（可逆）
REM    Run-Elevated.cmd Nuclear -> 全部清理
REM    Run-Elevated.cmd Watch 5 -> 监视 5 分钟看服务是否复活
REM
REM  可附加参数：
REM    -IncludeTombstones  清 0 字节墓碑残留
REM    -IncludeWinSxS      连 WinSxS 组件存储硬链接一起删（不推荐）
REM    -NoBackup           不导出注册表备份
REM ============================================================
setlocal EnableExtensions

set "MODE=%~1"
if "%MODE%"=="" set "MODE=Full"

REM 收集第 2 个及之后的参数，原样转发
set "EXTRA="
shift
:collect
if "%~1"=="" goto :collected
set "EXTRA=%EXTRA% %~1"
shift
goto :collect
:collected

REM 用高完整性级别判断是否已提权。
REM 不用 net session：Server(LanmanServer) 服务未启动时它会误报失败，
REM 导致明明已是管理员却又弹一次 UAC。
whoami /groups 2>nul | findstr /c:"S-1-16-12288" >nul 2>&1
if %errorlevel% equ 0 goto :elevated

echo.
echo   需要管理员权限，正在请求 UAC 提权...
echo   请在弹窗中点击"是"。
echo.

pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0Invoke-Elevated.ps1" -Mode %MODE%%EXTRA%
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
pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0Remove-GameInput.ps1" -Mode %MODE%%EXTRA%
set "RC=%errorlevel%"
echo.
pause
exit /b %RC%
