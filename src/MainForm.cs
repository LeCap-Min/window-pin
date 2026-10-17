using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows.Forms;

namespace WindowPin
{
    public sealed class MainForm : Form
    {
        private readonly WindowService service;
        private readonly TextBox search = new TextBox();
        private readonly ListView windows = new ListView();
        private readonly Label count = new Label();
        private readonly Label selection = new Label();
        private readonly Label status = new Label();
        private readonly Label shortcut = new Label();
        private readonly Button pin = MakeButton("置顶所选", true);
        private readonly Button unpin = MakeButton("取消置顶", false);
        private readonly Button restore = MakeButton("恢复本次修改", false);
        private readonly NotifyIcon tray = new NotifyIcon();
        private readonly ContextMenuStrip trayMenu = new ContextMenuStrip();
        private readonly ContextMenuStrip listMenu = new ContextMenuStrip();
        private readonly Timer refreshTimer = new Timer();
        private List<WindowEntry> entries = new List<WindowEntry>();
        private bool hotkeyRegistered;
        private string hotkeyText = "Ctrl + Alt + T";
        private bool refreshing;
        private readonly float dpiScale;
        private const int HotkeyId = 0x51A1;
        private static readonly Color Ink = Color.FromArgb(30, 41, 59);
        private static readonly Color Muted = Color.FromArgb(100, 116, 139);
        private static readonly Color Blue = Color.FromArgb(37, 99, 235);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

