// ============================================================================
//  GameInputTool - Microsoft GameInput 服务强制停用 / 删除工具
//
//  编译（无需 Visual Studio）：
//    C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe ^
//      /target:exe /platform:anycpu /optimize+ /out:GameInputTool.exe ^
//      /win32manifest:app.manifest ^
//      /reference:System.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll ^
//      GameInputTool.cs GuiForm.cs
//
//  双击运行会自动弹出 UAC（manifest 声明 requireAdministrator）。
//
//  启动方式：
//    无参数        打开常驻日志窗口（推荐；窗口不会自动关闭）
//    -Mode <模式>  命令行模式（传统用法，行为与以前一致）
//
//  模式：
//    Audit   只检测不修改
//    Soft    停用 + 摘除触发器（保留服务注册项，完全可逆）
//    Full    服务层 + 删除 DLL
//    Nuclear 全部清理（含 0 字节墓碑）
//    Watch   监视 N 分钟，检验服务是否会复活
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using System.Windows.Forms;

namespace GameInputTool
{
    internal enum Status { Ok, Skip, Warn, Fail }

    internal sealed class Result
    {
        public string Step, Target, Detail;
        public Status Level;
    }

    internal static class Program
    {
        // ── 关键常量 ────────────────────────────────────────────────────
        private const string SvcName = "GameInputSvc";
        private const string SvcKeyPath = @"SYSTEM\CurrentControlSet\Services\" + SvcName;
        private const string SessionMgrPath = @"SYSTEM\CurrentControlSet\Control\Session Manager";

        private static readonly string[] TargetFiles =
        {
            @"C:\Windows\System32\GameInput.dll",
            @"C:\Windows\System32\GameInputInbox.dll",
            @"C:\Windows\SysWOW64\GameInput.dll",
        };

        // 服务主程序。两种形态必须动态归类：
        //   非 0 字节 = 正常的服务二进制（WinSxS 硬链接投影）→ 步骤 4 删除
        //   0 字节    = 被安全软件替换成的墓碑（属性含未文档化 0x80000）→ 步骤 5 清理
        private const string SvcBinary = @"C:\Windows\System32\GameInputSvc.exe";

        // 固定为 0 字节"墓碑"的残留路径：同名路径被替换成空文件，属主 BUILTIN\Administrators，
        // 属性带未文档化的 0x80000，是安全软件（火绒）的文件保护产物。
        // 这两个路径不是服务二进制，不存在"真实文件"形态。
        private static readonly string[] Tombstones =
        {
            @"C:\Program Files\Microsoft GameInput",
            @"C:\Program Files (x86)\Microsoft Gameinput",
        };

        private static readonly List<Result> Results = new List<Result>();
        private static string _logFile;
        private static string _mode = "Audit";
        private static bool _includeTombstones, _includeWinSxS, _noBackup, _force;
        private static int _watchMinutes = 5;
        private static string _backupDir;
        private static string _logDir;

        // ── GUI 支持 ────────────────────────────────────────────────────
        // _gui 为真时：输出同时镜像到窗口；确认改用对话框；不再阻塞等回车。
        private static bool _gui;
        private static GuiForm _form;
        // 用户点“停止”后置位；长循环（Watch）会检查它并提前退出。
        private static volatile bool _cancelRequested;

        /// <summary>当前日志文件的完整路径（供 GUI 显示/打开）。</summary>
        internal static string CurrentLogFile { get { return _logFile; } }

        /// <summary>日志目录（供 GUI 打开文件夹）。</summary>
        internal static string LogDir { get { return _logDir; } }

        /// <summary>已登记的结果集合（供 GUI 汇总）。</summary>
        internal static List<Result> AllResults { get { return Results; } }

        /// <summary>请求停止当前操作。Watch 循环等长任务会尽快退出。</summary>
        internal static void RequestCancel() { _cancelRequested = true; }

        // ════════════════════════════════════════════════════════════════
        //  输出通道
        //  所有控制台输出都必须经这里，才能在 GUI 模式下同步进窗口。
        // ════════════════════════════════════════════════════════════════

        /// <summary>带颜色输出一行（GUI 模式下同时镜像到窗口）。</summary>
        internal static void Out(string text, ConsoleColor color)
        {
            try
            {
                Console.ForegroundColor = color;
                Console.WriteLine(text);
                Console.ResetColor();
            }
            catch { /* 控制台不可用时（GUI 子系统）忽略 */ }

            if (_gui && _form != null) _form.AppendText(text + Environment.NewLine, color);
        }

        /// <summary>无颜色输出一行。</summary>
        private static void Out(string text) { Out(text, ConsoleColor.Gray); }

        /// <summary>输出空行。</summary>
        private static void Out() { Out("", ConsoleColor.Gray); }

        // ════════════════════════════════════════════════════════════════
        //  P/Invoke - 服务控制
        // ════════════════════════════════════════════════════════════════
        [StructLayout(LayoutKind.Sequential)]
        private struct SERVICE_STATUS
        {
            public uint dwServiceType, dwCurrentState, dwControlsAccepted,
                        dwWin32ExitCode, dwServiceSpecificExitCode,
                        dwCheckPoint, dwWaitHint;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SERVICE_STATUS_PROCESS
        {
            public uint dwServiceType, dwCurrentState, dwControlsAccepted,
                        dwWin32ExitCode, dwServiceSpecificExitCode,
                        dwCheckPoint, dwWaitHint, dwProcessId, dwServiceFlags;
        }

        private const uint SC_MANAGER_CONNECT = 0x0001;
        private const uint SERVICE_QUERY_STATUS = 0x0004;
        private const uint SERVICE_STOP = 0x0020;
        private const uint SERVICE_CHANGE_CONFIG = 0x0002;
        private const uint SERVICE_START = 0x0010;
        private const uint SERVICE_CONTROL_STOP = 0x00000001;
        private const uint SERVICE_DISABLED = 0x00000004;
        private const int SC_STATUS_PROCESS_INFO = 0;

        private const uint SERVICE_STOPPED = 0x0001;
        private const uint SERVICE_START_PENDING = 0x0002;
        private const uint SERVICE_STOP_PENDING = 0x0003;
        private const uint SERVICE_RUNNING = 0x0004;

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr OpenSCManager(string machine, string database, uint access);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr OpenService(IntPtr hSCM, string name, uint access);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool QueryServiceStatus(IntPtr hSvc, ref SERVICE_STATUS st);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool QueryServiceStatusEx(IntPtr hSvc, int level,
            ref SERVICE_STATUS_PROCESS buf, int cbBufSize, out int needed);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool ControlService(IntPtr hSvc, uint ctrl, ref SERVICE_STATUS st);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool DeleteService(IntPtr hSvc);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool ChangeServiceConfig(IntPtr hSvc, uint type, uint startType,
            int errCtrl, string path, string loadOrderGroup, IntPtr tagId,
            string dependencies, string account, string password, string displayName);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool StartService(IntPtr hSvc, int numArgs, IntPtr args);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool CloseServiceHandle(IntPtr h);

