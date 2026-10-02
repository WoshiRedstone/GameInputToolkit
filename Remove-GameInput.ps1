<#
.SYNOPSIS
    Microsoft GameInput 强制停用 / 删除工具 (需要管理员权限)

.DESCRIPTION
    针对 GameInputSvc 反复自启动的问题，按"从软到硬"的顺序彻底处置：
      1. 摘除触发式启动 (TriggerInfo) 与失败自动重启 (FailureActions)
      2. 停止服务并强制 Start=4 (Disabled)
      3. 删除 SCM 服务注册项
      4. 强制删除 GameInput DLL (取得所有权 -> 解除只读 -> 删除)
      5. 清理 0 字节"墓碑"残留文件
      6. 对锁定文件登记 PendingFileRenameOperations，重启时删除

.PARAMETER Mode
    Audit   - 只检测不修改
    Soft    - 仅服务层 (步骤 1-2)，完全可逆
    Full    - 服务层 + 删除 SCM 注册项 + 删除 DLL (步骤 1-4)
    Nuclear - 全部 (步骤 1-6)
    Watch   - 监视服务是否在 N 分钟内复活（用来验证处置是否真的生效）

.PARAMETER IncludeTombstones
    同时清理 0 字节墓碑文件 (System32\GameInputSvc.exe 与
    Program Files\Microsoft GameInput 等)。

.PARAMETER IncludeWinSxS
    【高风险】连同 C:\Windows\WinSxS 中的硬链接源一起删除。
    默认不删：WinSxS 是受保护组件存储，直接删链接会使组件清单与
    实际文件不一致，导致 sfc /scannow 与 DISM 报错、且无法自动修复。
    不删的话 System32 侧链接消失已足以让程序无法被加载。

.PARAMETER NoBackup
    跳过注册表导出备份。

.PARAMETER Force
    跳过交互确认。

.PARAMETER WatchMinutes
    Watch 模式的监视时长（分钟），默认 5。

.EXAMPLE
    .\Remove-GameInput.ps1 -Mode Audit
    .\Remove-GameInput.ps1 -Mode Soft
    .\Remove-GameInput.ps1 -Mode Full -IncludeTombstones
    .\Remove-GameInput.ps1 -Mode Watch -WatchMinutes 10
#>
[CmdletBinding()]
param(
    [ValidateSet('Audit','Soft','Full','Nuclear','Watch')]
    [string]$Mode = 'Audit',

    [switch]$IncludeTombstones,
    [switch]$IncludeWinSxS,
    [switch]$NoBackup,
    [switch]$Force,

    [ValidateRange(1, 1440)]
    [int]$WatchMinutes = 5
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Continue'

#region ── 基础设施 ──────────────────────────────────────────────

$script:Root      = Split-Path -Parent $MyInvocation.MyCommand.Path
$script:LogDir    = Join-Path $script:Root 'logs'
$script:BackupDir = Join-Path $script:Root 'backup'
$script:RunId     = Get-Date -Format 'yyyyMMdd-HHmmss'
$script:LogFile   = Join-Path $script:LogDir "gameinput-$RunId.log"
$script:Results   = [System.Collections.Generic.List[object]]::new()

foreach ($d in @($script:LogDir, $script:BackupDir)) {
    if (-not (Test-Path -LiteralPath $d)) { New-Item -ItemType Directory -Path $d -Force | Out-Null }
}

function Write-Log {
    param(
        [Parameter(Mandatory)][string]$Message,
        [ValidateSet('INFO','OK','WARN','FAIL','STEP','HEAD')][string]$Level = 'INFO'
    )
    $stamp = Get-Date -Format 'HH:mm:ss'
    $line  = "[{0}] [{1,-4}] {2}" -f $stamp, $Level, $Message
    $color = switch ($Level) {
        'OK'   { 'Green' }
        'WARN' { 'Yellow' }
        'FAIL' { 'Red' }
        'STEP' { 'Cyan' }
        'HEAD' { 'Magenta' }
        default { 'Gray' }
    }
    Write-Host $line -ForegroundColor $color
    Add-Content -LiteralPath $script:LogFile -Value $line -Encoding UTF8
}

function Add-Result {
    param(
        [string]$Step,
        [string]$Target,
        [ValidateSet('OK','SKIP','WARN','FAIL')][string]$Status,
        [string]$Detail = ''
    )
    $script:Results.Add([pscustomobject]@{
        Step = $Step; Target = $Target; Status = $Status; Detail = $Detail
    })
    switch ($Status) {
        'OK'   { Write-Log "$Step :: $Target :: $Detail" 'OK' }
        'SKIP' { Write-Log "$Step :: $Target :: $Detail" 'INFO' }
        'WARN' { Write-Log "$Step :: $Target :: $Detail" 'WARN' }
        'FAIL' { Write-Log "$Step :: $Target :: $Detail" 'FAIL' }
    }
}

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    ([Security.Principal.WindowsPrincipal]$id).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Test-Exists([string]$Path) {
    try { return [System.IO.File]::Exists($Path) -or [System.IO.Directory]::Exists($Path) }
    catch { return $false }
}

# StrictMode 下访问不存在的属性会抛异常，统一走这个取值helper
function Get-Prop {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Name
    )
    try {
        $k = Get-Item -LiteralPath $Path -ErrorAction Stop
        if ($null -eq $k.GetValue($Name)) { return $null }
        return Get-ItemProperty -LiteralPath $Path -Name $Name -ErrorAction Stop |
               Select-Object -ExpandProperty $Name
    } catch {
        return $null
    }
}

