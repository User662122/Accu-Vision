using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using AccuVisionRepost.Core;
using AccuVisionRepost.Ui;

namespace AccuVisionRepost
{
    internal static class Program
    {
        private const int SW_HIDE = 0;

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [STAThread]
        private static int Main(string[] args)
        {
            if (args != null && args.Length > 0)
                return Diagnostics.Run(args);

            // Desktop mode: hide the diagnostic console.
            try
            {
                IntPtr hWnd = GetConsoleWindow();
                if (hWnd != IntPtr.Zero) ShowWindow(hWnd, SW_HIDE);
            }
            catch { }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            MainForm form = new MainForm();
            Application.Run(form);
            return 0;
        }
    }
}