        // ════════════════════════════════════════════════════════════════
        //  日志 / 结果
        // ════════════════════════════════════════════════════════════════
        private static void WriteLog(string msg, Status? level = null)
        {
            ConsoleColor c = ConsoleColor.Gray;
            string tag = "INFO";
            if (level.HasValue)
            {
                switch (level.Value)
                {
                    case Status.Ok:   c = ConsoleColor.Green;  tag = "OK  "; break;
                    case Status.Skip: c = ConsoleColor.DarkGray; tag = "SKIP"; break;
                    case Status.Warn: c = ConsoleColor.Yellow; tag = "WARN"; break;
                    case Status.Fail: c = ConsoleColor.Red;    tag = "FAIL"; break;
                }
            }
            Out(string.Format("[{0}] [{1}] {2}", DateTime.Now.ToString("HH:mm:ss"), tag, msg), c);

            if (_logFile != null)
            {
                try { File.AppendAllText(_logFile,
                    string.Format("[{0}] [{1}] {2}\r\n", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), tag, msg),
                    Encoding.UTF8); }
                catch { /* 日志失败不影响主流程 */ }
            }
        }

        private static void AddResult(string step, string target, Status lvl, string detail)
        {
            Results.Add(new Result { Step = step, Target = target, Detail = detail, Level = lvl });
            WriteLog(string.Format("{0} :: {1} :: {2}", step, target, detail), lvl);
        }

        // ════════════════════════════════════════════════════════════════
        //  基础设施
        // ════════════════════════════════════════════════════════════════
        private static bool IsAdmin()
        {
            try
            {
                using (var id = WindowsIdentity.GetCurrent())
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        /// <summary>Existence test that treats a 0-byte file as existing.</summary>
        private static bool Exists(string path)
        {
            if (File.Exists(path)) return true;
            try { return (File.GetAttributes(path) & FileAttributes.Directory) == 0; }
            catch { return false; }
        }

        private static string RunTool(string exe, string args, out int exitCode)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                using (var p = Process.Start(psi))
                {
                    string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                    p.WaitForExit(60000);
                    exitCode = p.ExitCode;
                    return o.Trim();
                }
            }
            catch (Exception ex) { exitCode = -1; return ex.Message; }
        }

