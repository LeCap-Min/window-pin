using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using WindowPin;

internal static class NativeTests
{
    private static readonly List<string> results = new List<string>();

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--fixture")
        {
            RunFixture(args[1]);
            return 0;
        }

        string output = args.Length == 0 ? AppDomain.CurrentDomain.BaseDirectory : args[0];
        Directory.CreateDirectory(output);
        string ready = Path.Combine(output, "fixture-" + Guid.NewGuid().ToString("N") + ".txt");
        Process fixture = null;
        FixtureForm ownWindow = null;
        WindowService service = new WindowService();
        IntPtr closeHandle = IntPtr.Zero;
        int exitCode = 0;
        try
        {
            ProcessStartInfo start = new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,
                "--fixture \"" + ready + "\"");
            start.UseShellExecute = false;
            start.CreateNoWindow = true;
            start.WindowStyle = ProcessWindowStyle.Hidden;
            fixture = Process.Start(start);
            WaitFor(delegate { return File.Exists(ready); }, "Fixture did not start.");
            Dictionary<string, IntPtr> handles = ReadHandles(ready);
            closeHandle = handles["normal"];
            uint pid = (uint)fixture.Id;
            WindowEntry normal = Find(service, handles["normal"]);
            WindowEntry topmost = Find(service, handles["topmost"]);
            WindowEntry owner = Find(service, handles["owner"]);
            WindowEntry dialog = Find(service, handles["dialog"]);
            WindowEntry topowner = Find(service, handles["topowner"]);
            WindowEntry topdialog = Find(service, handles["topdialog"]);
            WindowEntry disposable = Find(service, handles["disposable"]);
            Check(normal.ProcessId == pid && !normal.IsTopmost && topmost.IsTopmost,
                "Enumerates visible normal and pre-existing topmost windows");
            ownWindow = new FixtureForm("WindowPin own-process exclusion test", false);
            ownWindow.Show();
            Check(!HasProcess(service.EnumerateWindows(), (uint)Process.GetCurrentProcess().Id),
                "Excludes own process from enumeration");

            Rect boundsBefore;
            GetWindowRect(normal.Handle, out boundsBefore);
            IntPtr foregroundBefore = GetForegroundWindow();
            Set(service, normal, true);
            Rect boundsAfter;
            GetWindowRect(normal.Handle, out boundsAfter);
            Check(WindowService.IsWindowTopmost(normal.Handle), "Pin changes native WS_EX_TOPMOST");
            Check(boundsBefore.Equals(boundsAfter), "Pin preserves window position and size");
            Check(GetForegroundWindow() == foregroundBefore, "Pin preserves foreground window");
            Check(service.IsManaged(normal.Handle) && service.ManagedCount == 1,
                "Tracks only state changed by the tool");
            Set(service, normal, true);
            Check(service.ManagedCount == 1, "Repeated pin does not replace original baseline");
            Set(service, normal, false);
            Check(!WindowService.IsWindowTopmost(normal.Handle) && service.ManagedCount == 0,
                "Manual restore removes management tracking");

            Set(service, normal, true);
            Check(service.RestoreAll() == 0 && !WindowService.IsWindowTopmost(normal.Handle)
                && WindowService.IsWindowTopmost(topmost.Handle),
                "RestoreAll restores changed window and preserves untouched topmost window");
            Set(service, topmost, false);
            Check(service.IsManaged(topmost.Handle), "Tracks explicit unpin of an originally topmost window");
            Check(service.RestoreAll() == 0 && WindowService.IsWindowTopmost(topmost.Handle),
                "RestoreAll restores originally topmost state");

            Check(!WindowService.IsWindowTopmost(owner.Handle) && WindowService.IsWindowTopmost(dialog.Handle),
                "Fixture has a normal owner and an originally topmost owned dialog");
            Set(service, owner, true);
            Check(WindowService.IsWindowTopmost(owner.Handle) && WindowService.IsWindowTopmost(dialog.Handle),
                "Pin owner respects native owned-dialog topmost propagation");
            Check(service.RestoreAll() == 0 && !WindowService.IsWindowTopmost(owner.Handle)
                && WindowService.IsWindowTopmost(dialog.Handle),
                "RestoreAll preserves originally topmost owned dialog");
            Set(service, owner, true);
            Set(service, dialog, false);
            Check(service.RestoreAll() == 0 && !WindowService.IsWindowTopmost(owner.Handle)
                && WindowService.IsWindowTopmost(dialog.Handle),
                "Mixed owner/dialog toggles restore complete original family state");
            Set(service, topdialog, false);
            Check(service.RestoreAll() == 0 && WindowService.IsWindowTopmost(topowner.Handle)
                && WindowService.IsWindowTopmost(topdialog.Handle),
                "Originally topmost owner and dialog both restore");

            ShowWindow(normal.Handle, 7); // SW_SHOWMINNOACTIVE, fixture window only.
            WaitFor(delegate { return IsIconic(normal.Handle); }, "Fixture failed to minimize.");
            normal = Find(service, normal.Handle);
            Check(normal.IsMinimized, "Enumerates minimized windows");
            Set(service, normal, true);
            Check(IsIconic(normal.Handle) && WindowService.IsWindowTopmost(normal.Handle),
                "Pin preserves minimized state");
            Check(service.RestoreAll() == 0 && IsIconic(normal.Handle),
                "RestoreAll preserves minimized state");

