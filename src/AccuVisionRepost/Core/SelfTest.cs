using System;
using System.Collections.Generic;
using System.Data;
using System.Data.OleDb;
using System.IO;
using System.Text;
using AccuVisionRepost.Ui;

namespace AccuVisionRepost.Core
{
    /// <summary>
    /// Headless integration test: creates a scratch Accurate.Mdb with the real
    /// table shapes (from the analysis samples), seeds masters plus one source
    /// JV, one GST sales bill and one Bank-Cash (CB) entry - using the exact
    /// values of the byte-verified manual session where applicable - then
    /// clones them with edits through CloneEngine.Post and asserts the new
    /// rows, counters, balances and stock. Run: AccuVisionRepost.exe /selftest
    /// </summary>
    public static class SelfTest
    {
        private static void Check(bool ok, string msg)
        {
            Console.WriteLine((ok ? "  ok   " : "  FAIL ") + msg);
            if (!ok) throw new InvalidOperationException("SelfTest assertion failed: " + msg);
        }

        public static int Run(string workDir)
        {
            try
            {
                Directory.CreateDirectory(workDir);
                string db = Path.Combine(workDir, "Accurate_SelfTest.Mdb");
                if (File.Exists(db)) File.Delete(db);
                CreateMdb(db);
                JetDb jet = new JetDb(db, "");
                using (OleDbConnection cn = jet.Open(false))
                {
                    CreateTables(jet, cn);
                    Seed(jet, cn);
                }
                Console.WriteLine("SelfTest DB created: " + db);

                CompanyDb company = new CompanyDb(db, "");
                DbProbe probe = company.Probe();
                Check(probe.CompanyName == "SELFTEST CO", "probe company");
                CloneEngine engine = new CloneEngine(company);

                // ---------- Journal clone with edited amounts
                List<VoucherSummary> jvs = engine.Search(new string[] { "JV" }, null, DateTime.MinValue, DateTime.MinValue, "");
                Check(jvs.Count == 1, "found source JV");
                LoadedVoucher jv = engine.Load(jvs[0].UId);
                jv.NewDate = new DateTime(2026, 5, 15);
                foreach (DataRow r in jv.Rows("EFF")) JetDb.SetVal(r, "Tran_amt", 1250.0);
                List<string> w = new List<string>();
                engine.RecalcGeneric(jv, w);
                PostResult rj = engine.Post(jv, true);
                Console.WriteLine(rj.Report);
                Check(rj.Plan.NewVchNum == 347, "JV new vch 347, got " + rj.Plan.NewVchNum);
                Check(rj.Plan.NewIDNo == "0000006307", "JV new ID_NO 0000006307, got " + rj.Plan.NewIDNo);
                DbProbe afterJv = company.Probe();
                Check(Helpers.EqAmt(afterJv.Account("A000002032").DbAmt, 999.0 + 1250.0), "NAVIN Dr rolled");
                Check(Helpers.EqAmt(afterJv.Account("A000001303").CrAmt, 999.0 + 1250.0), "DEVENDRA Cr rolled");
                Check(Helpers.EqAmt(afterJv.Account("A000000002").CrAmt, 999.0 + 1250.0), "LIABILITY group rollup Cr");
                Check(Helpers.EqAmt(afterJv.Account("A000000002").DbAmt, 999.0 + 1250.0), "LIABILITY group rollup Db");

                // ---------- Sales clone with edited rate (auto GST + round-off)
                List<VoucherSummary> sls = engine.Search(new string[] { "SL" }, null, DateTime.MinValue, DateTime.MinValue, "");
                Check(sls.Count == 1, "found source sales bill");
                LoadedVoucher sl = engine.Load(sls[0].UId);
                sl.NewDate = new DateTime(2026, 5, 20);
                DataRow ptl = sl.Rows("PTL")[0];
                JetDb.SetVal(ptl, "Unit_Rate", 210.0);
                engine.RecalcSales(sl, true, w);
                Check(Helpers.EqAmt(JetDb.GetDbl(sl.Pth, "NetAmt"), 248.0), "sales Net recomputed 248, got " + JetDb.GetDbl(sl.Pth, "NetAmt"));
                Check(Helpers.EqAmt(sl.EffTotal("D"), 248.0), "sales EFF Dr 248");
                Check(Helpers.EqAmt(sl.EffTotal("C"), 248.0), "sales EFF Cr 248");
                PostResult rs = engine.Post(sl, true);
                Console.WriteLine(rs.Report);
                Check(rs.Plan.NewVchNum == 133, "SL new vch 133, got " + rs.Plan.NewVchNum);
                Check(rs.Plan.NewTaxInvNo == "133", "SL tax inv follows vch (SameAsVch), got " + rs.Plan.NewTaxInvNo);
                DbProbe afterSl = company.Probe();
                Check(Helpers.EqAmt(afterSl.Item(14).ClSt, -144.0), "item stock -144, got " + afterSl.Item(14).ClSt);
                Check(Helpers.EqAmt(afterSl.Account("A000003017").DbAmt, 247.0 + 248.0), "party Dr after sales");
                Check(Helpers.EqAmt(afterSl.Account("A000000351").CrAmt, 209.38 + 210.20), "income Cr incl round-off");

                // ---------- Bank-Cash clone
                List<VoucherSummary> cbs = engine.Search(new string[] { "CB" }, null, DateTime.MinValue, DateTime.MinValue, "");
                Check(cbs.Count == 1, "found source CB");
                LoadedVoucher cb = engine.Load(cbs[0].UId);
                cb.NewDate = new DateTime(2026, 5, 21);
                foreach (DataRow r in cb.Rows("EFF")) JetDb.SetVal(r, "Tran_amt", 7000.0);
                Check(cb.T("EFFLine") != null && cb.Rows("EFFLine").Count == 1, "CB cheque line loaded");
                engine.RecalcGeneric(cb, w);
                PostResult rc = engine.Post(cb, true);
                Console.WriteLine(rc.Report);
                Check(rc.Plan.NewVchNum == 11, "CB new vch 11, got " + rc.Plan.NewVchNum);
                LoadedVoucher cbVerify = engine.Load(rc.Plan.NewUId);
                Check(cbVerify.Rows("EFFLine").Count == 1, "CB cheque line cloned");
                Check(JetDb.IsNull(cbVerify.Rows("EFFLine")[0], "BankDate"), "CB new cheque is un-reconciled (BankDate cleared)");

                // ---------- Headless CLI: probe, search, inspect, review, error paths, post, batch-review & batch-post
                RunHeadlessCliTests(workDir, db);

                // ---------- Headless WinForms UI: MainForm driven with HeadlessMode=true (zero modal popups)
                int uiRc = RunUiTestOnDb(workDir, db);
                Check(uiRc == 0, "Headless WinForms MainForm UI test passed");

                Console.WriteLine("SELFTEST PASSED");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("SELFTEST FAILED: " + ex);
                return 1;
            }
        }

