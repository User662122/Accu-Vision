using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AccuVisionRepost.Core
{
    // Scratch-database diagnostics and the read-only Ledger Stories report only.
    // No voucher automation or posting commands are exposed here.
    internal static class Diagnostics
    {
        public static int Run(string[] args)
        {
            string command = (args[0] ?? "").TrimStart('-', '/').ToLowerInvariant();
            try
            {
                string dir = args.Length > 1 ? args[1] : Path.GetTempPath();
                switch (command)
                {
                    case "selftest": return RunSelfTestCmd(dir);
                    case "visualtest": return LedgerVisualSelfTest.Run(dir);
                    case "visual-report": return RunVisualReport(args);
                    default:
                        Console.Error.WriteLine("Unsupported command. Use the desktop app to load, edit and post entries.");
                        return 2;
                }
            }
            catch (Exception ex) { Console.Error.WriteLine("ERROR: " + ex); return 1; }
        }

        private sealed class DualWriter : TextWriter
        {
            private readonly TextWriter _a;
            private readonly TextWriter _b;
            public DualWriter(TextWriter a, TextWriter b) { _a = a; _b = b; }
            public override Encoding Encoding { get { return Encoding.UTF8; } }
            public override void Write(char value) { try { _a.Write(value); } catch { } try { _b.Write(value); } catch { } }
            public override void Write(string value) { try { _a.Write(value); } catch { } try { _b.Write(value); } catch { } }
            public override void WriteLine(string value) { try { _a.WriteLine(value); } catch { } try { _b.WriteLine(value); } catch { } }
            public override void Flush() { try { _a.Flush(); } catch { } try { _b.Flush(); } catch { } }
        }

        private static string FindOptionValue(string[] args, params string[] names)
        {
            if (args == null) return null;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i] ?? "";
                foreach (string n in names)
                {
                    if (string.Equals(a, n, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                        return args[i + 1];
                    if (a.StartsWith(n + "=", StringComparison.OrdinalIgnoreCase))
                        return a.Substring(n.Length + 1);
                }
            }
            return null;
        }

        private static int RunSelfTestCmd(string dir)
        {
            StreamWriter logFile = null;
            TextWriter origOut = Console.Out;
            TextWriter origErr = Console.Error;
            try
            {
                Directory.CreateDirectory(dir);
                logFile = new StreamWriter(Path.Combine(dir, "selftest_log.txt"), false, Encoding.UTF8);
                logFile.AutoFlush = true;
                Console.SetOut(new DualWriter(origOut, logFile));
                Console.SetError(new DualWriter(origErr, logFile));
            }
            catch { }

            int code = SelfTest.Run(dir);
            try
            {
                File.WriteAllText(Path.Combine(dir, "selftest_result.txt"), code == 0 ? "SELFTEST PASSED" : "SELFTEST FAILED " + code);
            }
            catch { }
            try { Console.Out.Flush(); Console.Error.Flush(); } catch { }
            if (logFile != null)
            {
                Console.SetOut(origOut);
                Console.SetError(origErr);
                try { logFile.Dispose(); } catch { }
            }
            return code;
        }

        private static int RunVisualReport(string[] args)
        {
            LedgerSnapshot snapshot = LedgerSnapshot.Load(FindOptionValue(args, "--db", "/db"), FindOptionValue(args, "--pwd", "/pwd") ?? "honda757acc");
            string account = FindOptionValue(args, "--account", "/account");
            string output = FindOptionValue(args, "--out", "/out");
            if (string.IsNullOrEmpty(output)) output = Path.Combine(Path.GetTempPath(), "AccuVision-Ledger-Story");
            string dir = Path.GetDirectoryName(Path.GetFullPath(output)); if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            DateTime from = snapshot.FyFrom, to = snapshot.FyTo; DateTime parsed;
            if (Helpers.TryParseDate(FindOptionValue(args, "--from", "/from"), out parsed)) from = parsed;
            if (Helpers.TryParseDate(FindOptionValue(args, "--to", "/to"), out parsed)) to = parsed;
            LedgerMetrics m = null;
            if (!string.IsNullOrEmpty(account))
            {
                LedgerAccount a = snapshot.Accounts.FirstOrDefault(x => string.Equals(x.Code, account, StringComparison.OrdinalIgnoreCase));
                if (a == null) a = snapshot.Accounts.FirstOrDefault(x => x.Name.IndexOf(account, StringComparison.OrdinalIgnoreCase) >= 0);
                if (a == null) throw new ArgumentException("Ledger not found: " + account);
                m = snapshot.Compute(a.Code, from, to);
            }
            LedgerOverview o = snapshot.Overview(from, to);
            Dictionary<string, object> report = new Dictionary<string, object>();
            report["ok"] = true; report["readOnly"] = true; report["company"] = snapshot.Company; report["from"] = Helpers.FmtDateIso(from); report["to"] = Helpers.FmtDateIso(to);
            report["dataThrough"] = Helpers.FmtDateIso(snapshot.DataTo); report["accounts"] = snapshot.Accounts.Count; report["activeLedgers"] = o.ActiveLedgers;
            report["receivables"] = o.Receivables; report["payables"] = o.Payables; report["customerAdvances"] = o.CustomerAdvances;
            if (m != null)
            {
                report["account"] = m.Account.Code; report["name"] = m.Account.Name; report["kind"] = m.Account.Kind;
                report["opening"] = m.Opening; report["closing"] = m.Closing; report["sales"] = m.Sales; report["purchases"] = m.Purchases;
                report["receipts"] = m.Receipts; report["payments"] = m.Payments; report["currentBillsPending"] = m.CurrentPending;
                report["unlinkedCash"] = m.UnlinkedCash; report["carriedDuplicateGroups"] = m.CarriedDuplicates;
                report["reconciliationDifference"] = m.ReconciliationDifference;
                report["insights"] = m.Insights.Select(i => new Dictionary<string, object> { { "title", i.Title }, { "detail", i.Detail }, { "severity", i.Severity } }).ToList();
                report["items"] = m.ItemMix.Select(i => new Dictionary<string, object> { { "name", i.Name }, { "value", i.Value }, { "detail", i.Detail } }).ToList();
            }
            File.WriteAllText(output + ".json", SimpleJson.Serialize(report, true), new UTF8Encoding(false));
            using (AccuVisionRepost.Ui.LedgerVisualControl view = new AccuVisionRepost.Ui.LedgerVisualControl())
            {
                view.Size = new System.Drawing.Size(1220, 850); IntPtr handle = view.Handle;
                view.LoadSnapshotForTest(snapshot); if (m != null) view.SelectLedger(m.Account.Code);
                view.SaveCanvasPng(output + ".png");
            }
            Console.WriteLine("VISUAL REPORT OK (read-only)"); Console.WriteLine(output + ".json"); Console.WriteLine(output + ".png");
            return 0;
        }

    }
}