            WindowEntry stale = new WindowEntry { Handle = topmost.Handle, ProcessId = 0, Title = "Wrong PID" };
            string error;
            Check(!service.SetTopmost(stale, false, out error) && !String.IsNullOrEmpty(error)
                && WindowService.IsWindowTopmost(topmost.Handle),
                "Rejects mismatched PID without changing target");
            Set(service, disposable, true);
            PostMessage(disposable.Handle, 0x0010, IntPtr.Zero, IntPtr.Zero);
            WaitFor(delegate { return !IsWindow(disposable.Handle); }, "Fixture window failed to close.");
            Check(!service.SetTopmost(disposable, false, out error) && !String.IsNullOrEmpty(error),
                "Rejects a closed target handle with an actionable error");
            Check(service.ManagedCount == 0 && service.RestoreAll() == 0,
                "Prunes closed managed windows");

            Set(service, topmost, false);
            service.Dispose();
            Check(WindowService.IsWindowTopmost(topmost.Handle), "Dispose restores original topmost state");
        }
        catch (Exception exception)
        {
            exitCode = 1;
            results.Add("FAIL: " + exception);
            Console.Error.WriteLine(exception);
        }
        finally
        {
            service.Dispose();
            if (ownWindow != null)
                ownWindow.Dispose();
            if (fixture != null)
            {
                if (!fixture.HasExited && closeHandle != IntPtr.Zero)
                    PostMessage(closeHandle, 0x0010, IntPtr.Zero, IntPtr.Zero);
                if (!fixture.WaitForExit(3000))
                {
                    fixture.Kill();
                    fixture.WaitForExit();
                }
                results.Add("Fixture cleanup: process exited");
                fixture.Dispose();
            }
            File.WriteAllLines(Path.Combine(output, "native-test-results.txt"), results.ToArray(), new UTF8Encoding(true));
            if (File.Exists(ready))
                File.Delete(ready);
        }
        return exitCode;
    }

    private static void Set(WindowService service, WindowEntry entry, bool topmost)
    {
        string error;
        if (!service.SetTopmost(entry, topmost, out error))
            throw new InvalidOperationException("SetTopmost(" + entry.Title + ", " + topmost + "): " + error);
    }

    private static WindowEntry Find(WindowService service, IntPtr hwnd)
    {
        foreach (WindowEntry entry in service.EnumerateWindows())
            if (entry.Handle == hwnd)
                return entry;
        throw new InvalidOperationException("Fixture window was not enumerated: " + hwnd);
    }

    private static bool HasProcess(List<WindowEntry> entries, uint pid)
    {
        return entries.Exists(delegate(WindowEntry entry) { return entry.ProcessId == pid; });
    }

    private static void Check(bool condition, string name)
    {
        if (!condition)
            throw new InvalidOperationException(name);
        results.Add("PASS: " + name);
        Console.WriteLine("PASS: " + name);
    }

    private static void WaitFor(Func<bool> condition, string failure)
    {
        Stopwatch watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.ElapsedMilliseconds > 8000)
                throw new TimeoutException(failure);
            Thread.Sleep(40);
        }
    }

    private static Dictionary<string, IntPtr> ReadHandles(string path)
    {
        Dictionary<string, IntPtr> handles = new Dictionary<string, IntPtr>();
        foreach (string line in File.ReadAllLines(path))
        {
            string[] pieces = line.Split('=');
            handles.Add(pieces[0], new IntPtr(Int64.Parse(pieces[1])));
        }
        return handles;
    }

    private static void RunFixture(string ready)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Dictionary<string, FixtureForm> windows = new Dictionary<string, FixtureForm>();
        windows.Add("normal", new FixtureForm("WindowPin test normal", false));
        windows.Add("topmost", new FixtureForm("WindowPin test original topmost", true));
        windows.Add("owner", new FixtureForm("WindowPin test owner", false));
        windows.Add("dialog", new FixtureForm("WindowPin test owned dialog", true));
        windows.Add("topowner", new FixtureForm("WindowPin test topmost owner", true));
        windows.Add("topdialog", new FixtureForm("WindowPin test topmost owned dialog", true));
        windows.Add("disposable", new FixtureForm("WindowPin test close target", false));
        windows["dialog"].Owner = windows["owner"];
        windows["topdialog"].Owner = windows["topowner"];
        foreach (FixtureForm form in windows.Values)
            form.Show();
        List<string> handles = new List<string>();
        foreach (KeyValuePair<string, FixtureForm> pair in windows)
            handles.Add(pair.Key + "=" + pair.Value.Handle.ToInt64());
        string pending = ready + ".pending";
        File.WriteAllLines(pending, handles.ToArray());
        File.Move(pending, ready);
        Application.Run(windows["normal"]);
        foreach (FixtureForm form in windows.Values)
            form.Dispose();
    }

    private sealed class FixtureForm : Form
    {
        public FixtureForm(string title, bool topmost)
        {
            Text = title;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Bounds = new Rectangle(-25000, -25000, 360, 220);
            TopMost = topmost;
        }
        protected override bool ShowWithoutActivation { get { return true; } }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rectangle);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wparam, IntPtr lparam);
}