#endregion

#region ── 关键常量：本机实测路径 ────────────────────────────────

$SvcName = 'GameInputSvc'
$SvcKey  = "HKLM:\SYSTEM\CurrentControlSet\Services\$SvcName"

# System32/SysWOW64 中的 DLL 会硬链接到 WinSxS，必须同时清理链接源，
# 否则只删 System32 侧会导致 WinSxS 侧仍持有数据、且服务可能被还原。
$TargetFiles = @(
    'C:\Windows\System32\GameInput.dll'
    'C:\Windows\System32\GameInputInbox.dll'
    'C:\Windows\SysWOW64\GameInput.dll'
)

# 服务主程序。它有两种可能形态，必须按"实际大小"动态归类，不能写死在任一列表里：
#   - 非 0 字节 = 正常的服务二进制（WinSxS 硬链接投影）→ 属于步骤 4 的删除集
#   - 0 字节    = 被安全软件替换成的墓碑（属性含未文档化 0x80000）→ 属于步骤 5 的清理集
$SvcBinary = 'C:\Windows\System32\GameInputSvc.exe'

# 固定为 0 字节墓碑的残留路径（这两个不是服务二进制，不存在"真实文件"形态）
$Tombstones = @(
    'C:\Program Files\Microsoft GameInput'
    'C:\Program Files (x86)\Microsoft Gameinput'
)

#endregion

#region ── 步骤 1：摘除触发器与失败重启 ─────────────────────────

function Invoke-DisableTriggers {
    Write-Log '── 步骤 1/6：摘除触发式启动与失败自动重启 ──' 'HEAD'

    if (-not (Test-Path -LiteralPath $SvcKey)) {
        Add-Result '1-Trigger' $SvcKey 'SKIP' '服务注册项不存在'
        return
    }

    # FailureActions：失败后 60 秒自动重启的元凶之一
    try {
        $fa = Get-Prop -Path $SvcKey -Name 'FailureActions'
        if ($null -ne $fa) {
            Remove-ItemProperty -LiteralPath $SvcKey -Name FailureActions -Force -ErrorAction Stop
            Add-Result '1-FailureActions' $SvcKey 'OK' "已移除 FailureActions (原长度 $($fa.Length) 字节)"
        } else {
            Add-Result '1-FailureActions' $SvcKey 'SKIP' '无 FailureActions'
        }
    } catch {
        Add-Result '1-FailureActions' $SvcKey 'FAIL' $_.Exception.Message
    }

    # TriggerInfo：GUID {2d7a2816-...} 类型 7 = 设备接口到达即启动，
    # 这是插拔手柄 / 驱动加载时服务被"唤起"的直接原因。
    $trigKey = Join-Path $SvcKey 'TriggerInfo'
    if (Test-Path -LiteralPath $trigKey) {
        try {
            $subs = @(Get-ChildItem -LiteralPath $trigKey -ErrorAction Stop)
            Remove-Item -LiteralPath $trigKey -Recurse -Force -ErrorAction Stop
            Add-Result '1-TriggerInfo' $trigKey 'OK' "已移除 TriggerInfo（含 $($subs.Count) 个触发器）"
        } catch {
            Add-Result '1-TriggerInfo' $trigKey 'FAIL' $_.Exception.Message
        }
    } else {
        Add-Result '1-TriggerInfo' $trigKey 'SKIP' '无 TriggerInfo'
    }

    # DelayedAutostart 兜底
    try {
        $da = Get-Prop -Path $SvcKey -Name 'DelayedAutostart'
        if ($null -ne $da) {
            Remove-ItemProperty -LiteralPath $SvcKey -Name DelayedAutostart -Force -ErrorAction Stop
            Add-Result '1-DelayedAutostart' $SvcKey 'OK' "已移除 DelayedAutostart (原值 $da)"
        } else {
            Add-Result '1-DelayedAutostart' $SvcKey 'SKIP' '无 DelayedAutostart'
        }
    } catch {
        Add-Result '1-DelayedAutostart' $SvcKey 'FAIL' $_.Exception.Message
    }
}

