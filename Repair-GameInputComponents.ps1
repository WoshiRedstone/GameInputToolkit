<#
.SYNOPSIS
    修复 Microsoft GameInput 组件存储不一致（定向修复，非全量 DISM 清理）
.DESCRIPTION
    排查结论：组件存储整体是健康的，只有一处真实不一致——

      C:\Windows\System32\GameInputSvc.exe 本应是 gameinputinbox 组件
      (10.0.19041.7725) 的硬链接投影（55248 字节），实际却是 0 字节墓碑文件，
      属主 BUILTIN\Administrators，属性含未文档化的 0x80000。

    本脚本只修复这一处：把投影恢复为指向组件存储正本的硬链接，
    并从同目录的 GameInputInbox.dll 复制正确的 ACL/属主，
    使 Windows 侧重新自洽。

    注意：修复的是"文件投影"，不会重新启用服务。
    GameInputSvc 的 Start=4 (Disabled) 与已摘除的 TriggerInfo 保持不变，
    因此服务依旧不会自行拉起。

.PARAMETER Mode
    Diagnose  只诊断，不修改（默认）
    Repair    执行定向修复（重建硬链接投影）
    Deep      修复后再跑 DISM /RestoreHealth 复查组件存储

.PARAMETER UseSfc
    改用 sfc /scannow 让 Windows 自己恢复该受保护文件（较慢，5-15 分钟），
    而不是手工重建硬链接。两种方式都会保留正确的属主。

.EXAMPLE
    .\Repair-GameInputComponents.ps1
    只诊断，打印当前不一致清单。

.EXAMPLE
    .\Repair-GameInputComponents.ps1 -Mode Repair
    重建 System32\GameInputSvc.exe 的硬链接投影。
