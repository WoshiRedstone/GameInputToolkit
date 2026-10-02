<#
.SYNOPSIS
    UAC 自动提权启动器 —— 可启动工具箱内任意主脚本
.DESCRIPTION
    检测当前是否已提权：已提权则直接运行目标脚本；否则用
    Start-Process -Verb RunAs 触发 UAC 弹窗并等待其结束，
    最后把子进程的退出码原样返回给调用方。
#>
[CmdletBinding()]
param(
    # 要运行的主脚本文件名（相对于本脚本所在目录）
    [string]$Script = 'Remove-GameInput.ps1',

    [ValidateSet('Audit','Soft','Full','Nuclear','Watch','Diagnose','Repair','Deep')]
    [string]$Mode = 'Full',

    [int]$WatchMinutes = 5,

    # 其余参数原样透传（例如 -IncludeTombstones / -UseSfc）
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$ExtraArgs
)

$root   = Split-Path -Parent $MyInvocation.MyCommand.Path
$target = Join-Path $root $Script

if (-not (Test-Path -LiteralPath $target)) {
    Write-Host "找不到目标脚本: $target" -ForegroundColor Red
    exit 2
}

function Test-IsAdmin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    ([Security.Principal.WindowsPrincipal]$id).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

# 组装参数：模式 + 监视时长 + 透传项
$inner = @('-Mode', $Mode)
if ($Mode -eq 'Watch') { $inner += @('-WatchMinutes', "$WatchMinutes") }
if ($ExtraArgs) { $inner += $ExtraArgs }

# 已提权：直接跑，不做二次弹窗
if (Test-IsAdmin) {
    & $target @inner
    exit $LASTEXITCODE
}

Write-Host ''
Write-Host '  正在请求管理员权限，请在弹出的 UAC 窗口中点击"是"...' -ForegroundColor Yellow
Write-Host ''

# 用当前 powershell 宿主自身的可执行文件路径来提权，避免 pwsh/powershell 混用
$exe = try { (Get-Process -Id $PID).Path } catch { $null }
if ([string]::IsNullOrWhiteSpace($exe)) { $exe = 'powershell.exe' }

# 目标路径可能含空格，必须显式加引号；Start-Process 会把数组用空格拼接
$argList = @(
    '-NoProfile'
    '-ExecutionPolicy', 'Bypass'
    '-File', "`"$target`""
) + $inner

try {
    $proc = Start-Process -FilePath $exe -Verb RunAs -PassThru -Wait -ArgumentList $argList -ErrorAction Stop
    exit $proc.ExitCode
} catch {
    # 用户在 UAC 弹窗点了"否"，或提权被策略拦截
    Write-Host "  提权失败: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host '  请改为：右键对应的 .cmd -> 以管理员身份运行' -ForegroundColor Yellow
    exit 5
}