#endregion

#region ── 步骤 2：停止服务并设为禁用 ───────────────────────────

function Invoke-StopAndDisable {
    Write-Log '── 步骤 2/6：停止服务并强制 Disabled ──' 'HEAD'

    $svc = Get-Service -Name $SvcName -ErrorAction SilentlyContinue
    if (-not $svc) {
        Add-Result '2-Stop' $SvcName 'SKIP' '服务未注册'
        return
    }

    # 先 stop，忽略"未启动"错误
    if ($svc.Status -ne 'Stopped') {
        try {
            Stop-Service -Name $SvcName -Force -ErrorAction Stop
            Start-Sleep -Milliseconds 800
            Add-Result '2-Stop' $SvcName 'OK' '已停止'
        } catch {
            Add-Result '2-Stop' $SvcName 'FAIL' $_.Exception.Message
        }
    } else {
        Add-Result '2-Stop' $SvcName 'SKIP' '当前已是 Stopped'
    }

    # sc.exe 写 Start=4 比 Set-Service 更可靠（后者对已删注册项会报错）
    $out = & sc.exe config $SvcName start= disabled 2>&1
    if ($LASTEXITCODE -eq 0) {
        Add-Result '2-Disable' $SvcName 'OK' 'Start=4 (Disabled)'
    } else {
        Add-Result '2-Disable' $SvcName 'FAIL' ($out -join ' ')
    }
}

#endregion

#region ── 步骤 3：删除服务注册项 ───────────────────────────────

function Invoke-DeleteService {
    Write-Log '── 步骤 3/6：删除 SCM 服务注册项 ──' 'HEAD'

    if (-not (Test-Path -LiteralPath $SvcKey)) {
        Add-Result '3-DeleteSvc' $SvcName 'SKIP' '注册项已不存在'
        return
    }

    $out = & sc.exe delete $SvcName 2>&1
    if ($LASTEXITCODE -eq 0) {
        Add-Result '3-DeleteSvc' $SvcName 'OK' '服务已标记删除'
    } else {
        # 常见：标记删除后需重启才真正消失 (1072)
        Add-Result '3-DeleteSvc' $SvcName 'WARN' (($out -join ' ').Trim())
    }
}

#endregion

#region ── 步骤 4：取得所有权并强制删除文件 ─────────────────────

function Invoke-TakeOwnership {
    param([Parameter(Mandatory)][string]$Path)

    # 优先用 takeown/icacls 原生命令：即使 PowerShell 受限也能工作
    & takeown.exe /F "$Path" /A 2>&1 | Out-Null
    & icacls.exe "$Path" /grant "*S-1-5-32-544:(F)" 2>&1 | Out-Null
    & attrib.exe -R -S -H "$Path" 2>&1 | Out-Null
}

function Remove-TargetFile {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Exists $Path)) {
        Add-Result '4-DeleteFile' $Path 'SKIP' '文件不存在'
        return
    }

    # 记录原始信息
    try {
        $fi   = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
        $size = if ($fi.PSIsContainer) { 'DIR' } else { $fi.Length }
        $owner = (Get-Acl -LiteralPath $Path -ErrorAction SilentlyContinue).Owner
    } catch {
        $size = '?'; $owner = '?'
    }

    # 第一步：直接尝试
    try {
        Remove-Item -LiteralPath $Path -Force -ErrorAction Stop
        Add-Result '4-DeleteFile' $Path 'OK' "已删除 (原大小 $size, 属主 $owner)"
        return
    } catch {
        $firstErr = $_.Exception.Message
    }

    # 第二步：取得所有权后重试
    Write-Log "  取得所有权后重试: $Path" 'INFO'
    Invoke-TakeOwnership -Path $Path
    try {
        Remove-Item -LiteralPath $Path -Force -ErrorAction Stop
        Add-Result '4-DeleteFile' $Path 'OK' "取得所有权后删除成功 (原属主 $owner)"
        return
    } catch {
        $secondErr = $_.Exception.Message
    }

    # 第三步：登记重启删除
    if (Register-PendingDelete -Path $Path) {
        Add-Result '4-DeleteFile' $Path 'WARN' "文件被占用，已登记重启后删除 (占用错误: $secondErr)"
    } else {
        Add-Result '4-DeleteFile' $Path 'FAIL' "删除失败: $firstErr / $secondErr"
    }
}

