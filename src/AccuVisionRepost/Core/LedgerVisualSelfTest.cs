using System;
using System.Collections.Generic;
using System.Data.OleDb;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using AccuVisionRepost.Ui;

namespace AccuVisionRepost.Core
{
    /// <summary>Scratch-database tests for the visual model. Never accepts a live database path.</summary>
    public static class LedgerVisualSelfTest
    {
        static void Check(bool condition, string message)
        {
            Console.WriteLine((condition ? "  ok   " : "  FAIL ") + message);
            if (!condition) throw new InvalidOperationException("Visual self-test: " + message);
        }
        static string Hash(string path) { using (SHA256 h = SHA256.Create()) using (FileStream f = File.OpenRead(path)) return BitConverter.ToString(h.ComputeHash(f)); }
        public static int Run(string workDir)
        {
            try
            {
                Directory.CreateDirectory(workDir);
                string db = Path.Combine(workDir, "AccuVision_VisualTest.Mdb");
                if (File.Exists(db)) File.Delete(db);
                SelfTest.CreateScratch(db);
                SeedScenarios(db);
                string before = Hash(db);
                LedgerSnapshot s = LedgerSnapshot.Load(db, "");
                Check(Hash(db) == before, "loading the visual snapshot does not change the database");
                DateTime from = new DateTime(2026, 4, 1), to = new DateTime(2027, 3, 31);
                LedgerMetrics customer = s.Compute("CUST", from, to);
                Check(customer.Account.Kind == "Customer", "sales activity classifies a customer even outside a debtor group");
                Check(Helpers.EqAmt(customer.Opening, 1000) && Helpers.EqAmt(customer.Closing, 1700), "customer opening + sales - receipts = closing");
                Check(Helpers.EqAmt(customer.Sales, 1500) && Helpers.EqAmt(customer.Receipts, 800), "billing and cash are separate");
                Check(Helpers.EqAmt(customer.CurrentPending, 900), "partial bill settlement leaves 900 pending");
                Check(Helpers.EqAmt(customer.UnlinkedCash, 200), "unallocated cash is not silently assigned to a bill");
                Check(customer.CarriedDuplicates == 1 && customer.Bills.All(b => !b.IsOpening), "duplicate carried references are excluded and flagged");
                Check(Helpers.EqAmt(customer.ReconciliationDifference, 0), "customer movement reconciles to the master");
                Check(customer.ItemMix.Count == 2, "item quantities are not mixed across different units");
                LedgerMetrics advance = s.Compute("ADV", from, to);
                Check(Helpers.EqAmt(advance.Closing, -300) && advance.CurrentPending == 0, "customer credit is an advance/credit, not negative unpaid sales");
                Check(advance.OverAdjustedBills == 1 && Helpers.EqAmt(advance.OverAdjusted, 300), "over-adjustment is surfaced instead of hidden");
                LedgerMetrics supplier = s.Compute("SUPP", from, to);
                Check(supplier.Account.Kind == "Supplier" && Helpers.EqAmt(supplier.Purchases, 2000) && Helpers.EqAmt(supplier.Payments, 2500), "supplier purchases and payments use the correct sides");
                Check(Helpers.EqAmt(supplier.Closing, -500) && Helpers.EqAmt(supplier.CurrentPending, 200), "supplier opening payable and current pending remain distinct");
                LedgerMetrics bank = s.Compute("BANK1", from, to), bank2 = s.Compute("BANK2", from, to);
                Check(bank.Account.Kind == "Bank & cash" && Helpers.EqAmt(bank.TransfersOut, 200) && Helpers.EqAmt(bank2.TransfersIn, 200), "internal bank transfer is separated on both ledgers");
                Check(Helpers.EqAmt(bank.Closing, -100), "bank balance includes opening and all movement");
                LedgerMetrics income = s.Compute("INC", from, to), expense = s.Compute("EXP", from, to);
                Check(Helpers.EqAmt(income.Closing, 0) && Helpers.EqAmt(income.OperatingIncome, 2000), "year-end income transfer does not erase operating activity");
                Check(Helpers.EqAmt(expense.Closing, 0) && Helpers.EqAmt(expense.OperatingExpense, 2000), "year-end expense transfer does not erase operating activity");
                LedgerMetrics empty = s.Compute("EMPTY", from, to);
                Check(empty.Lines.Count == 0 && Helpers.EqAmt(empty.Closing, 777), "every ledger works even with no current activity");
                LedgerMetrics group = s.Compute("GRP", from, to);
                Check(Helpers.EqAmt(group.Opening, 1000) && Helpers.EqAmt(group.Closing, 1400), "group roll-up counts each child once");
                LedgerMetrics later = s.Compute("CUST", new DateTime(2026, 5, 1), to);
                Check(Helpers.EqAmt(later.Opening, 2000) && Helpers.EqAmt(later.Closing, 1700), "date filter carries earlier movement into its opening balance");
                LedgerOverview overview = s.Overview(from, to);
                Check(overview.ActiveLedgers >= 8 && Helpers.EqAmt(overview.Receivables, 1947) && Helpers.EqAmt(overview.CustomerAdvances, 300), "company overview separates receivables and credits");
                string reportDir = Path.Combine(workDir, "visual-test"); Directory.CreateDirectory(reportDir);
                using (LedgerVisualControl view = new LedgerVisualControl())
                {
                    view.Size = new System.Drawing.Size(1220, 850); IntPtr handle = view.Handle;
                    view.LoadSnapshotForTest(s);
                    view.SaveCanvasPng(Path.Combine(reportDir, "overview.png"));
                    foreach (string code in new string[] { "CUST", "ADV", "SUPP", "BANK1", "INC", "EMPTY" })
                    { view.SelectLedger(code); view.SaveCanvasPng(Path.Combine(reportDir, code + ".png")); Check(view.CurrentMetrics != null, "visual view renders for " + code); }
                }
                Check(Hash(db) == before, "rendering and exporting leave the database unchanged");
                Console.WriteLine("VISUALTEST PASSED"); return 0;
            }
            catch (Exception ex) { Console.WriteLine("VISUALTEST FAILED: " + ex); return 1; }
        }
        static void SeedScenarios(string db)
        {
            JetDb j = new JetDb(db, "");
            using (OleDbConnection cn = j.Open(false))
            {
                Action<string, string, double, string, string> account = delegate(string code, string name, double opening, string side, string parent)
                {
                    j.Exec(cn, null, "INSERT INTO [AccMst] ([Acc_code],[Name],[Op_bal],[Op_dbcr],[Db_amt],[Cr_amt],[Cl_bal],[Cl_dbcr],[Grp_flag],[Acc_type],[LVL]) VALUES (?,?,?,?,?,?,?,?,?,?,?)", code, name, Math.Abs(opening), opening < 0 ? "C" : "D", 0.0, 0.0, Math.Abs(opening), opening < 0 ? "C" : "D", "N", "", 3);
                    if (parent.Length > 0) j.Exec(cn, null, "INSERT INTO [AccBom] VALUES (?,?)", parent, code);
                };
                account("A000000035", "BANKS", 0, "D", "A000000001"); account("A000000004", "EXPANCES", 0, "D", "");
                account("GRP", "TEST CUSTOMER GROUP", 0, "D", "");
                j.Exec(cn, null, "UPDATE [AccMst] SET [Grp_flag]='Y' WHERE [Acc_code]='GRP'");
                account("CUST", "TEST CUSTOMER", 1000, "D", "GRP"); account("ADV", "TEST ADVANCE CUSTOMER", 0, "D", "GRP");
                account("SUPP", "TEST SUPPLIER", -1000, "C", ""); account("BANK1", "TEST BANK ONE", 1000, "D", "A000000035");
                account("BANK2", "TEST BANK TWO", 0, "D", "A000000035"); account("INC", "TEST OPERATING INCOME", 0, "D", "A000000003");
                account("EXP", "TEST OPERATING EXPENSE", 0, "D", "A000000004"); account("EMPTY", "TEST DORMANT LEDGER", 777, "D", "");
                j.Exec(cn, null, "INSERT INTO [AccBom] VALUES (?,?)", "A000000001", "A000000035");
                j.Exec(cn, null, "INSERT INTO [TranType] ([BT],[Tran_type],[Name],[ShortName]) VALUES (?,?,?,?)", "SL", "90", "TEST SALES", "TS");
                j.Exec(cn, null, "INSERT INTO [TranType] ([BT],[Tran_type],[Name],[ShortName]) VALUES (?,?,?,?)", "PU", "90", "TEST PURCHASES", "TP");
                j.Exec(cn, null, "INSERT INTO [TranType] ([BT],[Tran_type],[Name],[ShortName]) VALUES (?,?,?,?)", "RE", "90", "TEST RECEIPTS", "TR");
                j.Exec(cn, null, "INSERT INTO [TranType] ([BT],[Tran_type],[Name],[ShortName]) VALUES (?,?,?,?)", "PA", "90", "TEST PAYMENTS", "TPY");
                j.Exec(cn, null, "INSERT INTO [TranType] ([BT],[Tran_type],[Name],[ShortName]) VALUES (?,?,?,?)", "JV", "90", "P & L JV", "PL");
                int uid = 900;
                Action<string, string, DateTime, string, double, string, double, string> voucher = delegate(string bt, string id, DateTime date, string a1, double amount1, string a2, double amount2, string narration)
                {
                    uid++;
                    j.Exec(cn, null, "INSERT INTO [INF] ([BT],[ID_NO],[VchNo],[VchDate],[Tran_Type],[Acc_Code],[tran_amt],[DbCr],[Naration],[UId]) VALUES (?,?,?,?,?,?,?,?,?,?)", bt, id, id, date, "90", a1, Math.Max(amount1, amount2), "D", narration, uid);
                    j.Exec(cn, null, "INSERT INTO [EFF] ([BT],[ID_NO],[VchNo],[VchDate],[Acc_Code],[Tran_amt],[DbCr],[Naration],[Acc_Code2],[Tran_Type],[Line_no],[UId]) VALUES (?,?,?,?,?,?,?,?,?,?,?,?)", bt, id, id, date, a1, amount1, "D", narration, a2, "90", 1, uid);
                    j.Exec(cn, null, "INSERT INTO [EFF] ([BT],[ID_NO],[VchNo],[VchDate],[Acc_Code],[Tran_amt],[DbCr],[Naration],[Acc_Code2],[Tran_Type],[Line_no],[UId]) VALUES (?,?,?,?,?,?,?,?,?,?,?,?)", bt, id, id, date, a2, amount2, "C", narration, a1, "90", 2, uid);
                };
                Action<string, string, DateTime, string, double, double, string> bill = delegate(string bt, string id, DateTime date, string party, double amount, double adjusted, string reference)
                {
                    j.Exec(cn, null, "INSERT INTO [PTH] ([BT],[ID_NO],[VchNo],[VchDate],[Tran_Type],[Acc_Code],[GoodAmt],[NetAmt],[RefNo]) VALUES (?,?,?,?,?,?,?,?,?)", bt, id, id, date, "90", party, amount, amount, reference);
                    j.Exec(cn, null, "INSERT INTO [BillHist] ([BT],[ID_NO],[VchNo],[VchDate],[Tran_Type],[Acc_Code],[Tran_amt],[Adj_Amt],[RefNo],[op_flag]) VALUES (?,?,?,?,?,?,?,?,?,?)", bt, id, id, date, "90", party, amount, adjusted, reference, id.StartsWith("O-") ? "Y" : "N");
                };
                Action<string, string, DateTime, string, string, double, double> link = delegate(string bt, string id, DateTime date, string party, string billId, double amount, double discount)
                {
                    j.Exec(cn, null, "INSERT INTO [PayHist] ([BT],[ID_NO],[Our_VchNo],[BT2],[Tran_Type],[VchNo],[Acc_Code],[Adj_Amt],[Disc_Amt],[VchDate]) VALUES (?,?,?,?,?,?,?,?,?,?)", bt, id, id, bt == "RE" ? "SL" : "PU", "90", billId, party, amount, discount, date);
                };
                voucher("SL", "S1", new DateTime(2026, 4, 10), "CUST", 1000, "INC", 1000, "April goods"); bill("SL", "S1", new DateTime(2026, 4, 10), "CUST", 1000, 600, "S1");
                voucher("SL", "S2", new DateTime(2026, 5, 10), "CUST", 500, "INC", 500, "May goods"); bill("SL", "S2", new DateTime(2026, 5, 10), "CUST", 500, 0, "S2");
                voucher("RE", "R1", new DateTime(2026, 5, 20), "BANK1", 800, "CUST", 800, "Part payment and advance"); link("RE", "R1", new DateTime(2026, 5, 20), "CUST", "S1", 600, 0);
                voucher("SL", "S3", new DateTime(2026, 4, 1), "ADV", 500, "INC", 500, "Advance customer bill"); bill("SL", "S3", new DateTime(2026, 4, 1), "ADV", 500, 800, "S3");
                voucher("RE", "R2", new DateTime(2026, 4, 2), "BANK1", 800, "ADV", 800, "Advance received"); link("RE", "R2", new DateTime(2026, 4, 2), "ADV", "S3", 500, 0);
                voucher("PU", "P1", new DateTime(2026, 4, 15), "EXP", 2000, "SUPP", 2000, "Supplier purchase"); bill("PU", "P1", new DateTime(2026, 4, 15), "SUPP", 2000, 1800, "P1");
                voucher("PA", "PAY1", new DateTime(2026, 5, 1), "SUPP", 2500, "BANK1", 2500, "Supplier payment"); link("PA", "PAY1", new DateTime(2026, 5, 1), "SUPP", "P1", 1800, 0);
                voucher("PA", "TR1", new DateTime(2026, 6, 1), "BANK2", 200, "BANK1", 200, "Internal transfer");
                voucher("JV", "CL1", new DateTime(2027, 3, 31), "INC", 2000, "A000000002", 2000, "Year-end income transfer");
                voucher("JV", "CL2", new DateTime(2027, 3, 31), "A000000002", 2000, "EXP", 2000, "Year-end expense transfer");
                // Two imported references to the same opening bill: must not become two debts.
                bill("SL", "O-1", new DateTime(2024, 4, 1), "CUST", 9999, 0, "OLD"); bill("SL", "O-2", new DateTime(2025, 4, 1), "CUST", 9999, 0, "OLD");
                j.Exec(cn, null, "UPDATE [BillHist] SET [VchNo]='OLD' WHERE [ID_NO] IN ('O-1','O-2')");
                j.Exec(cn, null, "INSERT INTO [PTL] ([BT],[ID_NO],[VchNo],[Tran_Type],[ItemCode],[Unit],[Tran_Qty],[Unit_Rate],[Net_Value],[Acc_COde]) VALUES (?,?,?,?,?,?,?,?,?,?)", "SL", "S1", "S1", "90", 14, "NOS", 10.0, 100.0, 1000.0, "INC");
                j.Exec(cn, null, "INSERT INTO [PTL] ([BT],[ID_NO],[VchNo],[Tran_Type],[ItemCode],[Unit],[Tran_Qty],[Unit_Rate],[Net_Value],[Acc_COde]) VALUES (?,?,?,?,?,?,?,?,?,?)", "SL", "S2", "S2", "90", 14, "KG", 5.0, 100.0, 500.0, "INC");
                // Rebuild scratch master balances from the scenario lines, exactly once.
                foreach (System.Data.DataRow r in j.Query(cn, null, "SELECT * FROM [AccMst]").Rows)
                {
                    string code = Convert.ToString(r["Acc_code"]); double op = Convert.ToDouble(r["Op_bal"]) * (Convert.ToString(r["Op_dbcr"]) == "C" ? -1 : 1);
                    double dr = 0, cr = 0;
                    foreach (System.Data.DataRow l in j.Query(cn, null, "SELECT [Tran_amt],[DbCr] FROM [EFF] WHERE [Acc_Code]=?", code).Rows)
                    { if (Convert.ToString(l["DbCr"]) == "D") dr += Convert.ToDouble(l["Tran_amt"]); else cr += Convert.ToDouble(l["Tran_amt"]); }
                    double close = op + dr - cr;
                    j.Exec(cn, null, "UPDATE [AccMst] SET [Db_amt]=?,[Cr_amt]=?,[Cl_bal]=?,[Cl_dbcr]=? WHERE [Acc_code]=?", dr, cr, Math.Abs(close), close < 0 ? "C" : "D", code);
                }
            }
        }
    }
}
