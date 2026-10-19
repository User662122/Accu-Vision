using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using AccuVisionRepost.Core;
using AccuVisionRepost.Ui;

namespace AccuVisionRepost
{
    internal static class Program
    {
        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int dwProcessId);

        [STAThread]
        private static int Main(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                if (a == "/selftest" || a == "--selftest")
                {
                    AttachConsole(-1);
                    string dir = (i + 1 < args.Length) ? args[i + 1] : Path.GetTempPath();
                    // Mirror console output to a file: on CI runners there is
                    // often no parent console, so stdout alone would vanish.
                    try
                    {
                        Directory.CreateDirectory(dir);
                        StreamWriter logFile = new StreamWriter(Path.Combine(dir, "selftest_log.txt"), false);
                        logFile.AutoFlush = true;
                        Console.SetOut(new TeeWriter(Console.Out, logFile));
                    }
                    catch { }
                    int code = SelfTest.Run(dir);
                    try { File.WriteAllText(Path.Combine(dir, "selftest_result.txt"), code == 0 ? "SELFTEST PASSED" : "SELFTEST FAILED " + code); }
                    catch { }
                    Console.Out.Flush();
                    return code;
                }
                if (a == "/probe" || a == "--probe")
                {
                    AttachConsole(-1);
                    if (i + 1 >= args.Length)
                    {
                        Console.WriteLine("Usage: AccuVisionRepost.exe /probe <path-to-Accurate.Mdb> [Password]");
                        return 2;
                    }
                    string pwd = (i + 2 < args.Length) ? args[i + 2] : "honda757acc";
                    return Probe(args[i + 1], pwd);
                }
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
            return 0;
        }

        private static int Probe(string dbPath, string pwd)
        {
            try
            {
                CompanyDb db = new CompanyDb(dbPath, pwd);
                DbProbe probe = db.Probe();
                Console.WriteLine("Company : " + probe.CompanyName);
                Console.WriteLine("FY      : " + Helpers.FmtDate(probe.FyFrom) + " to " + Helpers.FmtDate(probe.FyTo));
                Console.WriteLine("UID     : " + probe.Uid);
                Console.WriteLine("Accounts: " + probe.Accounts.Count + "  Items: " + probe.Items.Count + "  Series: " + probe.Series.Count);
                foreach (SeriesRow s in probe.Series)
                    Console.WriteLine(string.Format("  BT={0} Type={1} {2}  NextVch={3}", s.BT, s.TranType, s.Name, s.No1 + 1));
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("PROBE FAILED: " + ex.Message);
                return 1;
            }
        }
    }
}