function Invoke-DeleteBinaries {
    Write-Log '── 步骤 4/6：删除 GameInput 二进制 ──' 'HEAD'

    # 服务主程序按"实际形态"归类：非 0 字节才是真二进制，纳入删除集；
    # 0 字节说明它已被替换成墓碑，交给步骤 5 清理，不在这里删。
    $candidates = [System.Collections.Generic.List[string]]::new()
    foreach ($f in $TargetFiles) { $candidates.Add($f) }

    if (Test-Exists $SvcBinary) {
        $sfi = Get-Item -LiteralPath $SvcBinary -Force
        if ((-not $sfi.PSIsContainer) -and ($sfi.Length -gt 0)) {
            $candidates.Add($SvcBinary)
        } else {
            Add-Result '4-SvcBinary' $SvcBinary 'SKIP' `
                "0 字节墓碑形态，交给步骤 5 清理（不在步骤 4 删除）"
        }
    }

    # 先枚举硬链接：WinSxS 侧必须一并处理
    $allPaths = [System.Collections.Generic.List[string]]::new()
    foreach ($f in $candidates) { $allPaths.Add($f) }

    # WinSxS 链接源默认保留：删掉它会让组件清单与实际文件不一致，
    # sfc/DISM 会报错且无法自动修复。System32 侧链接消失后，
    # 该 DLL 已无法被正常加载，保留 WinSxS 只是留下可回滚的副本。
    $winsxsLinks = [System.Collections.Generic.List[string]]::new()
    foreach ($f in $candidates) {
        if (-not (Test-Exists $f)) { continue }
        try {
            $links = & fsutil.exe hardlink list $f 2>$null
            foreach ($l in $links) {
                $l = "$l".Trim()
                if ($l -match '^\\Windows\\') {
                    $full = 'C:\' + ($l -replace '^\\', '')
                    if ($allPaths.Contains($full)) { continue }
                    if ($full -match '\\WinSxS\\') {
                        if (-not $winsxsLinks.Contains($full)) { $winsxsLinks.Add($full) }
                    } else {
                        $allPaths.Add($full)
                    }
                }
            }
        } catch { }
    }

    if ($winsxsLinks.Count -gt 0) {
        if ($IncludeWinSxS) {
            foreach ($w in $winsxsLinks) { $allPaths.Add($w) }
            Add-Result '4-WinSxS' 'C:\Windows\WinSxS' 'WARN' `
                "已按 -IncludeWinSxS 纳入 $($winsxsLinks.Count) 个组件存储链接；sfc/DISM 可能报组件不一致"
        } else {
            Add-Result '4-WinSxS' 'C:\Windows\WinSxS' 'SKIP' `
                "保留 $($winsxsLinks.Count) 个硬链接源 (组件存储受保护；加 -IncludeWinSxS 可强制删除)"
        }
    }

    Write-Log "  待处理路径 $($allPaths.Count) 个" 'INFO'
    foreach ($p in $allPaths) { Remove-TargetFile -Path $p }

    # WinSxS 组件清单位于受保护目录，仅做提示不删除
    $manifests = @(Get-ChildItem 'C:\Windows\WinSxS\Manifests' -Filter '*gameinput*' -ErrorAction SilentlyContinue)
    if ($manifests.Count -gt 0) {
        Add-Result '4-Manifests' 'C:\Windows\WinSxS\Manifests' 'WARN' `
            "保留 $($manifests.Count) 个组件清单；这些由服务栈管理，强删会导致 sfc/DISM 组件存储不一致"
    }
}

#endregion

#region ── 步骤 5：清理 0 字节墓碑 ──────────────────────────────

function Invoke-CleanTombstones {
    Write-Log '── 步骤 5/6：清理 0 字节墓碑残留 ──' 'HEAD'

    # 候选集 = 固定墓碑路径 + （仅当它确实是 0 字节时才加入）服务主程序路径。
    # 这样修复过的真实二进制绝不会进入本步骤。
    $candidates = [System.Collections.Generic.List[string]]::new()
    foreach ($t in $Tombstones) { $candidates.Add($t) }

    if (Test-Exists $SvcBinary) {
        $sfi = Get-Item -LiteralPath $SvcBinary -Force
        if ((-not $sfi.PSIsContainer) -and ($sfi.Length -eq 0)) {
            $candidates.Add($SvcBinary)
        } else {
            Add-Result '5-SvcBinary' $SvcBinary 'SKIP' `
                "非 0 字节文件 (大小 $($sfi.Length))，是正常的服务二进制，不按墓碑清理"
        }
    }

    foreach ($p in $candidates) {
        if (-not (Test-Exists $p)) {
            Add-Result '5-Tombstone' $p 'SKIP' '不存在'
            continue
        }

        $fi = Get-Item -LiteralPath $p -Force
        $isZero = (-not $fi.PSIsContainer) -and ($fi.Length -eq 0)
        $attr   = [int]$fi.Attributes

        if (-not $isZero) {
            Add-Result '5-Tombstone' $p 'SKIP' "非 0 字节文件 (大小 $($fi.Length))，跳过以免误删"
            continue
        }

        try {
            Remove-Item -LiteralPath $p -Force -ErrorAction Stop
            Add-Result '5-Tombstone' $p 'OK' "已删除 (0 字节, 原属性 0x$('{0:X}' -f $attr))"
        } catch {
            Invoke-TakeOwnership -Path $p
            try {
                Remove-Item -LiteralPath $p -Force -ErrorAction Stop
                Add-Result '5-Tombstone' $p 'OK' '取得所有权后删除成功'
            } catch {
                if (Register-PendingDelete -Path $p) {
                    Add-Result '5-Tombstone' $p 'WARN' '已登记重启后删除'
                } else {
                    Add-Result '5-Tombstone' $p 'FAIL' $_.Exception.Message
                }
            }
        }
    }

    Add-Result '5-Note' '火绒(Huorong)' 'WARN' `
        'HRWSCCtrl 正在运行；这些墓碑很可能由它的文件保护/回滚机制产生。若删除后再次出现，请检查火绒的防护日志'
}

#endregion

#region ── 步骤 6：登记重启删除 ─────────────────────────────────

function Register-PendingDelete {
    param([Parameter(Mandatory)][string]$Path)

    try {
        $smKey = 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager'
        $existing = @()
        $cur = Get-Prop -Path $smKey -Name 'PendingFileRenameOperations'
        if ($null -ne $cur) { $existing = @($cur) }

        # 格式: \??\C:\path  (源)  + '' (目标为空 = 删除)
        $entry = '\??\' + $Path
        if ($existing -contains $entry) { return $true }

        $new = $existing + @($entry, '')
        Set-ItemProperty -LiteralPath $smKey -Name PendingFileRenameOperations -Value $new -Type MultiString -Force -ErrorAction Stop
        Write-Log "  已登记重启删除: $Path" 'OK'
        return $true
    } catch {
        Write-Log "  登记重启删除失败 ($Path): $($_.Exception.Message)" 'FAIL'
        return $false
    }
}

function Invoke-PendingCleanup {
    Write-Log '── 步骤 6/6：重启删除队列确认 ──' 'HEAD'

    $smKey = 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager'
    $cur = Get-Prop -Path $smKey -Name 'PendingFileRenameOperations'
    $hits = @()
    if ($null -ne $cur) {
        $hits = @($cur | Where-Object { $_ -match 'GameInput' })
    }

    if ($hits.Count -gt 0) {
        Add-Result '6-Pending' 'PendingFileRenameOperations' 'OK' "队列中有 $($hits.Count) 条 GameInput 相关项，重启后生效"
        Add-Result '6-Reboot' '系统' 'WARN' '需要重启才能完成清理'
    } else {
        Add-Result '6-Pending' 'PendingFileRenameOperations' 'SKIP' '无遗留项'
    }
}

#endregion

#region ── 服务状态复检 ─────────────────────────────────────────

function Invoke-PostCheck {
    Write-Log '── 复检 ──' 'HEAD'

    $svc = Get-Service -Name $SvcName -ErrorAction SilentlyContinue
    if ($svc) {
        Add-Result 'Post-Service' $SvcName 'WARN' "服务仍存在: Status=$($svc.Status) StartType=$($svc.StartType)"
    } else {
        Add-Result 'Post-Service' $SvcName 'OK' '服务已不存在'
    }

    foreach ($f in $TargetFiles) {
        if (Test-Exists $f) {
            $fi = Get-Item -LiteralPath $f -Force
            Add-Result 'Post-File' $f 'WARN' "仍存在 (大小 $($fi.Length))"
        } else {
            Add-Result 'Post-File' $f 'OK' '已清除'
        }
    }

    # 进程检查
    $procs = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.Name -match 'GameInput' })
    if ($procs.Count -eq 0) {
        Add-Result 'Post-Process' 'GameInputSvc' 'OK' '无相关进程'
    } else {
        Add-Result 'Post-Process' 'GameInputSvc' 'WARN' "仍有进程: $($procs.Name -join ', ')"
    }
}

function Invoke-Watch {
    param([int]$Minutes = 5)

    Write-Log "── 监视模式：$Minutes 分钟内观察服务是否复活 ──" 'HEAD'
    Write-Log '现在可以插拔手柄 / 启动游戏，检验处置是否真的挡住了拉起。' 'INFO'

    $deadline   = (Get-Date).AddMinutes($Minutes)
    $baseline   = Get-Service -Name $SvcName -ErrorAction SilentlyContinue
    $startState = if ($baseline) { "$($baseline.Status)/$($baseline.StartType)" } else { '不存在' }
    Add-Result 'Watch-Baseline' $SvcName 'SKIP' "起始状态: $startState"

    # 记录文件当前是否存在，只有"从无到有"才算复活；
    # 处置前本来就在的文件不应被报成失败。
    $fileBefore = @{}
    foreach ($f in $TargetFiles) { $fileBefore[$f] = (Test-Exists $f) }

    $respawned = $false
    while ((Get-Date) -lt $deadline) {
        $s = Get-Service -Name $SvcName -ErrorAction SilentlyContinue
        if ($s -and $s.Status -ne 'Stopped') {
            Add-Result 'Watch-Respawn' $SvcName 'FAIL' `
                "$(Get-Date -Format 'HH:mm:ss') 服务复活: Status=$($s.Status) StartType=$($s.StartType)"
            $respawned = $true
            break
        }
        Start-Sleep -Seconds 5
    }

    if (-not $respawned) {
        Add-Result 'Watch-Respawn' $SvcName 'OK' "监视 $Minutes 分钟内服务未复活"
    }

    # 顺带看文件是否被还原
    foreach ($f in $TargetFiles) {
        $now = Test-Exists $f
        if ($now -and -not $fileBefore[$f]) {
            Add-Result 'Watch-File' $f 'FAIL' '文件被还原（原本已删除）'
        } elseif ($now) {
            Add-Result 'Watch-File' $f 'SKIP' '文件仍存在（本次监视前就存在，未被删除）'
        } else {
            Add-Result 'Watch-File' $f 'OK' '仍不存在'
        }
    }
}

#endregion

#region ── 主流程 ────────────────────────────────────────────────

function Show-Banner {
    Write-Host ''
    Write-Host '  ╔══════════════════════════════════════════════════════════╗' -ForegroundColor Magenta
    Write-Host '  ║   Microsoft GameInput 强制停用 / 删除工具                ║' -ForegroundColor Magenta
    Write-Host '  ║   Remove-GameInput.ps1                                   ║' -ForegroundColor Magenta
    Write-Host '  ╚══════════════════════════════════════════════════════════╝' -ForegroundColor Magenta
    Write-Host ''
    Write-Log "模式: $Mode | 墓碑清理: $IncludeTombstones | 已提权: $(Test-Admin)" 'HEAD'
    Write-Log "日志: $script:LogFile" 'INFO'
}

function Invoke-Backup {
    if ($NoBackup) { Write-Log '已跳过注册表备份 (-NoBackup)' 'WARN'; return }
    $file = Join-Path $script:BackupDir "GameInputSvc-$script:RunId.reg"
    $out = & reg.exe export "HKLM\SYSTEM\CurrentControlSet\Services\$SvcName" "$file" /y 2>&1
    if ($LASTEXITCODE -eq 0) {
        Write-Log "注册表已备份: $file" 'OK'
    } else {
        Write-Log "注册表备份失败: $($out -join ' ')" 'WARN'
    }
}

function Invoke-Preflight {
    Write-Log '── 预检 ──' 'HEAD'

    if (-not (Test-Admin)) {
        Write-Log '未以管理员身份运行，无法修改服务与系统文件。' 'FAIL'
        Write-Log '请右键以管理员身份运行 Run-Elevated.cmd，或执行：' 'INFO'
        Write-Log '  Start-Process pwsh -Verb RunAs -ArgumentList ''-NoProfile -File "本脚本" -Mode Full''' 'INFO'
        return $false
    }

    # 系统还原点提示
    try {
        $sr = Get-CimInstance -Namespace 'root\default' -ClassName SystemRestore -ErrorAction Stop |
              Where-Object { $_.CreationTime -gt (Get-Date).AddDays(-7) }
        if ($sr) {
            Write-Log "存在 7 天内的还原点 $($sr.Count) 个，可用于回滚" 'OK'
        } else {
            Write-Log '最近 7 天无系统还原点；建议先手动创建后再执行 Full/Nuclear' 'WARN'
        }
    } catch {
        Write-Log '无法查询系统还原点（服务可能已关闭）' 'WARN'
    }

    # 火绒提示
    if (Get-Service -Name 'HRWSCCtrl' -ErrorAction SilentlyContinue) {
        Write-Log '检测到火绒 (HRWSCCtrl)。其文件保护可能拦截删除或回滚改动；' 'WARN'
        Write-Log '若操作失败，请在火绒中临时关闭"文件实时监控"与"自我保护"。' 'WARN'
    }

    return $true
}

function Invoke-Confirm {
    if ($Force) { return $true }
    Write-Host ''
    Write-Host "  即将执行模式 [$Mode]，这会修改系统服务与 System32 下的文件。" -ForegroundColor Yellow
    Write-Host '  强烈建议先创建系统还原点。' -ForegroundColor Yellow
    $ans = Read-Host '  输入 YES 继续，其它任意键取消'
    return ($ans -ceq 'YES')
}

function Invoke-Summary {
    Write-Host ''
    Write-Host '  ══════════════════ 执行摘要 ══════════════════' -ForegroundColor Magenta
    $script:Results | Format-Table -AutoSize Step, Status, Target, Detail |
        Out-String -Width 200 | Write-Host

    $ok   = @($script:Results | Where-Object Status -eq 'OK').Count
    $warn = @($script:Results | Where-Object Status -eq 'WARN').Count
    $fail = @($script:Results | Where-Object Status -eq 'FAIL').Count
    $skip = @($script:Results | Where-Object Status -eq 'SKIP').Count

    Write-Host "  成功 $ok | 警告 $warn | 失败 $fail | 跳过 $skip" -ForegroundColor Cyan
    Write-Host "  完整日志: $script:LogFile" -ForegroundColor Gray
    Write-Host ''

    if ($warn -gt 0 -or $fail -gt 0) {
        Write-Host '  提示：标记为"警告"的项目通常需要重启后生效。' -ForegroundColor Yellow
        Write-Host ''
    }
}

function Get-ExitCode {
    # 0 全部成功 / 1 未提权 / 3 存在失败项 / 4 用户取消
    $fail = @($script:Results | Where-Object Status -eq 'FAIL').Count
    if ($fail -gt 0) { return 3 }
    return 0
}

function Invoke-Main {
    Show-Banner

    # Audit / Watch 不需要提权
    if ($Mode -eq 'Audit') {
        Invoke-Audit
        Invoke-Summary
        return (Get-ExitCode)
    }
    if ($Mode -eq 'Watch') {
        Invoke-Watch -Minutes $WatchMinutes
        Invoke-Summary
        return (Get-ExitCode)
    }

    if (-not (Invoke-Preflight)) { Invoke-Summary; return 1 }
    if (-not (Invoke-Confirm))   { Write-Log '用户取消操作' 'WARN'; return 4 }

    Invoke-Backup

    Write-Log "── 执行模式：$Mode ──" 'STEP'
    Invoke-DisableTriggers
    Invoke-StopAndDisable

    # Soft 模式止步于此：停用 + 摘触发器，服务注册项保留，随时可 set-service 恢复
    if ($Mode -in @('Full','Nuclear')) {
        Invoke-DeleteService
        Invoke-DeleteBinaries
        # 清掉服务被移除后遗留的 0 字节可执行文件
        Remove-TargetFile -Path 'C:\Windows\System32\GameInputSvc.exe'
    }

    if ($Mode -eq 'Nuclear' -or $IncludeTombstones) { Invoke-CleanTombstones }

    Invoke-PendingCleanup
    Invoke-PostCheck
    Invoke-Summary

    return (Get-ExitCode)
}

function Invoke-Audit {
    Write-Log '── 审计模式：只检测，不修改 ──' 'HEAD'

    $svc = Get-Service -Name $SvcName -ErrorAction SilentlyContinue
    if ($svc) {
        Add-Result 'Audit-Service' $SvcName 'WARN' "Status=$($svc.Status) StartType=$($svc.StartType)"
    } else {
        Add-Result 'Audit-Service' $SvcName 'OK' '未注册'
    }

    if (Test-Path -LiteralPath $SvcKey) {
        $startVal = Get-Prop -Path $SvcKey -Name 'Start'
        $faVal    = Get-Prop -Path $SvcKey -Name 'FailureActions'
        $hasTrig  = Test-Path -LiteralPath (Join-Path $SvcKey 'TriggerInfo')

        Add-Result 'Audit-Start' 'Start' 'SKIP' "Start=$startVal (4=Disabled, 3=Manual, 2=Automatic)"
        Add-Result 'Audit-FailureActions' 'FailureActions' `
            $(if ($null -ne $faVal) { 'WARN' } else { 'OK' }) `
            $(if ($null -ne $faVal) { '存在：失败会自动重启' } else { '无' })
        Add-Result 'Audit-TriggerInfo' 'TriggerInfo' `
            $(if ($hasTrig) { 'WARN' } else { 'OK' }) `
            $(if ($hasTrig) { '存在：按设备接口触发启动' } else { '无' })
    }

    foreach ($f in $TargetFiles) {
        if (Test-Exists $f) {
            $fi = Get-Item -LiteralPath $f -Force
            $links = @(& fsutil.exe hardlink list $f 2>$null | Where-Object { "$_".Trim() -match '^\\Windows\\' })
            Add-Result 'Audit-File' $f 'WARN' "存在 大小=$($fi.Length) 硬链接=$($links.Count)"
        } else {
            Add-Result 'Audit-File' $f 'OK' '已清除'
        }
    }

    foreach ($t in $Tombstones) {
        if (Test-Exists $t) {
            $fi = Get-Item -LiteralPath $t -Force
            $desc = if ($fi.PSIsContainer) { '目录' } else { "$($fi.Length) 字节, 属性 0x$('{0:X}' -f [int]$fi.Attributes)" }
            Add-Result 'Audit-Tombstone' $t 'WARN' $desc
        } else {
            Add-Result 'Audit-Tombstone' $t 'OK' '不存在'
        }
    }

    # 服务主程序：按实际形态报告，避免把修好的真二进制误报成墓碑
    if (Test-Exists $SvcBinary) {
        $fi = Get-Item -LiteralPath $SvcBinary -Force
        $attr = [int]$fi.Attributes
        if ($fi.PSIsContainer) {
            Add-Result 'Audit-SvcBinary' $SvcBinary 'WARN' '是目录，形态异常'
        } elseif ($fi.Length -eq 0) {
            Add-Result 'Audit-SvcBinary' $SvcBinary 'WARN' `
                "0 字节墓碑 (属性 0x$('{0:X}' -f $attr))，步骤 5 可清理"
        } else {
            $links = @(& fsutil.exe hardlink list $SvcBinary 2>$null | Where-Object { "$_".Trim() -match '^\\Windows\\' })
            Add-Result 'Audit-SvcBinary' $SvcBinary 'WARN' `
                "存在 大小=$($fi.Length) 硬链接=$($links.Count)（正常的服务二进制；步骤 4 会删除它）"
        }
    } else {
        Add-Result 'Audit-SvcBinary' $SvcBinary 'OK' '不存在'
    }
}

# Invoke-Main 只返回一个 int（其余输出都走 Write-Host），
# 取最后一个元素并强转，避免任何意外泄漏到管道的输出破坏退出码。
$exitCode = [int](@(Invoke-Main) | Select-Object -Last 1)
exit $exitCode

#endregion