        public static int RunUiTest(string workDir)
        {
            try
            {
                Directory.CreateDirectory(workDir);
                string db = Path.Combine(workDir, "Accurate_UiTest.Mdb");
                if (File.Exists(db)) File.Delete(db);
                CreateMdb(db);
                JetDb jet = new JetDb(db, "");
                using (OleDbConnection cn = jet.Open(false))
                {
                    CreateTables(jet, cn);
                    Seed(jet, cn);
                }
                int rc = RunUiTestOnDb(workDir, db);
                if (rc == 0) Console.WriteLine("UITEST PASSED");
                return rc;
            }
            catch (Exception ex)
            {
                Console.WriteLine("UITEST FAILED: " + ex);
                return 1;
            }
        }

        private static void RunHeadlessCliTests(string workDir, string db)
        {
            Console.WriteLine("--- Headless CLI & Error Reporting Self-Tests ---");

            // 1. Probe with JSON + status file
            string probeJson = Path.Combine(workDir, "probe_out.json");
            string probeStatus = Path.Combine(workDir, "probe_status.txt");
            int rc = HeadlessCli.Run(new string[] { "probe", "--db", db, "--pwd", "", "--accounts", "--items", "--addons", "--json-out", probeJson, "--status-file", probeStatus });
            Check(rc == 0, "CLI probe exit code 0");
            Check(File.Exists(probeJson) && File.ReadAllText(probeJson).Contains("SELFTEST CO"), "CLI probe JSON output written");
            Check(File.Exists(probeStatus) && File.ReadAllText(probeStatus).Contains("STATUS=OK"), "CLI probe status file STATUS=OK");

            // 2. Search with TSV + JSON output
            string searchTsv = Path.Combine(workDir, "search_sl.tsv");
            string searchJson = Path.Combine(workDir, "search_sl.json");
            rc = HeadlessCli.Run(new string[] { "search", "--db", db, "--pwd", "", "--bt", "SL", "--format", "tsv", "--out", searchTsv, "--json-out", searchJson });
            Check(rc == 0, "CLI search SL exit code 0");
            List<string[]> tsvRows = TabularIo.ReadFile(searchTsv, '\t');
            Check(tsvRows.Count == 3, "CLI search SL found 2 bills (+ header = 3 TSV rows), got " + tsvRows.Count);

            // 3. Inspect + export JSON spec + round-trip review from spec
            string specJson = Path.Combine(workDir, "spec_102.json");
            rc = HeadlessCli.Run(new string[] { "inspect", "--db", db, "--pwd", "", "--uid", "102", "--export", specJson });
            Check(rc == 0 && File.Exists(specJson), "CLI inspect --uid 102 exported JSON spec");

            string revStatus = Path.Combine(workDir, "rev_status.txt");
            string revJson = Path.Combine(workDir, "rev_out.json");
            rc = HeadlessCli.Run(new string[]
            {
                "review", "--db", db, "--pwd", "",
                "--spec", specJson,
                "--new-date", "2026-06-10",
                "--rate", "300",
                "--tax-inv-mode", "offset",
                "--replace-narration", "residence=commercial office",
                "--status-file", revStatus,
                "--json-out", revJson
            });
            Check(rc == 0, "CLI review from exported spec exit code 0, got " + rc);
            string revJsonText = File.ReadAllText(revJson);
            Check(revJsonText.Contains("\"netAmt\": 354") && revJsonText.Contains("commercial office"), "CLI review recomputed Net=354 (300 + 27 SGST + 27 CGST) and replaced narration");

            // 4. Error path tests ("with errors and everything"):
            // 4a. Duplicate TAX_INV_NO caught in headless review (before posting!)
            //     Note: Next VchNo is 134, and source Vch 132 already used TAX_INV_NO '134', so default 'same' mode or explicit '134' must be rejected!
            string dupInvStatus = Path.Combine(workDir, "err_dupinv_status.txt");
            string dupInvJson = Path.Combine(workDir, "err_dupinv.json");
            rc = HeadlessCli.Run(new string[]
            {
                "review", "--db", db, "--pwd", "",
                "--uid", "102", "--new-date", "2026-06-10",
                "--tax-inv-no", "134",
                "--status-file", dupInvStatus,
                "--json-out", dupInvJson
            });
            Check(rc == 3, "CLI review duplicate TAX_INV_NO returns exit code 3, got " + rc);
            Check(File.ReadAllText(dupInvStatus).Contains("STATUS=ERROR") && File.ReadAllText(dupInvStatus).Contains("already exists"),
                "CLI duplicate TAX_INV_NO recorded in status file");
            Check(File.ReadAllText(dupInvJson).Contains("\"ok\": false") && File.ReadAllText(dupInvJson).Contains("TAX Invoice No '134' already exists"),
                "CLI duplicate TAX_INV_NO recorded in JSON errors[]");

            // 4b. Out-of-financial-year date error
            string fyErrJson = Path.Combine(workDir, "err_fy.json");
            rc = HeadlessCli.Run(new string[]
            {
                "review", "--db", db, "--pwd", "",
                "--uid", "102", "--new-date", "2028-01-15",
                "--tax-inv-mode", "offset",
                "--json-out", fyErrJson
            });
            Check(rc == 3 && File.ReadAllText(fyErrJson).Contains("outside the financial year"),
                "CLI out-of-FY date rejected with exit code 3 and detailed error");

            // 4c. Group account rejection
            string grpErrJson = Path.Combine(workDir, "err_grp.json");
            rc = HeadlessCli.Run(new string[]
            {
                "review", "--db", db, "--pwd", "",
                "--uid", "102", "--new-date", "2026-06-10",
                "--tax-inv-mode", "offset",
                "--party", "A000000001",
                "--json-out", grpErrJson
            });
            Check(rc == 3 && File.ReadAllText(grpErrJson).Contains("GROUP account"),
                "CLI group account party rejected with exit code 3");

            // 4d. Unknown account/item rejection
            string unkErrJson = Path.Combine(workDir, "err_unk.json");
            rc = HeadlessCli.Run(new string[]
            {
                "review", "--db", db, "--pwd", "",
                "--uid", "102", "--new-date", "2026-06-10",
                "--tax-inv-mode", "offset",
                "--item", "NON_EXISTENT_ITEM_XYZ",
                "--json-out", unkErrJson
            });
            Check(rc == 3 && File.ReadAllText(unkErrJson).Contains("does not exist in the Item master"),
                "CLI unknown item rejected with exit code 3");

            // 4e. Unbalanced JV ledger rejection
            string unbalErrJson = Path.Combine(workDir, "err_unbal.json");
            rc = HeadlessCli.Run(new string[]
            {
                "review", "--db", db, "--pwd", "",
                "--uid", "101", "--new-date", "2026-06-10",
                "--set-eff", "1:amt=1500",
                "--json-out", unbalErrJson
            });
            Check(rc == 3 && File.ReadAllText(unbalErrJson).Contains("Ledger lines do not balance"),
                "CLI unbalanced JV rejected with exit code 3");

            // 4f. Warning vs --strict mode (CB voucher with non-Bank/Cash accounts -> warning)
            string warnJson = Path.Combine(workDir, "warn_cb.json");
            rc = HeadlessCli.Run(new string[]
            {
                "review", "--db", db, "--pwd", "",
                "--uid", "103", "--new-date", "2026-06-10",
                "--dr-acc", "A000002032", "--cr-acc", "A000001303",
                "--json-out", warnJson
            });
            Check(rc == 0 && File.ReadAllText(warnJson).Contains("Bank/Cash type account"),
                "CLI review without --strict allows warning with exit code 0");

            rc = HeadlessCli.Run(new string[]
            {
                "review", "--db", db, "--pwd", "",
                "--uid", "103", "--new-date", "2026-06-10",
                "--dr-acc", "A000002032", "--cr-acc", "A000001303",
                "--strict"
            });
            Check(rc == 3, "CLI --strict turns warning into exit code 3");

            // 4g. Bad CLI option -> exit code 2
            rc = HeadlessCli.Run(new string[] { "review", "--db", db, "--no-such-flag" });
            Check(rc == 2, "CLI unknown flag returns exit code 2, got " + rc);

            // 4h. Missing DB file -> exit code 1
            rc = HeadlessCli.Run(new string[] { "probe", "--db", Path.Combine(workDir, "missing_db_12345.Mdb") });
            Check(rc == 1, "CLI missing DB returns exit code 1, got " + rc);

            // 5. Single headless post via CLI
            string postStatus = Path.Combine(workDir, "post_sl_status.txt");
            string postJson = Path.Combine(workDir, "post_sl.json");
            rc = HeadlessCli.Run(new string[]
            {
                "post", "--db", db, "--pwd", "",
                "--bt", "SL", "--vch", "132",
                "--new-date", "2026-06-15",
                "--rate", "250",
                "--tax-inv-mode", "offset",
                "--user", "CLI",
                "--ignore-accnew-running",
                "--status-file", postStatus,
                "--json-out", postJson
            });
            Check(rc == 0, "CLI single post SL exit code 0, got " + rc);
            string postStatusText = File.ReadAllText(postStatus);
            Check(postStatusText.Contains("STATUS=OK") && postStatusText.Contains("NEW_VCH=134"),
                "CLI single post SL allocated NewVchNo 134: " + postStatusText);

            // 6. Batch review -> TSV export -> edit TSV (Select=N on two rows, Rate=400 on third) -> batch-post
            string batchTsv = Path.Combine(workDir, "batch_review.tsv");
            rc = HeadlessCli.Run(new string[]
            {
                "batch-review", "--db", db, "--pwd", "",
                "--bt", "SL",
                "--new-date", "2026-07-01",
                "--tax-inv-mode", "offset",
                "--export-batch", batchTsv
            });
            Check(rc == 0 && File.Exists(batchTsv), "CLI batch-review exported TSV, rc=" + rc);
            List<string[]> bRows = TabularIo.ReadFile(batchTsv, '\t');
            Check(bRows.Count == 4, "Batch TSV has header + 3 SL bills = 4 rows, got " + bRows.Count);

            // Find column indices in batch TSV
            Dictionary<string, int> colIdx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int c = 0; c < bRows[0].Length; c++) colIdx[bRows[0][c]] = c;
            // Skip row 1 & row 2, select only row 3 and set its Rate to 400
            bRows[1][colIdx["Select"]] = "N";
            bRows[2][colIdx["Select"]] = "N";
            bRows[3][colIdx["Select"]] = "Y";
            bRows[3][colIdx["Rate"]] = "400";
            bRows[3][colIdx["TaxInvMode"]] = "manual";
            bRows[3][colIdx["TaxInvNo"]] = "INV-BATCH-400";
            TabularIo.WriteFile(batchTsv, new List<string>(bRows[0]).ToArray(), bRows.GetRange(1, bRows.Count - 1), '\t');