#>
[CmdletBinding()]
param(
    [ValidateSet('Diagnose','Repair','Deep')]
    [string]$Mode = 'Diagnose',

    [switch]$UseSfc
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Continue'

#region ── 基础设施 ────────────────────────────────────────────────

$script:Results  = [System.Collections.Generic.List[object]]::new()
$script:RunId    = Get-Date -Format 'yyyyMMdd-HHmmss'
$script:Root     = Split-Path -Parent $MyInvocation.MyCommand.Path
$script:LogDir   = Join-Path $script:Root 'logs'
$script:LogFile  = $null

try {
    if (-not (Test-Path -LiteralPath $script:LogDir)) {
        New-Item -ItemType Directory -Path $script:LogDir -Force | Out-Null
    }
    $script:LogFile = Join-Path $script:LogDir "repair-components-$($script:RunId).log"
} catch { $script:LogFile = $null }

function Write-Log {
    param(
        [Parameter(Mandatory)][string]$Message,
        [ValidateSet('INFO','OK','SKIP','WARN','FAIL','HEAD','STEP')]
        [string]$Level = 'INFO'
    )

    $color = switch ($Level) {
        'OK'   { 'Green' }
        'SKIP' { 'DarkGray' }
        'WARN' { 'Yellow' }
        'FAIL' { 'Red' }
        'HEAD' { 'Cyan' }
        'STEP' { 'Magenta' }
        default { 'Gray' }
    }
    Write-Host ("[{0}] [{1,-4}] {2}" -f (Get-Date -Format 'HH:mm:ss'), $Level, $Message) -ForegroundColor $color

    if ($script:LogFile) {
        try {
            Add-Content -LiteralPath $script:LogFile -Encoding UTF8 `
                -Value ("[{0}] [{1,-4}] {2}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Level, $Message)
        } catch { }
    }
}

function Add-Result {
    param(
        [Parameter(Mandatory)][string]$Step,
        [Parameter(Mandatory)][string]$Target,
        [Parameter(Mandatory)][ValidateSet('OK','SKIP','WARN','FAIL')][string]$Status,
        [string]$Detail = ''
    )
    $script:Results.Add([pscustomobject]@{
        Step = $Step; Target = $Target; Status = $Status; Detail = $Detail
    })
    Write-Log -Message "$Step :: $Target :: $Detail" -Level $Status
}

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    ([Security.Principal.WindowsPrincipal]$id).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Test-Exists {
    param([Parameter(Mandatory)][string]$Path)
    if (Test-Path -LiteralPath $Path) { return $true }
    try { return $null -ne [System.IO.File]::GetAttributes($Path) } catch { return $false }
}

function Get-FileId {
    param([Parameter(Mandatory)][string]$Path)
    try {
        $out = & fsutil.exe file queryfileid "$Path" 2>$null
        if ($LASTEXITCODE -eq 0 -and $out) {
            # 输出形如 "File ID is 0x0000..."
            if ("$out" -match '(0x[0-9a-fA-F]+)') { return $Matches[1].ToLowerInvariant() }
        }
    } catch { }
    return $null
}

#endregion

#region ── 关键常量 ────────────────────────────────────────────────

$SvcName = 'GameInputSvc'
$SvcKey  = 'HKLM:\SYSTEM\CurrentControlSet\Services\GameInputSvc'

# 目标投影（当前是 0 字节墓碑）
$Projection = 'C:\Windows\System32\GameInputSvc.exe'

# 组件存储中的正本。7725 是当前生效版本。
$Payload7725 = 'C:\Windows\WinSxS\amd64_microsoft-onecore-gameinputinbox_31bf3856ad364e35_10.0.19041.7725_none_f96e731c6b0b0a4d\GameInputSvc.exe'
$Payload7663 = 'C:\Windows\WinSxS\amd64_microsoft-onecore-gameinputinbox_31bf3856ad364e35_10.0.19041.7663_none_f97d71c06aff2076\GameInputSvc.exe'

# ACL 参照物：同一组件的另一个投影，权限/属主即为正确值
$AclReference = 'C:\Windows\System32\GameInputInbox.dll'

# 其余三个投影（用于完整性对账，正常情况下本脚本不会改动它们）
$OtherProjections = @(
    @{ Proj = 'C:\Windows\System32\GameInput.dll'
       Payload = 'C:\Windows\WinSxS\amd64_microsoft-onecore-gameinput_31bf3856ad364e35_10.0.19041.7725_none_ebae64e35c070635\GameInput.dll' }
    @{ Proj = 'C:\Windows\System32\GameInputInbox.dll'
       Payload = 'C:\Windows\WinSxS\amd64_microsoft-onecore-gameinputinbox_31bf3856ad364e35_10.0.19041.7725_none_f96e731c6b0b0a4d\GameInputInbox.dll' }
    @{ Proj = 'C:\Windows\SysWOW64\GameInput.dll'
       Payload = 'C:\Windows\WinSxS\wow64_microsoft-onecore-gameinput_31bf3856ad364e35_10.0.19041.7663_none_f6120dd9905bde59\GameInput.dll' }
)

#endregion

#region ── 诊断 ──────────────────────────────────────────────────

function Test-Projection {
    <#  返回一个对象的数组，描述每个投影是否与组件存储正本一致。
        判据：FileId 相同 = 真正的硬链接；大小相同但 FileId 不同 = 独立副本；
        0 字节 = 墓碑（损坏）；不存在 = 投影缺失。 #>
    param([Parameter(Mandatory)][array]$Map)

    $rows = foreach ($m in $Map) {
        $proj = $m.Proj
        $pay  = $m.Payload

        $payExists = Test-Exists $pay
        $paySize   = if ($payExists) { (Get-Item -LiteralPath $pay -Force).Length } else { -1 }

        $projExists = Test-Exists $proj
        $projSize   = if ($projExists) { (Get-Item -LiteralPath $proj -Force).Length } else { -1 }

        $state = 'UNKNOWN'
        if (-not $payExists) {
            $state = 'PAYLOAD_MISSING'
        } elseif (-not $projExists) {
            $state = 'PROJECTION_MISSING'
        } elseif ($projSize -eq 0) {
            $state = 'TOMBSTONE'
        } else {
            $idP = Get-FileId $proj
            $idL = Get-FileId $pay
            if ($idP -and $idL -and $idP -eq $idL) { $state = 'LINKED' }
            elseif ($projSize -eq $paySize)        { $state = 'COPY' }
            else                                   { $state = 'SIZE_MISMATCH' }
        }

        [pscustomobject]@{
            Projection = $proj
            Payload    = $pay
            ProjSize   = $projSize
            PayloadSize= $paySize
            State      = $state
        }
    }
    return @($rows)
}

function Invoke-Diagnose {
    Write-Log '── 诊断：组件存储投影一致性 ──' -Level 'HEAD'

    # 正本是否健在
    $payOk = $false
    foreach ($p in @($Payload7725, $Payload7663)) {
        if (Test-Exists $p) {
            $sz = (Get-Item -LiteralPath $p -Force).Length
            Add-Result 'Payload' $p 'OK' "$sz 字节"
            if ($sz -gt 0) { $payOk = $true }
        } else {
            Add-Result 'Payload' $p 'SKIP' '不存在（可能已被清理）'
        }
    }

    if (-not $payOk) {
        Add-Result 'Payload' '全部正本' 'FAIL' '组件存储中没有可用正本，无法修复'
    }

    # 主目标
    $target = Test-Projection -Map @(@{ Proj = $Projection; Payload = $Payload7725 })
    $t = $target[0]

    switch ($t.State) {
        'LINKED' {
            Add-Result 'Check' $Projection 'OK' "已是正确硬链接（$($t.ProjSize) 字节）"
        }
        'TOMBSTONE' {
            Add-Result 'Check' $Projection 'WARN' `
                "0 字节墓碑 —— 本应是 $($t.PayloadSize) 字节的硬链接投影"
        }
        'PROJECTION_MISSING' {
            Add-Result 'Check' $Projection 'WARN' '投影缺失'
        }
        'COPY' {
            Add-Result 'Check' $Projection 'WARN' `
                "内容正确但是独立副本（非硬链接），大小 $($t.ProjSize)"
        }
        'SIZE_MISMATCH' {
            Add-Result 'Check' $Projection 'FAIL' `
                "大小不符：投影 $($t.ProjSize) vs 正本 $($t.PayloadSize)"
        }
        default {
            Add-Result 'Check' $Projection 'FAIL' "状态 $($t.State)"
        }
    }

    # 其它三个投影对账
    foreach ($r in (Test-Projection -Map $OtherProjections)) {
        $name = Split-Path $r.Projection -Leaf
        switch ($r.State) {
            'LINKED' { Add-Result 'Check' $r.Projection 'OK' "硬链接正常（$($r.ProjSize) 字节）" }
            'COPY'   { Add-Result 'Check' $r.Projection 'WARN' '是独立副本而非硬链接' }
            default  { Add-Result 'Check' $r.Projection 'FAIL' "状态 $($r.State)" }
        }
    }

    # CBS 是否已记录损坏
    $cbs = 'C:\Windows\Logs\CBS\CBS.log'
    if (Test-Path -LiteralPath $cbs) {
        try {
            $n = @(Select-String -LiteralPath $cbs -Pattern 'gameinput' -SimpleMatch -ErrorAction Stop).Count
            if ($n -eq 0) {
                Add-Result 'CBS' 'CBS.log' 'OK' '无 GameInput 相关记录（Windows 未标记损坏）'
            } else {
                Add-Result 'CBS' 'CBS.log' 'WARN' "有 $n 条 GameInput 记录，建议查看"
            }
        } catch {
            Add-Result 'CBS' 'CBS.log' 'SKIP' "读取失败: $($_.Exception.Message)"
        }
    } else {
        Add-Result 'CBS' 'CBS.log' 'SKIP' '日志不存在'
    }

    # 服务层状态：确认修复文件不会让服务跑起来
    if (Test-Path -LiteralPath $SvcKey) {
        $st = (Get-ItemProperty -LiteralPath $SvcKey -ErrorAction SilentlyContinue).Start
        $hasTrig = Test-Path -LiteralPath (Join-Path $SvcKey 'TriggerInfo')
        Add-Result 'Service' $SvcName 'SKIP' "Start=$st (4=Disabled)  TriggerInfo=$hasTrig"
        if ($hasTrig) {
            Add-Result 'Service' 'TriggerInfo' 'WARN' '触发器仍在 —— 修复文件后服务可能被设备事件拉起，建议先跑 Remove-GameInput.ps1 -Mode Soft'
        }
    } else {
        Add-Result 'Service' $SvcName 'OK' '服务注册项已不存在'
    }

    return $t
}

#endregion

#region ── 修复 ──────────────────────────────────────────────────

function Remove-Tombstone {
    <#  删掉 0 字节墓碑。仅当确认是 0 字节文件时才动手，
        避免误删真实二进制。 #>
    param([Parameter(Mandatory)][string]$Path)

    $fi = Get-Item -LiteralPath $Path -Force
    if ($fi.PSIsContainer) {
        Add-Result 'Remove' $Path 'FAIL' '是目录，拒绝删除'
        return $false
    }
    if ($fi.Length -ne 0) {
        Add-Result 'Remove' $Path 'FAIL' "不是 0 字节（$($fi.Length) 字节），拒绝删除以免误删真实文件"
        return $false
    }

    try {
        Remove-Item -LiteralPath $Path -Force -ErrorAction Stop
        Add-Result 'Remove' $Path 'OK' '已删除 0 字节墓碑'
        return $true
    } catch {
        $first = $_.Exception.Message
    }

    Write-Log "  直接删除失败，取得所有权后重试" -Level 'INFO'
    & takeown.exe /F "$Path" /A 2>&1 | Out-Null
    & icacls.exe "$Path" /grant '*S-1-5-32-544:(F)' 2>&1 | Out-Null
    & attrib.exe -R -S -H "$Path" 2>&1 | Out-Null

    try {
        Remove-Item -LiteralPath $Path -Force -ErrorAction Stop
        Add-Result 'Remove' $Path 'OK' '取得所有权后删除成功'
        return $true
    } catch {
        Add-Result 'Remove' $Path 'FAIL' "删除失败: $first / $($_.Exception.Message)"
        return $false
    }
}

function New-HardlinkProjection {
    <#  用 mklink /H 重建硬链接投影，随后从同组件的健康投影复制 ACL，
        使属主恢复为 NT SERVICE\TrustedInstaller，与 Windows 原生状态一致。 #>
    param(
        [Parameter(Mandatory)][string]$Link,
        [Parameter(Mandatory)][string]$Target
    )

    # mklink 是 cmd 内建命令，必须经 cmd /c 调用
    $out = & cmd.exe /c "mklink /H `"$Link`" `"$Target`"" 2>&1
    if ($LASTEXITCODE -ne 0) {
        Add-Result 'Link' $Link 'FAIL' "mklink 失败: $($out -join ' ')"
        return $false
    }

    $newSize = (Get-Item -LiteralPath $Link -Force).Length
    Add-Result 'Link' $Link 'OK' "已重建硬链接 → 正本 ($newSize 字节)"
    return $true
}

function Copy-ReferenceAcl {
    <#  把参照投影的属主与 DACL 原样套到新文件上。
        这样新文件与同组件其它投影的权限完全一致。

        注意：硬链接共享同一条 MFT 记录，属主与 ACL 对全部链接生效，
        因此设置一次即可让 System32 侧与 WinSxS 侧同时正确。 #>
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Reference
    )

    if (-not (Test-Exists $Reference)) {
        Add-Result 'Acl' $Path 'SKIP' "参照文件不存在（$Reference），保留默认权限"
        return
    }

    # ── 属主 + DACL：整体套用参照文件的安全描述符 ──
    # 为什么不用逐条 AddAccessRule：
    #   AccessControlList.AddAccessRule 对同一 SID 是"合并"而非"替换"，
    #   原有残留规则（例如 BU:FA 完全控制）会与参照规则（BU:0x1200a9 读+执行）
    #   求并集后仍是 FA，反而留下一个比原状更宽松的权限。曾实测踩到此坑。
    # 正确做法：把参照文件的完整 SDDL 直接建成新描述符整体套用，
    #   一次性覆盖属主、组与 DACL，不残留任何旧 ACE。
    try {
        $wantSddl = (Get-Acl -LiteralPath $Reference).Sddl
        $beforeS  = (Get-Acl -LiteralPath $Path).Sddl

        if ($beforeS -eq $wantSddl) {
            Add-Result 'Acl' $Path 'OK' '安全描述符已与参照投影一致'
        } else {
            $fs = New-Object System.Security.AccessControl.FileSecurity
            $fs.SetSecurityDescriptorSddlForm($wantSddl)
            Set-Acl -LiteralPath $Path -AclObject $fs -ErrorAction Stop

            $afterS = (Get-Acl -LiteralPath $Path).Sddl
            if ($afterS -eq $wantSddl) {
                Add-Result 'Acl' $Path 'OK' "属主与访问控制列表已整体对齐参照（$(Split-Path $Reference -Leaf)）"
            } else {
                # 逐项报告差异，便于定位是属主还是 DACL 没设上
                $after = Get-Acl -LiteralPath $Path
                $ref   = Get-Acl -LiteralPath $Reference
                $detail = @()
                if ($after.Owner -ne $ref.Owner) { $detail += "属主 $($after.Owner) ≠ $($ref.Owner)" }
                if ($after.Sddl -ne $wantSddl)   { $detail += "DACL 未完全一致" }
                Add-Result 'Acl' $Path 'WARN' ("部分未对齐: " + ($detail -join '; '))
            }
        }
    } catch {
        Add-Result 'Acl' $Path 'WARN' "套用安全描述符失败: $($_.Exception.Message)"
    }
}

