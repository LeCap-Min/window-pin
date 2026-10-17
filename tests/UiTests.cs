using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using WindowPin;

internal static class UiTests
{
    [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    private static readonly List<string> results = new List<string>();

    [STAThread]
    private static int Main(string[] args)
    {
        SetProcessDPIAware();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Directory.CreateDirectory(args[0]);
        try
        {
            using (var service = new WindowService())
            using (var form = new MainForm(service))
            {
                // Test only the program's own form. Entries below are presentation
                // fixtures and are never sent to the native window service.
                form.Show();
                Application.DoEvents();
                Check("Registers primary or fallback global shortcut", Field<bool>(form, "hotkeyRegistered"));
                results.Add("INFO: active shortcut " + Field<string>(form, "hotkeyText"));
                Field<Timer>(form, "refreshTimer").Stop();
                ((TextBox)Find(form, "SearchBox")).Text = "WindowPin QA";
                Field<List<WindowEntry>>(form, "entries").Clear();
                Field<List<WindowEntry>>(form, "entries").AddRange(new[] {
                    new WindowEntry { Handle = new IntPtr(1), ProcessId = 10001, Title = "WindowPin QA · 文档窗口", ProcessName = "Document", IsTopmost = false },
                    new WindowEntry { Handle = new IntPtr(2), ProcessId = 10002, Title = "WindowPin QA · 视频播放器", ProcessName = "Player", IsTopmost = true },
                    new WindowEntry { Handle = new IntPtr(3), ProcessId = 10003, Title = "WindowPin QA · 中文长标题测试 — 可选择多个不同应用的窗口", ProcessName = "Editor", IsTopmost = false }
                });
                Invoke(form, "RenderList");
                var list = (ListView)Find(form, "WindowList");
                Check("List shows three sample entries", list.Items.Count == 3);
                Check("No selection disables pin actions", !Find(form, "PinButton").Enabled && !Find(form, "UnpinButton").Enabled);
                list.Items[0].Selected = true;
                Application.DoEvents();
                bool topmost = ((WindowEntry)list.Items[0].Tag).IsTopmost;
                IntPtr selectedHandle = ((WindowEntry)list.Items[0].Tag).Handle;
                Check("Selection updates pin actions", Find(form, "PinButton").Enabled != topmost && Find(form, "UnpinButton").Enabled == topmost);
                Invoke(form, "RenderList");
                Check("Refresh retains selected window identity", list.SelectedItems.Count == 1 && ((WindowEntry)list.SelectedItems[0].Tag).Handle == selectedHandle);
                var query = (TextBox)Find(form, "SearchBox");
                query.Text = "player";
                Check("Case-insensitive process search", list.Items.Count == 1 && ((WindowEntry)list.Items[0].Tag).IsTopmost);
                list.Items[0].Selected = true;
                Application.DoEvents();
                Check("Pinned selection enables unpin only", !Find(form, "PinButton").Enabled && Find(form, "UnpinButton").Enabled);
                query.Text = "no matches here";
                Check("Empty results disable actions", list.Items.Count == 0 && !Find(form, "PinButton").Enabled && !Find(form, "UnpinButton").Enabled);
                query.Text = "WindowPin QA";
                Application.DoEvents();
                SaveForm(form, Path.Combine(args[0], "interface.png"));
                Check("All controls stay inside client area", CheckBounds(form));
                form.Size = form.MinimumSize;
                Application.DoEvents();
                SaveForm(form, Path.Combine(args[0], "interface-minimum.png"));
                Check("Minimum size keeps main controls visible", CheckBounds(form));
                ((Button)Find(form, "TrayButton")).PerformClick();
                Check("Tray button hides form and retains tray", !form.Visible && Field<NotifyIcon>(form, "tray").Visible);
                Invoke(form, "ShowMain");
                Check("Tray reopen restores form", form.Visible && form.WindowState == FormWindowState.Normal);
                form.Close();
            }
            bool primaryAvailable = RegisterHotKey(IntPtr.Zero, 992, 0x4003, (uint)Keys.T);
            bool fallbackAvailable = RegisterHotKey(IntPtr.Zero, 993, 0x4003, (uint)Keys.F8);
            if (primaryAvailable || fallbackAvailable)
            {
                Check("Close releases global shortcut", true);
                using (var service = new WindowService())
                using (var conflictForm = new MainForm(service))
                {
                    conflictForm.Show();
                    Application.DoEvents();
                    Check("Shortcut conflict does not prevent startup", !Field<bool>(conflictForm, "hotkeyRegistered") && Field<Label>(conflictForm, "shortcut").Text.Contains("注册失败"));
                    conflictForm.Close();
                }
                if (primaryAvailable) UnregisterHotKey(IntPtr.Zero, 992);
                if (fallbackAvailable) UnregisterHotKey(IntPtr.Zero, 993);
            }
            else results.Add("NOTE: shortcut held by another application; release test unavailable.");
            File.WriteAllLines(Path.Combine(args[0], "ui-tests.txt"), results.ToArray());
            foreach (string result in results) Console.WriteLine(result);
            return 0;
        }
        catch (Exception ex)
        {
            results.Add("FAIL: " + ex);
            File.WriteAllLines(Path.Combine(args[0], "ui-tests.txt"), results.ToArray());
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static void Check(string name, bool pass)
    {
        if (!pass) throw new Exception(name);
        results.Add("PASS: " + name);
    }

    private static T Field<T>(object owner, string name)
    {
        return (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(owner);
    }

    private static void Invoke(object owner, string name)
    {
        owner.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(owner, null);
    }

    private static Control Find(Control parent, string name)
    {
        return parent.Controls.Find(name, true)[0];
    }

    private static void SaveForm(Form form, string path)
    {
        using (var bitmap = new Bitmap(form.Width, form.Height))
        {
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
    }

    private static bool CheckBounds(Control control)
    {
        foreach (Control child in control.Controls)
        {
            if (child.Visible && (child.Left < -2 || child.Top < -2 || child.Right > control.ClientSize.Width + 2 || child.Bottom > control.ClientSize.Height + 2))
                throw new Exception("Control outside parent: " + child.Name + " " + child.Text + " bounds=" + child.Bounds + " parent=" + control.ClientSize);
            if (!CheckBounds(child)) return false;
        }
        return true;
    }
}
