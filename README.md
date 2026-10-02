# Microsoft GameInput 强制停用 / 删除工具

针对 `GameInputSvc` 服务的提权处置工具。**双击 `GameInputTool.exe` 即可**，程序会自动弹出 UAC 请求管理员权限。

---

## 一、为什么这个服务"删不掉 / 老是回来"

排查你机器上的实际状态后，找到**两个自动拉起机制**——这就是"关掉又自己回来"的直接原因：

| 机制 | 位置 | 作用 |
|---|---|---|
| **TriggerInfo** | `HKLM\SYSTEM\CurrentControlSet\Services\GameInputSvc\TriggerInfo\0` | 触发器 `Type=7`（**设备接口到达**）。插上手柄 / 某个设备接口一出现，服务立刻被拉起 |
| **FailureActions** | 同一服务键下 | 服务失败后**自动重启**（原值配置为 60 秒） |

**只做 `sc stop` 或 `sc config start=disabled` 是不够的**——`Disabled` 只挡住"开机启动"，挡不住触发器拉起。必须把这两个机制摘掉。

此外还发现 **0 字节"墓碑"文件**（同名路径被替换成空文件，属主 `BUILTIN\Administrators`，属性含未文档化的 `0x80000`）：

- `C:\Program Files\Microsoft GameInput`
- `C:\Program Files (x86)\Microsoft Gameinput`

这类痕迹通常由安全软件（你机器上运行着**火绒** `HRWSCCtrl`）的文件保护 / 回滚机制产生。如果删掉后它们又冒出来，请查火绒的防护日志。

### 关于 `C:\Windows\System32\GameInputSvc.exe`（按实际形态归类）

服务主程序这个路径**有过两种形态**，所以程序不把它写死在任何一个列表里，而是运行时按大小判定：

| 实际大小 | 判定 | 归属步骤 |
|---|---|---|
| **非 0 字节** | 正常的服务二进制（`WinSxS` 硬链接投影） | **步骤 4** 删除集 |
| **0 字节** | 被安全软件替换成的墓碑 | **步骤 5** 清理集 |

> 该文件曾是 0 字节墓碑，已用 `Repair-GameInputComponents.ps1` 修复为 55248 字节的正规硬链接（属主 `NT SERVICE\TrustedInstaller`，SDDL 与同组件参照文件一致）。
> **不要**再把它当成墓碑——它是真实的服务二进制。上面表格的判定逻辑已覆盖这两种情况。

---

## 二、用法

### 图形化操作（推荐）

双击 **`GameInputTool.exe`** → UAC 弹窗点"是" → 打开**常驻日志窗口**。

窗口打开时会自动跑一次只读的 `Audit`，日志实时显示在窗口里，**不会一闪而过**。窗口顶部有模式按钮和选项，底部状态栏显示本次运行的成功/警告/失败计数与日志文件路径。

| 按钮 | 说明 |
|---|---|
| **审计（只读）** | 只检测不修改，随便点 |
| **可逆停用** | 摘触发器 + 停止 + `Disabled`，保留服务注册项 |
| **彻底删除** | 服务层 + 删除 SCM 注册项 + 删除 DLL |
| **全部清理** | 彻底删除 + 清理 0 字节墓碑 |
| **监视复活** | 监视 N 分钟（默认 5，可调 1–1440） |
| **停止** | 运行中可点，1 秒内响应；`Watch` 会报告真正监视了多久 |

其余按钮：`清空窗口`、`打开日志`（记事本打开本次日志）、`日志文件夹`、`复制全部`、`关闭`。
选项（勾选后对**新点开的**运行生效）：`清理 0 字节墓碑`、`含 WinSxS（不推荐）`、`跳过注册表备份`、`跳过确认`。

**安全性**：任何会修改系统的模式（`Soft` / `Full` / `Nuclear`）都会先弹出确认框，**必须手动输入大写的 `YES`** 才能点"继续"——避免误点。`Audit` 和 `Watch` 不修改系统，不会弹确认。

![窗口外观](window-preview.png)

> 窗口模式固定走 `requireAdministrator`（双击即提权），因此不会在窗口里出现"权限不足"。如果在窗口里点 `Soft` 却看到 `[FAIL] 未以管理员身份运行`，说明用 `asInvoker` 清单重新编译过。
>
> 控制台窗口在 GUI 模式下会被自动隐藏（`ShowWindow(GetConsoleWindow(), SW_HIDE)`），只留日志窗口，不会出现两个窗口。

### 命令行

命令行方式完全保留，行为不变：

```
GameInputTool.exe [模式] [选项]
```