function Invoke-RepairProjection {
    param([Parameter(Mandatory)][object]$Target)

    Write-Log '── 修复：重建 GameInputSvc.exe 投影 ──' -Level 'HEAD'

    # 选一个可用的正本：优先 7725（当前生效版本）
    $payload = $null
    foreach ($p in @($Payload7725, $Payload7663)) {
        if (Test-Exists $p) {
            $sz = (Get-Item -LiteralPath $p -Force).Length
            if ($sz -gt 0) { $payload = $p; break }
        }
    }

    if (-not $payload) {
        Add-Result 'Repair' $Projection 'FAIL' '组件存储中没有可用正本，修复中止'
        return $false
    }
    Write-Log "  使用正本: $payload" -Level 'INFO'

    # 已经正确就不要再动
    if ($Target.State -eq 'LINKED') {
        Add-Result 'Repair' $Projection 'SKIP' '投影已正确，无需修复'
        return $true
    }

    # 1) 清掉损坏投影
    if (Test-Exists $Projection) {
        if (-not (Remove-Tombstone -Path $Projection)) { return $false }
    } else {
        Add-Result 'Remove' $Projection 'SKIP' '投影不存在（将由下一步创建）'
    }

    # 2) 重建
    if ($UseSfc) {
        Write-Log '  改用 sfc /scannow，由 Windows 自己恢复该受保护文件（可能需要 5-15 分钟）…' -Level 'WARN'
        $out = & sfc.exe /scannow 2>&1
        $tail = @($out | Select-Object -Last 12)
        $tail | ForEach-Object { Write-Log "    $_" -Level 'INFO' }

        if (-not (Test-Exists $Projection)) {
            Add-Result 'Repair' $Projection 'FAIL' 'sfc 未能恢复该文件；可去掉 -UseSfc 改为手工重建硬链接'
            return $false
        }
        Add-Result 'Repair' $Projection 'OK' 'sfc 已恢复该文件'
    } else {
        if (-not (New-HardlinkProjection -Link $Projection -Target $payload)) { return $false }
    }

    # 3) 权限对齐
    Copy-ReferenceAcl -Path $Projection -Reference $AclReference

    return $true
}

