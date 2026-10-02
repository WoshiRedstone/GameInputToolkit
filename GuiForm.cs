// ============================================================================
//  GuiForm.cs - GameInputTool 的常驻日志窗口
//
//  作用：把控制台输出实时镜像到一个不会自动关闭的窗口中。
//        任务结束后窗口依然保留，可以继续翻阅、复制、打开日志文件。
//
//  设计要点：
//    · 所有输出仍走 Console，窗口只是"镜像"，所以命令行模式行为完全不变。
//    · 镜像通过 Program 的 Out()/OutRaw() 完成，颜色信息一并传递。
//    · 破坏性模式（Soft/Full/Nuclear）在窗口里用模态对话框二次确认，
//      沿用命令行"必须输入 YES"的语义。
//    · 窗口只是外壳：真正的逻辑仍在 GameInputTool.cs 里，两边共用同一套代码。
// ============================================================================

using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace GameInputTool
{
    internal sealed class GuiForm : Form
    {
        // ── 配色（深色日志区）────────────────────────────────────────
        private static readonly Color BgDark   = Color.FromArgb(24, 24, 24);
        private static readonly Color FgNormal = Color.FromArgb(212, 212, 212);

        // ── 控件 ────────────────────────────────────────────────────
        private readonly RichTextBox _log        = new RichTextBox();
        private readonly Label       _stateLabel = new Label();
        private readonly Label       _statusText = new Label();
        private readonly CheckBox    _autoScroll = new CheckBox();
        private readonly CheckBox    _optTomb    = new CheckBox();
        private readonly CheckBox    _optWinSxS  = new CheckBox();
        private readonly CheckBox    _optNoBak   = new CheckBox();
        private readonly CheckBox    _optForce   = new CheckBox();
        private readonly NumericUpDown _watchMin = new NumericUpDown();
        private readonly Button      _btnCancel  = new Button();

        private readonly Button[] _runButtons;

        private bool _running;
        private int  _exitCode;

        internal int ExitCode { get { return _exitCode; } }

        // ════════════════════════════════════════════════════════════
        //  构造
        // ════════════════════════════════════════════════════════════
        internal GuiForm()
        {
            Text = "Microsoft GameInput 处置工具";
            ClientSize = new Size(1000, 700);
            MinimumSize = new Size(760, 520);
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Font;
            Font = new Font("Microsoft YaHei UI", 9f);
            BackColor = Color.FromArgb(240, 240, 240);

            // ── 第 0 行：标题 + 状态 + 按钮 + 选项 ──
            // 用 TableLayoutPanel 纵向排列，避免按钮换行时压住下面的选项行。
            var head = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(0)
            };
            head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            head.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            head.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            head.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            head.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var title = new Label
            {
                Text = "Microsoft GameInput 强制停用 / 删除工具",
                Font = new Font("Microsoft YaHei UI", 12f, FontStyle.Bold),
                ForeColor = Color.FromArgb(40, 40, 40),
                AutoSize = true,
                Margin = new Padding(12, 10, 0, 0)
            };

            _stateLabel.AutoSize = true;
            _stateLabel.ForeColor = Color.FromArgb(90, 90, 90);
            _stateLabel.Margin = new Padding(14, 3, 0, 0);

            // 运行按钮
            var btnAudit = MakeButton("审计（只读）", 130, () => StartRun("Audit"));
            var btnSoft  = MakeButton("可逆停用",     110, () => StartRun("Soft"));
            var btnFull  = MakeButton("彻底删除",     110, () => StartRun("Full"));
            var btnNuke  = MakeButton("全部清理",     110, () => StartRun("Nuclear"));
            var btnWatch = MakeButton("监视复活",     110, () => StartRun("Watch"));

            _btnCancel.Text = "停止";
            _btnCancel.Size = new Size(80, 30);
            _btnCancel.Enabled = false;
            _btnCancel.Click += (s, e) =>
            {
                Program.RequestCancel();
                App("已请求停止，正在等待当前步骤结束…", ConsoleColor.Yellow);
            };

            var btnClear  = MakeButton("清空窗口",   100, () => { _log.Clear(); });
            var btnOpen   = MakeButton("打开日志",   100, OpenCurrentLog);
            var btnFolder = MakeButton("日志文件夹", 110, OpenLogFolder);
            var btnCopy   = MakeButton("复制全部",   100, CopyAll);
            var btnClose  = MakeButton("关闭",       80,  () => Close());

            _runButtons = new[] { btnAudit, btnSoft, btnFull, btnNuke, btnWatch };

            var buttons = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = true,
                Margin = new Padding(8, 6, 0, 0),
                MaximumSize = new Size(980, 0)
            };
            buttons.Controls.AddRange(new Control[]
            {
                btnAudit, btnSoft, btnFull, btnNuke, btnWatch, _btnCancel,
                btnClear, btnOpen, btnFolder, btnCopy, btnClose
            });

            // 选项
            _optTomb.Text = "清理 0 字节墓碑";
            _optTomb.AutoSize = true;
            _optTomb.Checked = false;

            _optWinSxS.Text = "含 WinSxS（不推荐）";
            _optWinSxS.AutoSize = true;
            _optWinSxS.ForeColor = Color.FromArgb(180, 60, 40);

            _optNoBak.Text = "跳过注册表备份";
            _optNoBak.AutoSize = true;

            _optForce.Text = "跳过确认";
            _optForce.AutoSize = true;

            var watchLabel = new Label { Text = "监视分钟:", AutoSize = true, Margin = new Padding(12, 6, 2, 0) };
            _watchMin.Minimum = 1;
            _watchMin.Maximum = 1440;
            _watchMin.Value = 5;
            _watchMin.Width = 60;

            _autoScroll.Text = "自动滚动";
            _autoScroll.AutoSize = true;
            _autoScroll.Checked = true;
            _autoScroll.Margin = new Padding(16, 6, 2, 0);

            var options = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = true,
                Margin = new Padding(8, 4, 0, 6),
                MaximumSize = new Size(980, 0)
            };
            options.Controls.AddRange(new Control[]
            {
                _optTomb, _optWinSxS, _optNoBak, _optForce, watchLabel, _watchMin, _autoScroll
            });

            head.Controls.Add(title,    0, 0);
            head.Controls.Add(_stateLabel, 0, 1);
            head.Controls.Add(buttons,  0, 2);
            head.Controls.Add(options,  0, 3);

            // ── 第 1 行：日志 ──
            _log.Dock = DockStyle.Fill;
            _log.ReadOnly = true;
            _log.BackColor = BgDark;
            _log.ForeColor = FgNormal;
            _log.Font = new Font("Consolas", 9.5f);
            _log.WordWrap = false;
            _log.ScrollBars = RichTextBoxScrollBars.Both;
            _log.BorderStyle = BorderStyle.None;
            _log.DetectUrls = false;
            _log.HideSelection = false;

            var logHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 4, 10, 4) };
            logHost.Controls.Add(_log);

            // ── 第 2 行：状态栏 ──
            _statusText.Dock = DockStyle.Fill;
            _statusText.TextAlign = ContentAlignment.MiddleLeft;
            _statusText.ForeColor = Color.FromArgb(70, 70, 70);
            _statusText.Padding = new Padding(12, 0, 12, 0);
            _statusText.AutoSize = false;
            _statusText.Height = 24;

            // ── 装配：用 TableLayoutPanel 保证停靠顺序确定 ──
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new Padding(0)
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26f));
            root.Controls.Add(head, 0, 0);
            root.Controls.Add(logHost, 0, 1);
            root.Controls.Add(_statusText, 0, 2);
            Controls.Add(root);

            // ── 初始提示 ──
            App("窗口已就绪。所有输出同时写入下方日志区与 logs\\ 目录下的日志文件。", ConsoleColor.Gray);
            App("提示：本程序已提权运行；破坏性操作会先弹出确认框。", ConsoleColor.Gray);
            App("", ConsoleColor.Gray);
            RefreshStateLabel();

            Shown += (s, e) =>
            {
                // 打开窗口立刻做一次只读审计，省得用户先点一次
                StartRun("Audit");
            };

            FormClosing += OnFormClosing;
        }

        // ════════════════════════════════════════════════════════════
        //  供 Program 调用：把控制台输出镜像进窗口
        // ════════════════════════════════════════════════════════════
        internal void AppendText(string text, ConsoleColor color)
        {
            if (IsDisposed) return;
            if (!IsHandleCreated) return;

            if (InvokeRequired)
            {
                try { BeginInvoke(new Action<string, ConsoleColor>(AppendText), text, color); }
                catch (ObjectDisposedException) { }
                catch (InvalidOperationException) { }
                return;
            }

            try
            {
                // 长时间运行的保护：超过 512KB 就丢弃前半段
                if (_log.TextLength > 512 * 1024)
                {
                    _log.Select(0, _log.TextLength / 2);
                    _log.SelectedText = "";
                }

                _log.SelectionStart = _log.TextLength;
                _log.SelectionLength = 0;
                _log.SelectionColor = MapColor(color);
                _log.AppendText(text);
                _log.SelectionColor = _log.ForeColor;

                if (_autoScroll.Checked)
                {
                    _log.SelectionStart = _log.TextLength;
                    _log.ScrollToCaret();
                }
            }
            catch { /* 界面更新失败不应影响主流程 */ }
        }

        /// <summary>在 UI 线程上弹出模态确认框（沿用命令行“输入 YES”的语义）。</summary>
        internal bool ConfirmOnUi(string headline, string detail)
        {
            if (IsDisposed || !IsHandleCreated) return false;
            try
            {
                return (bool)Invoke(new Func<bool>(delegate
                {
                    using (var dlg = new ConfirmDialog(headline, detail))
                        return dlg.ShowDialog(this) == DialogResult.OK;
                }));
            }
            catch { return false; }
        }

        internal void SetRunning(bool running)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action<bool>(SetRunning), running); }
                catch { }
                return;
            }
            _running = running;
            foreach (var b in _runButtons) b.Enabled = !running;
            _btnCancel.Enabled = running;
        }

        internal void UpdateStatus(string text)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action<string>(UpdateStatus), text); }
                catch { }
                return;
            }
            _statusText.Text = text;
            RefreshStateLabel();
        }

        // ════════════════════════════════════════════════════════════
        //  内部
        // ════════════════════════════════════════════════════════════
        private static Button MakeButton(string text, int width, Action onClick)
        {
            var b = new Button { Text = text, Width = width, Height = 30, Margin = new Padding(2, 2, 4, 2) };
            b.Click += (s, e) =>
            {
                try { onClick(); }
                catch (Exception ex) { MessageBox.Show("操作失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            };
            return b;
        }

        private void App(string text, ConsoleColor color)
        {
            // 走 Program 的输出管道，保证窗口与日志文件内容一致
            Program.Out(text, color);
        }

        private void RefreshStateLabel()
        {
            try { _stateLabel.Text = Program.DescribeCurrentState(); }
            catch { _stateLabel.Text = ""; }
        }

        private void StartRun(string mode)
        {
            if (_running)
            {
                MessageBox.Show(this, "已有任务在运行中，请等待结束或点“停止”。", "请稍候",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            bool tombstones = _optTomb.Checked;
            bool winSxS     = _optWinSxS.Checked;
            bool noBackup   = _optNoBak.Checked;
            bool force      = _optForce.Checked;
            int  watchMin   = (int)_watchMin.Value;

            SetRunning(true);
            _exitCode = 0;

            var worker = new System.Threading.Thread(delegate ()
            {
                int rc = 1;
                try
                {
                    rc = Program.RunForGui(mode, tombstones, winSxS, noBackup, force, watchMin);
                }
                catch (Exception ex)
                {
                    Program.Out("  发生未预期的错误: " + ex, ConsoleColor.Red);
                    rc = 1;
                }
                finally
                {
                    _exitCode = rc;
                    SetRunning(false);
                    UpdateStatus(BuildStatusLine(rc));
                    App("", ConsoleColor.Gray);
                    App("── 本次运行结束（退出码 " + rc + "）──", rc == 0 ? ConsoleColor.Green : ConsoleColor.Yellow);
                }
            });
            worker.IsBackground = true;
            worker.Start();
        }

        private static string BuildStatusLine(int rc)
        {
            int ok = 0, warn = 0, fail = 0, skip = 0;
            foreach (var r in Program.AllResults)
            {
                switch (r.Level)
                {
                    case Status.Ok:   ok++;   break;
                    case Status.Warn: warn++; break;
                    case Status.Fail: fail++; break;
                    default:          skip++; break;
                }
            }
            string logPath = Program.CurrentLogFile;
            return string.Format("成功 {0} | 警告 {1} | 失败 {2} | 跳过 {3}   —   日志: {4}",
                ok, warn, fail, skip, logPath == null ? "(未启用)" : logPath);
        }

        private void OpenCurrentLog()
        {
            string p = Program.CurrentLogFile;
            if (p == null || !File.Exists(p))
            {
                MessageBox.Show(this, "还没有生成日志文件。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try { System.Diagnostics.Process.Start("notepad.exe", "\"" + p + "\""); }
            catch (Exception ex) { MessageBox.Show(this, "打开失败: " + ex.Message, "错误"); }
        }

        private void OpenLogFolder()
        {
            try
            {
                string dir = Program.LogDir;
                if (dir == null) { MessageBox.Show(this, "日志目录不可用。", "提示"); return; }
                Directory.CreateDirectory(dir);
                System.Diagnostics.Process.Start("explorer.exe", "\"" + dir + "\"");
            }
            catch (Exception ex) { MessageBox.Show(this, "打开失败: " + ex.Message, "错误"); }
        }

        private void CopyAll()
        {
            try
            {
                if (_log.TextLength == 0) return;
                Clipboard.SetText(_log.Text);
                App("已复制全部日志到剪贴板。", ConsoleColor.Green);
            }
            catch (Exception ex) { MessageBox.Show(this, "复制失败: " + ex.Message, "错误"); }
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (!_running) return;
            var r = MessageBox.Show(this,
                "任务仍在进行中。确定要退出吗？\n（会请求停止后台操作，可能需要几秒）",
                "确认退出", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (r != DialogResult.Yes) { e.Cancel = true; return; }
            Program.RequestCancel();
        }

        private static Color MapColor(ConsoleColor c)
        {
            switch (c)
            {
                case ConsoleColor.Green:    return Color.FromArgb(106, 201, 106);
                case ConsoleColor.Yellow:   return Color.FromArgb(229, 192, 123);
                case ConsoleColor.Red:      return Color.FromArgb(240, 113, 120);
                case ConsoleColor.DarkGray: return Color.FromArgb(128, 128, 128);
                case ConsoleColor.Cyan:     return Color.FromArgb(86, 182, 194);
                case ConsoleColor.Magenta:  return Color.FromArgb(198, 120, 221);
                case ConsoleColor.White:    return Color.White;
                case ConsoleColor.Blue:     return Color.FromArgb(97, 175, 239);
                default:                    return FgNormal;
            }
        }

        // ════════════════════════════════════════════════════════════
        //  确认对话框：必须手动输入 YES
        // ════════════════════════════════════════════════════════════
        private sealed class ConfirmDialog : Form
        {
            private readonly TextBox _input = new TextBox();
            private readonly Button  _ok    = new Button();

            internal ConfirmDialog(string headline, string detail)
            {
                Text = "请确认";
                ClientSize = new Size(520, 260);
                FormBorderStyle = FormBorderStyle.FixedDialog;
                StartPosition = FormStartPosition.CenterParent;
                MinimizeBox = false;
                MaximizeBox = false;
                Font = new Font("Microsoft YaHei UI", 9f);

                var head = new Label
                {
                    Text = headline,
                    Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold),
                    ForeColor = Color.FromArgb(180, 60, 40),
                    Location = new Point(16, 14),
                    AutoSize = true
                };

                var body = new Label
                {
                    Text = detail,
                    Location = new Point(18, 44),
                    Size = new Size(484, 140),
                    AutoSize = false
                };

                var ask = new Label
                {
                    Text = "输入 YES 继续，其它内容会取消：",
                    Location = new Point(18, 186),
                    AutoSize = true
                };

                _input.Location = new Point(20, 210);
                _input.Width = 200;
                _input.TextChanged += (s, e) => { _ok.Enabled = _input.Text == "YES"; };

                _ok.Text = "继续";
                _ok.Location = new Point(320, 208);
                _ok.Size = new Size(84, 30);
                _ok.Enabled = false;
                _ok.DialogResult = DialogResult.OK;

                var cancel = new Button
                {
                    Text = "取消",
                    Location = new Point(412, 208),
                    Size = new Size(84, 30),
                    DialogResult = DialogResult.Cancel
                };

                Controls.AddRange(new Control[] { head, body, ask, _input, _ok, cancel });
                AcceptButton = _ok;
                CancelButton = cancel;
            }
        }
    }
}