        private static string GetRegValueString(string subKey, string name)
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(subKey))
                {
                    if (k == null) return null;
                    object v = k.GetValue(name);
                    return v == null ? null : v.ToString();
                }
            }
            catch { return null; }
        }

        // ════════════════════════════════════════════════════════════════
        //  服务状态
        // ════════════════════════════════════════════════════════════════
        private sealed class SvcInfo
        {
            public bool Registered;
            public uint State;         // SERVICE_*
            public uint ProcessId;
            public uint Win32Exit;
            public int RegStart = -1;  // Start 值
        }

        private static SvcInfo GetSvcInfo()
        {
            var info = new SvcInfo();
            info.RegStart = ReadStartValue();

            IntPtr scm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
            if (scm == IntPtr.Zero) return info;
            try
            {
                IntPtr h = OpenService(scm, SvcName, SERVICE_QUERY_STATUS);
                if (h == IntPtr.Zero) return info;   // ERROR_SERVICE_DOES_NOT_EXIST
                try
                {
                    info.Registered = true;
                    var ssp = new SERVICE_STATUS_PROCESS();
                    int needed;
                    if (QueryServiceStatusEx(h, SC_STATUS_PROCESS_INFO, ref ssp,
                            Marshal.SizeOf(typeof(SERVICE_STATUS_PROCESS)), out needed))
                    {
                        info.State = ssp.dwCurrentState;
                        info.ProcessId = ssp.dwProcessId;
                        info.Win32Exit = ssp.dwWin32ExitCode;
                    }
                }
                finally { CloseServiceHandle(h); }
            }
            finally { CloseServiceHandle(scm); }
            return info;
        }

        private static int ReadStartValue()
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(SvcKeyPath))
                {
                    if (k == null) return -1;
                    object v = k.GetValue("Start");
                    return v == null ? -1 : Convert.ToInt32(v);
                }
            }
            catch { return -1; }
        }

        private static string StateName(uint s)
        {
            switch (s)
            {
                case SERVICE_STOPPED: return "Stopped";
                case SERVICE_START_PENDING: return "StartPending";
                case SERVICE_STOP_PENDING: return "StopPending";
                case SERVICE_RUNNING: return "Running";
                default: return "0x" + s.ToString("X");
            }
        }

        private static string StartTypeName(int v)
        {
            switch (v)
            {
                case 0: return "Boot";
                case 1: return "System";
                case 2: return "Automatic";
                case 3: return "Manual";
                case 4: return "Disabled";
                default: return v < 0 ? "(无注册项)" : "?" + v;
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  步骤 1：摘除自动拉起机制
        // ════════════════════════════════════════════════════════════════
        private static void DisableTriggers()
        {
            WriteLog("── 步骤 1：摘除自动拉起机制 ──");

            if (Registry.LocalMachine.OpenSubKey(SvcKeyPath) == null)
            {
                AddResult("1-SvcKey", SvcKeyPath, Status.Skip, "服务注册项不存在");
                return;
            }

            // FailureActions：失败后 60 秒自动重启
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(SvcKeyPath, true))
                {
                    if (k.GetValue("FailureActions") != null)
                    {
                        k.DeleteValue("FailureActions", false);
                        AddResult("1-FailureActions", "FailureActions", Status.Ok, "已移除失败自动重启");
                    }
                    else
                    {
                        AddResult("1-FailureActions", "FailureActions", Status.Skip, "无此值");
                    }

                    if (k.GetValue("DelayedAutostart") != null)
                    {
                        k.DeleteValue("DelayedAutostart", false);
                        AddResult("1-DelayedAutostart", "DelayedAutostart", Status.Ok, "已移除");
                    }
                }
            }
            catch (Exception ex)
            {
                AddResult("1-FailureActions", "FailureActions", Status.Fail, ex.Message);
            }

            // TriggerInfo：类型 7 = 设备接口到达即启动。
            // 这是"插手柄/游戏一运行服务就回来"的直接原因。
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(SvcKeyPath, true))
                {
                    if (k != null && k.OpenSubKey("TriggerInfo") != null)
                    {
                        int n = k.OpenSubKey("TriggerInfo").GetSubKeyNames().Length;
                        k.DeleteSubKeyTree("TriggerInfo", false);
                        AddResult("1-TriggerInfo", @"...\TriggerInfo", Status.Ok,
                            string.Format("已移除（含 {0} 个触发器）", n));
                    }
                    else
                    {
                        AddResult("1-TriggerInfo", @"...\TriggerInfo", Status.Skip, "无此子项");
                    }
                }
            }
            catch (Exception ex)
            {
                AddResult("1-TriggerInfo", @"...\TriggerInfo", Status.Fail, ex.Message);
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  步骤 2：强制停止 + 禁用
        // ════════════════════════════════════════════════════════════════
        private static void StopAndDisable()
        {
            WriteLog("── 步骤 2：强制停止服务并设为禁用 ──");

            var info = GetSvcInfo();
            if (!info.Registered)
            {
                AddResult("2-Stop", SvcName, Status.Skip, "服务未注册（无需停止）");
            }
            else if (info.State == SERVICE_STOPPED)
            {
                AddResult("2-Stop", SvcName, Status.Skip,
                    string.Format("已是 Stopped（Win32Exit={0}）", info.Win32Exit));
            }
            else
            {
                StopServiceForcefully(info);
            }

            // 设为 Disabled（Start=4）
            IntPtr scm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
            if (scm == IntPtr.Zero)
            {
                AddResult("2-Disable", SvcName, Status.Fail,
                    "无法连接服务控制管理器, 错误 " + Marshal.GetLastWin32Error());
            }
            else
            {
                try
                {
                    IntPtr h = OpenService(scm, SvcName, SERVICE_CHANGE_CONFIG);
                    if (h == IntPtr.Zero)
                    {
                        AddResult("2-Disable", SvcName, Status.Fail,
                            "打开服务失败, 错误 " + Marshal.GetLastWin32Error());
                    }
                    else
                    {
                        try
                        {
                            if (ChangeServiceConfig(h, 0xFFFFFFFF, SERVICE_DISABLED, -1,
                                    null, null, IntPtr.Zero, null, null, null, null))
                            {
                                AddResult("2-Disable", "Start", Status.Ok, "Start=4 (Disabled)");
                            }
                            else
                            {
                                AddResult("2-Disable", "Start", Status.Fail,
                                    "ChangeServiceConfig 失败, 错误 " + Marshal.GetLastWin32Error());
                            }
                        }
                        finally { CloseServiceHandle(h); }
                    }
                }
                finally { CloseServiceHandle(scm); }
            }
        }

        /// <summary>先温和停止，超时后再强杀宿主进程 —— 这就是"强制关闭"。</summary>
        private static void StopServiceForcefully(SvcInfo info)
        {
            IntPtr scm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
            if (scm == IntPtr.Zero)
            {
                AddResult("2-Stop", SvcName, Status.Fail,
                    "无法连接 SCM, 错误 " + Marshal.GetLastWin32Error());
                return;
            }
            try
            {
                IntPtr h = OpenService(scm, SvcName, SERVICE_STOP | SERVICE_QUERY_STATUS);
                if (h == IntPtr.Zero)
                {
                    AddResult("2-Stop", SvcName, Status.Fail,
                        "打开服务失败, 错误 " + Marshal.GetLastWin32Error());
                    return;
                }
                try
                {
                    var st = new SERVICE_STATUS();
                    ControlService(h, SERVICE_CONTROL_STOP, ref st);
                    WriteLog("  已发送停止请求，等待服务退出…");

                    var sw = Stopwatch.StartNew();
                    while (sw.ElapsedMilliseconds < 15000)
                    {
                        System.Threading.Thread.Sleep(400);
                        var ssp = new SERVICE_STATUS_PROCESS();
                        int needed;
                        if (!QueryServiceStatusEx(h, SC_STATUS_PROCESS_INFO, ref ssp,
                                Marshal.SizeOf(typeof(SERVICE_STATUS_PROCESS)), out needed))
                            break;
                        if (ssp.dwCurrentState == SERVICE_STOPPED)
                        {
                            AddResult("2-Stop", SvcName, Status.Ok,
                                string.Format("已停止（耗时 {0:F1}s）", sw.Elapsed.TotalSeconds));
                            return;
                        }
                    }

                    // 温和停止失败 → 强杀进程
                    WriteLog("  服务未在 15 秒内停止，转为强制结束进程。", Status.Warn);
                    ForceKillPid(info.ProcessId, h);
                }
                finally { CloseServiceHandle(h); }
            }
            finally { CloseServiceHandle(scm); }
        }

        private static void ForceKillPid(uint pid, IntPtr hSvc)
        {
            if (pid == 0)
            {
                AddResult("2-Stop", SvcName, Status.Fail,
                    "停止超时，且服务未暴露进程 ID（可能从未真正启动）");
                return;
            }
            try
            {
                using (var p = Process.GetProcessById((int)pid))
                {
                    string name = p.ProcessName;
                    p.Kill();
                    p.WaitForExit(10000);
                    AddResult("2-Stop", SvcName, Status.Warn,
                        string.Format("温和停止超时，已强制结束进程 {0} (PID {1})", name, pid));
                }
            }
            catch (Exception ex)
            {
                AddResult("2-Stop", SvcName, Status.Fail,
                    string.Format("强杀 PID {0} 失败: {1}", pid, ex.Message));
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  步骤 3：删除服务注册项
        // ════════════════════════════════════════════════════════════════
        private static void DeleteServiceReg()
        {
            WriteLog("── 步骤 3：删除 SCM 服务注册项 ──");

            if (Registry.LocalMachine.OpenSubKey(SvcKeyPath) == null)
            {
                AddResult("3-DeleteSvc", SvcName, Status.Skip, "注册项已不存在");
                return;
            }

            IntPtr scm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
            if (scm == IntPtr.Zero)
            {
                AddResult("3-DeleteSvc", SvcName, Status.Fail, "无法连接 SCM");
                return;
            }
            try
            {
                IntPtr h = OpenService(scm, SvcName, 0x10000 /*DELETE*/);
                if (h == IntPtr.Zero)
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err == 1060) { AddResult("3-DeleteSvc", SvcName, Status.Skip, "服务不存在"); return; }
                    AddResult("3-DeleteSvc", SvcName, Status.Fail, "打开服务失败, 错误 " + err);
                    return;
                }
                try
                {
                    if (DeleteService(h))
                        AddResult("3-DeleteSvc", SvcName, Status.Ok, "服务已标记删除");
                    else
                    {
                        int err = Marshal.GetLastWin32Error();
                        // 1072 = ERROR_SERVICE_MARKED_FOR_DELETE
                        AddResult("3-DeleteSvc", SvcName,
                            err == 1072 ? Status.Warn : Status.Fail,
                            "DeleteService 失败, 错误 " + err + (err == 1072 ? "（已标记删除，重启后消失）" : ""));
                    }
                }
                finally { CloseServiceHandle(h); }
            }
            finally { CloseServiceHandle(scm); }
        }

        // ════════════════════════════════════════════════════════════════
        //  步骤 4：取得所有权并删除二进制
        // ════════════════════════════════════════════════════════════════
        private static void TakeOwnership(string path)
        {
            int rc;
            RunTool("takeown.exe", "/F \"" + path + "\" /A", out rc);
            RunTool("icacls.exe", "\"" + path + "\" /grant *S-1-5-32-544:(F)", out rc);
            RunTool("attrib.exe", "-R -S -H \"" + path + "\"", out rc);
        }

        private static void RemoveTargetFile(string path)
        {
            if (!Exists(path))
            {
                AddResult("4-DeleteFile", path, Status.Skip, "不存在");
                return;
            }

            string size = "?", owner = "?";
            try
            {
                var fi = new FileInfo(path);
                size = fi.Length.ToString();
                try { owner = fi.GetAccessControl().GetOwner(typeof(NTAccount)).ToString(); }
                catch { }
            }
            catch { }

            // 第一轮：直接删
            string firstErr;
            try
            {
                File.Delete(path);
                AddResult("4-DeleteFile", path, Status.Ok,
                    string.Format("已删除（原大小 {0}, 属主 {1}）", size, owner));
                return;
            }
            catch (Exception ex) { firstErr = ex.Message; }

            // 第二轮：取得所有权后重试
            WriteLog("  取得所有权后重试: " + path);
            TakeOwnership(path);
            try
            {
                File.Delete(path);
                AddResult("4-DeleteFile", path, Status.Ok,
                    string.Format("取得所有权后删除成功（原属主 {0}）", owner));
                return;
            }
            catch (Exception ex)
            {
                // 第三轮：登记重启删除
                if (RegisterPendingDelete(path))
                    AddResult("4-DeleteFile", path, Status.Warn,
                        "文件被占用，已登记重启后删除（" + ex.Message + "）");
                else
                    AddResult("4-DeleteFile", path, Status.Fail,
                        "删除失败: " + firstErr + " / " + ex.Message);
            }
        }

        private static void DeleteBinaries()
        {
            WriteLog("── 步骤 4：删除 GameInput 二进制 ──");

            var all = new List<string>(TargetFiles);

            // 服务主程序按"实际形态"归类：非 0 字节才是真二进制，纳入删除集；
            // 0 字节说明它已被替换成墓碑，交给步骤 5 清理，不在这里删。
            if (Exists(SvcBinary))
            {
                long svcLen = -1;
                try { svcLen = new FileInfo(SvcBinary).Length; }
                catch { }
                if (svcLen > 0) all.Add(SvcBinary);
                else AddResult("4-SvcBinary", SvcBinary, Status.Skip,
                    "0 字节墓碑形态，交给步骤 5 清理（不在步骤 4 删除）");
            }

            var winsxs = new List<string>();

            // 枚举硬链接。WinSxS 是受保护的组件存储：单独删掉它会让
            // 组件清单与实际文件不一致，导致 sfc/DISM 报错且无法自动修复。
            // 默认保留；System32 侧链接消失后程序已无法正常加载。
            foreach (var f in all.ToArray())
            {
                if (!Exists(f)) continue;
                int rc;
                string outp = RunTool("fsutil.exe", "hardlink list \"" + f + "\"", out rc);
                foreach (var raw in outp.Split('\n'))
                {
                    string l = raw.Trim().TrimEnd('\r');
                    if (l.Length == 0 || l[0] != '\\') continue;
                    if (l.StartsWith(@"\Windows\", StringComparison.OrdinalIgnoreCase))
                    {
                        string full = "C:" + l;
                        if (all.Contains(full)) continue;
                        if (full.IndexOf(@"\WinSxS\", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            if (!winsxs.Contains(full)) winsxs.Add(full);
                        }
                        else all.Add(full);
                    }
                }
            }

            if (winsxs.Count > 0)
            {
                if (_includeWinSxS)
                {
                    all.AddRange(winsxs);
                    AddResult("4-WinSxS", @"C:\Windows\WinSxS", Status.Warn,
                        string.Format("已按 -IncludeWinSxS 纳入 {0} 个组件存储链接", winsxs.Count));
                }
                else
                {
                    AddResult("4-WinSxS", @"C:\Windows\WinSxS", Status.Skip,
                        string.Format("保留 {0} 个硬链接源（组件存储受保护；加 -IncludeWinSxS 可强制删除）", winsxs.Count));
                }
            }

            WriteLog(string.Format("  待处理路径 {0} 个", all.Count));
            foreach (var p in all) RemoveTargetFile(p);

            try
            {
                string[] man = Directory.GetFiles(@"C:\Windows\WinSxS\Manifests", "*gameinput*");
                if (man.Length > 0)
                    AddResult("4-Manifests", @"C:\Windows\WinSxS\Manifests", Status.Warn,
                        string.Format("保留 {0} 个组件清单（强删会导致 sfc/DISM 组件存储不一致）", man.Length));
            }
            catch { }
        }

        // ════════════════════════════════════════════════════════════════
        //  步骤 5：清理 0 字节墓碑
        // ════════════════════════════════════════════════════════════════
        private static void CleanTombstones()
        {
            WriteLog("── 步骤 5：清理 0 字节墓碑残留 ──");

            // 候选集 = 固定墓碑路径 + （仅当它确实是 0 字节时才加入）服务主程序路径。
            // 这样修复过的真实二进制绝不会进入本步骤。
            var candidates = new List<string>(Tombstones);
            if (Exists(SvcBinary))
            {
                long svcLen = -1;
                try { svcLen = new FileInfo(SvcBinary).Length; }
                catch { }
                if (svcLen == 0) candidates.Add(SvcBinary);
                else AddResult("5-SvcBinary", SvcBinary, Status.Skip,
                    string.Format("非 0 字节文件 (大小 {0})，是正常的服务二进制，不按墓碑清理", svcLen));
            }

            foreach (var p in candidates)
            {
                if (!Exists(p))
                {
                    AddResult("5-Tombstone", p, Status.Skip, "不存在");
                    continue;
                }

                bool isDir;
                long len = -1;
                try
                {
                    var attr = File.GetAttributes(p);
                    isDir = (attr & FileAttributes.Directory) != 0;
                    if (!isDir) len = new FileInfo(p).Length;
                }
                catch (Exception ex)
                {
                    AddResult("5-Tombstone", p, Status.Fail, "读取属性失败: " + ex.Message);
                    continue;
                }

                // 只删"确认是 0 字节文件"的目标，避免误删真实程序
                if (isDir || len != 0)
                {
                    AddResult("5-Tombstone", p, Status.Skip,
                        isDir ? "是目录，跳过" : string.Format("非 0 字节（{0}），跳过以免误删", len));
                    continue;
                }

                try
                {
                    File.Delete(p);
                    AddResult("5-Tombstone", p, Status.Ok, "已删除（0 字节墓碑）");
                    continue;
                }
                catch { }

                TakeOwnership(p);
                try
                {
                    File.Delete(p);
                    AddResult("5-Tombstone", p, Status.Ok, "取得所有权后删除成功");
                }
                catch (Exception ex)
                {
                    if (RegisterPendingDelete(p))
                        AddResult("5-Tombstone", p, Status.Warn, "已登记重启后删除");
                    else
                        AddResult("5-Tombstone", p, Status.Fail, ex.Message);
                }
            }

            AddResult("5-Note", "火绒(Huorong)", Status.Warn,
                "HRWSCCtrl 正在运行；这些墓碑很可能是它的文件保护产物，删除后若再次出现请查其防护日志");
        }

        // ════════════════════════════════════════════════════════════════
        //  步骤 6：登记重启删除
        // ════════════════════════════════════════════════════════════════
        private static bool RegisterPendingDelete(string path)
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(SessionMgrPath, true))
                {
                    if (k == null) return false;
                    var cur = k.GetValue("PendingFileRenameOperations") as string[];
                    var list = new List<string>(cur ?? new string[0]);

                    // 格式：源路径 + 空目标 = 删除
                    string entry = @"\??\" + path;
                    if (list.Contains(entry)) return true;

                    list.Add(entry);
                    list.Add("");
                    k.SetValue("PendingFileRenameOperations", list.ToArray(), RegistryValueKind.MultiString);
                    WriteLog("  已登记重启删除: " + path, Status.Ok);
                    return true;
                }
            }
            catch (Exception ex)
            {
                WriteLog("  登记重启删除失败 (" + path + "): " + ex.Message, Status.Fail);
                return false;
            }
        }

        private static void PendingCleanup()
        {
            WriteLog("── 步骤 6：重启删除队列确认 ──");
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(SessionMgrPath))
                {
                    var cur = k == null ? null : k.GetValue("PendingFileRenameOperations") as string[];
                    int hits = 0;
                    if (cur != null)
                        foreach (var s in cur)
                            if (s.IndexOf("GameInput", StringComparison.OrdinalIgnoreCase) >= 0) hits++;

                    if (hits > 0)
                    {
                        AddResult("6-Pending", "PendingFileRenameOperations", Status.Ok,
                            string.Format("队列中有 {0} 条 GameInput 相关项，重启后生效", hits));
                        AddResult("6-Reboot", "系统", Status.Warn, "需要重启才能完成清理");
                    }
                    else AddResult("6-Pending", "PendingFileRenameOperations", Status.Skip, "无遗留项");
                }
            }
            catch (Exception ex)
            {
                AddResult("6-Pending", "PendingFileRenameOperations", Status.Warn, ex.Message);
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  复检
        // ════════════════════════════════════════════════════════════════
        private static void PostCheck()
        {
            WriteLog("── 复检 ──");

            var info = GetSvcInfo();
            if (info.Registered)
                AddResult("Post-Service", SvcName, Status.Warn,
                    string.Format("服务仍存在: {0} / {1}", StateName(info.State), StartTypeName(info.RegStart)));
            else
                AddResult("Post-Service", SvcName, Status.Ok,
                    string.Format("服务已注销（注册项 Start={0}）", StartTypeName(info.RegStart)));

            foreach (var f in TargetFiles)
            {
                if (Exists(f)) AddResult("Post-File", f, Status.Warn, "仍存在");
                else AddResult("Post-File", f, Status.Ok, "已清除");
            }

            int n = 0;
            foreach (var p in Process.GetProcesses())
            {
                try { if (p.ProcessName.IndexOf("GameInput", StringComparison.OrdinalIgnoreCase) >= 0) n++; }
                catch { }
                finally { p.Dispose(); }
            }
            AddResult("Post-Process", "GameInput*", n == 0 ? Status.Ok : Status.Warn,
                n == 0 ? "无相关进程" : string.Format("仍有 {0} 个进程", n));
        }

        // ════════════════════════════════════════════════════════════════
        //  审计
        // ════════════════════════════════════════════════════════════════
        private static void Audit()
        {
            WriteLog("── 审计模式：只检测，不修改 ──");

            var info = GetSvcInfo();
            if (info.Registered)
                AddResult("Audit-Service", SvcName, Status.Warn,
                    string.Format("{0} / {1}", StateName(info.State), StartTypeName(info.RegStart)));
            else
                AddResult("Audit-Service", SvcName, Status.Ok, "未注册");

            AddResult("Audit-Start", "Start", Status.Skip,
                string.Format("Start={0} ({1})", info.RegStart, StartTypeName(info.RegStart)));

            bool hasFa = GetRegValueString(SvcKeyPath, "FailureActions") != null;
            AddResult("Audit-FailureActions", "FailureActions", hasFa ? Status.Warn : Status.Ok,
                hasFa ? "存在：失败会自动重启" : "无");

            bool hasTrig;
            using (var k = Registry.LocalMachine.OpenSubKey(SvcKeyPath))
                hasTrig = k != null && k.OpenSubKey("TriggerInfo") != null;
            AddResult("Audit-TriggerInfo", "TriggerInfo", hasTrig ? Status.Warn : Status.Ok,
                hasTrig ? "存在：按设备接口触发启动（插手柄即拉起）" : "无");

            foreach (var f in TargetFiles)
            {
                if (!Exists(f)) { AddResult("Audit-File", f, Status.Ok, "已清除"); continue; }
                int rc;
                string outp = RunTool("fsutil.exe", "hardlink list \"" + f + "\"", out rc);
                int links = 0;
                foreach (var raw in outp.Split('\n'))
                {
                    string l = raw.Trim();
                    if (l.Length > 0 && l[0] == '\\') links++;
                }
                long len = 0;
                try { len = new FileInfo(f).Length; } catch { }
                AddResult("Audit-File", f, Status.Warn,
                    string.Format("存在 大小={0} 硬链接={1}", len, links));
            }

            foreach (var t in Tombstones)
            {
                if (!Exists(t)) { AddResult("Audit-Tombstone", t, Status.Ok, "不存在"); continue; }
                bool isDir;
                long len = 0;
                try
                {
                    var a = File.GetAttributes(t);
                    isDir = (a & FileAttributes.Directory) != 0;
                    if (!isDir) len = new FileInfo(t).Length;
                }
                catch { isDir = false; }
                AddResult("Audit-Tombstone", t, Status.Warn,
                    isDir ? "目录" : string.Format("{0} 字节墓碑", len));
            }

            // 服务主程序：按实际形态报告，避免把修好的真二进制误报成墓碑
            if (!Exists(SvcBinary))
            {
                AddResult("Audit-SvcBinary", SvcBinary, Status.Ok, "不存在");
            }
            else
            {
                bool isDir;
                long len = -1;
                try
                {
                    var a = File.GetAttributes(SvcBinary);
                    isDir = (a & FileAttributes.Directory) != 0;
                    if (!isDir) len = new FileInfo(SvcBinary).Length;
                }
                catch { isDir = false; }

                if (isDir)
                    AddResult("Audit-SvcBinary", SvcBinary, Status.Warn, "是目录，形态异常");
                else if (len == 0)
                    AddResult("Audit-SvcBinary", SvcBinary, Status.Warn, "0 字节墓碑，步骤 5 可清理");
                else
                    AddResult("Audit-SvcBinary", SvcBinary, Status.Warn,
                        string.Format("存在 大小={0}（正常的服务二进制；步骤 4 会删除它）", len));
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  监视：验证服务是否会复活
        // ════════════════════════════════════════════════════════════════
        private static void Watch(int minutes)
        {
            WriteLog(string.Format("── 监视模式：{0} 分钟内观察服务是否复活 ──", minutes));
            WriteLog("现在可以插拔手柄 / 启动游戏，检验处置是否真的挡住了拉起。");

            var info = GetSvcInfo();
            AddResult("Watch-Baseline", SvcName, Status.Skip,
                info.Registered
                    ? string.Format("起始状态: {0}/{1}", StateName(info.State), StartTypeName(info.RegStart))
                    : "起始状态: 不存在");

            // 记录文件当前是否存在：只有"从无到有"才算被还原
            var before = new Dictionary<string, bool>();
            foreach (var f in TargetFiles) before[f] = Exists(f);

            var deadline = DateTime.Now.AddMinutes(minutes);
            var watchStart = DateTime.Now;
            bool respawned = false;
            bool cancelled = false;
            while (DateTime.Now < deadline)
            {
                if (_cancelRequested) { cancelled = true; break; }
                var cur = GetSvcInfo();
                if (cur.Registered && cur.State != SERVICE_STOPPED)
                {
                    AddResult("Watch-Respawn", SvcName, Status.Fail,
                        string.Format("{0} 服务复活: {1}/{2}",
                            DateTime.Now.ToString("HH:mm:ss"), StateName(cur.State), StartTypeName(cur.RegStart)));
                    respawned = true;
                    break;
                }
                // 分片睡眠，保证“停止”能在 1 秒内被响应
                for (int i = 0; i < 5 && !_cancelRequested; i++)
                    System.Threading.Thread.Sleep(1000);
            }
            if (cancelled)
            {
                // 报告"真正监视了多久"，而不是配置的时长
                var elapsed = DateTime.Now - watchStart;
                string spent = elapsed.TotalMinutes >= 1
                    ? string.Format("{0:F1} 分钟", elapsed.TotalMinutes)
                    : string.Format("{0:F0} 秒", elapsed.TotalSeconds);
                AddResult("Watch-Respawn", SvcName, Status.Warn,
                    string.Format("{0} 用户请求停止，监视提前结束（已监视 {1}，计划 {2} 分钟）",
                        DateTime.Now.ToString("HH:mm:ss"), spent, minutes));
            }
            else if (!respawned)
                AddResult("Watch-Respawn", SvcName, Status.Ok,
                    string.Format("监视 {0} 分钟内服务未复活", minutes));

            foreach (var f in TargetFiles)
            {
                bool now = Exists(f);
                if (now && !before[f]) AddResult("Watch-File", f, Status.Fail, "文件被还原（原本已删除）");
                else if (now) AddResult("Watch-File", f, Status.Skip, "文件仍存在（监视前就存在，未被删除）");
                else AddResult("Watch-File", f, Status.Ok, "仍不存在");
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  备份 / 摘要 / 主流程
        // ════════════════════════════════════════════════════════════════
        private static void Backup()
        {
            if (_noBackup) { WriteLog("已跳过注册表备份 (-NoBackup)", Status.Warn); return; }
            string file = Path.Combine(_backupDir, "GameInputSvc-" +
                DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".reg");
            int rc;
            string outp = RunTool("reg.exe",
                "export \"HKLM\\SYSTEM\\CurrentControlSet\\Services\\" + SvcName + "\" \"" + file + "\" /y", out rc);
            if (rc == 0) WriteLog("注册表已备份: " + file, Status.Ok);
            else WriteLog("注册表备份失败: " + outp, Status.Warn);
        }

        private static int Summary()
        {
            Out();
            Out("  ══════════════════ 执行摘要 ══════════════════", ConsoleColor.Gray);
            Out();

            int ok = 0, warn = 0, fail = 0, skip = 0;
            foreach (var r in Results)
            {
                switch (r.Level)
                {
                    case Status.Ok: ok++; break;
                    case Status.Warn: warn++; break;
                    case Status.Fail: fail++; break;
                    default: skip++; break;
                }
                Out(string.Format("  [{0,-4}] {1,-22} {2}",
                        r.Level.ToString().ToUpperInvariant(), r.Step, r.Target),
                    r.Level == Status.Ok ? ConsoleColor.Green :
                    r.Level == Status.Warn ? ConsoleColor.Yellow :
                    r.Level == Status.Fail ? ConsoleColor.Red : ConsoleColor.DarkGray);
                if (!string.IsNullOrEmpty(r.Detail))
                    Out("           └─ " + r.Detail, ConsoleColor.DarkGray);
            }

            Out();
            Out(string.Format("  成功 {0} | 警告 {1} | 失败 {2} | 跳过 {3}", ok, warn, fail, skip), ConsoleColor.Cyan);
            if (_logFile != null) Out("  完整日志: " + _logFile, ConsoleColor.Cyan);
            Out();

            if (warn > 0 || fail > 0)
            {
                Out("  提示：标记为\"警告\"的项目通常需要重启后生效。", ConsoleColor.Yellow);
                Out();
            }

            // GUI 模式没有控制台，结果写进状态栏
            if (_gui && _form != null)
                _form.UpdateStatus(string.Format(
                    "成功 {0} | 警告 {1} | 失败 {2} | 跳过 {3}   —   日志: {4}",
                    ok, warn, fail, skip, _logFile == null ? "(未启用)" : _logFile));

            return fail > 0 ? 3 : 0;
        }

        private static void ShowBanner()
        {
            Out();
            Out("  ╔══════════════════════════════════════════════════════════╗", ConsoleColor.Magenta);
            Out("  ║   Microsoft GameInput 强制停用 / 删除工具                ║", ConsoleColor.Magenta);
            Out("  ║   GameInputTool.exe                                      ║", ConsoleColor.Magenta);
            Out("  ╚══════════════════════════════════════════════════════════╝", ConsoleColor.Magenta);
            Out();
            WriteLog(string.Format("模式: {0} | 墓碑清理: {1} | WinSxS: {2} | 已提权: {3}",
                _mode, _includeTombstones, _includeWinSxS, IsAdmin()));
        }

        private static bool Confirm()
        {
            if (_force) return true;

            string headline = string.Format("即将执行模式 [{0}]", _mode);
            string detail =
                "这会修改系统服务与 System32 下的文件，操作可能无法自动回滚。\r\n" +
                "强烈建议先创建系统还原点。\r\n\r\n" +
                (string.IsNullOrEmpty(_logFile) ? "" : "运行日志：" + _logFile);

            // GUI 模式：弹出必须手动输入 YES 的对话框
            if (_gui && _form != null) return _form.ConfirmOnUi(headline, detail);

            Out();
            Out(string.Format("  即将执行模式 [{0}]，这会修改系统服务与 System32 下的文件。", _mode), ConsoleColor.Yellow);
            Out("  强烈建议先创建系统还原点。", ConsoleColor.Yellow);
            Console.Write("  输入 YES 继续，其它任意键取消: ");
            return Console.ReadLine() == "YES";
        }

        private static bool Preflight()
        {
            WriteLog("── 预检 ──");

            if (!IsAdmin())
            {
                WriteLog("未以管理员身份运行，无法修改服务与系统文件。", Status.Fail);
                WriteLog("请右键本程序 -> 以管理员身份运行。");
                return false;
            }

            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(SessionMgrPath, true))
                {
                    if (k == null) WriteLog("无法写入 Session Manager 键", Status.Warn);
                }
            }
            catch (Exception ex) { WriteLog("注册表写入测试失败: " + ex.Message, Status.Warn); }

            // 火绒提示：它的文件保护会拦截删除或回滚改动
            bool huorong = false;
            ServiceKeyExists("HRWSCCtrl", ref huorong);
            if (huorong)
            {
                WriteLog("检测到火绒 (HRWSCCtrl)。其文件保护可能拦截删除或回滚改动；", Status.Warn);
                WriteLog("若操作失败，请在火绒中临时关闭\"文件实时监控\"与\"自我保护\"。", Status.Warn);
            }

            return true;
        }

        private static void ServiceKeyExists(string name, ref bool found)
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Services\" + name))
                    found = k != null;
            }
            catch { }
        }

        [STAThread]
        private static int Main(string[] args)
        {
            // Console.OutputEncoding 必须在任何输出之前设置，否则中文会是乱码。
            // 输出被重定向（管道/文件）时不允许设置，故先判断再兜底 try/catch。
            if (!Console.IsOutputRedirected)
            {
                try { Console.OutputEncoding = Encoding.UTF8; }
                catch { }
            }

            // ── 无参数（或只有 -Gui）：打开常驻窗口 ──
            // 这是双击启动的默认路径。窗口不会自动关闭，日志随时可看。
            if (args.Length == 0) return RunGui();
            if (args.Length == 1)
            {
                string only = args[0].ToLowerInvariant();
                if (only == "-gui" || only == "/gui" || only == "--gui") return RunGui();
            }

            bool modeSet = false;

            // ── 解析参数 ──
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                switch (a)
                {
                    case "-mode":
                    case "/mode":
                    case "-m":
                        if (i + 1 < args.Length) { _mode = args[++i]; modeSet = true; }
                        break;
                    case "-includetombstones": case "/includetombstones": _includeTombstones = true; break;
                    case "-includewinsxs": case "/includewinsxs": _includeWinSxS = true; break;
                    case "-nobackup": case "/nobackup": _noBackup = true; break;
                    case "-force": case "/force": case "-y": _force = true; break;
                    case "-watchminutes":
                    case "/watchminutes":
                        if (i + 1 < args.Length) int.TryParse(args[++i], out _watchMinutes);
                        break;
                    case "-h": case "-?": case "/?": case "--help":
                        PrintUsage(); return 0;
                    default:
                        // 允许把模式名当位置参数：GameInputTool.exe Full
                        // 也允许跟在其它开关后面：-Force Full
                        if (a.Length > 0 && a[0] != '-' && a[0] != '/' && !modeSet)
                        {
                            _mode = args[i];
                            modeSet = true;
                        }
                        break;
                }
            }

            // 规范化模式名
            switch (_mode.ToLowerInvariant())
            {
                case "audit": _mode = "Audit"; break;
                case "soft": _mode = "Soft"; break;
                case "full": _mode = "Full"; break;
                case "nuclear": _mode = "Nuclear"; break;
                case "watch": _mode = "Watch"; break;
                default:
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("未知模式: " + _mode);
                    Console.ResetColor();
                    PrintUsage();
                    return 2;
            }
            if (_watchMinutes < 1) _watchMinutes = 1;
            if (_watchMinutes > 1440) _watchMinutes = 1440;

            Console.OutputEncoding = Encoding.UTF8;
            try { Console.Title = "Microsoft GameInput 处置工具 [" + _mode + "]"; } catch { }

            InitPaths();

            try
            {
                return Dispatch();
            }
            finally
            {
                if (_mode != "Audit" && _mode != "Watch")
                {
                    Out("  按回车键退出…", ConsoleColor.Gray);
                    try { Console.ReadLine(); } catch { }
                }
            }
        }

        /// <summary>建立 logs\ 与 backup\ 目录，并生成本次运行的日志文件名。</summary>
        private static void InitPaths()
        {
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                _logDir = Path.Combine(baseDir, "logs");
                _backupDir = Path.Combine(baseDir, "backup");
                Directory.CreateDirectory(_logDir);
                Directory.CreateDirectory(_backupDir);
                _logFile = Path.Combine(_logDir,
                    "gameinput-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
            }
            catch { _logFile = null; }
        }

        // ════════════════════════════════════════════════════════════════
        //  窗口模式
        // ════════════════════════════════════════════════════════════════

        /// <summary>打开常驻日志窗口。双击 exe（无参数）走这条路。</summary>
        private static int RunGui()
        {
            _gui = true;
            InitPaths();

            // 控制台子系统程序启动时 Windows 一定会分配控制台窗口，
            // 这里把它藏起来，只留我们自己的日志窗口，避免出现两个窗口。
            HideConsoleWindow();

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            _form = new GuiForm();
            Application.Run(_form);

            // 窗口关闭后，用最后一次运行的退出码作为进程退出码
            return _form == null ? 0 : _form.ExitCode;
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const int SW_HIDE = 0;

        /// <summary>隐藏本进程的控制台窗口（GUI 模式下用）。失败不影响功能。</summary>
        private static void HideConsoleWindow()
        {
            try
            {
                IntPtr h = GetConsoleWindow();
                if (h != IntPtr.Zero) ShowWindow(h, SW_HIDE);
            }
            catch { }
        }

        /// <summary>
        /// 由窗口按钮触发的单次运行。与命令行入口共用 Dispatch()，
        /// 因此两者的行为完全一致，只是确认方式与退出处理不同。
        /// </summary>
        internal static int RunForGui(string mode, bool tombstones, bool winSxS,
                                      bool noBackup, bool force, int watchMinutes)
        {
            _mode = mode;
            _includeTombstones = tombstones;
            _includeWinSxS = winSxS;
            _noBackup = noBackup;
            _force = force;
            _watchMinutes = watchMinutes < 1 ? 1 : (watchMinutes > 1440 ? 1440 : watchMinutes);
            _cancelRequested = false;

            // 每次运行单独计数，摘要只反映本次
            Results.Clear();

            // 每次运行一份新日志文件，便于事后比对
            InitPaths();

            return Dispatch();
        }

        /// <summary>一行当前状态，显示在窗口顶部（只读，不修改系统）。</summary>
        internal static string DescribeCurrentState()
        {
            try
            {
                var info = GetSvcInfo();
                string svc = info.Registered
                    ? string.Format("{0} / {1}", StateName(info.State), StartTypeName(info.RegStart))
                    : "服务不存在";

                string exe;
                try
                {
                    var fi = new FileInfo(SvcBinary);
                    if (!fi.Exists) exe = "已删除";
                    else if (fi.Length == 0) exe = "0 字节墓碑";
                    else exe = fi.Length.ToString("N0") + " 字节";
                }
                catch { exe = "无法读取"; }

                return string.Format("服务 GameInputSvc: {0}     GameInputSvc.exe: {1}     管理员: {2}",
                    svc, exe, IsAdmin() ? "是" : "否");
            }
            catch (Exception ex) { return "状态读取失败: " + ex.Message; }
        }

        /// <summary>
        /// 模式分发与执行。控制台与窗口两种入口共用这一份逻辑，
        /// 保证界面模式不会出现行为分叉。
        /// </summary>
        private static int Dispatch()
        {
            try
            {
                ShowBanner();

                if (_mode == "Audit") { Audit(); return Summary(); }
                if (_mode == "Watch") { Watch(_watchMinutes); return Summary(); }

                if (!Preflight()) { Summary(); return 1; }
                if (!Confirm()) { WriteLog("用户取消操作", Status.Warn); return 4; }

                Backup();
                WriteLog("── 执行模式：" + _mode + " ──");

                DisableTriggers();
                StopAndDisable();

                if (_mode == "Full" || _mode == "Nuclear")
                {
                    DeleteServiceReg();
                    DeleteBinaries();
                    // 服务被移除后遗留的 0 字节可执行文件
                    RemoveTargetFile(@"C:\Windows\System32\GameInputSvc.exe");
                }

                if (_mode == "Nuclear" || _includeTombstones) CleanTombstones();

                PendingCleanup();
                PostCheck();
                return Summary();
            }
            catch (Exception ex)
            {
                Out();
                Out("  发生未预期的错误: " + ex, ConsoleColor.Red);
                return 1;
            }
        }

        private static void PrintUsage()
        {
            Console.WriteLine();
            Console.WriteLine("  GameInputTool.exe - Microsoft GameInput 服务强制停用 / 删除工具");
            Console.WriteLine();
            Console.WriteLine("  用法: GameInputTool.exe [-Mode <模式>] [选项]");
            Console.WriteLine();
            Console.WriteLine("  模式:");
            Console.WriteLine("    Audit    只检测不修改（默认）");
            Console.WriteLine("    Soft     停用 + 摘除触发器，保留服务注册项，完全可逆");
            Console.WriteLine("    Full     服务层 + 删除 SCM 注册项 + 删除 DLL");
            Console.WriteLine("    Nuclear  全部清理（含 0 字节墓碑文件）");
            Console.WriteLine("    Watch    监视服务是否复活");
            Console.WriteLine();
            Console.WriteLine("  选项:");
            Console.WriteLine("    -IncludeTombstones   清理 0 字节墓碑残留");
            Console.WriteLine("    -IncludeWinSxS       连同 WinSxS 组件存储硬链接一起删除（不推荐）");
            Console.WriteLine("    -NoBackup            跳过注册表导出备份");
            Console.WriteLine("    -Force               跳过交互确认");
            Console.WriteLine("    -WatchMinutes N      Watch 模式监视时长（分钟，默认 5）");
            Console.WriteLine();
            Console.WriteLine("  示例:");
            Console.WriteLine("    GameInputTool.exe                        # 审计");
            Console.WriteLine("    GameInputTool.exe Soft                   # 可逆停用");
            Console.WriteLine("    GameInputTool.exe Full -IncludeTombstones");
            Console.WriteLine("    GameInputTool.exe Watch -WatchMinutes 10");
            Console.WriteLine();
            Console.WriteLine("  双击运行时程序会自动请求管理员权限（UAC）。");
            Console.WriteLine();
        }
    }
}
