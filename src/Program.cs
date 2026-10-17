using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("窗口置顶")]
[assembly: AssemblyDescription("选择任意普通桌面窗口，设置或取消置顶")]
[assembly: AssemblyCompany("WindowPin")]
[assembly: AssemblyProduct("窗口置顶")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace WindowPin
{
    internal static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [STAThread]
        private static void Main()
        {
            bool firstInstance;
            using (var mutex = new Mutex(true, @"Local\WindowPin.Desktop.v1", out firstInstance))
            {
                if (!firstInstance)
                {
                    MessageBox.Show("窗口置顶已经运行。请从任务栏或右下角托盘打开。", "窗口置顶",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                SetProcessDPIAware();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (var service = new WindowService())
                using (var form = new MainForm(service))
                    Application.Run(form);
            }
        }
    }
}