            string postedBatchTsv = Path.Combine(workDir, "batch_posted.tsv");
            string batchStatus = Path.Combine(workDir, "batch_status.txt");
            rc = HeadlessCli.Run(new string[]
            {
                "batch-post", "--db", db, "--pwd", "",
                "--batch-file", batchTsv,
                "--out-batch", postedBatchTsv,
                "--status-file", batchStatus,
                "--ignore-accnew-running"
            });
            Check(rc == 0, "CLI batch-post from edited TSV exit code 0, got " + rc);
            string batchStatusText = File.ReadAllText(batchStatus);
            Check(batchStatusText.Contains("SELECTED_ROWS=1") && batchStatusText.Contains("SUCCEEDED_ROWS=1") && batchStatusText.Contains("SKIPPED_ROWS=2"),
                "CLI batch-post status counts match (1 posted, 2 skipped)");
            List<string[]> postedRows = TabularIo.ReadFile(postedBatchTsv, '\t');
            Check(Helpers.EqAmt(Helpers.ParseDbl(postedRows[3][colIdx["NetAmt"]], 0), 472.0) && postedRows[3][colIdx["Status"]] == "POSTED",
                "CLI batch-post recalculated Rate=400 -> NetAmt=472 (400 + 36 SGST + 36 CGST) and marked POSTED");
        }