| 模式 | 作用 | 可逆性 |
|---|---|---|
| `Audit` | **只检测不修改**（默认） | — |
| `Soft` | 摘触发器 + 停止 + 设为 `Disabled`，**保留服务注册项** | ✅ 完全可逆 |
| `Full` | `Soft` + 删除 SCM 服务注册项 + 删除 DLL | ⚠️ 需备份 |
| `Nuclear` | `Full` + 清理 0 字节墓碑 | ⚠️ 需备份 |
| `Watch` | 监视 N 分钟，验证服务是否真的不复活了 | — |

| 选项 | 说明 |
|---|---|
| `-IncludeTombstones` | 清理 0 字节墓碑残留 |
| `-IncludeWinSxS` | ⚠️ **不推荐**，见下方说明 |
| `-NoBackup` | 跳过注册表导出备份 |
| `-Force` | 跳过交互确认 |
| `-WatchMinutes N` | `Watch` 模式监视时长（分钟，默认 5） |

示例：

```powershell
.\GameInputTool.exe                          # 审计
.\GameInputTool.exe Soft                     # 可逆停用（先跑这个）
.\GameInputTool.exe Full -IncludeTombstones  # 彻底删除
.\GameInputTool.exe Watch -WatchMinutes 10   # 验证不再复活
```

---

## 三、典型流程

```powershell
# 1. 先看现状
.\GameInputTool.exe -Mode Audit

# 2. 可逆停用 —— 如果只是不想让它自动起来，到这一步就够了
.\GameInputTool.exe -Mode Soft

# 3. 关键一步：插拔手柄 / 启动游戏，验证它不会复活
.\GameInputTool.exe -Mode Watch -WatchMinutes 10

# 4. 确认没问题后，再彻底删除
.\GameInputTool.exe -Mode Full -IncludeTombstones
```

> **建议先跑 `Soft` + `Watch`。** 如果 `Watch` 显示服务不复活，说明处置已经生效，此时是否还要删 DLL 由你决定——删掉后手柄相关功能会走其他路径。

---

## 四、删除 DLL 的代价（`Full` / `Nuclear` 才涉及）

删除以下文件会让**使用 GameInput API 的游戏失去对应支持**（多数游戏会回退到 XInput，通常仍能用，但不再享受新 API 的低延迟特性）：

- `C:\Windows\System32\GameInput.dll`（109,552 字节）
- `C:\Windows\System32\GameInputInbox.dll`（393,160 字节）
- `C:\Windows\SysWOW64\GameInput.dll`（90,072 字节，32 位版本）
- `C:\Windows\System32\GameInputSvc.exe`（55,248 字节，服务主程序；**仅当它是非 0 字节的真二进制时**才纳入删除）

**这三个文件与 `WinSxS` 组件存储是硬链接关系**（`fsutil hardlink list` 各显示 2 个链接）：

- 只删 `System32` 侧 → `WinSxS` 仍持有数据，程序无法被正常加载，但**可以回滚**
- 连 `WinSxS` 一起删（`-IncludeWinSxS`）→ **不推荐**。`WinSxS` 是受保护的组件存储，单独删链接会让组件清单与实际文件不一致，导致 `sfc /scannow` 和 `DISM` 报错**且无法自动修复**

所以默认行为是：**只删 `System32`/`SysWOW64` 侧，保留 `WinSxS` 作为可回滚副本**。同理，`WinSxS\Manifests` 下的 5 个组件清单也不会被删除，只会提示。

### 权限问题

这些 DLL 属主是 `NT SERVICE\TrustedInstaller`，`Administrators` 组只有读/执行权限（`0x1200a9`），**没有删除权限**。因此程序在删除失败时会自动执行：

1. `takeown /F <路径> /A` → 取得所有权
2. `icacls <路径> /grant *S-1-5-32-544:(F)` → 授予 Administrators 完全控制
3. `attrib -R -S -H <路径>` → 清掉只读/系统/隐藏属性
4. 重试删除；若文件被占用，则登记到 `PendingFileRenameOperations`，**重启后自动删除**

---

## 五、回滚

`Soft` 模式随时可以恢复：

```powershell
sc config GameInputSvc start= demand
sc start  GameInputSvc
```

