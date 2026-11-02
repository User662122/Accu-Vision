using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace AccuVisionRepost.Core
{
    /// <summary>
    /// Command-line interface for running AccuVisionRepost completely headlessly.
    /// Supports single-voucher and batch/Excel-compatible workflows, full error
    /// and warning reporting, JSON/TSV/CSV input &amp; output, status files, and
    /// deterministic exit codes.
    /// </summary>
    public static class HeadlessCli
    {
        public static bool IsCliInvocation(string[] args)
        {
            if (args == null || args.Length == 0) return false;
            string a0 = (args[0] ?? "").Trim().ToLowerInvariant();
            if (a0 == "/gui" || a0 == "--gui" || a0 == "gui") return false;
            return true;
        }

        public static int Run(string[] args)
        {
            string logPath = FindOptionValue(args, "--log", "/log");
            StreamWriter logWriter = null;
            TextWriter origOut = Console.Out;
            TextWriter origErr = Console.Error;
            try
            {
                if (!string.IsNullOrEmpty(logPath))
                {
                    try
                    {
                        string dir = Path.GetDirectoryName(Path.GetFullPath(logPath));
                        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                        logWriter = new StreamWriter(logPath, false, Encoding.UTF8);
                        logWriter.AutoFlush = true;
                        Console.SetOut(new DualWriter(origOut, logWriter));
                        Console.SetError(new DualWriter(origErr, logWriter));
                    }
                    catch { }
                }

                return RunInternal(args);
            }
            finally
            {
                try { Console.Out.Flush(); Console.Error.Flush(); } catch { }
                if (logWriter != null)
                {
                    Console.SetOut(origOut);
                    Console.SetError(origErr);
                    try { logWriter.Dispose(); } catch { }
                }
            }
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

        private static int RunInternal(string[] rawArgs)
        {
            if (rawArgs == null || rawArgs.Length == 0)
            {
                PrintHelp();
                return 0;
            }

            string cmd = NormalizeCmd(rawArgs[0]);
            if (cmd == "help" || HasFlag(rawArgs, "--help", "-h", "/?", "/help"))
            {
                PrintHelp();
                return 0;
            }

            if (cmd == "selftest")
            {
                string dir = (rawArgs.Length > 1 && !IsSwitch(rawArgs[1])) ? rawArgs[1] : FindOptionValue(rawArgs, "--dir", "/dir");
                if (string.IsNullOrEmpty(dir)) dir = Path.GetTempPath();
                return RunSelfTestCmd(dir);
            }

            if (cmd == "uitest")
            {
                string dir = (rawArgs.Length > 1 && !IsSwitch(rawArgs[1])) ? rawArgs[1] : FindOptionValue(rawArgs, "--dir", "/dir");
                if (string.IsNullOrEmpty(dir)) dir = Path.GetTempPath();
                return SelfTest.RunUiTest(dir);
            }

            if (cmd == "visualtest")
            {
                string dir = (rawArgs.Length > 1 && !IsSwitch(rawArgs[1])) ? rawArgs[1] : FindOptionValue(rawArgs, "--dir", "/dir");
                return LedgerVisualSelfTest.Run(string.IsNullOrEmpty(dir) ? Path.GetTempPath() : dir);
            }

            CliOptions opts = new CliOptions();
            List<string> parseErrors = new List<string>();
            ParseArgs(rawArgs, opts, parseErrors);

            if (parseErrors.Count > 0)
            {
                return EmitUsageErrors(opts, cmd, parseErrors);
            }

            if (string.IsNullOrEmpty(opts.DbPath))
            {
                parseErrors.Add("Database path is required. Pass --db <path-to-Accurate.Mdb>.");
                return EmitUsageErrors(opts, cmd, parseErrors);
            }
            if (!File.Exists(opts.DbPath))
            {
                List<string> errs = new List<string>() { "Database file not found: " + opts.DbPath };
                return EmitFailure(opts, cmd, 1, errs, new List<string>(), "ERROR: Database file not found: " + opts.DbPath);
            }

            if (cmd == "visual-report")
            {
                try { return RunVisualReport(rawArgs, opts); }
                catch (Exception ex) { Console.Error.WriteLine("ERROR: " + ex); return 1; }
            }

            try
            {
                CompanyDb db = new CompanyDb(opts.DbPath, opts.Password);
                DbProbe probe = db.Probe();
                CloneEngine engine = new CloneEngine(db);

                switch (cmd)
                {
                    case "probe":
                        return RunProbe(db, probe, opts);
                    case "search":
                        return RunSearch(db, engine, opts);
                    case "inspect":
                        return RunInspect(db, engine, opts);
                    case "review":
                        return RunSingle(db, engine, opts, false);
                    case "post":
                        return RunSingle(db, engine, opts, true);
                    case "batch-review":
                        return RunBatch(db, engine, opts, false);
                    case "batch-post":
                        return RunBatch(db, engine, opts, true);
                    default:
                        parseErrors.Add("Unknown command '" + rawArgs[0] + "'. Run 'AccuVisionRepost.exe --help' for usage.");
                        return EmitUsageErrors(opts, cmd, parseErrors);
                }
            }
            catch (Exception ex)
            {
                List<string> errs = new List<string>() { ex.Message };
                return EmitFailure(opts, cmd, 1, errs, new List<string>(), "FATAL ERROR: " + ex.Message);
            }
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

        private static string NormalizeCmd(string arg)
        {
            string a = (arg ?? "").Trim().TrimStart('-', '/').ToLowerInvariant();
            switch (a)
            {
                case "":
                case "h":
                case "?":
                case "help":
                    return "help";
                case "selftest":
                case "test":
                    return "selftest";
                case "uitest":
                case "headless-ui-test":
                case "ui-smoke":
                    return "uitest";
                case "probe":
                case "info":
                case "status":
                    return "probe";
                case "search":
                case "find":
                case "list":
                    return "search";
                case "inspect":
                case "show":
                case "load":
                case "export":
                    return "inspect";
                case "review":
                case "preview":
                case "validate":
                case "check":
                case "dry-run":
                case "dryrun":
                    return "review";
                case "post":
                case "repost":
                    return "post";
                case "batch-review":
                case "batchreview":
                case "batch-preview":
                case "batch-export":
                case "batch-validate":
                    return "batch-review";
                case "batch-post":
                case "batchpost":
                case "batch-repost":
                case "batch":
                    return "batch-post";
                default:
                    return a;
            }
        }

        private static bool IsSwitch(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            return s.StartsWith("-") || s.StartsWith("/");
        }

        private static bool HasFlag(string[] args, params string[] flags)
        {
            foreach (string a in args)
                foreach (string f in flags)
                    if (string.Equals(a, f, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // ================================================================ OPTIONS PARSING

        private sealed class CliOptions
        {
            public string DbPath = "";
            public string Password = "honda757acc";
            public string Format = "table"; // table | tsv | json
            public bool JsonStdout;
            public string JsonOutPath = "";
            public string OutPath = "";
            public string StatusFilePath = "";
            public string SpecFilePath = "";
            public string ExportSpecPath = "";
            public string BatchFilePath = "";
            public string ExportBatchPath = "";

            // Probe flags
            public bool ShowAccounts;
            public string AccountFilter = "";
            public bool ShowItems;
            public string ItemFilter = "";
            public bool ShowAddons;

            // Search / Batch-search filters
            public string BT = "";
            public string Book = "";
            public string FromDate = "";
            public string ToDate = "";
            public bool PrevMonth;
            public bool LatestMonth;
            public string SearchText = "";

            // Execution controls
            public bool MakeBackup = true;
            public bool StrictWarnings;
            public bool ContinueOnError;
            public bool IgnoreAccNewRunning;

            public VoucherEditSpec Spec = new VoucherEditSpec();
        }

        private static void ParseArgs(string[] args, CliOptions opts, List<string> errors)
        {
            List<string> positionals = new List<string>();
            int i = 1; // skip command at index 0
            string cmd = NormalizeCmd(args[0]);

            while (i < args.Length)
            {
                string raw = args[i] ?? "";
                if (!IsSwitch(raw))
                {
                    positionals.Add(raw);
                    i++;
                    continue;
                }

                string key = raw;
                string val = null;
                int eq = raw.IndexOf('=');
                if (eq > 0)
                {
                    key = raw.Substring(0, eq);
                    val = raw.Substring(eq + 1);
                }
                string k = key.TrimStart('-', '/').ToLowerInvariant();

                Func<string> nextVal = delegate()
                {
                    if (val != null) return val;
                    if (i + 1 < args.Length) { i++; return args[i]; }
                    errors.Add("Option '" + key + "' requires a value.");
                    return "";
                };

                switch (k)
                {
                    case "db":
                    case "database":
                    case "mdb":
                        opts.DbPath = nextVal();
                        break;
                    case "pwd":
                    case "password":
                    case "pass":
                        opts.Password = nextVal();
                        break;
                    case "log":
                        nextVal(); // already handled by outer wrapper
                        break;
                    case "account":
                    case "ledger":
                        nextVal(); // consumed by visual-report
                        break;
                    case "json":
                        opts.JsonStdout = true;
                        opts.Format = "json";
                        break;
                    case "json-out":
                    case "jsonout":
                        opts.JsonOutPath = nextVal();
                        break;
                    case "out":
                    case "output":
                        opts.OutPath = nextVal();
                        break;
                    case "status-file":
                    case "statusfile":
                    case "status":
                        opts.StatusFilePath = nextVal();
                        break;
                    case "format":
                    case "fmt":
                        opts.Format = (nextVal() ?? "table").Trim().ToLowerInvariant();
                        if (opts.Format == "json") opts.JsonStdout = true;
                        break;
                    case "spec":
                    case "from-json":
                    case "from-spec":
                        opts.SpecFilePath = nextVal();
                        break;
                    case "export":
                    case "export-spec":
                    case "save-spec":
                        opts.ExportSpecPath = nextVal();
                        break;
                    case "batch-file":
                    case "batchfile":
                    case "in-batch":
                        opts.BatchFilePath = nextVal();
                        break;
                    case "export-batch":
                    case "out-batch":
                    case "batch-out":
                        opts.ExportBatchPath = nextVal();
                        break;
                    case "accounts":
                        opts.ShowAccounts = true;
                        if (val != null) opts.AccountFilter = val;
                        else if (i + 1 < args.Length && !IsSwitch(args[i + 1])) { i++; opts.AccountFilter = args[i]; }
                        break;
                    case "items":
                        opts.ShowItems = true;
                        if (val != null) opts.ItemFilter = val;
                        else if (i + 1 < args.Length && !IsSwitch(args[i + 1])) { i++; opts.ItemFilter = args[i]; }
                        break;
                    case "addons":
                        opts.ShowAddons = true;
                        break;

                    // Source & search selectors
                    case "uid":
                    case "src-uid":
                        long u;
                        string us = nextVal();
                        if (!long.TryParse(us, out u)) errors.Add("Invalid --uid '" + us + "' (expected integer).");
                        else opts.Spec.SrcUId = u;
                        break;
                    case "bt":
                    case "src-bt":
                    case "kind":
                        opts.BT = nextVal();
                        opts.Spec.SrcBT = opts.BT;
                        break;
                    case "book":
                    case "src-book":
                    case "tran-type":
                    case "trantype":
                        opts.Book = nextVal();
                        opts.Spec.SrcBook = opts.Book;
                        break;
                    case "vch":
                    case "vch-no":
                    case "vchno":
                    case "src-vch":
                        opts.Spec.SrcVchNo = nextVal();
                        break;
                    case "id-no":
                    case "idno":
                    case "src-id":
                        opts.Spec.SrcIDNo = nextVal();
                        break;
                    case "from":
                    case "from-date":
                        opts.FromDate = nextVal();
                        break;
                    case "to":
                    case "to-date":
                        opts.ToDate = nextVal();
                        break;
                    case "prev-month":
                    case "prevmonth":
                        opts.PrevMonth = true;
                        break;
                    case "latest-month":
                    case "latestmonth":
                        opts.LatestMonth = true;
                        break;
                    case "text":
                    case "search":
                    case "query":
                    case "q":
                        opts.SearchText = nextVal();
                        break;

                    // Target / Header edits
                    case "new-date":
                    case "newdate":
                    case "date":
                        opts.Spec.NewDate = nextVal();
                        break;
                    case "today":
                        opts.Spec.NewDate = "today";
                        break;
                    case "plus-months":
                    case "plusmonths":
                    case "add-months":
                        int pm;
                        string pms = nextVal();
                        if (!int.TryParse(pms, out pm)) errors.Add("Invalid --plus-months '" + pms + "'.");
                        else opts.Spec.PlusMonths = pm;
                        break;
                    case "new-book":
                    case "newbook":
                    case "target-book":
                        opts.Spec.NewBook = nextVal();
                        break;
                    case "bump-narration":
                        opts.Spec.BumpNarration = val == null ? true : ParseBoolStr(val, true);
                        break;
                    case "no-bump-narration":
                        opts.Spec.BumpNarration = false;
                        break;
                    case "narration":
                    case "header-narration":
                        opts.Spec.Narration = nextVal();
                        break;
                    case "replace-narration":
                        opts.Spec.ReplaceNarration.Add(nextVal());
                        break;
                    case "party":
                    case "party-code":
                    case "acc":
                        opts.Spec.Party = nextVal();
                        break;
                    case "tax-inv-mode":
                    case "taxinvmode":
                        opts.Spec.TaxInvMode = nextVal();
                        break;
                    case "tax-inv-no":
                    case "taxinvno":
                    case "inv-no":
                        opts.Spec.TaxInvNo = nextVal();
                        break;
                    case "tax-code":
                    case "taxcode":
                        opts.Spec.TaxCode = nextVal();
                        opts.Spec.AutoTaxCode = false;
                        break;
                    case "auto-tax":
                        opts.Spec.AutoTaxCode = true;
                        break;
                    case "no-auto-tax":
                        opts.Spec.AutoTaxCode = false;
                        break;
                    case "pay-mode":
                    case "paymode":
                        opts.Spec.PayMode = nextVal();
                        break;
                    case "cr-days":
                    case "crdays":
                        int crd;
                        string crds = nextVal();
                        if (!int.TryParse(crds, out crd)) errors.Add("Invalid --cr-days '" + crds + "'.");
                        else opts.Spec.CrDays = crd;
                        break;
                    case "ref-no":
                    case "refno":
                        opts.Spec.RefNo = nextVal();
                        break;
                    case "ref-date":
                    case "refdate":
                        opts.Spec.RefDate = nextVal();
                        break;
                    case "user":
                    case "user-id":
                    case "userid":
                        opts.Spec.UserId = nextVal();
                        break;

                    // Quick Sales / Generic line edits
                    case "item":
                    case "item-code":
                        opts.Spec.Item = nextVal();
                        break;
                    case "qty":
                    case "tran-qty":
                        opts.Spec.Qty = ParseCliDbl(nextVal(), "--qty", errors);
                        break;
                    case "nos":
                    case "tran-nos":
                        opts.Spec.Nos = ParseCliDbl(nextVal(), "--nos", errors);
                        break;
                    case "unit":
                        opts.Spec.Unit = nextVal();
                        break;
                    case "rate":
                    case "unit-rate":
                        opts.Spec.Rate = ParseCliDbl(nextVal(), "--rate", errors);
                        break;
                    case "item-narration":
                    case "line-narration":
                        opts.Spec.ItemNarration = nextVal();
                        break;
                    case "sgst-per":
                        opts.Spec.SgstPer = ParseCliDbl(nextVal(), "--sgst-per", errors);
                        break;
                    case "sgst-amt":
                        opts.Spec.SgstAmt = ParseCliDbl(nextVal(), "--sgst-amt", errors);
                        break;
                    case "cgst-per":
                        opts.Spec.CgstPer = ParseCliDbl(nextVal(), "--cgst-per", errors);
                        break;
                    case "cgst-amt":
                        opts.Spec.CgstAmt = ParseCliDbl(nextVal(), "--cgst-amt", errors);
                        break;
                    case "igst-per":
                        opts.Spec.IgstPer = ParseCliDbl(nextVal(), "--igst-per", errors);
                        break;
                    case "igst-amt":
                        opts.Spec.IgstAmt = ParseCliDbl(nextVal(), "--igst-amt", errors);
                        break;
                    case "amount":
                    case "amt":
                    case "tran-amt":
                        opts.Spec.Amount = ParseCliDbl(nextVal(), "--amount", errors);
                        break;
                    case "dr-acc":
                    case "dr-account":
                        opts.Spec.DrAccount = nextVal();
                        break;
                    case "cr-acc":
                    case "cr-account":
                        opts.Spec.CrAccount = nextVal();
                        break;
                    case "chq-no":
                    case "cheque-no":
                        opts.Spec.ChqNo = nextVal();
                        break;
                    case "chq-date":
                    case "cheque-date":
                        opts.Spec.ChqDate = nextVal();
                        break;
                    case "bank-name":
                    case "bank":
                        opts.Spec.BankName = nextVal();
                        break;
                    case "clear-adj":
                    case "clear-adjustments":
                        opts.Spec.ClearAdjustments = true;
                        break;

                    // Granular line specs
                    case "set-item":
                        ParseItemCliSpec("set", nextVal(), opts.Spec.ItemLines, errors);
                        break;
                    case "add-item":
                        ParseItemCliSpec("add", nextVal(), opts.Spec.ItemLines, errors);
                        break;
                    case "del-item":
                    case "delete-item":
                        ItemLineSpec delIt = new ItemLineSpec();
                        delIt.Action = "delete";
                        delIt.LineNo = Helpers.ParseInt(nextVal(), 1);
                        opts.Spec.ItemLines.Add(delIt);
                        break;
                    case "set-addon":
                        ParseAddonCliSpec("set", nextVal(), opts.Spec.AddonLines, errors);
                        break;
                    case "add-addon":
                        ParseAddonCliSpec("add", nextVal(), opts.Spec.AddonLines, errors);
                        break;
                    case "del-addon":
                    case "delete-addon":
                        AddonLineSpec delAo = new AddonLineSpec();
                        delAo.Action = "delete";
                        int aoNum = Helpers.ParseInt(nextVal(), 0);
                        if (aoNum >= 100) delAo.Line = aoNum; else delAo.AddOns = aoNum;
                        opts.Spec.AddonLines.Add(delAo);
                        break;
                    case "set-eff":
                        ParseEffCliSpec("set", nextVal(), opts.Spec.EffLines, errors);
                        break;
                    case "add-eff":
                        ParseEffCliSpec("add", nextVal(), opts.Spec.EffLines, errors);
                        break;
                    case "del-eff":
                    case "delete-eff":
                        EffLineSpec delEf = new EffLineSpec();
                        delEf.Action = "delete";
                        delEf.LineNo = Helpers.ParseInt(nextVal(), 1);
                        opts.Spec.EffLines.Add(delEf);
                        break;
                    case "set-cheque":
                        ParseChequeCliSpec("set", nextVal(), opts.Spec.ChequeLines, errors);
                        break;
                    case "add-cheque":
                        ParseChequeCliSpec("add", nextVal(), opts.Spec.ChequeLines, errors);
                        break;
                    case "del-cheque":
                        ChequeLineSpec delChq = new ChequeLineSpec();
                        delChq.Action = "delete";
                        delChq.RowIndex = Helpers.ParseInt(nextVal(), 0);
                        opts.Spec.ChequeLines.Add(delChq);
                        break;
                    case "set-adj":
                        ParsePayHistCliSpec("set", nextVal(), opts.Spec.PayHistLines, errors);
                        break;
                    case "del-adj":
                        PayHistLineSpec delPh = new PayHistLineSpec();
                        delPh.Action = "delete";
                        string phRef = nextVal();
                        int phIdx;
                        if (int.TryParse(phRef, out phIdx) && phIdx < 20) delPh.RowIndex = phIdx;
                        else delPh.BillVchNo = phRef;
                        opts.Spec.PayHistLines.Add(delPh);
                        break;

                    // Safety & batch flags
                    case "backup":
                        opts.MakeBackup = val == null ? true : ParseBoolStr(val, true);
                        break;
                    case "no-backup":
                    case "nobackup":
                        opts.MakeBackup = false;
                        break;
                    case "strict":
                    case "fail-on-warnings":
                        opts.StrictWarnings = true;
                        break;
                    case "continue-on-error":
                        opts.ContinueOnError = true;
                        break;
                    case "ignore-accnew-running":
                        opts.IgnoreAccNewRunning = true;
                        break;

                    default:
                        errors.Add("Unknown option '" + raw + "'. Run 'AccuVisionRepost.exe --help' for usage.");
                        break;
                }
                i++;
            }

            // Positional fallback: first positional is DbPath if --db was not specified,
            // second positional for 'probe' is Password (backward compatible with '/probe <db> [pwd]')
            if (string.IsNullOrEmpty(opts.DbPath) && positionals.Count > 0)
            {
                opts.DbPath = positionals[0];
                positionals.RemoveAt(0);
            }
            if (cmd == "probe" && positionals.Count > 0)
            {
                opts.Password = positionals[0];
                positionals.RemoveAt(0);
            }

            // If --spec <file.json> was passed, load it as base and apply any CLI flags on top
            if (!string.IsNullOrEmpty(opts.SpecFilePath))
            {
                if (!File.Exists(opts.SpecFilePath))
                {
                    errors.Add("Spec file not found: " + opts.SpecFilePath);
                }
                else
                {
                    try
                    {
                        object parsed = SimpleJson.Parse(File.ReadAllText(opts.SpecFilePath, Encoding.UTF8));
                        Dictionary<string, object> dict = parsed as Dictionary<string, object>;
                        if (dict == null)
                        {
                            errors.Add("Spec file '" + opts.SpecFilePath + "' must contain a JSON object.");
                        }
                        else
                        {
                            VoucherEditSpec fromFile = VoucherEditSpec.FromDictionary(dict);
                            MergeCliOntoSpec(fromFile, opts.Spec);
                            opts.Spec = fromFile;
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add("Failed to parse spec file '" + opts.SpecFilePath + "': " + ex.Message);
                    }
                }
            }
        }

        private static void MergeCliOntoSpec(VoucherEditSpec target, VoucherEditSpec cli)
        {
            if (cli.SrcUId > 0) target.SrcUId = cli.SrcUId;
            if (!string.IsNullOrEmpty(cli.SrcBT)) target.SrcBT = cli.SrcBT;
            if (!string.IsNullOrEmpty(cli.SrcBook)) target.SrcBook = cli.SrcBook;
            if (!string.IsNullOrEmpty(cli.SrcVchNo)) target.SrcVchNo = cli.SrcVchNo;
            if (!string.IsNullOrEmpty(cli.SrcIDNo)) target.SrcIDNo = cli.SrcIDNo;
            if (!string.IsNullOrEmpty(cli.NewDate)) target.NewDate = cli.NewDate;
            if (cli.PlusMonths.HasValue) target.PlusMonths = cli.PlusMonths;
            if (!string.IsNullOrEmpty(cli.NewBook)) target.NewBook = cli.NewBook;
            if (cli.BumpNarration.HasValue) target.BumpNarration = cli.BumpNarration;
            if (cli.Narration != null) target.Narration = cli.Narration;
            if (cli.ReplaceNarration.Count > 0) target.ReplaceNarration.AddRange(cli.ReplaceNarration);
            if (cli.Party != null) target.Party = cli.Party;
            if (cli.TaxInvMode != null) target.TaxInvMode = cli.TaxInvMode;
            if (cli.TaxInvNo != null) target.TaxInvNo = cli.TaxInvNo;
            if (cli.TaxCode != null) target.TaxCode = cli.TaxCode;
            if (cli.AutoTaxCode.HasValue) target.AutoTaxCode = cli.AutoTaxCode;
            if (cli.PayMode != null) target.PayMode = cli.PayMode;
            if (cli.CrDays.HasValue) target.CrDays = cli.CrDays;
            if (cli.RefNo != null) target.RefNo = cli.RefNo;
            if (cli.RefDate != null) target.RefDate = cli.RefDate;
            if (cli.UserId != null) target.UserId = cli.UserId;
            if (cli.Item != null) target.Item = cli.Item;
            if (cli.Qty.HasValue) target.Qty = cli.Qty;
            if (cli.Nos.HasValue) target.Nos = cli.Nos;
            if (cli.Unit != null) target.Unit = cli.Unit;
            if (cli.Rate.HasValue) target.Rate = cli.Rate;
            if (cli.ItemNarration != null) target.ItemNarration = cli.ItemNarration;
            if (cli.SgstPer.HasValue) target.SgstPer = cli.SgstPer;
            if (cli.SgstAmt.HasValue) target.SgstAmt = cli.SgstAmt;
            if (cli.CgstPer.HasValue) target.CgstPer = cli.CgstPer;
            if (cli.CgstAmt.HasValue) target.CgstAmt = cli.CgstAmt;
            if (cli.IgstPer.HasValue) target.IgstPer = cli.IgstPer;
            if (cli.IgstAmt.HasValue) target.IgstAmt = cli.IgstAmt;
            if (cli.Amount.HasValue) target.Amount = cli.Amount;
            if (cli.DrAccount != null) target.DrAccount = cli.DrAccount;
            if (cli.CrAccount != null) target.CrAccount = cli.CrAccount;
            if (cli.ChqNo != null) target.ChqNo = cli.ChqNo;
            if (cli.ChqDate != null) target.ChqDate = cli.ChqDate;
            if (cli.BankName != null) target.BankName = cli.BankName;
            if (cli.ClearAdjustments) target.ClearAdjustments = true;
            if (cli.ItemLines.Count > 0) target.ItemLines.AddRange(cli.ItemLines);
            if (cli.AddonLines.Count > 0) target.AddonLines.AddRange(cli.AddonLines);
            if (cli.EffLines.Count > 0) target.EffLines.AddRange(cli.EffLines);
            if (cli.ChequeLines.Count > 0) target.ChequeLines.AddRange(cli.ChequeLines);
            if (cli.PayHistLines.Count > 0) target.PayHistLines.AddRange(cli.PayHistLines);
        }

        private static bool ParseBoolStr(string s, bool def)
        {
            if (string.IsNullOrEmpty(s)) return def;
            string u = s.Trim().ToUpperInvariant();
            if (u == "1" || u == "TRUE" || u == "YES" || u == "Y" || u == "ON") return true;
            if (u == "0" || u == "FALSE" || u == "NO" || u == "N" || u == "OFF") return false;
            return def;
        }

        private static double? ParseCliDbl(string s, string flag, List<string> errors)
        {
            double d = Helpers.ParseDbl(s, double.NaN);
            if (double.IsNaN(d))
            {
                errors.Add("Invalid numeric value '" + s + "' for " + flag + ".");
                return null;
            }
            return d;
        }

        private static Dictionary<string, string> SplitKeyValSpec(string text, out string prefix)
        {
            prefix = null;
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(text)) return map;
            string body = text;
            int colon = text.IndexOf(':');
            int eq = text.IndexOf('=');
            if (colon > 0 && (eq < 0 || colon < eq))
            {
                prefix = text.Substring(0, colon).Trim();
                body = text.Substring(colon + 1);
            }
            string[] parts = body.Split(',');
            foreach (string p in parts)
            {
                int kEq = p.IndexOf('=');
                if (kEq <= 0) continue;
                string k = p.Substring(0, kEq).Trim();
                string v = p.Substring(kEq + 1).Trim();
                map[k] = v;
            }
            return map;
        }

        private static void ParseItemCliSpec(string action, string raw, List<ItemLineSpec> list, List<string> errors)
        {
            string prefix;
            Dictionary<string, string> m = SplitKeyValSpec(raw, out prefix);
            ItemLineSpec s = new ItemLineSpec();
            s.Action = action;
            if (!string.IsNullOrEmpty(prefix)) s.LineNo = Helpers.ParseInt(prefix, 1);
            string v;
            if (m.TryGetValue("line", out v) || m.TryGetValue("lineno", out v)) s.LineNo = Helpers.ParseInt(v, 1);
            if (m.TryGetValue("item", out v) || m.TryGetValue("code", out v)) s.Item = v;
            if (m.TryGetValue("qty", out v)) s.Qty = ParseCliDbl(v, "--" + action + "-item qty", errors);
            if (m.TryGetValue("nos", out v)) s.Nos = ParseCliDbl(v, "--" + action + "-item nos", errors);
            if (m.TryGetValue("unit", out v)) s.Unit = v;
            if (m.TryGetValue("rate", out v)) s.Rate = ParseCliDbl(v, "--" + action + "-item rate", errors);
            if (m.TryGetValue("narration", out v) || m.TryGetValue("nar", out v)) s.Narration = v;
            if (m.TryGetValue("acc", out v) || m.TryGetValue("acccode", out v)) s.AccCode = v;
            list.Add(s);
        }

        private static void ParseAddonCliSpec(string action, string raw, List<AddonLineSpec> list, List<string> errors)
        {
            string prefix;
            Dictionary<string, string> m = SplitKeyValSpec(raw, out prefix);
            AddonLineSpec s = new AddonLineSpec();
            s.Action = action;
            if (!string.IsNullOrEmpty(prefix))
            {
                int n = Helpers.ParseInt(prefix, 0);
                if (n >= 100) s.Line = n; else if (n > 0) s.AddOns = n;
            }
            string v;
            if (m.TryGetValue("line", out v)) s.Line = Helpers.ParseInt(v, 101);
            if (m.TryGetValue("code", out v) || m.TryGetValue("addons", out v)) s.AddOns = Helpers.ParseInt(v, 0);
            if (m.TryGetValue("type", out v) || m.TryGetValue("sign", out v)) s.Type = v;
            if (m.TryGetValue("per", out v)) s.Per = ParseCliDbl(v, "--" + action + "-addon per", errors);
            if (m.TryGetValue("amount", out v) || m.TryGetValue("amt", out v)) s.Amount = ParseCliDbl(v, "--" + action + "-addon amount", errors);
            if (m.TryGetValue("ass_val", out v) || m.TryGetValue("assval", out v)) s.AssVal = ParseCliDbl(v, "--" + action + "-addon ass_val", errors);
            if (m.TryGetValue("acc", out v) || m.TryGetValue("acccode", out v)) s.AccCode = v;
            list.Add(s);
        }

        private static void ParseEffCliSpec(string action, string raw, List<EffLineSpec> list, List<string> errors)
        {
            string prefix;
            Dictionary<string, string> m = SplitKeyValSpec(raw, out prefix);
            EffLineSpec s = new EffLineSpec();
            s.Action = action;
            if (!string.IsNullOrEmpty(prefix)) s.LineNo = Helpers.ParseInt(prefix, 1);
            string v;
            if (m.TryGetValue("line", out v) || m.TryGetValue("lineno", out v)) s.LineNo = Helpers.ParseInt(v, 1);
            if (m.TryGetValue("acc", out v) || m.TryGetValue("account", out v)) s.Account = v;
            if (m.TryGetValue("dc", out v) || m.TryGetValue("dbcr", out v) || m.TryGetValue("drcr", out v)) s.DbCr = v;
            if (m.TryGetValue("amount", out v) || m.TryGetValue("amt", out v)) s.Amount = ParseCliDbl(v, "--" + action + "-eff amount", errors);
            if (m.TryGetValue("narration", out v) || m.TryGetValue("nar", out v)) s.Narration = v;
            if (m.TryGetValue("acc2", out v) || m.TryGetValue("other", out v)) s.OtherAccount = v;
            list.Add(s);
        }

        private static void ParseChequeCliSpec(string action, string raw, List<ChequeLineSpec> list, List<string> errors)
        {
            string prefix;
            Dictionary<string, string> m = SplitKeyValSpec(raw, out prefix);
            ChequeLineSpec s = new ChequeLineSpec();
            s.Action = action;
            if (!string.IsNullOrEmpty(prefix)) s.RowIndex = Helpers.ParseInt(prefix, 0);
            string v;
            if (m.TryGetValue("index", out v) || m.TryGetValue("row", out v)) s.RowIndex = Helpers.ParseInt(v, 0);
            if (m.TryGetValue("line", out v) || m.TryGetValue("lineno", out v)) s.LineNo = Helpers.ParseInt(v, 1);
            if (m.TryGetValue("acc", out v) || m.TryGetValue("account", out v)) s.Account = v;
            if (m.TryGetValue("acc2", out v) || m.TryGetValue("other", out v)) s.OtherAccount = v;
            if (m.TryGetValue("bank", out v) || m.TryGetValue("bankname", out v)) s.BankName = v;
            if (m.TryGetValue("chqno", out v) || m.TryGetValue("chq", out v)) s.ChqNo = v;
            if (m.TryGetValue("chqdate", out v) || m.TryGetValue("date", out v)) s.ChqDate = v;
            list.Add(s);
        }

        private static void ParsePayHistCliSpec(string action, string raw, List<PayHistLineSpec> list, List<string> errors)
        {
            string prefix;
            Dictionary<string, string> m = SplitKeyValSpec(raw, out prefix);
            PayHistLineSpec s = new PayHistLineSpec();
            s.Action = action;
            if (!string.IsNullOrEmpty(prefix)) s.RowIndex = Helpers.ParseInt(prefix, 0);
            string v;
            if (m.TryGetValue("index", out v) || m.TryGetValue("row", out v)) s.RowIndex = Helpers.ParseInt(v, 0);
            if (m.TryGetValue("vch", out v) || m.TryGetValue("vchno", out v)) s.BillVchNo = v;
            if (m.TryGetValue("adj", out v) || m.TryGetValue("adjamt", out v)) s.AdjAmt = ParseCliDbl(v, "--set-adj adj", errors);
            if (m.TryGetValue("disc", out v) || m.TryGetValue("discamt", out v)) s.DiscAmt = ParseCliDbl(v, "--set-adj disc", errors);
            list.Add(s);
        }

        // ================================================================ COMMAND HANDLERS

        private static int RunVisualReport(string[] args, CliOptions opts)
        {
            LedgerSnapshot snapshot = LedgerSnapshot.Load(opts.DbPath, opts.Password);
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

        private static int RunProbe(CompanyDb db, DbProbe probe, CliOptions opts)
        {
            StringBuilder sb = new StringBuilder();
            bool accRunning = CloneEngine.IsAccNewRunning();
            bool ldbPresent = File.Exists(CloneEngine.LockFilePath(db.DbPath));

            sb.AppendLine("Company : " + probe.CompanyName);
            sb.AppendLine("FY      : " + Helpers.FmtDate(probe.FyFrom) + " to " + Helpers.FmtDate(probe.FyTo));
            sb.AppendLine("UID     : " + probe.Uid);
            sb.AppendLine("Accounts: " + probe.Accounts.Count + "  Items: " + probe.Items.Count + "  Series: " + probe.Series.Count + "  AddOns: " + probe.AddOns.Count);
            sb.AppendLine("AccNew  : " + (accRunning ? "RUNNING (close before posting)" : "Closed") + (ldbPresent ? "  [.ldb lock file present]" : ""));
            foreach (SeriesRow s in probe.Series)
            {
                sb.AppendLine(string.Format("  BT={0} Type={1} {2}  NextVch={3}  IncomeAcc={4}  RoundOff={5}",
                    s.BT, s.TranType, s.Name, s.No1 + 1, s.AccCode, s.NetRoundoff));
            }

            if (opts.ShowAccounts)
            {
                sb.AppendLine();
                sb.AppendLine("ACCOUNTS:");
                string q = Helpers.NormName(opts.AccountFilter);
                List<AccountRow> sorted = new List<AccountRow>(probe.Accounts.Values);
                sorted.Sort(delegate(AccountRow a, AccountRow b) { return string.Compare(a.Code, b.Code, StringComparison.OrdinalIgnoreCase); });
                foreach (AccountRow a in sorted)
                {
                    if (q.Length > 0 && !Helpers.NormName(a.Name).Contains(q) && a.Code.IndexOf(opts.AccountFilter.Trim(), StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    sb.AppendLine(string.Format("  {0,-12} {1,-4} {2,-40} Cl={3,12} {4}  (Dr={5}, Cr={6})",
                        a.Code, a.IsGroup ? "GRP" : "LEAF", a.Name.Trim(),
                        Helpers.FmtAmt(a.ClBal), a.ClDbCr, Helpers.FmtAmt(a.DbAmt), Helpers.FmtAmt(a.CrAmt)));
                }
            }

            if (opts.ShowItems)
            {
                sb.AppendLine();
                sb.AppendLine("ITEMS:");
                string q = Helpers.NormName(opts.ItemFilter);
                foreach (ItemRow it in probe.Items)
                {
                    if (q.Length > 0 && !Helpers.NormName(it.Name).Contains(q) && it.ItemCode.ToString() != opts.ItemFilter.Trim())
                        continue;
                    sb.AppendLine(string.Format("  ItemCode={0,-6} Unit={1,-6} Stock={2,10}  Acc={3,-12} Name={4}",
                        it.ItemCode, it.UnitCode, it.ClSt, it.AccCode.Trim(), it.Name.Trim()));
                }
            }

            if (opts.ShowAddons)
            {
                sb.AppendLine();
                sb.AppendLine("ADD-ONS:");
                foreach (AddOnRow ao in probe.AddOns.Values)
                {
                    sb.AppendLine(string.Format("  AddOns={0,-4} Sign={1} AccSL={2,-12} Name={3}",
                        ao.Code, ao.DefSign, ao.AccCodeSL, ao.Name));
                }
            }

            Dictionary<string, object> j = new Dictionary<string, object>();
            j["ok"] = true;
            j["exitCode"] = 0;
            j["command"] = "probe";
            j["companyName"] = probe.CompanyName;
            j["fyFrom"] = Helpers.FmtDateIso(probe.FyFrom);
            j["fyTo"] = Helpers.FmtDateIso(probe.FyTo);
            j["uid"] = probe.Uid;
            j["accNewRunning"] = accRunning;
            j["lockFilePresent"] = ldbPresent;
            j["accountCount"] = probe.Accounts.Count;
            j["itemCount"] = probe.Items.Count;
            List<object> serList = new List<object>();
            foreach (SeriesRow s in probe.Series)
            {
                Dictionary<string, object> sd = new Dictionary<string, object>();
                sd["bt"] = s.BT;
                sd["tranType"] = s.TranType;
                sd["name"] = s.Name;
                sd["shortName"] = s.ShortName;
                sd["lastVch"] = s.No1;
                sd["nextVch"] = s.No1 + 1;
                sd["accCode"] = s.AccCode;
                sd["netRoundoff"] = s.NetRoundoff;
                serList.Add(sd);
            }
            j["series"] = serList;
            j["errors"] = new List<object>();
            j["warnings"] = new List<object>();

            WriteOutputs(opts, sb.ToString(), j, 0, "OK", "", "");
            return 0;
        }

        private static string[] ParseBTs(string btArg)
        {
            string b = (btArg ?? "").Trim().ToUpperInvariant();
            if (b.Length == 0 || b == "ALL" || b == "ANY" || b == "*")
                return new string[] { "SL", "CB", "RE", "PA", "JV" };
            if (b == "BANK")
                return new string[] { "CB", "RE", "PA" };
            string[] parts = b.Split(new char[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++) parts[i] = parts[i].Trim();
            return parts;
        }

        private static bool ResolveSearchDates(CompanyDb db, CloneEngine engine, string[] bts, string tranType, CliOptions opts, out DateTime fromDate, out DateTime toDate, List<string> errors)
        {
            fromDate = DateTime.MinValue;
            toDate = DateTime.MinValue;

            if (opts.PrevMonth)
            {
                DateTime first = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(-1);
                fromDate = first;
                toDate = first.AddMonths(1).AddDays(-1);
            }
            else if (opts.LatestMonth)
            {
                List<VoucherSummary> all = engine.Search(bts, tranType, DateTime.MinValue, DateTime.MinValue, "");
                if (all.Count > 0)
                {
                    DateTime latest = all[0].VchDate;
                    fromDate = new DateTime(latest.Year, latest.Month, 1);
                    toDate = fromDate.AddMonths(1).AddDays(-1);
                }
            }

            if (!string.IsNullOrEmpty(opts.FromDate))
            {
                if (!Helpers.TryParseDate(opts.FromDate, out fromDate))
                {
                    errors.Add("Invalid --from date '" + opts.FromDate + "'.");
                    return false;
                }
            }
            if (!string.IsNullOrEmpty(opts.ToDate))
            {
                if (!Helpers.TryParseDate(opts.ToDate, out toDate))
                {
                    errors.Add("Invalid --to date '" + opts.ToDate + "'.");
                    return false;
                }
            }
            return true;
        }

        private static List<VoucherSummary> QueryVouchers(CompanyDb db, CloneEngine engine, CliOptions opts, List<string> errors)
        {
            string[] bts = ParseBTs(opts.BT);
            string tranType = null;
            if (!string.IsNullOrEmpty(opts.Book))
            {
                string btForBook = bts.Length == 1 ? bts[0] : "";
                SeriesRow s = HeadlessEngine.ResolveSeries(db.State, btForBook, opts.Book, errors);
                if (s == null) return new List<VoucherSummary>();
                tranType = s.TranType;
                if (bts.Length > 1) bts = new string[] { s.BT };
            }

            DateTime fromDate, toDate;
            if (!ResolveSearchDates(db, engine, bts, tranType, opts, out fromDate, out toDate, errors))
                return new List<VoucherSummary>();

            List<VoucherSummary> results = engine.Search(bts, tranType, fromDate, toDate, opts.SearchText);

            if (!string.IsNullOrEmpty(opts.Spec.Party))
            {
                string pq = Helpers.NormName(opts.Spec.Party);
                List<VoucherSummary> filtered = new List<VoucherSummary>();
                foreach (VoucherSummary v in results)
                {
                    if (string.Equals(v.AccCode, opts.Spec.Party.Trim(), StringComparison.OrdinalIgnoreCase)
                        || Helpers.NormName(v.AccName).Contains(pq))
                        filtered.Add(v);
                }
                results = filtered;
            }
            if (!string.IsNullOrEmpty(opts.Spec.SrcVchNo))
            {
                List<VoucherSummary> filtered = new List<VoucherSummary>();
                foreach (VoucherSummary v in results)
                    if (string.Equals(v.VchNoTrim, opts.Spec.SrcVchNo.Trim(), StringComparison.OrdinalIgnoreCase))
                        filtered.Add(v);
                results = filtered;
            }
            if (!string.IsNullOrEmpty(opts.Spec.SrcIDNo))
            {
                long idNum;
                string padded = long.TryParse(opts.Spec.SrcIDNo.Trim(), out idNum) ? Helpers.PadId(idNum) : opts.Spec.SrcIDNo.Trim();
                List<VoucherSummary> filtered = new List<VoucherSummary>();
                foreach (VoucherSummary v in results)
                    if (string.Equals(v.IDNo.Trim(), opts.Spec.SrcIDNo.Trim(), StringComparison.OrdinalIgnoreCase)
                        || string.Equals(v.IDNo.Trim(), padded, StringComparison.OrdinalIgnoreCase))
                        filtered.Add(v);
                results = filtered;
            }
            if (opts.Spec.SrcUId > 0)
            {
                List<VoucherSummary> filtered = new List<VoucherSummary>();
                foreach (VoucherSummary v in results)
                    if (v.UId == opts.Spec.SrcUId) filtered.Add(v);
                results = filtered;
            }
            return results;
        }

        private static int RunSearch(CompanyDb db, CloneEngine engine, CliOptions opts)
        {
            List<string> errors = new List<string>();
            List<VoucherSummary> results = QueryVouchers(db, engine, opts, errors);
            if (errors.Count > 0)
            {
                return EmitFailure(opts, "search", 3, errors, new List<string>(), "SEARCH FAILED:\r\n  " + string.Join("\r\n  ", errors.ToArray()));
            }

            double totalAmt = 0;
            List<object> jItems = new List<object>();
            foreach (VoucherSummary s in results)
            {
                totalAmt = Helpers.Round2(totalAmt + s.Amount);
                Dictionary<string, object> row = new Dictionary<string, object>();
                row["uid"] = s.UId;
                row["bt"] = s.BT;
                row["tranType"] = s.TranType;
                row["bookName"] = s.BookName;
                row["vchNo"] = s.VchNoTrim;
                row["idNo"] = s.IDNo;
                row["vchDate"] = Helpers.FmtDateIso(s.VchDate);
                row["accCode"] = s.AccCode;
                row["accName"] = s.AccName;
                row["amount"] = s.Amount;
                row["narration"] = s.Narration;
                jItems.Add(row);
            }

            StringBuilder sb = new StringBuilder();
            if (opts.Format == "tsv")
            {
                sb.AppendLine(TabularIo.JoinRow(new string[] { "UId", "Date", "BT", "BookCode", "BookName", "VchNo", "ID_NO", "AccCode", "AccName", "Amount", "Narration" }, '\t'));
                foreach (VoucherSummary s in results)
                {
                    sb.AppendLine(TabularIo.JoinRow(new string[]
                    {
                        s.UId.ToString(CultureInfo.InvariantCulture),
                        Helpers.FmtDateIso(s.VchDate),
                        s.BT,
                        s.TranType,
                        s.BookName,
                        s.VchNoTrim,
                        s.IDNo,
                        s.AccCode,
                        s.AccName,
                        Helpers.FmtNumRaw(s.Amount),
                        s.Narration
                    }, '\t'));
                }
            }
            else
            {
                sb.AppendLine(string.Format("FOUND {0} VOUCHER(S)   (Total Rs. {1})", results.Count, Helpers.FmtAmt(totalAmt)));
                sb.AppendLine(string.Format("  {0,-6} {1,-10} {2,-6} {3,-20} {4,-7} {5,-10} {6,-30} {7,12}  {8}",
                    "UId", "Date", "BT-TT", "Book", "VchNo", "ID_NO", "Party / Head Account", "Amount", "Narration"));
                sb.AppendLine("  " + new string('-', 120));
                foreach (VoucherSummary s in results)
                {
                    sb.AppendLine(string.Format("  {0,-6} {1,-10} {2,-6} {3,-20} {4,-7} {5,-10} {6,-30} {7,12}  {8}",
                        s.UId, Helpers.FmtDate(s.VchDate), s.BT + "-" + s.TranType,
                        Cut(s.BookName, 20), s.VchNoTrim, s.IDNo,
                        Cut(s.AccName, 30), Helpers.FmtAmt(s.Amount), Cut(s.Narration, 40)));
                }
            }

            Dictionary<string, object> j = new Dictionary<string, object>();
            j["ok"] = true;
            j["exitCode"] = 0;
            j["command"] = "search";
            j["count"] = results.Count;
            j["totalAmount"] = totalAmt;
            j["vouchers"] = jItems;
            j["errors"] = new List<object>();
            j["warnings"] = new List<object>();

            WriteOutputs(opts, sb.ToString(), j, 0, "OK", "", "BILLS=" + results.Count + "\r\nTOTAL_NET=" + Helpers.FmtNumRaw(totalAmt));
            return 0;
        }

        private static int RunInspect(CompanyDb db, CloneEngine engine, CliOptions opts)
        {
            List<string> errors = new List<string>();
            List<string> warnings = new List<string>();
            LoadedVoucher v = HeadlessEngine.ResolveSourceVoucher(db, engine, opts.Spec, errors);
            if (v == null)
            {
                return EmitFailure(opts, "inspect", 3, errors, warnings, "INSPECT FAILED:\r\n  ERROR: " + string.Join("\r\n  ERROR: ", errors.ToArray()));
            }

            engine.Validate(v, errors, warnings);
            VoucherEditSpec exported = HeadlessEngine.ExportToSpec(db, v);
            if (!string.IsNullOrEmpty(opts.ExportSpecPath))
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(opts.ExportSpecPath));
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(opts.ExportSpecPath, SimpleJson.Serialize(exported.ToDictionary(), true), Encoding.UTF8);
            }

            string report = HeadlessEngine.FormatInspectReport(db, v, errors, warnings);
            if (!string.IsNullOrEmpty(opts.ExportSpecPath))
                report += "\r\nExported editable spec JSON: " + opts.ExportSpecPath + "\r\n";

            Dictionary<string, object> j = new Dictionary<string, object>();
            j["ok"] = errors.Count == 0;
            j["exitCode"] = errors.Count == 0 ? 0 : 3;
            j["command"] = "inspect";
            j["spec"] = exported.ToDictionary();
            List<object> errs = new List<object>();
            foreach (string e in errors) errs.Add(e);
            j["errors"] = errs;
            List<object> warns = new List<object>();
            foreach (string w in warnings) warns.Add(w);
            j["warnings"] = warns;
            j["report"] = report;

            int code = errors.Count == 0 ? 0 : 3;
            WriteOutputs(opts, report, j, code, code == 0 ? "OK" : "ERROR", string.Join("; ", errors.ToArray()), "");
            return code;
        }

        private static int RunSingle(CompanyDb db, CloneEngine engine, CliOptions opts, bool doPost)
        {
            SingleVoucherRunResult res = HeadlessEngine.ExecuteSingle(
                db, engine, opts.Spec, doPost, opts.MakeBackup, opts.StrictWarnings, opts.IgnoreAccNewRunning);

            if (!string.IsNullOrEmpty(opts.ExportSpecPath) && res.Voucher != null)
            {
                VoucherEditSpec exported = HeadlessEngine.ExportToSpec(db, res.Voucher);
                string dir = Path.GetDirectoryName(Path.GetFullPath(opts.ExportSpecPath));
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(opts.ExportSpecPath, SimpleJson.Serialize(exported.ToDictionary(), true), Encoding.UTF8);
            }

            StringBuilder extraStatus = new StringBuilder();
            if (res.Plan != null)
            {
                extraStatus.AppendLine("NEW_VCH=" + (res.Plan.NewVchNo ?? "").Trim());
                extraStatus.AppendLine("NEW_ID_NO=" + (res.Plan.NewIDNo ?? ""));
                extraStatus.AppendLine("NEW_UID=" + res.Plan.NewUId);
                if (!string.IsNullOrEmpty(res.Plan.NewTaxInvNo))
                    extraStatus.AppendLine("NEW_TAX_INV_NO=" + res.Plan.NewTaxInvNo);
            }
            if (res.PostResult != null)
            {
                if (!string.IsNullOrEmpty(res.PostResult.BackupPath))
                    extraStatus.AppendLine("BACKUP=" + res.PostResult.BackupPath);
                if (!string.IsNullOrEmpty(res.PostResult.LogPath))
                    extraStatus.AppendLine("LOG=" + res.PostResult.LogPath);
            }
            if (res.Warnings.Count > 0)
                extraStatus.AppendLine("WARNINGS=" + string.Join("; ", res.Warnings.ToArray()));

            WriteOutputs(
                opts,
                res.TextReport,
                res.ToDictionary(),
                res.ExitCode,
                res.Ok ? "OK" : "ERROR",
                string.Join("; ", res.Errors.ToArray()),
                extraStatus.ToString().TrimEnd('\r', '\n'));

            if (!res.Ok)
            {
                Console.Error.WriteLine((doPost ? "POST FAILED" : "REVIEW VALIDATION FAILED") + " (" + res.Errors.Count + " error(s)):");
                foreach (string e in res.Errors) Console.Error.WriteLine("  ERROR: " + e);
            }
            return res.ExitCode;
        }

        private static int RunBatch(CompanyDb db, CloneEngine engine, CliOptions opts, bool doPost)
        {
            List<string> errors = new List<string>();
            List<VoucherEditSpec> specs = new List<VoucherEditSpec>();

            if (!string.IsNullOrEmpty(opts.BatchFilePath))
            {
                specs = HeadlessEngine.ReadBatchFile(opts.BatchFilePath, errors);
                if (errors.Count > 0)
                {
                    return EmitFailure(opts, doPost ? "batch-post" : "batch-review", 3, errors, new List<string>(),
                        "BATCH FILE ERROR:\r\n  ERROR: " + string.Join("\r\n  ERROR: ", errors.ToArray()));
                }
                // Apply any global CLI overrides (e.g. --new-date, --plus-months, --user) onto each row where not set in the file
                foreach (VoucherEditSpec sp in specs)
                {
                    if (!sp.Select) continue;
                    if (string.IsNullOrEmpty(sp.NewDate) && !string.IsNullOrEmpty(opts.Spec.NewDate))
                        sp.NewDate = opts.Spec.NewDate;
                    if (!sp.PlusMonths.HasValue && opts.Spec.PlusMonths.HasValue)
                        sp.PlusMonths = opts.Spec.PlusMonths;
                    if (string.IsNullOrEmpty(sp.NewBook) && !string.IsNullOrEmpty(opts.Spec.NewBook))
                        sp.NewBook = opts.Spec.NewBook;
                    if (string.IsNullOrEmpty(sp.UserId) && !string.IsNullOrEmpty(opts.Spec.UserId))
                        sp.UserId = opts.Spec.UserId;
                    if (!sp.BumpNarration.HasValue && opts.Spec.BumpNarration.HasValue)
                        sp.BumpNarration = opts.Spec.BumpNarration;
                }
            }
            else
            {
                List<VoucherSummary> matches = QueryVouchers(db, engine, opts, errors);
                if (errors.Count > 0)
                {
                    return EmitFailure(opts, doPost ? "batch-post" : "batch-review", 3, errors, new List<string>(),
                        "BATCH SEARCH ERROR:\r\n  ERROR: " + string.Join("\r\n  ERROR: ", errors.ToArray()));
                }
                // Sort in chronological + ascending voucher order (like SaleBillRepost.bas)
                matches.Sort(delegate(VoucherSummary a, VoucherSummary b)
                {
                    int c = a.VchDate.CompareTo(b.VchDate);
                    if (c != 0) return c;
                    c = string.Compare(a.TranType, b.TranType, StringComparison.OrdinalIgnoreCase);
                    if (c != 0) return c;
                    return Helpers.VchNoToLong(a.VchNo).CompareTo(Helpers.VchNoToLong(b.VchNo));
                });

                foreach (VoucherSummary m in matches)
                {
                    VoucherEditSpec sp = VoucherEditSpec.FromDictionary(opts.Spec.ToDictionary());
                    sp.Select = true;
                    sp.SrcUId = m.UId;
                    sp.SrcBT = m.BT;
                    sp.SrcBook = m.TranType;
                    sp.SrcVchNo = m.VchNoTrim;
                    sp.SrcIDNo = m.IDNo;
                    specs.Add(sp);
                }
            }

            BatchRunResult bres = HeadlessEngine.ExecuteBatch(
                db, engine, specs, doPost, opts.MakeBackup, opts.StrictWarnings, opts.ContinueOnError, opts.IgnoreAccNewRunning);

            if (!string.IsNullOrEmpty(opts.ExportBatchPath))
            {
                if (opts.ExportBatchPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    List<object> jSpecs = new List<object>();
                    for (int i = 0; i < bres.Items.Count; i++)
                    {
                        if (bres.Items[i].Voucher != null)
                            jSpecs.Add(HeadlessEngine.ExportToSpec(db, bres.Items[i].Voucher).ToDictionary());
                        else if (i < specs.Count)
                            jSpecs.Add(specs[i].ToDictionary());
                    }
                    string dir = Path.GetDirectoryName(Path.GetFullPath(opts.ExportBatchPath));
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllText(opts.ExportBatchPath, SimpleJson.Serialize(jSpecs, true), Encoding.UTF8);
                }
                else
                {
                    HeadlessEngine.WriteBatchTable(db, bres, specs, opts.ExportBatchPath);
                }
                bres.TextReport += "\r\nBatch table written to: " + opts.ExportBatchPath + "\r\n";
            }

            StringBuilder extra = new StringBuilder();
            extra.AppendLine("TOTAL_ROWS=" + bres.TotalRows);
            extra.AppendLine("SELECTED_ROWS=" + bres.SelectedRows);
            extra.AppendLine("SUCCEEDED_ROWS=" + bres.SucceededRows);
            extra.AppendLine("FAILED_ROWS=" + bres.FailedRows);
            extra.AppendLine("SKIPPED_ROWS=" + bres.SkippedRows);
            if (!string.IsNullOrEmpty(bres.BackupPath))
                extra.AppendLine("BACKUP=" + bres.BackupPath);

            WriteOutputs(
                opts,
                bres.TextReport,
                bres.ToDictionary(),
                bres.ExitCode,
                bres.Ok ? "OK" : "ERROR",
                string.Join("; ", bres.Errors.ToArray()),
                extra.ToString().TrimEnd('\r', '\n'));

            if (!bres.Ok)
            {
                Console.Error.WriteLine((doPost ? "BATCH POST FAILED" : "BATCH REVIEW FAILED") + " (" + bres.Errors.Count + " error(s)):");
                foreach (string e in bres.Errors) Console.Error.WriteLine("  ERROR: " + e);
            }
            return bres.ExitCode;
        }

        // ================================================================ OUTPUT & ERROR HELPERS

        private static int EmitUsageErrors(CliOptions opts, string cmd, List<string> errors)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("COMMAND LINE ERROR:");
            foreach (string e in errors) sb.AppendLine("  ERROR: " + e);
            sb.AppendLine("Run 'AccuVisionRepost.exe --help' for full command-line documentation.");
            return EmitFailure(opts, cmd, 2, errors, new List<string>(), sb.ToString());
        }

        private static int EmitFailure(CliOptions opts, string cmd, int exitCode, List<string> errors, List<string> warnings, string report)
        {
            Dictionary<string, object> j = new Dictionary<string, object>();
            j["ok"] = false;
            j["exitCode"] = exitCode;
            j["command"] = cmd ?? "";
            List<object> errs = new List<object>();
            foreach (string e in errors) errs.Add(e);
            j["errors"] = errs;
            List<object> warns = new List<object>();
            foreach (string w in warnings) warns.Add(w);
            j["warnings"] = warns;
            j["report"] = report;

            WriteOutputs(opts, report, j, exitCode, "ERROR", string.Join("; ", errors.ToArray()), "");
            foreach (string e in errors)
                Console.Error.WriteLine("ERROR: " + e);
            return exitCode;
        }

        private static void WriteOutputs(
            CliOptions opts,
            string textReport,
            Dictionary<string, object> jsonPayload,
            int exitCode,
            string status,
            string errorSummary,
            string extraStatusLines)
        {
            string jsonText = SimpleJson.Serialize(jsonPayload, true);

            if (opts.JsonStdout)
            {
                Console.WriteLine(jsonText);
            }
            else
            {
                Console.WriteLine((textReport ?? "").TrimEnd('\r', '\n'));
            }

            if (!string.IsNullOrEmpty(opts.JsonOutPath))
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(opts.JsonOutPath));
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(opts.JsonOutPath, jsonText, Encoding.UTF8);
            }

            if (!string.IsNullOrEmpty(opts.OutPath))
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(opts.OutPath));
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(opts.OutPath, opts.Format == "json" ? jsonText : (textReport ?? ""), Encoding.UTF8);
            }

            if (!string.IsNullOrEmpty(opts.StatusFilePath))
            {
                StringBuilder st = new StringBuilder();
                st.AppendLine("STATUS=" + status);
                st.AppendLine("EXIT_CODE=" + exitCode);
                st.AppendLine("ERROR=" + (errorSummary ?? "").Replace('\r', ' ').Replace('\n', ' '));
                if (!string.IsNullOrEmpty(extraStatusLines))
                    st.AppendLine(extraStatusLines);
                string dir = Path.GetDirectoryName(Path.GetFullPath(opts.StatusFilePath));
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(opts.StatusFilePath, st.ToString(), Encoding.UTF8);
            }
        }

        private static string Cut(string s, int max)
        {
            s = (s ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
            return s.Length <= max ? s : s.Substring(0, max - 2) + "..";
        }

        public static void PrintHelp()
        {
            Console.WriteLine(@"AccuVisionRepost.exe — GUI & Full Headless CLI for AccNew (Accurate.Mdb)
========================================================================

GUI MODE (no arguments):
  AccuVisionRepost.exe
  AccuVisionRepost.exe /gui [--headless] [--db <Accurate.Mdb>] [--pwd <password>]

HEADLESS COMMANDS:
  AccuVisionRepost.exe probe        --db <Accurate.Mdb> [--pwd <pwd>] [--accounts [q]] [--items [q]] [--addons] [--json]
  AccuVisionRepost.exe search       --db <Accurate.Mdb> [--bt SL|JV|CB|RE|PA|BANK|ALL] [--book <code>]
                                    [--from <date>] [--to <date>] [--prev-month] [--latest-month]
                                    [--party <codeOrName>] [--text <query>] [--format table|tsv|json] [--out <file>]
  AccuVisionRepost.exe inspect      --db <Accurate.Mdb> (--uid <UId> | --bt <BT> --vch <VchNo>)
                                    [--export <spec.json>] [--json]
  AccuVisionRepost.exe review       --db <Accurate.Mdb> (--uid <UId> | --bt <BT> --vch <VchNo> | --spec <spec.json>)
                                    [EDIT FLAGS...] [--strict] [--json] [--status-file <status.txt>]
  AccuVisionRepost.exe post         --db <Accurate.Mdb> (--uid <UId> | --bt <BT> --vch <VchNo> | --spec <spec.json>)
                                    [EDIT FLAGS...] [--no-backup] [--strict] [--json] [--status-file <status.txt>]
  AccuVisionRepost.exe batch-review --db <Accurate.Mdb> ([SEARCH FLAGS...] | --batch-file <review.tsv|.csv|.json>)
                                    [EDIT FLAGS...] [--export-batch <review.tsv|.csv|.json>] [--json]
  AccuVisionRepost.exe batch-post   --db <Accurate.Mdb> ([SEARCH FLAGS...] | --batch-file <review.tsv|.csv|.json>)
                                    [EDIT FLAGS...] [--out-batch <posted.tsv|.csv|.json>] [--continue-on-error]
  AccuVisionRepost.exe /selftest    [<temp-dir>]
  AccuVisionRepost.exe /uitest      [<temp-dir>]
  AccuVisionRepost.exe /visualtest  [<temp-dir>]
  AccuVisionRepost.exe visual-report --db <Accurate.Mdb> [--account <code>] [--out <prefix>]

EDIT FLAGS (for review, post, batch-review, batch-post):
  Date & Book:
    --new-date <yyyy-MM-dd|dd-MM-yyyy|today>   Set new voucher date
    --plus-months <N>                          Add N months to source date (default +1 month on load)
    --new-book <TranType_or_Name>              Target book/series (auto-remaps Sales income account)
    --narration <text>                         Override header narration
    --no-bump-narration                        Disable automatic month/date shifting inside narrations
    --replace-narration <old=new>              Find & replace text in header and line narrations
    --user <UserId>                            Operator stamp in INF.UserId

  Sales (SL) Header, Items & GST/Add-Ons:
    --party <AccCode_or_Name>                  Change customer party (updates PTH, BillHist, EFF)
    --tax-inv-mode <same|offset|manual>        TAX_INV_NO mode (0=same as VchNo, 1=keep gap, 2=manual)
    --tax-inv-no <InvNo>                       Explicit TAX_INV_NO (switches mode to manual)
    --tax-code <Code> | --no-auto-tax          Explicit TaxCode (e.g. G18, TF)
    --pay-mode <Mode>  --cr-days <Days>        Sales payment mode and credit days
    --item <ItemCode_or_Name>                  Line 1 item code or name
    --qty <N>  --nos <N>  --unit <U>           Line 1 quantity, nos, unit
    --rate <R>                                 Line 1 unit rate (recomputes Goods, GST, Round-Off, Net)
    --item-narration <text>                    Line 1 item narration
    --sgst-per <P>  --sgst-amt <A>             SGST (AddOns 4) % and/or manual amount
    --cgst-per <P>  --cgst-amt <A>             CGST (AddOns 5) % and/or manual amount
    --igst-per <P>  --igst-amt <A>             IGST (AddOns 6) % and/or manual amount
    --set-item <line>:item=..,qty=..,rate=..   Edit specific PTL line (repeatable)
    --add-item item=..,qty=..,rate=..          Add new PTL item line (repeatable)
    --del-item <line>                          Delete PTL item line

  Journal (JV) / Bank (CB) / Receipt-Payment (RE/PA):
    --amount <Amt>                             Set total voucher Dr/Cr amount
    --dr-acc <AccCode_or_Name>                 Change Debit ledger account (updates mirror Acc_Code2)
    --cr-acc <AccCode_or_Name>                 Change Credit ledger account (updates mirror Acc_Code2)
    --set-eff <line>:acc=..,dc=D|C,amt=..      Edit specific EFF ledger line (repeatable)
    --add-eff acc=..,dc=D|C,amt=..,nar=..      Add new EFF ledger line (repeatable)
    --del-eff <line>                           Delete EFF ledger line
    --chq-no <No> --chq-date <Date> --bank <B> Edit cheque/bank details in EFFLine
    --clear-adj                                Clear all bill adjustments (PayHist) on cloned RE/PA
    --set-adj <idx>:adj=..,disc=..             Edit PayHist bill adjustment row

GLOBAL OUTPUT & DIAGNOSTIC FLAGS:
  --json                 Print structured JSON (with ok, exitCode, errors[], warnings[], report) to stdout
  --json-out <file.json> Save structured JSON result to file
  --out <file>           Save text/TSV/JSON report to file
  --status-file <file>   Write KEY=VALUE status summary (STATUS=OK|ERROR, EXIT_CODE, ERROR, NEW_VCH, BACKUP...)
  --log <file>           Mirror all stdout and stderr output to a log file
  --strict               Treat any warning as an error (blocks posting)

EXIT CODES:
  0 = Success (Probe OK / Search OK / Review Valid / Posted OK / SelfTest Passed)
  1 = Runtime or database connection/IO error
  2 = Invalid command-line arguments
  3 = Validation or pre-post check error (unbalanced Dr/Cr, duplicate TAX_INV_NO, bad account/item, etc.)");
        }
    }
}