        private static int RunUiTestOnDb(string workDir, string db)
        {
            Console.WriteLine("--- Headless WinForms MainForm UI Self-Test ---");
            using (MainForm form = new MainForm())
            {
                form.HeadlessMode = true;
                IntPtr handle = form.Handle; // force handle creation without showing a visible window
                Check(handle != IntPtr.Zero, "MainForm handle created in headless mode");

                bool connected = form.HeadlessConnect(db, "", "UITEST");
                Check(connected, "MainForm.HeadlessConnect succeeded without modal dialog");

                int count = form.HeadlessSearch(0, "", new DateTime(2026, 4, 1), new DateTime(2027, 3, 31));
                Check(count >= 1, "MainForm.HeadlessSearch found Sales bills: " + count);

                bool loaded = form.HeadlessSelectAndLoad(0);
                Check(loaded && form.CurrentVoucher != null, "MainForm.HeadlessSelectAndLoad loaded voucher into Edit tab");

                form.HeadlessSetNewDate(new DateTime(2026, 8, 5));
                form.HeadlessSetItemRate(0, 500.0);

                // Test live DB duplicate TAX_INV_NO detection inside MainForm Review tab
                form.HeadlessSetTaxInvManual("134");
                form.HeadlessOpenReview();
                form.HeadlessSetConfirm(true, true);
                Check(!form.IsPostButtonEnabled, "MainForm disables Post button when live DB has duplicate TAX_INV_NO '134'");
                Check(form.CurrentReviewText.Contains("TAX Invoice No '134' already exists"), "MainForm Review text displays live DB duplicate TAX_INV_NO error");

                // Switch back to unique TAX_INV_NO and post headlessly through MainForm
                form.HeadlessSetTaxInvManual("INV-UI-805");
                form.HeadlessOpenReview();
                form.HeadlessSetConfirm(true, true);
                Check(form.IsPostButtonEnabled, "MainForm enables Post button once all errors are resolved");

                PostResult uiPost = form.HeadlessClickPost();
                Check(uiPost != null && uiPost.Plan != null && uiPost.Plan.NewTaxInvNo == "INV-UI-805",
                    "MainForm.HeadlessClickPost posted new Sales bill with TAX_INV_NO 'INV-UI-805'");
            }
            return 0;
        }

        // ------------------------------------------------------------ scratch DB

