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
            if (HeadlessCli.IsCliInvocation(args))
            {
                return HeadlessCli.Run(args);
            }

            // GUI mode: hide the console window unless --show-console was requested
            bool showConsole = false;
            bool headlessGui = false;
            string initDb = null;
            string initPwd = null;
            if (args != null)
            {
                for (int i = 0; i < args.Length; i++)
                {
                    string a = (args[i] ?? "").Trim().ToLowerInvariant();
                    if (a == "--show-console" || a == "/show-console") showConsole = true;
                    else if (a == "--headless" || a == "/headless") headlessGui = true;
                    else if ((a == "--db" || a == "/db") && i + 1 < args.Length) { i++; initDb = args[i]; }
                    else if ((a == "--pwd" || a == "/pwd") && i + 1 < args.Length) { i++; initPwd = args[i]; }
                }
            }

            if (!showConsole && !headlessGui)
            {
                try
                {
                    IntPtr hWnd = GetConsoleWindow();
                    if (hWnd != IntPtr.Zero) ShowWindow(hWnd, SW_HIDE);
                }
                catch { }
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            MainForm form = new MainForm();
            form.HeadlessMode = headlessGui;
            if (!string.IsNullOrEmpty(initDb))
            {
                form.HeadlessConnect(initDb, initPwd, null);
            }
            Application.Run(form);
            return 0;
        }
    }
}