`Full` / `Nuclear` 需从备份恢复（程序执行前会自动导出注册表到 `backup\`）：

```powershell
reg import .\backup\GameInputSvc-<时间戳>.reg
```

DLL 的回滚：因为默认保留了 `WinSxS` 副本，可执行 `sfc /scannow` 让系统从组件存储重建链接；或先建系统还原点回滚。

---

## 六、文件清单

| 文件 | 说明 |
|---|---|
| **`GameInputTool.exe`** | **主程序**（C#，44 KB，已内嵌 `requireAdministrator` 清单，双击自动提权并打开日志窗口） |
| `GameInputTool.cs` | 源代码（主逻辑、命令行、输出路由） |
| `GuiForm.cs` | 常驻日志窗口的实现（WinForms） |
| `app.manifest` | UAC 清单（`requireAdministrator`） |
| `Remove-GameInput.ps1` | PowerShell 版本（功能等价，供审阅/二次开发） |
| `Run-Elevated.cmd` | PowerShell 版的双击启动器 |
| `Invoke-Elevated.ps1` | UAC 提权辅助脚本 |
| `Repair-GameInputComponents.ps1` | 组件存储投影诊断/修复（见第八节） |
| `Repair-GameInput.cmd` | 上者的双击启动器 |
| `window-preview.png` | 窗口外观截图 |
| `logs\` | 每次运行的时间戳日志 |
| `backup\` | 注册表导出备份 |

### 重新编译

无需 Visual Studio。窗口用了 WinForms，所以要**同时编译两个 `.cs`** 并额外引用两个程序集：

```powershell
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe `
  /nologo /target:exe /platform:anycpu /optimize+ /out:GameInputTool.exe `
  /win32manifest:app.manifest `
  /reference:System.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll `
  GameInputTool.cs GuiForm.cs
```

> 只编译 `GameInputTool.cs` 会报 `CS0103: 不存在名称"Application"`。

**想调试又不想每次都弹 UAC**：把 `app.manifest` 里改成 `level="asInvoker"`，另存一份清单编译成测试副本即可（窗口、日志、只读的 `Audit` / `Watch` 都能正常用；`Soft` / `Full` / `Nuclear` 会被预检挡下并提示需要管理员）。

---

## 七、注意事项

- **程序的"强制关闭"** = 先 `ControlService(STOP)` 温和停止并等待 15 秒；超时则通过 `SERVICE_STATUS_PROCESS.dwProcessId` 拿到宿主进程 PID 并 `Process.Kill()` 强杀。
- 如果删除失败，**先临时关闭火绒的"文件实时监控"和"自我保护"**，再重试。
- 程序**不会**动 `GamingServices` / `GamingServicesNet` 服务（那是 Xbox/Gaming Services，与 GameInput 无关）——已确认它们仍在运行且未被修改。
- 做 `Full` / `Nuclear` 前建议**先创建系统还原点**。

---

## 八、组件存储修复（可选，已完成）

`Repair-GameInputComponents.ps1` 用于处理 `System32` 投影与 `WinSxS` 组件存储之间的不一致（投影被替换成 0 字节墓碑时）。**当前机器已修复完毕**，日常无需再跑。

| 模式 | 作用 |
|---|---|
| `Diagnose` | **只诊断**（默认，不需要管理员） |
| `Repair` | 重建硬链接投影并对齐属主 / ACL |
| `Deep` | 额外跑 `dism /Cleanup-Image /RestoreHealth` |

```powershell
# 只诊断
.\Repair-GameInput.cmd

# 修复（会弹 UAC）
.\Repair-GameInput.cmd Repair
```

修复后的正确形态（已核验）：

| 项目 | 期望值 |
|---|---|
| 大小 | `55248` 字节 |
| FileId | 与 `WinSxS\...gameinputinbox_7725...\GameInputSvc.exe` **一致** |
| 硬链接数 | `3` |
| 属主 | `NT SERVICE\TrustedInstaller` |
| SDDL | 与同组件参照文件 `GameInputInbox.dll` **完全一致** |
| 属性 | `0x20`（不再是墓碑的 `0x80020`） |

修复脚本的两个关键实现细节（踩过的坑）：

1. **属主必须整体套用 SDDL**，不能用 `AddAccessRule` 逐条合并——`AddAccessRule` 是**求并集**，若目标残留 `BU:FA`（完全控制）而参照是 `BU:0x1200a9`（读+执行），合并结果是 `FA`，**权限比原来更宽松**。正确做法是 `FileSecurity.SetSecurityDescriptorSddlForm($参照SDDL)` + `Set-Acl` 整体覆盖。
2. **`.NET` 的 `Acl.SetOwner()` 不接受字符串**，传 `"NT SERVICE\TrustedInstaller"` 会抛 `IdentityReference` 转换异常；改用整体 SDDL 后这个问题一并消失（`icacls /setowner` 只在已提权时可用，`ERROR_INVALID_OWNER 1307` 是非提权下的必然结果）。