#endregion

#region ── 校验 ──────────────────────────────────────────────────

function Invoke-Verify {
    Write-Log '── 校验：确认修复结果 ──' -Level 'HEAD'

    $t = (Test-Projection -Map @(@{ Proj = $Projection; Payload = $Payload7725 }))[0]

    switch ($t.State) {
        'LINKED' {
            Add-Result 'Verify' $Projection 'OK' `
                "硬链接已建立，FileId 与正本一致（$($t.ProjSize) 字节）"
        }
        'COPY' {
            Add-Result 'Verify' $Projection 'WARN' `
                "内容正确（$($t.ProjSize) 字节）但不是硬链接；组件存储仍会认为投影缺失"
        }
        'TOMBSTONE' {
            Add-Result 'Verify' $Projection 'FAIL' '仍是 0 字节墓碑，修复未生效'
        }
        'PROJECTION_MISSING' {
            Add-Result 'Verify' $Projection 'FAIL' '投影仍缺失'
        }
        default {
            Add-Result 'Verify' $Projection 'FAIL' "状态 $($t.State)"
        }
    }

    # 属主核对
    try {
        $o = (Get-Acl -LiteralPath $Projection).Owner
        $ok = $o -match 'TrustedInstaller'
        Add-Result 'Verify-Owner' $Projection $(if ($ok) { 'OK' } else { 'WARN' }) "属主 = $o"
    } catch {
        Add-Result 'Verify-Owner' $Projection 'SKIP' $_.Exception.Message
    }

    # 服务层：确认没有被这次修复意外启用
    if (Test-Path -LiteralPath $SvcKey) {
        $st = (Get-ItemProperty -LiteralPath $SvcKey -ErrorAction SilentlyContinue).Start
        $hasTrig = Test-Path -LiteralPath (Join-Path $SvcKey 'TriggerInfo')
        $bad = ($st -ne 4) -or $hasTrig
        Add-Result 'Verify-Service' $SvcName $(if ($bad) { 'WARN' } else { 'OK' }) `
            "Start=$st (4=Disabled)  TriggerInfo=$hasTrig"
    } else {
        Add-Result 'Verify-Service' $SvcName 'OK' '服务注册项不存在（保持已移除状态）'
    }
}

function Invoke-DeepCheck {
    Write-Log '── 深度复查：DISM /RestoreHealth ──' -Level 'HEAD'
    Write-Log '  这一步会联网校验组件存储，可能需要 10 分钟以上…' -Level 'WARN'

    $out = & dism.exe /Online /Cleanup-Image /RestoreHealth 2>&1
    $tail = @($out | Select-Object -Last 20)
    $tail | ForEach-Object { Write-Log "    $_" -Level 'INFO' }

    $joined = $out -join ' '
    if ($joined -match '还原操作已成功完成|The restore operation completed successfully') {
        Add-Result 'DISM' 'RestoreHealth' 'OK' '组件存储健康，未发现损坏'
    } elseif ($joined -match '找不到源文件|source files could not be found') {
        Add-Result 'DISM' 'RestoreHealth' 'WARN' '无法访问修复源（需联网或指定 /Source）'
    } else {
        Add-Result 'DISM' 'RestoreHealth' 'SKIP' '见上方 DISM 输出'
    }
}

#endregion

#region ── 主流程 ────────────────────────────────────────────────

function Show-Banner {
    Write-Host ''
    Write-Host '  ╔══════════════════════════════════════════════════════════╗' -ForegroundColor Magenta
    Write-Host '  ║   GameInput 组件存储不一致 —— 定向修复                    ║' -ForegroundColor Magenta
    Write-Host '  ║   Repair-GameInputComponents.ps1                          ║' -ForegroundColor Magenta
    Write-Host '  ╚══════════════════════════════════════════════════════════╝' -ForegroundColor Magenta
    Write-Host ''
    Write-Log "模式: $Mode | 用 sfc: $UseSfc | 已提权: $(Test-Admin)"
    if ($script:LogFile) { Write-Log "日志: $($script:LogFile)" }
}

function Invoke-Summary {
    Write-Host ''
    Write-Host '  ══════════════════ 结果摘要 ══════════════════' -ForegroundColor Magenta
    $script:Results | Format-Table -AutoSize Step, Status, Target, Detail |
        Out-String -Width 200 | Write-Host

    $ok   = @($script:Results | Where-Object Status -eq 'OK').Count
    $warn = @($script:Results | Where-Object Status -eq 'WARN').Count
    $fail = @($script:Results | Where-Object Status -eq 'FAIL').Count
    $skip = @($script:Results | Where-Object Status -eq 'SKIP').Count

    Write-Host "  成功 $ok | 警告 $warn | 失败 $fail | 跳过 $skip" -ForegroundColor Cyan
    if ($script:LogFile) { Write-Host "  完整日志: $($script:LogFile)" -ForegroundColor Gray }
    Write-Host ''
}

function Invoke-Main {
    Show-Banner

    if ($Mode -eq 'Diagnose') {
        [void](Invoke-Diagnose)
        Invoke-Summary
        return 0
    }

    # Repair / Deep 需要管理员
    if (-not (Test-Admin)) {
        Write-Log '修复需要管理员权限。' -Level 'FAIL'
        Write-Log '请右键本脚本 -> 以管理员身份运行，或使用同目录的 Repair-GameInput.cmd。' -Level 'INFO'
        Invoke-Summary
        return 1
    }

    $target = Invoke-Diagnose

    if ($target.State -eq 'LINKED') {
        Write-Log '投影本来就是正确的，无需修复。' -Level 'OK'
        Invoke-Verify
        Invoke-Summary
        return 0
    }

    if (-not (Invoke-RepairProjection -Target $target)) {
        Write-Log '修复未完成。' -Level 'FAIL'
        Invoke-Summary
        return 3
    }

    Invoke-Verify
    if ($Mode -eq 'Deep') { Invoke-DeepCheck }

    Invoke-Summary

    $fail = @($script:Results | Where-Object Status -eq 'FAIL').Count
    if ($fail -gt 0) { return 3 }
    return 0
}

$exitCode = [int](@(Invoke-Main) | Select-Object -Last 1)
exit $exitCode

#endregion