        public MainForm(WindowService service)
        {
            this.service = service;
            Text = "窗口置顶";
            Name = "WindowPinMain";
            Font = new Font("Microsoft YaHei UI", 9.5f);
            BackColor = Color.FromArgb(246, 248, 252);
            ForeColor = Ink;
            AutoScaleMode = AutoScaleMode.None;
            using (Graphics screenGraphics = Graphics.FromHwnd(IntPtr.Zero))
                dpiScale = screenGraphics.DpiX / 96f;
            ClientSize = new Size(920, 620);
            MinimumSize = new Size(800, 580);
            StartPosition = FormStartPosition.CenterScreen;
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            KeyPreview = true;

            BuildLayout();
            ConfigureTray();
            // Scale the layout once using system DPI. Point-sized fonts already
            // follow that DPI; classic WinForms can autoscale too early when
            // constructing nested layout panels dynamically.
            if (dpiScale != 1f) Scale(new SizeF(dpiScale, dpiScale));
            SizeColumns();
            search.TextChanged += delegate { RenderList(); };
            windows.SelectedIndexChanged += delegate { UpdateSelection(); };
            windows.DoubleClick += delegate { ToggleSelected(); };
            windows.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter)
                {
                    ToggleSelected();
                    e.Handled = e.SuppressKeyPress = true;
                }
            };
            pin.Click += delegate { ChangeSelected(true); };
            unpin.Click += delegate { ChangeSelected(false); };
            restore.Click += delegate { RestoreChanges(); };
            KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.F5)
                {
                    RefreshWindows();
                    SetStatus("窗口列表已刷新。", false);
                    e.Handled = true;
                }
                else if (e.KeyCode == Keys.Escape && search.Text.Length > 0)
                    search.Clear();
            };
            refreshTimer.Interval = 2500;
            refreshTimer.Tick += delegate
            {
                if (Visible && WindowState != FormWindowState.Minimized && !listMenu.Visible)
                    RefreshWindows();
            };
            Shown += delegate
            {
                RefreshWindows();
                refreshTimer.Start();
                search.Focus();
            };
        }

        private void BuildLayout()
        {
            var layout = new TableLayoutPanel {
                Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6,
                Padding = new Padding(26, 20, 26, 16), BackColor = BackColor
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 90));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            Controls.Add(layout);

            var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
            var titleBlock = new Panel { Dock = DockStyle.Fill };
            titleBlock.Controls.Add(new Label {
                Text = "窗口置顶", AutoSize = true, Font = new Font(Font.FontFamily, 25, FontStyle.Bold),
                Location = new Point(0, 0), ForeColor = Ink
            });
            titleBlock.Controls.Add(new Label {
                Text = "选一个窗口，让它始终保持在普通窗口上方。", AutoSize = true,
                Location = new Point(2, 53), ForeColor = Muted
            });
            shortcut.Dock = DockStyle.Fill;
            shortcut.TextAlign = ContentAlignment.MiddleRight;
            shortcut.ForeColor = Muted;
            header.Controls.Add(titleBlock, 0, 0);
            header.Controls.Add(shortcut, 1, 0);
            layout.Controls.Add(header, 0, 0);

            var searchRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = new Padding(0, 4, 0, 8) };
            searchRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78));
            searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            searchRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
            searchRow.Controls.Add(new Label { Text = "搜索窗口", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty }, 0, 0);
            search.Name = "SearchBox";
            search.Dock = DockStyle.Fill;
            search.Margin = new Padding(0, 9, 12, 0);
            search.Font = new Font(Font.FontFamily, 10.5f);
            search.AccessibleName = "按窗口标题或程序名称搜索";
            searchRow.Controls.Add(search, 1, 0);
            var refresh = MakeButton("刷新  F5", false);
            refresh.Name = "RefreshButton";
            refresh.Dock = DockStyle.Fill;
            refresh.Margin = new Padding(0, 2, 0, 2);
            refresh.Click += delegate { RefreshWindows(); SetStatus("窗口列表已刷新。", false); };
            searchRow.Controls.Add(refresh, 2, 0);
            layout.Controls.Add(searchRow, 0, 1);

            count.Dock = DockStyle.Fill;
            count.TextAlign = ContentAlignment.MiddleLeft;
            count.ForeColor = Muted;
            count.Margin = Padding.Empty;
            layout.Controls.Add(count, 0, 2);

            windows.Name = "WindowList";
            windows.Dock = DockStyle.Fill;
            windows.Margin = new Padding(0, 0, 0, 0);
            windows.View = View.Details;
            windows.FullRowSelect = true;
            windows.MultiSelect = false;
            windows.HideSelection = false;
            windows.BorderStyle = BorderStyle.FixedSingle;
            windows.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            windows.ShowItemToolTips = true;
            windows.BackColor = Color.White;
            windows.ForeColor = Ink;
            windows.Columns.Add("状态", 90);
            windows.Columns.Add("窗口标题", 470);
            windows.Columns.Add("程序", 165);
            windows.Columns.Add("进程 ID", 95);
            windows.SizeChanged += delegate { SizeColumns(); };
            // A small image list supplies comfortable, DPI-scaled row height.
            windows.SmallImageList = new ImageList { ImageSize = new Size(1, (int)(30 * dpiScale)), ColorDepth = ColorDepth.Depth32Bit };
            listMenu.Items.Add("置顶所选", null, delegate { ChangeSelected(true); });
            listMenu.Items.Add("取消置顶", null, delegate { ChangeSelected(false); });
            listMenu.Opening += delegate(object sender, System.ComponentModel.CancelEventArgs e)
            {
                WindowEntry current = SelectedEntry();
                e.Cancel = current == null;
                if (current != null)
                {
                    listMenu.Items[0].Enabled = !current.IsTopmost;
                    listMenu.Items[1].Enabled = current.IsTopmost;
                }
            };
            windows.MouseDown += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Right) return;
                ListViewItem item = windows.GetItemAt(e.X, e.Y);
                windows.SelectedItems.Clear();
                if (item != null) item.Selected = true;
            };
            windows.ContextMenuStrip = listMenu;
            layout.Controls.Add(windows, 0, 3);

            var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 1, Margin = new Padding(0, 16, 0, 10) };
            actions.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 126));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 126));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 154));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 116));
            pin.Name = "PinButton";
            unpin.Name = "UnpinButton";
            restore.Name = "RestoreButton";
            pin.Dock = unpin.Dock = restore.Dock = DockStyle.Fill;
            pin.Margin = unpin.Margin = new Padding(0, 0, 10, 0);
            restore.Margin = new Padding(0, 0, 10, 0);
            actions.Controls.Add(pin, 0, 0);
            actions.Controls.Add(unpin, 1, 0);
            selection.Dock = DockStyle.Fill;
            selection.TextAlign = ContentAlignment.MiddleLeft;
            selection.AutoEllipsis = true;
            selection.ForeColor = Muted;
            selection.Margin = new Padding(2, 0, 10, 0);
            actions.Controls.Add(selection, 2, 0);
            actions.Controls.Add(restore, 3, 0);
            var hide = MakeButton("收起到托盘", false);
            hide.Name = "TrayButton";
            hide.Dock = DockStyle.Fill;
            hide.Margin = Padding.Empty;
            hide.Click += delegate
            {
                Hide();
                tray.ShowBalloonTip(2000, "窗口置顶仍在运行", "双击托盘图标可重新打开；右键菜单可退出。", ToolTipIcon.Info);
            };
            actions.Controls.Add(hide, 4, 0);
            layout.Controls.Add(actions, 0, 4);

            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            footer.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            status.Name = "StatusLabel";
            status.Dock = DockStyle.Fill;
            status.AutoEllipsis = true;
            status.TextAlign = ContentAlignment.MiddleLeft;
            status.Margin = Padding.Empty;
            status.ForeColor = Blue;
            status.Text = "选择窗口后点击“置顶所选”；双击列表可切换状态。";
            footer.Controls.Add(status, 0, 0);
            footer.Controls.Add(new Label {
                Text = "关闭工具会恢复本次修改。若目标以管理员身份运行，请也以管理员身份运行本工具。",
                Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Muted,
                Margin = Padding.Empty, AutoEllipsis = true, Font = new Font(Font.FontFamily, 8.5f)
            }, 0, 1);
            layout.Controls.Add(footer, 0, 5);
            UpdateSelection();
        }

        private static Button MakeButton(string text, bool primary)
        {
            var result = new Button {
                Text = text, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
                BackColor = primary ? Color.FromArgb(37, 99, 235) : Color.White,
                ForeColor = primary ? Color.White : Color.FromArgb(30, 41, 59),
                UseVisualStyleBackColor = false, Height = 42
            };
            result.FlatAppearance.BorderSize = primary ? 0 : 1;
            result.FlatAppearance.BorderColor = Color.FromArgb(218, 225, 235);
            return result;
        }

        private void ConfigureTray()
        {
            tray.Icon = Icon;
            tray.Text = hotkeyRegistered ? "窗口置顶 · " + hotkeyText : "窗口置顶";
            trayMenu.Items.Add("打开窗口置顶", null, delegate { ShowMain(); });
            trayMenu.Items.Add("恢复本次修改", null, delegate { RestoreChanges(); });
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add("退出并恢复", null, delegate { Close(); });
            tray.ContextMenuStrip = trayMenu;
            tray.DoubleClick += delegate { ShowMain(); };
            tray.Visible = true;
        }

        private void ShowMain()
        {
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            RefreshWindows();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            hotkeyRegistered = RegisterHotKey(Handle, HotkeyId, 0x4003, (uint)Keys.T);
            hotkeyText = "Ctrl + Alt + T";
            bool usingFallback = !hotkeyRegistered;
            if (usingFallback)
            {
                hotkeyRegistered = RegisterHotKey(Handle, HotkeyId, 0x4003, (uint)Keys.F8);
                hotkeyText = "Ctrl + Alt + F8";
            }
            bool admin;
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                admin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            shortcut.Text = hotkeyRegistered
                ? hotkeyText + (usingFallback ? "  ·  备用快捷键" : "") + "\n切换当前活动窗口的置顶状态"
                : "快捷键 T / F8 均注册失败\n仍可使用列表按钮";
            if (admin) shortcut.Text += "  ·  管理员";
            tray.Text = hotkeyRegistered ? "窗口置顶 · " + hotkeyText : "窗口置顶 · 快捷键不可用";
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            if (hotkeyRegistered) UnregisterHotKey(Handle, HotkeyId);
            hotkeyRegistered = false;
            base.OnHandleDestroyed(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0312 && m.WParam.ToInt32() == HotkeyId)
            {
                WindowEntry target = service.GetForegroundWindowEntry();
                if (target == null)
                {
                    SetStatus("请先切换到需要置顶的应用窗口，再按 " + hotkeyText + "。", true);
                    return;
                }
                ChangeWindow(target, !target.IsTopmost, true);
                return;
            }
            base.WndProc(ref m);
        }

        private void SizeColumns()
        {
            if (windows.Columns.Count != 4) return;
            float scale = dpiScale;
            windows.Columns[0].Width = (int)(90 * scale);
            windows.Columns[2].Width = (int)(150 * scale);
            windows.Columns[3].Width = (int)(90 * scale);
            windows.Columns[1].Width = Math.Max((int)(200 * scale), windows.ClientSize.Width
                - windows.Columns[0].Width - windows.Columns[2].Width - windows.Columns[3].Width - SystemInformation.VerticalScrollBarWidth - 4);
        }

        private WindowEntry SelectedEntry()
        {
            return windows.SelectedItems.Count == 0 ? null : (WindowEntry)windows.SelectedItems[0].Tag;
        }

        private void RefreshWindows()
        {
            entries = service.EnumerateWindows();
            RenderList();
        }

        private void RenderList()
        {
            WindowEntry previous = SelectedEntry();
            IntPtr previousHandle = previous == null ? IntPtr.Zero : previous.Handle;
            uint previousPid = previous == null ? 0 : previous.ProcessId;
            int topIndex = windows.TopItem == null ? 0 : windows.TopItem.Index;
            string query = search.Text.Trim();
            List<WindowEntry> filtered = entries.Where(w => query.Length == 0
                || w.Title.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0
                || w.ProcessName.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0).ToList();
            filtered.Sort(delegate(WindowEntry a, WindowEntry b)
            {
                int titleOrder = StringComparer.CurrentCultureIgnoreCase.Compare(a.Title, b.Title);
                return titleOrder != 0 ? titleOrder : a.Handle.ToInt64().CompareTo(b.Handle.ToInt64());
            });
            refreshing = true;
            windows.BeginUpdate();
            windows.Items.Clear();
            foreach (WindowEntry entry in filtered)
            {
                var item = new ListViewItem(entry.IsTopmost ? "● 已置顶" : "普通");
                item.SubItems.Add(entry.Title);
                item.SubItems.Add(entry.ProcessName);
                item.SubItems.Add(entry.ProcessId.ToString());
                item.Tag = entry;
                item.ToolTipText = entry.Title + "\n" + entry.ProcessName + " · PID " + entry.ProcessId
                    + (entry.IsMinimized ? " · 已最小化" : "")
                    + (service.IsManaged(entry.Handle) ? "\n本工具已修改，退出时恢复原状态。" : "");
                if (entry.IsTopmost) item.ForeColor = Color.FromArgb(21, 128, 91);
                windows.Items.Add(item);
                if (entry.Handle == previousHandle && entry.ProcessId == previousPid)
                {
                    item.Selected = true;
                    item.Focused = true;
                }
            }
            if (windows.Items.Count > 0)
                windows.TopItem = windows.Items[Math.Min(topIndex, windows.Items.Count - 1)];
            windows.EndUpdate();
            refreshing = false;
            count.Text = string.Format("当前窗口  {0} / {1}     ·     已置顶  {2}     ·     本次修改  {3}",
                filtered.Count, entries.Count, entries.Count(w => w.IsTopmost), service.ManagedCount);
            if (filtered.Count == 0) count.Text += "     没有匹配的窗口";
            UpdateSelection();
        }

        private void UpdateSelection()
        {
            if (refreshing) return;
            WindowEntry current = SelectedEntry();
            pin.Enabled = current != null && !current.IsTopmost;
            unpin.Enabled = current != null && current.IsTopmost;
            restore.Enabled = service.ManagedCount > 0;
            selection.Text = current == null ? "尚未选择窗口" : current.ProcessName;
        }

        private void ChangeSelected(bool topmost)
        {
            WindowEntry target = SelectedEntry();
            if (target != null) ChangeWindow(target, topmost, false);
        }

        private void ToggleSelected()
        {
            WindowEntry target = SelectedEntry();
            if (target != null) ChangeWindow(target, !WindowService.IsWindowTopmost(target.Handle), false);
        }

        private void ChangeWindow(WindowEntry target, bool topmost, bool fromHotkey)
        {
            string error;
            if (!service.SetTopmost(target, topmost, out error))
            {
                SetStatus(error, true);
                if (fromHotkey || !Visible)
                    tray.ShowBalloonTip(3000, "未能修改置顶状态", error, ToolTipIcon.Warning);
                else MessageBox.Show(this, error, "未能修改置顶状态", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                RefreshWindows();
                return;
            }
            string action = topmost ? "已置顶：" : "已取消置顶：";
            SetStatus(action + target.Title, false);
            if (fromHotkey)
                tray.ShowBalloonTip(1800, topmost ? "已置顶" : "已取消置顶", target.Title, ToolTipIcon.Info);
            RefreshWindows();
        }

        private void RestoreChanges()
        {
            int failed = service.RestoreAll();
            string message = failed == 0 ? "已恢复本次修改的窗口状态。" : string.Format("有 {0} 个窗口未能恢复；请确认应用已恢复响应，或尝试以管理员身份运行。", failed);
            SetStatus(message, failed > 0);
            RefreshWindows();
            if (!Visible) tray.ShowBalloonTip(2000, "窗口置顶", message, failed > 0 ? ToolTipIcon.Warning : ToolTipIcon.Info);
        }

        private void SetStatus(string message, bool error)
        {
            status.Text = message;
            status.ForeColor = error ? Color.FromArgb(185, 28, 28) : Blue;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            int failed = service.RestoreAll();
            if (failed > 0 && e.CloseReason == CloseReason.UserClosing)
            {
                var answer = MessageBox.Show(this,
                    string.Format("有 {0} 个窗口未能恢复原来的置顶状态。\n\n仍要退出吗？选择“否”可保留工具并重试。", failed),
                    "恢复未完成", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                e.Cancel = answer == DialogResult.No;
            }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                refreshTimer.Dispose();
                tray.Visible = false;
                tray.Dispose();
                trayMenu.Dispose();
                listMenu.Dispose();
                if (windows.SmallImageList != null) windows.SmallImageList.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