        private static void CreateMdb(string path)
        {
            Type catType = Type.GetTypeFromProgID("ADOX.Catalog");
            if (catType == null) throw new InvalidOperationException("ADOX.Catalog COM is not available on this machine.");
            object cat = Activator.CreateInstance(catType);
            catType.InvokeMember("Create", System.Reflection.BindingFlags.InvokeMethod, null, cat,
                new object[] { "Provider=Microsoft.Jet.OLEDB.4.0;Data Source=" + path + ";Jet OLEDB:Engine Type=5;" });
            // ADOX stays connected to the brand-new database until every COM
            // reference is gone; without a full release + GC, later EXCLUSIVE
            // opens fail with "already opened exclusively by user 'Admin'".
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(cat);
            cat = null;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        private static void Ddl(JetDb jet, OleDbConnection cn, string sql)
        {
            jet.Exec(cn, null, sql);
        }

        private static void CreateTables(JetDb jet, OleDbConnection cn)
        {
            Ddl(jet, cn, "CREATE TABLE [Company] ([comp_name] TEXT(100), [fin_year_from] DATETIME, [fin_year_upto] DATETIME)");
            Ddl(jet, cn, "CREATE TABLE [UID] ([UID] INTEGER)");
            Ddl(jet, cn, "CREATE TABLE [Control] ([No_1] DOUBLE, [No_2] DOUBLE, [Prog_name] TEXT(20), [Series] TEXT(10), [flag] TEXT(10))");
            Ddl(jet, cn, "CREATE TABLE [TranType] ([No_1] DOUBLE, [BT] TEXT(4), [Tran_type] TEXT(4), [Name] TEXT(60), [ShortName] TEXT(10), [Type_Flag] TEXT(2), [addon_flag] TEXT(2), [Acc_Code] TEXT(20), [Acc_Code_SL] TEXT(20), [Net_Roundoff] TEXT(2), [Line_RoundOff] TEXT(2), [PayMod] DOUBLE)");
            Ddl(jet, cn, "CREATE TABLE [AccMst] ([Acc_code] TEXT(20), [Name] TEXT(100), [Op_bal] DOUBLE, [Op_dbcr] TEXT(2), [Db_amt] DOUBLE, [Cr_amt] DOUBLE, [Cl_bal] DOUBLE, [Cl_dbcr] TEXT(2), [Grp_flag] TEXT(2), [Acc_type] TEXT(4), [LVL] INTEGER)");
            Ddl(jet, cn, "CREATE TABLE [AccBom] ([parent_code] TEXT(20), [compon_code] TEXT(20))");
            Ddl(jet, cn, "CREATE TABLE [AddOns] ([AddOns] INTEGER, [disp_sr] INTEGER, [Name] TEXT(60), [Acc_Code] TEXT(20), [Acc_Code_SL] TEXT(20), [Calc_On] TEXT(10), [Flag] TEXT(10), [Sys_Flag] TEXT(10), [def_sign] TEXT(2), [FieldName] TEXT(20))");
            Ddl(jet, cn, "CREATE TABLE [Item] ([ItemCode] INTEGER, [Acc_Code] TEXT(20), [Name] TEXT(100), [Cl_St] DOUBLE, [QtyPcsPur] DOUBLE, [QtyPcsClStock] DOUBLE, [QtyMtrPur] DOUBLE, [QtyMtrClStock] DOUBLE, [Unit_Code] TEXT(10))");
            Ddl(jet, cn, "CREATE TABLE [PriseList] ([ItemCode] INTEGER, [Acc_Code] TEXT(20), [Rate] DOUBLE, [Lstdate] DATETIME)");
            Ddl(jet, cn, "CREATE TABLE [INF] ([BT] TEXT(4), [ID_NO] TEXT(20), [Acc_Code] TEXT(20), [VchNo] TEXT(20), [VchDate] DATETIME, [RefNo] TEXT(20), [RefDate] DATETIME, [Naration] TEXT(255), [Tran_Type] TEXT(4), [tran_amt] DOUBLE, [DbCr] TEXT(2), [Entry_Date] DATETIME, [UserId] TEXT(10), [Adj_Amt] DOUBLE, [TaxCode] TEXT(6), [AgentCode] INTEGER, [CForm_No] TEXT(20), [CForm_Date] DATETIME, [PayMode] TEXT(4), [TransportCode] INTEGER, [UId] INTEGER)");
            Ddl(jet, cn, "CREATE TABLE [EFF] ([BT] TEXT(4), [ID_NO] TEXT(20), [VchNo] TEXT(20), [VchDate] DATETIME, [Acc_Code] TEXT(20), [Tran_amt] DOUBLE, [DbCr] TEXT(2), [Naration] TEXT(255), [Acc_Code2] TEXT(20), [Tran_Type] TEXT(4), [Other_amt] DOUBLE, [Other_rate] DOUBLE, [Gross_qty] DOUBLE, [Line_no] INTEGER, [Check_Sys] TEXT(10), [UId] INTEGER, [Uid2] INTEGER)");
            Ddl(jet, cn, "CREATE TABLE [EFFLine] ([BT] TEXT(4), [ID_NO] TEXT(20), [VchNo] TEXT(20), [Tran_Type] TEXT(4), [Line_no] INTEGER, [Acc_Code] TEXT(20), [Acc_Code2] TEXT(20), [BankName] TEXT(60), [ChqNo] TEXT(20), [ChqDate] DATETIME, [BankDate] DATETIME)");
            Ddl(jet, cn, "CREATE TABLE [INFRem] ([BT] TEXT(4), [Tran_Type] TEXT(4), [VchNo] TEXT(20), [ID_NO] TEXT(20), [remcode] TEXT(10), [remark] TEXT(200))");
            Ddl(jet, cn, "CREATE TABLE [PTH] ([BT] TEXT(4), [ID_NO] TEXT(20), [VchNo] TEXT(20), [VchDate] DATETIME, [RefNo] TEXT(20), [RefDate] DATETIME, [Acc_Code] TEXT(20), [GoodAmt] DOUBLE, [AddAmt] DOUBLE, [LessAmt] DOUBLE, [NetAmt] DOUBLE, [TaxCode] TEXT(6), [Tran_Type] TEXT(4), [AgentCode] INTEGER, [PayMode] TEXT(4), [BT_DISP] TEXT(4), [IssueFull] TEXT(2), [Tran_Qty] DOUBLE, [Tran_Nos] DOUBLE, [TransportCode] INTEGER, [cr_day] SMALLINT, [Comm_paid] TEXT(2), [Comm_paid_Date] DATETIME, [UID] INTEGER, [UID2] INTEGER, [TAX_INV_NO] TEXT(20), [TAX_INV_DATE] DATETIME, [RefNoP] TEXT(40), [GstDate] DATETIME)");
            Ddl(jet, cn, "CREATE TABLE [PTHL] ([BT] TEXT(4), [ID_NO] TEXT(20), [VchNo] TEXT(20), [Type] TEXT(2), [AddOns] INTEGER, [Per] DOUBLE, [Amount] DOUBLE, [Acc_Code] TEXT(20), [Tran_Type] TEXT(4), [Line] INTEGER, [UID] INTEGER, [Exc_Inc] TEXT(2), [Ass_val] DOUBLE)");
            Ddl(jet, cn, "CREATE TABLE [PTL] ([BT] TEXT(4), [ID_NO] TEXT(20), [VchNo] TEXT(20), [ItemCode] INTEGER, [Unit] TEXT(10), [Tran_Qty] DOUBLE, [Unit_Rate] DOUBLE, [Value] DOUBLE, [Net_Value] DOUBLE, [Acc_COde] TEXT(20), [Tran_Type] TEXT(4), [Cut] DOUBLE, [Tran_Nos] DOUBLE, [Rate_Per] TEXT(2), [Other_Amt] DOUBLE, [Other_rate] DOUBLE, [Gross_qty] DOUBLE, [Naration] TEXT(255), [Srno] TEXT(20), [Sets] INTEGER, [Suits] DOUBLE, [Rolls] DOUBLE, [TopPcs] INTEGER, [TopCut] DOUBLE, [TopMtr] DOUBLE, [BotPcs] INTEGER, [BotCut] DOUBLE, [BotMtr] DOUBLE, [DupPcs] INTEGER, [DupCut] DOUBLE, [DupMtr] DOUBLE, [TopRecNo] TEXT(20), [BotRecNo] TEXT(20), [DupRecNo] TEXT(20), [Line_no] INTEGER, [Stock_Flag] TEXT(2), [Pur_Rate] DOUBLE, [Uid] INTEGER, [Uid2] INTEGER, [Remark1] TEXT(100), [Remark2] TEXT(100), [Entry_Rate] DOUBLE, [chno] TEXT(20), [ch_line_no] INTEGER, [BT1] TEXT(4), [ID_NO1] TEXT(20))");
            Ddl(jet, cn, "CREATE TABLE [ItemTrn] ([ItemCode] INTEGER, [BT] TEXT(4), [Tran_Date] DATETIME, [Doc_No] TEXT(20), [Doc_Date] DATETIME, [Old_Bal] DOUBLE, [Tran_Qty] DOUBLE, [New_Bal] DOUBLE, [Acc_Code] TEXT(20), [Tran_Type] TEXT(4), [New_Vch_No] TEXT(20), [Tran_Qty1] DOUBLE, [Line_no] INTEGER, [BT_DISP] TEXT(4), [Tran_Qty_NOS] DOUBLE, [ID_NO] TEXT(20), [Check_Sys] TEXT(10), [UId] INTEGER)");
            Ddl(jet, cn, "CREATE TABLE [BillHist] ([BT] TEXT(4), [ID_NO] TEXT(20), [VchNo] TEXT(20), [VchDate] DATETIME, [RefNo] TEXT(20), [RefDate] DATETIME, [Acc_Code] TEXT(20), [Tran_amt] DOUBLE, [Adj_Amt] DOUBLE, [op_flag] TEXT(4), [Tran_Type] TEXT(4), [Print_flag] TEXT(4), [Check_Sys] TEXT(10), [UID] INTEGER, [UID2] INTEGER, [Excess_Amt] DOUBLE)");
            Ddl(jet, cn, "CREATE TABLE [PayHist] ([BT] TEXT(4), [BT2] TEXT(4), [Tran_Type] TEXT(4), [VchNo] TEXT(20), [ID_NO] TEXT(20), [Our_Tran_Type] TEXT(4), [Our_VchNo] TEXT(20), [Acc_Code] TEXT(20), [VchDate] DATETIME, [Adj_Amt] DOUBLE, [Disc_Amt] DOUBLE, [Excess_Amt] DOUBLE, [Total_Amt] DOUBLE, [UID] INTEGER)");
        }

        private static void Ins(JetDb jet, OleDbConnection cn, string sql, params object[] vals)
        {
            jet.Exec(cn, null, sql, vals);
        }

        private static void Seed(JetDb jet, OleDbConnection cn)
        {
            DateTime fy1 = new DateTime(2026, 4, 1), fy2 = new DateTime(2027, 3, 31);
            Ins(jet, cn, "INSERT INTO [Company] VALUES (?,?,?)", "SELFTEST CO", fy1, fy2);
            Ins(jet, cn, "INSERT INTO [UID] VALUES (?)", 100);
            Ins(jet, cn, "INSERT INTO [Control] VALUES (?,?,?,?,?)", 6306.0, 0.0, "JV", "", "");
            Ins(jet, cn, "INSERT INTO [Control] VALUES (?,?,?,?,?)", 3252.0, 0.0, "SL", "", "");
            Ins(jet, cn, "INSERT INTO [Control] VALUES (?,?,?,?,?)", 100.0, 0.0, "CB", "", "");
            Ins(jet, cn, "INSERT INTO [TranType] VALUES (?,?,?,?,?,?,?,?,?,?,?,?)", 346.0, "JV", "03", "Journal Voucher", "JV", "A", "P", "", "", "1", "0", 0.0);
            Ins(jet, cn, "INSERT INTO [TranType] VALUES (?,?,?,?,?,?,?,?,?,?,?,?)", 132.0, "SL", "01", "Rent Sales", "SLS", "A", "P", "A000000351", "A000000049", "1", "0", 2.0);
            Ins(jet, cn, "INSERT INTO [TranType] VALUES (?,?,?,?,?,?,?,?,?,?,?,?)", 10.0, "CB", "02", "Deposit", "CBD", "A", "P", "", "", "1", "0", 0.0);

            // accounts: LIABILITY(A000000002,C) <- JV leaves; INCOMES(A000000003) <- RENT INCOME etc; ASSETS <- party/bank/cash/gst
            Action<string, string, string, string, double, double, double, string> acc = delegate(string code, string name, string opdc, string grp, double db, double cr, double cl, string cldc)
            {
                Ins(jet, cn, "INSERT INTO [AccMst] VALUES (?,?,?,?,?,?,?,?,?,?,?)", code, name, 0.0, opdc, db, cr, cl, cldc, grp, "", 1);
            };
            acc("A000000001", "ASSETS", "D", "Y", 247.0, 37.62 + 5000.0, -4790.62, "D");
            acc("A000000002", "LIABILITY", "C", "Y", 999.0, 999.0, 0.0, "C");
            acc("A000000003", "INCOMES", "D", "Y", 0.0, 209.38, 209.38, "C");
            acc("A000002032", "NAVINCHANDRA RANCHHODBHAI CHANGANI", "D", "N", 999.0, 0.0, 999.0, "D");
            acc("A000001303", "DEVENDRA PANNALAL BACHKANIWALA", "D", "N", 0.0, 999.0, 999.0, "C");
            acc("A000003017", "SHREE ENTERPRISE - RENT", "D", "N", 247.0, 0.0, 247.0, "D");
            acc("A000000351", "RENT INCOME (COMMERCIAL)", "D", "N", 0.0, 209.38, 209.38, "C");
            acc("A000000393", "S. G. S. T.", "C", "N", 0.0, 18.81, 18.81, "C");
            acc("A000000392", "C. G. S. T.", "C", "N", 0.0, 18.81, 18.81, "C");
            acc("A000000241", "BANK ACCOUNT", "D", "N", 5000.0, 0.0, 5000.0, "D");
            acc("A000000242", "CASH ACCOUNT", "D", "N", 0.0, 5000.0, -5000.0, "D");
            // fix Acc_type for bank/cash warnings: 03=cash? 04=bank (as filtered by frmBankCash)
            jet.Exec(cn, null, "UPDATE [AccMst] SET [Acc_type]='04' WHERE [Acc_code]='A000000241'");
            jet.Exec(cn, null, "UPDATE [AccMst] SET [Acc_type]='03' WHERE [Acc_code]='A000000242'");

            Action<string, string> bom = delegate(string parent, string child)
            {
                Ins(jet, cn, "INSERT INTO [AccBom] VALUES (?,?)", parent, child);
            };
            bom("A000000000", "A000000001"); bom("A000000000", "A000000002"); bom("A000000000", "A000000003");
            bom("A000000002", "A000002032"); bom("A000000002", "A000001303");
            bom("A000000001", "A000003017"); bom("A000000001", "A000000393"); bom("A000000001", "A000000392");
            bom("A000000001", "A000000241"); bom("A000000001", "A000000242");
            bom("A000000003", "A000000351");

            Ins(jet, cn, "INSERT INTO [AddOns] VALUES (?,?,?,?,?,?,?,?,?,?)", 4, 1, "SGST EXPENSES", "A000000393", "A000000393", "", "", "", "+", "SGST");
            Ins(jet, cn, "INSERT INTO [AddOns] VALUES (?,?,?,?,?,?,?,?,?,?)", 5, 2, "CGST EXPENSES", "A000000392", "A000000392", "", "", "", "+", "CGST");
            Ins(jet, cn, "INSERT INTO [AddOns] VALUES (?,?,?,?,?,?,?,?,?,?)", 6, 3, "IGST EXPENSES", "A000000391", "A000000391", "", "", "", "+", "IGST");
            Ins(jet, cn, "INSERT INTO [AddOns] VALUES (?,?,?,?,?,?,?,?,?,?)", 99, 4, "ROUND OFF", "", "", "", "", "", "+", "ROUNDOFF");
            Ins(jet, cn, "INSERT INTO [Item] VALUES (?,?,?,?,?,?,?,?,?)", 14, "", "RENT ", -143.0, -143.0, -143.0, -143.0, -143.0, "NOS");
            Ins(jet, cn, "INSERT INTO [PriseList] VALUES (?,?,?,?)", 14, "A000003017", 209.0, new DateTime(2026, 4, 29));

            DateTime d1 = new DateTime(2026, 4, 29);
            // source JV 345 (UID 101): NAVIN Dr / DEVENDRA Cr 999
            Ins(jet, cn, "INSERT INTO [INF] ([BT],[ID_NO],[Acc_Code],[VchNo],[VchDate],[RefNo],[RefDate],[Naration],[Tran_Type],[tran_amt],[DbCr],[Entry_Date],[UserId],[Adj_Amt],[TaxCode],[AgentCode],[CForm_No],[CForm_Date],[PayMode],[TransportCode],[UId]) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
                "JV", "0000006305", "A000002032", "      345", d1, null, null, "Narration on head", "03", 0.0, "D", d1, "OMY  ", 0.0, null, 0, null, null, null, 0, 101);
            Ins(jet, cn, "INSERT INTO [EFF] VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
                "JV", "0000006305", "      345", d1, "A000002032", 999.0, "D", "Narration on line 1", "A000001303", "03", 0.0, 0.0, 0.0, 1, null, 101, 1001);
            Ins(jet, cn, "INSERT INTO [EFF] VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
                "JV", "0000006305", "      345", d1, "A000001303", 999.0, "C", "Narration on line 2", "A000002032", "03", 0.0, 0.0, 0.0, 2, null, 101, 1002);

            // source Sales 132 (UID 102): 209 + 18.81 + 18.81 + ro 0.38 = 247 (manual ground truth)
            Ins(jet, cn, "INSERT INTO [INF] ([BT],[ID_NO],[Acc_Code],[VchNo],[VchDate],[RefNo],[RefDate],[Naration],[Tran_Type],[tran_amt],[DbCr],[Entry_Date],[UserId],[Adj_Amt],[TaxCode],[AgentCode],[CForm_No],[CForm_Date],[PayMode],[TransportCode],[UId]) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
                "SL", "0000003251", "A000003017", "      132", d1, "      132", d1, "Narration on head on Bill of residence ", "01", 247.0, "D", d1, "OMY  ", 0.0, "G18", 0, null, null, null, 0, 102);
            Ins(jet, cn, "INSERT INTO [PTH] VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
                "SL", "0000003251", "      132", d1, "      132", d1, "A000003017", 209.0, 38.0, 0.0, 247.0, "G18", "01", 0, "CR", null, "N", 1.0, 1.0, 0, 0, null, null, 102, 4537, "134", d1, "132", null);
            Ins(jet, cn, "INSERT INTO [PTHL] VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?)", "SL", "0000003251", "      132", "+", 4, 9.0, 18.81, "A000000393", "01", 101, 102, "E", 209.0);
            Ins(jet, cn, "INSERT INTO [PTHL] VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?)", "SL", "0000003251", "      132", "+", 5, 9.0, 18.81, "A000000392", "01", 102, 102, "E", 209.0);
            Ins(jet, cn, "INSERT INTO [PTHL] VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?)", "SL", "0000003251", "      132", "+", 99, 0.0, 0.38, "A000000351", "01", 103, 102, "E", 246.62);
            Ins(jet, cn, "INSERT INTO [PTL] ([BT],[ID_NO],[VchNo],[ItemCode],[Unit],[Tran_Qty],[Unit_Rate],[Value],[Net_Value],[Acc_COde],[Tran_Type],[Cut],[Tran_Nos],[Rate_Per],[Other_Amt],[Other_rate],[Gross_qty],[Naration],[Srno],[Sets],[Suits],[Rolls],[TopPcs],[TopCut],[TopMtr],[BotPcs],[BotCut],[BotMtr],[DupPcs],[DupCut],[DupMtr],[TopRecNo],[BotRecNo],[DupRecNo],[Line_no],[Stock_Flag],[Pur_Rate],[Uid],[Uid2],[Remark1],[Remark2],[Entry_Rate],[chno],[ch_line_no],[BT1],[ID_NO1]) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
                "SL", "0000003251", "      132", 14, "NOS", 1.0, 209.0, 209.0, 209.0, "A000000351", "01", 1.0, 1.0, "Q", 0.0, 0.0, 1.0, "Narration on line of residence", null, 0, 0.0, 0.0, 0, 0.0, 0.0, 0, 0.0, 0.0, 0, 0.0, 0.0, null, null, null, 1, "F", 0.0, 102, 6306, "", "", 209.0, "", 0, "SO", "");
            Ins(jet, cn, "INSERT INTO [EFF] VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
                "SL", "0000003251", "      132", d1, "A000003017", 247.0, "D", "Narration on head on Bill of residence ", "A000000351", "01", 0.0, 0.0, 0.0, 0, null, 102, 1003);
            Ins(jet, cn, "INSERT INTO [EFF] VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
                "SL", "0000003251", "      132", d1, "A000000351", 209.38, "C", "Narration on head on Bill of residence ", "A000003017", "01", 0.0, 0.0, 1.0, 1, null, 102, 1004);
            Ins(jet, cn, "INSERT INTO [EFF] VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
                "SL", "0000003251", "      132", d1, "A000000393", 18.81, "C", "SGST EXPENSES", "A000003017", "01", 0.0, 0.0, 0.0, 101, null, 102, 1005);
            Ins(jet, cn, "INSERT INTO [EFF] VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
                "SL", "0000003251", "      132", d1, "A000000392", 18.81, "C", "CGST EXPENSES", "A000003017", "01", 0.0, 0.0, 0.0, 102, null, 102, 1006);
            Ins(jet, cn, "INSERT INTO [ItemTrn] VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
                14, "SL", d1, "      132", d1, -142.0, 1.0, -143.0, "A000000351", "01", null, 0.0, 0, null, 1.0, "0000003251", null, 102);
            Ins(jet, cn, "INSERT INTO [BillHist] VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
                "SL", "0000003251", "      132", d1, "      132", d1, "A000003017", 247.0, 0.0, null, "01", null, null, 102, 0, 0.0);

            // source CB Deposit 10 (UID 103): Bank Dr / Cash Cr 5000 + cheque line (bank-cleared)
            Ins(jet, cn, "INSERT INTO [INF] ([BT],[ID_NO],[Acc_Code],[VchNo],[VchDate],[RefNo],[RefDate],[Naration],[Tran_Type],[tran_amt],[DbCr],[Entry_Date],[UserId],[Adj_Amt],[TaxCode],[AgentCode],[CForm_No],[CForm_Date],[PayMode],[TransportCode],[UId]) VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
                "CB", "0000000100", "A000000241", "       10", d1, null, d1, "Cash deposited", "02", 5000.0, "D", d1, "OMY  ", 0.0, null, 0, null, null, null, 0, 103);
            Ins(jet, cn, "INSERT INTO [EFF] VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
                "CB", "0000000100", "       10", d1, "A000000241", 5000.0, "D", "Cash deposited", "A000000242", "02", 0.0, 0.0, 0.0, 1, null, 103, 1007);
            Ins(jet, cn, "INSERT INTO [EFF] VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
                "CB", "0000000100", "       10", d1, "A000000242", 5000.0, "C", "Cash deposited", "A000000241", "02", 0.0, 0.0, 0.0, 2, null, 103, 1008);
            Ins(jet, cn, "INSERT INTO [EFFLine] VALUES (?,?,?,?,?,?,?,?,?,?,?)",
                "CB", "0000000100", "       10", "02", 1, "A000000242", "A000000241", "HDFC", "000111", d1, d1);
        }
    }
}
