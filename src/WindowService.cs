using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace WindowPin
{
    public sealed class WindowEntry
    {
        public IntPtr Handle { get; set; }
        public uint ProcessId { get; set; }
        public string Title { get; set; }
        public string ProcessName { get; set; }
        public bool IsTopmost { get; set; }
        public bool IsMinimized { get; set; }
    }

    public sealed class WindowService : IDisposable
    {
        private const int GwlExStyle = -20;
        private const int WsExTopmost = 0x00000008;
        private const uint GaRootOwner = 3;
        private const uint DwmwaCloaked = 14;
        private const uint PositionFlags = 0x0001 | 0x0002 | 0x0010 | 0x0200;
        private static readonly IntPtr HwndTopmost = new IntPtr(-1);
        private static readonly IntPtr HwndNotTopmost = new IntPtr(-2);
        private readonly uint ownProcessId = (uint)Process.GetCurrentProcess().Id;
        private readonly Dictionary<IntPtr, WindowBaseline> baselines = new Dictionary<IntPtr, WindowBaseline>();

        private sealed class WindowBaseline
        {
            public IntPtr Handle;
            public uint ProcessId;
            public IntPtr Family;
            public bool WasTopmost;
        }

        public int ManagedCount
        {
            get
            {
                PruneBaselines();
                int count = 0;
                foreach (WindowBaseline state in baselines.Values)
                    if (IsWindowTopmost(state.Handle) != state.WasTopmost)
                        count++;
                return count;
            }
        }

        public bool IsManaged(IntPtr hwnd)
        {
            WindowBaseline state;
            return baselines.TryGetValue(hwnd, out state) && HasIdentity(hwnd, state.ProcessId)
                && IsWindowTopmost(hwnd) != state.WasTopmost;
        }

        public List<WindowEntry> EnumerateWindows()
        {
            PruneBaselines();
            List<WindowEntry> entries = new List<WindowEntry>();
            EnumWindows(delegate(IntPtr hwnd, IntPtr parameter)
            {
                WindowEntry entry = ReadEntry(hwnd);
                if (entry != null)
                    entries.Add(entry);
                return true;
            }, IntPtr.Zero);
            entries.Sort(delegate(WindowEntry left, WindowEntry right)
            {
                int processOrder = StringComparer.CurrentCultureIgnoreCase.Compare(left.ProcessName, right.ProcessName);
                return processOrder != 0 ? processOrder : StringComparer.CurrentCultureIgnoreCase.Compare(left.Title, right.Title);
            });
            return entries;
        }

        public WindowEntry GetForegroundWindowEntry()
        {
            return ReadEntry(GetForegroundWindow());
        }

        public bool SetTopmost(WindowEntry entry, bool topmost, out string error)
        {
            error = null;
            if (entry == null || !HasIdentity(entry.Handle, entry.ProcessId))
            {
                error = "目标窗口已关闭或被重新创建，请刷新列表后重新选择。";
                return false;
            }
            if (entry.ProcessId == ownProcessId || IsShellWindow(entry.Handle))
            {
                error = "请选择其他应用的窗口。";
                return false;
            }

            PruneBaselines();
            bool previous = IsWindowTopmost(entry.Handle);
            if (previous == topmost)
            {
                entry.IsTopmost = previous;
                return true;
            }
            if (IsHungAppWindow(entry.Handle))
            {
                error = "目标应用当前无响应，请等待它恢复后重试。";
                return false;
            }

            // Windows propagates topmost changes through owner/owned dialogs. Keep
            // their original states too, including dialogs that were already topmost.
            List<WindowBaseline> family = SnapshotFamily(entry.Handle);
            bool result = SetWindowPos(entry.Handle, topmost ? HwndTopmost : HwndNotTopmost,
                0, 0, 0, 0, PositionFlags);
            int nativeError = Marshal.GetLastWin32Error();
            bool changed = false;
            foreach (WindowBaseline state in family)
                if (HasIdentity(state.Handle, state.ProcessId) && IsWindowTopmost(state.Handle) != state.WasTopmost)
                    changed = true;
            if (changed)
            {
                foreach (WindowBaseline state in family)
                    if (!baselines.ContainsKey(state.Handle) && HasIdentity(state.Handle, state.ProcessId))
                        baselines.Add(state.Handle, state);
            }
            PruneBaselines();

            if (!HasIdentity(entry.Handle, entry.ProcessId))
            {
                error = "操作期间目标窗口已关闭，请刷新列表。";
                return false;
            }
            entry.IsTopmost = IsWindowTopmost(entry.Handle);
            entry.IsMinimized = IsIconic(entry.Handle);
            if (result && entry.IsTopmost == topmost)
                return true;

            if (!result)
            {
                error = nativeError == 5
                    ? "访问被拒绝。若目标应用以管理员身份运行，请以管理员身份重新运行本工具。"
                    : "Windows 未能更改该窗口的置顶状态（错误 " + nativeError + "："
                        + new Win32Exception(nativeError).Message + "）。请刷新列表后重试。";
            }
            else
            {
                error = "目标应用未保留请求的置顶状态。它可能自行控制窗口层级；若目标以管理员身份运行，请以管理员身份重试。";
            }
            return false;
        }

        // Returns the number of windows whose original state could not be restored.
        public int RestoreAll()
        {
            PruneBaselines();
            List<WindowBaseline> states = new List<WindowBaseline>(baselines.Values);
            // Demoting an owner also demotes its dialogs. Restore originally topmost
            // dialogs last so an unrelated pre-existing pin is preserved.
            for (int pass = 0; pass < 2; pass++)
            {
                bool restoreTopmost = pass == 1;
                foreach (WindowBaseline state in states)
                {
                    if (state.WasTopmost == restoreTopmost && HasIdentity(state.Handle, state.ProcessId)
                        && IsWindowTopmost(state.Handle) != state.WasTopmost && !IsHungAppWindow(state.Handle))
                        SetWindowPos(state.Handle, restoreTopmost ? HwndTopmost : HwndNotTopmost,
                            0, 0, 0, 0, PositionFlags);
                }
            }
            PruneBaselines();
            return ManagedCount;
        }

        public void Dispose()
        {
            RestoreAll();
        }

        public static bool IsWindowTopmost(IntPtr handle)
        {
            return (GetWindowLong(handle, GwlExStyle) & WsExTopmost) != 0;
        }

        private WindowEntry ReadEntry(IntPtr hwnd)
        {
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            if (pid == 0 || pid == ownProcessId || !IsWindowVisible(hwnd) || IsShellWindow(hwnd))
                return null;
            uint cloaked;
            if (DwmGetWindowAttribute(hwnd, DwmwaCloaked, out cloaked, sizeof(uint)) == 0 && cloaked != 0)
                return null;
            int length = GetWindowTextLength(hwnd);
            if (length == 0)
                return null;
            StringBuilder title = new StringBuilder(length + 1);
            GetWindowText(hwnd, title, title.Capacity);
            if (String.IsNullOrWhiteSpace(title.ToString()) || !HasIdentity(hwnd, pid))
                return null;

            string processName = "PID " + pid;
            try
            {
                using (Process process = Process.GetProcessById((int)pid))
                    processName = process.ProcessName;
            }
            catch (ArgumentException) { return null; }
            catch (InvalidOperationException) { return null; }
            catch (Win32Exception) { /* Elevated applications can deny process queries. */ }
            return new WindowEntry
            {
                Handle = hwnd,
                ProcessId = pid,
                Title = title.ToString(),
                ProcessName = processName,
                IsTopmost = IsWindowTopmost(hwnd),
                IsMinimized = IsIconic(hwnd)
            };
        }

        private List<WindowBaseline> SnapshotFamily(IntPtr target)
        {
            IntPtr family = GetAncestor(target, GaRootOwner);
            List<WindowBaseline> states = new List<WindowBaseline>();
            EnumWindows(delegate(IntPtr hwnd, IntPtr parameter)
            {
                if (GetAncestor(hwnd, GaRootOwner) == family)
                {
                    uint pid;
                    GetWindowThreadProcessId(hwnd, out pid);
                    if (pid != 0)
                        states.Add(new WindowBaseline
                        {
                            Handle = hwnd,
                            ProcessId = pid,
                            Family = family,
                            WasTopmost = IsWindowTopmost(hwnd)
                        });
                }
                return true;
            }, IntPtr.Zero);
            return states;
        }

        private void PruneBaselines()
        {
            List<IntPtr> stale = new List<IntPtr>();
            HashSet<IntPtr> changedFamilies = new HashSet<IntPtr>();
            foreach (WindowBaseline state in baselines.Values)
            {
                if (!HasIdentity(state.Handle, state.ProcessId))
                    stale.Add(state.Handle);
                else if (IsWindowTopmost(state.Handle) != state.WasTopmost)
                    changedFamilies.Add(state.Family);
            }
            foreach (WindowBaseline state in baselines.Values)
                if (!changedFamilies.Contains(state.Family) && !stale.Contains(state.Handle))
                    stale.Add(state.Handle);
            foreach (IntPtr hwnd in stale)
                baselines.Remove(hwnd);
        }

        private static bool HasIdentity(IntPtr hwnd, uint expectedProcessId)
        {
            uint actualProcessId;
            return IsWindow(hwnd) && GetWindowThreadProcessId(hwnd, out actualProcessId) != 0
                && actualProcessId == expectedProcessId;
        }

        private static bool IsShellWindow(IntPtr hwnd)
        {
            if (hwnd == GetShellWindow() || hwnd == GetDesktopWindow())
                return true;
            StringBuilder className = new StringBuilder(256);
            GetClassName(hwnd, className, className.Capacity);
            string name = className.ToString();
            return name == "Shell_TrayWnd" || name == "Shell_SecondaryTrayWnd"
                || name == "Progman" || name == "WorkerW";
        }

        private delegate bool EnumWindowsCallback(IntPtr hwnd, IntPtr parameter);
        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);
        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        private static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")]
        private static extern IntPtr GetDesktopWindow();
        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")]
        private static extern bool IsHungAppWindow(IntPtr hwnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLength(IntPtr hwnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int maxCount);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, uint attribute, out uint value, int size);
    }
}
