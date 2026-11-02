using System;
using System.Collections.Generic;
using System.Data;
using System.Data.OleDb;
using System.Globalization;
using System.Linq;

namespace AccuVisionRepost.Core
{
    // Read-only analytics model. Nothing in this file inserts, updates or deletes data.
    public sealed class LedgerAccount
    {
        public string Code = "", Name = "", Parent = "", GroupPath = "", Root = "";
        public bool IsGroup;
        public double Opening, MasterClosing, Debit, Credit;
        public int Entries;
        public string Kind = "Other ledger";
    }
    public sealed class LedgerLine
    {
        public string Account, BT, Book, Voucher, Id, Narration, Contra, Category, Key;
        public DateTime Date;
        public double Amount, Signed;
        public int Uid;
        public bool IsClosing;
    }
    public sealed class LedgerBill
    {
        public string Account, BT, Book, Voucher, Id, Reference, Key;
        public DateTime Date;
        public double Amount, Adjusted, Signed;
        public int CreditDays, AgeDays;
        public bool IsOpening, OverAdjusted;
        public double Pending { get { return Math.Max(0, Amount - Adjusted); } }
    }
    public sealed class LedgerPaymentLink
    {
        public string Account, BT, BillBT, Voucher, BillVoucher, BillId;
        public DateTime Date, BillDate;
        public double Amount, Discount;
        public bool IsOpeningPayment;
        public int Days { get { return Math.Max(0, (Date.Date - BillDate.Date).Days); } }
    }
    public sealed class LedgerItemSale
    {
        public string Account, BT, Voucher, Key, Name, Unit;
        public DateTime Date;
        public int ItemCode;
        public double Quantity, Rate, Value;
    }
    public sealed class NameValue
    {
        public string Name, Detail;
        public double Value, Secondary;
        public NameValue() { }
        public NameValue(string name, double value, string detail) { Name = name; Value = value; Detail = detail; }
    }
    public sealed class LedgerMonth
    {
        public DateTime Month;
        public double Billing, Cash, MoneyIn, MoneyOut, Purchases, Payments, Income, Expense, Journals, Transfers, Closing, Balance;
        public int Entries;
    }
    public sealed class LedgerInsight
    {
        public string Title, Detail, Severity;
        public LedgerInsight(string title, string detail, string severity) { Title = title; Detail = detail; Severity = severity; }
    }
    public sealed class LedgerMetrics
    {
        public LedgerAccount Account;
        public DateTime From, To, DataTo, LastActivity;
        public double Opening, Closing, Debit, Credit, Sales, Purchases, Receipts, Payments, CashIn, CashOut;
        public double JournalNet, OtherNet, ClosingTransfers, TransfersIn, TransfersOut, OperatingIncome, OperatingExpense;
        public double CurrentBilled, CurrentSettled, CurrentPending, OverAdjusted, UnlinkedCash, ReconciliationDifference;
        public double LinkedCash, MedianPaymentDays, CashCoverage;
        public int ActiveDays, LinkedPayments, CarriedDuplicates, OverAdjustedBills;
        public List<LedgerLine> Lines = new List<LedgerLine>();
        public List<LedgerBill> Bills = new List<LedgerBill>();
        public List<LedgerPaymentLink> Links = new List<LedgerPaymentLink>();
        public List<LedgerItemSale> Items = new List<LedgerItemSale>();
        public List<LedgerMonth> Months = new List<LedgerMonth>();
        public List<NameValue> Categories = new List<NameValue>(), Sources = new List<NameValue>(), Destinations = new List<NameValue>(), ItemMix = new List<NameValue>();
        public List<LedgerInsight> Insights = new List<LedgerInsight>();
        public double[] Aging = new double[4];
        public string BalanceLabel { get { return Math.Abs(Closing) < .005 ? "Balanced" : (Closing > 0 ? "Debit balance" : "Credit balance"); } }
    }
    public sealed class LedgerOverview
    {
        public DateTime From, To, DataTo;
        public int ActiveLedgers, CustomerCount, SupplierCount, AttentionCount;
        public double Sales, Purchases, Receipts, Payments, Receivables, Payables, CustomerAdvances, SupplierAdvances, BankBalance;
        public List<LedgerMetrics> Ledgers = new List<LedgerMetrics>();
        public List<LedgerMonth> Months = new List<LedgerMonth>();
        public List<LedgerInsight> Insights = new List<LedgerInsight>();
    }

    public sealed class LedgerSnapshot
    {
        public string Company = "", DbPath = "";
        public DateTime FyFrom, FyTo, DataFrom, DataTo, LoadedAt;
        public readonly List<LedgerAccount> Accounts = new List<LedgerAccount>();
        public readonly List<LedgerLine> Lines = new List<LedgerLine>();
        public readonly List<LedgerBill> Bills = new List<LedgerBill>();
        public readonly List<LedgerPaymentLink> Links = new List<LedgerPaymentLink>();
        public readonly List<LedgerItemSale> ItemSales = new List<LedgerItemSale>();
        public readonly Dictionary<string, string> Parents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, string> Books = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, LedgerAccount> _accounts = new Dictionary<string, LedgerAccount>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, List<LedgerLine>> _lines = new Dictionary<string, List<LedgerLine>>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, List<LedgerBill>> _bills = new Dictionary<string, List<LedgerBill>>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, List<LedgerPaymentLink>> _links = new Dictionary<string, List<LedgerPaymentLink>>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, List<LedgerItemSale>> _items = new Dictionary<string, List<LedgerItemSale>>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, HashSet<string>> _voucherAccounts = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> _closingBooks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> _salesParties = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> _purchaseParties = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static string S(object v) { return v == null || v == DBNull.Value ? "" : Convert.ToString(v).Trim(); }
        static double D(object v) { return v == null || v == DBNull.Value ? 0 : Convert.ToDouble(v, CultureInfo.InvariantCulture); }
        static DateTime DT(object v) { return v == null || v == DBNull.Value ? DateTime.MinValue : Convert.ToDateTime(v).Date; }
        static string Key(string bt, string tt, string id) { return bt.Trim().ToUpperInvariant() + "|" + tt.Trim() + "|" + id.Trim(); }
        static string BookKey(string bt, string tt) { return bt.Trim().ToUpperInvariant() + "|" + tt.Trim(); }
        static double SignedBalance(double amount, string side) { return Math.Abs(amount) * (S(side).Equals("C", StringComparison.OrdinalIgnoreCase) ? -1 : 1); }
        static void AddName(Dictionary<string, NameValue> map, string name, double value, string detail)
        {
            if (Math.Abs(value) < .005) return;
            NameValue nv;
            if (!map.TryGetValue(name, out nv)) { nv = new NameValue(name, 0, detail); map[name] = nv; }
            nv.Value += value;
        }
        static List<NameValue> Ranked(Dictionary<string, NameValue> map) { return map.Values.OrderByDescending(x => Math.Abs(x.Value)).ToList(); }
        static T GetOrAdd<T>(Dictionary<string, T> map, string key) where T : new()
        { T v; if (!map.TryGetValue(key, out v)) { v = new T(); map[key] = v; } return v; }

        public static LedgerSnapshot Load(string path, string password)
        {
            string stage = "start";
            try
            {
            JetDb jet = new JetDb(path, password);
            LedgerSnapshot s = new LedgerSnapshot(); s.DbPath = path; s.LoadedAt = DateTime.Now;
            // Mode=Read is enforced by Jet, not merely a convention in the calling code.
            using (OleDbConnection cn = new OleDbConnection(jet.BuildConnectionString(false) + "Mode=Read;"))
            {
                cn.Open(); jet.LoadSchema(cn);
                DataTable company = jet.QuerySafe(cn, "SELECT * FROM [Company]");
                if (company.Rows.Count > 0) { s.Company = S(company.Rows[0]["comp_name"]); s.FyFrom = DT(company.Rows[0]["fin_year_from"]); s.FyTo = DT(company.Rows[0]["fin_year_upto"]); }
                DataTable accounts = jet.Query(cn, null, "SELECT * FROM [AccMst]");
                foreach (DataRow r in accounts.Rows)
                {
                    LedgerAccount a = new LedgerAccount(); a.Code = S(r["Acc_code"]); a.Name = S(r["Name"]);
                    a.Opening = SignedBalance(D(r["Op_bal"]), S(r["Op_dbcr"])); a.MasterClosing = SignedBalance(D(r["Cl_bal"]), S(r["Cl_dbcr"]));
                    a.Debit = D(r["Db_amt"]); a.Credit = D(r["Cr_amt"]); a.IsGroup = S(r["Grp_flag"]).Equals("Y", StringComparison.OrdinalIgnoreCase);
                    if (a.Code.Length == 0) continue;
                    s.Accounts.Add(a); s._accounts[a.Code] = a;
                }
                stage = "AccBom";
                if (jet.TableExists("AccBom"))
                    foreach (DataRow r in jet.Query(cn, null, "SELECT [parent_code],[compon_code] FROM [AccBom]").Rows)
                        s.Parents[S(r["compon_code"])] = S(r["parent_code"]);
                // Two passes: every account must know its parent before any path is walked,
                // otherwise an account listed before its own ancestor would read a null parent.
                foreach (LedgerAccount a in s.Accounts)
                { string parent; a.Parent = s.Parents.TryGetValue(a.Code, out parent) ? parent : ""; }
                foreach (LedgerAccount a in s.Accounts)
                {
                    List<string> pathNames = new List<string>(); HashSet<string> seen = new HashSet<string>(); seen.Add(a.Code);
                    string cur = a.Parent; a.Root = "";
                    while (!string.IsNullOrEmpty(cur) && seen.Add(cur))
                    {
                        LedgerAccount p; if (!s._accounts.TryGetValue(cur, out p)) break;
                        pathNames.Add(p.Name); a.Root = cur; cur = p.Parent;
                    }
                    pathNames.Reverse(); a.GroupPath = string.Join(" / ", pathNames.ToArray());
                }
                stage = "TranType";
                if (jet.TableExists("TranType"))
                    foreach (DataRow r in jet.QuerySafe(cn, "SELECT * FROM [TranType]").Rows)
                    {
                        string k = BookKey(S(r["BT"]), S(r["Tran_type"])), name = S(r["Name"]); s.Books[k] = name;
                        string n = name.ToUpperInvariant();
                        if (n.Contains("P & L") || n.Contains("P&L") || n.Contains("PROFIT AND") || n.Contains("PROFIT &")) s._closingBooks.Add(k);
                    }
                stage = "INF";
                DataTable inf = jet.QuerySafe(cn, "SELECT * FROM [INF]");
                foreach (DataRow r in inf.Rows)
                {
                    string k = Key(S(r["BT"]), S(r["Tran_Type"]), S(r["ID_NO"]));
                    HashSet<string> set = GetOrAdd<HashSet<string>>(s._voucherAccounts, k); set.Add(S(r["Acc_Code"]));
                }
                DataTable eff = jet.Query(cn, null, "SELECT * FROM [EFF]");
                foreach (DataRow r in eff.Rows)
                {
                    LedgerLine l = new LedgerLine(); l.Account = S(r["Acc_Code"]); l.BT = S(r["BT"]).ToUpperInvariant(); l.Date = DT(r["VchDate"]);
                    l.Voucher = S(r["VchNo"]); l.Id = S(r["ID_NO"]); l.Amount = Math.Abs(D(r["Tran_amt"]));
                    l.Signed = l.Amount * (S(r["DbCr"]).Equals("C", StringComparison.OrdinalIgnoreCase) ? -1 : 1);
                    l.Narration = S(r["Naration"]); l.Contra = S(r["Acc_Code2"]); l.Uid = (int)D(r["UId"]);
                    string tt = S(r["Tran_Type"]), bk = BookKey(l.BT, tt); l.Book = s.Books.ContainsKey(bk) ? s.Books[bk] : l.BT;
                    l.Key = Key(l.BT, tt, l.Id); l.IsClosing = s._closingBooks.Contains(bk);
                    if (l.Account.Length == 0) continue;
                    s.Lines.Add(l); GetOrAdd<List<LedgerLine>>(s._lines, l.Account).Add(l);
                    GetOrAdd<HashSet<string>>(s._voucherAccounts, l.Key).Add(l.Account);
                    LedgerAccount a; if (s._accounts.TryGetValue(l.Account, out a)) { a.Entries++; if (l.Date > s.DataTo) s.DataTo = l.Date; if (s.DataFrom == DateTime.MinValue || l.Date < s.DataFrom) s.DataFrom = l.Date; }
                }
                if (s.FyFrom == DateTime.MinValue) s.FyFrom = s.DataFrom == DateTime.MinValue ? DateTime.Today : s.DataFrom;
                if (s.FyTo == DateTime.MinValue) s.FyTo = s.DataTo == DateTime.MinValue ? DateTime.Today : s.DataTo;
                if (s.DataTo == DateTime.MinValue) s.DataTo = s.FyTo;
                if (jet.TableExists("PTH"))
                    foreach (DataRow r in jet.Query(cn, null, "SELECT * FROM [PTH]").Rows)
                    {
                        string bt = S(r["BT"]).ToUpperInvariant(), code = S(r["Acc_Code"]);
                        if (bt == "SL") s._salesParties.Add(code); else if (bt == "PU") s._purchaseParties.Add(code);
                    }
                foreach (LedgerAccount a in s.Accounts) a.Kind = s.Classify(a);
                stage = "BillHist";
                if (jet.TableExists("BillHist"))
                    foreach (DataRow r in jet.Query(cn, null, "SELECT * FROM [BillHist]").Rows)
                    {
                        LedgerBill b = new LedgerBill(); b.Account = S(r["Acc_Code"]); b.BT = S(r["BT"]).ToUpperInvariant(); b.Date = DT(r["VchDate"]);
                        b.Voucher = S(r["VchNo"]); b.Id = S(r["ID_NO"]); b.Reference = S(r["RefNo"]); b.Amount = D(r["Tran_amt"]); b.Adjusted = D(r["Adj_Amt"]);
                        string tt = S(r["Tran_Type"]), bk = BookKey(b.BT, tt); b.Book = s.Books.ContainsKey(bk) ? s.Books[bk] : b.BT; b.Key = Key(b.BT, tt, b.Id);
                        b.IsOpening = b.Id.StartsWith("O-", StringComparison.OrdinalIgnoreCase) || S(r["op_flag"]).Equals("Y", StringComparison.OrdinalIgnoreCase) || b.Date < s.FyFrom;
                        b.OverAdjusted = b.Adjusted - b.Amount > .01; b.AgeDays = Math.Max(0, (s.DataTo - b.Date).Days);
                        double sign = b.BT == "PU" ? -1 : 1;
                        HashSet<string> parties; if (s._voucherAccounts.TryGetValue(b.Key, out parties) && parties.Contains(b.Account))
                        { double sum = s.Lines.Where(x => x.Key == b.Key && x.Account == b.Account).Sum(x => x.Signed); if (Math.Abs(sum) > .005) sign = Math.Sign(sum); }
                        b.Signed = b.Amount * sign;
                        GetOrAdd<List<LedgerBill>>(s._bills, b.Account).Add(b); s.Bills.Add(b);
                    }
                stage = "PayHist";
                if (jet.TableExists("PayHist"))
                    foreach (DataRow r in jet.Query(cn, null, "SELECT * FROM [PayHist]").Rows)
                    {
                        LedgerPaymentLink p = new LedgerPaymentLink(); p.Account = S(r["Acc_Code"]); p.BT = S(r["BT"]).ToUpperInvariant(); p.BillBT = S(r["BT2"]).ToUpperInvariant();
                        p.Voucher = S(r["Our_VchNo"]); p.BillVoucher = S(r["VchNo"]); p.BillId = S(r["ID_NO"]); p.Date = r.Table.Columns.Contains("Our_VchDate") ? DT(r["Our_VchDate"]) : DT(r["VchDate"]); p.BillDate = r.Table.Columns.Contains("VchDate") ? DT(r["VchDate"]) : DateTime.MinValue;
                        p.Amount = D(r["Adj_Amt"]); p.Discount = D(r["Disc_Amt"]); p.IsOpeningPayment = p.Voucher.StartsWith("O-", StringComparison.OrdinalIgnoreCase);
                        GetOrAdd<List<LedgerPaymentLink>>(s._links, p.Account).Add(p); s.Links.Add(p);
                    }
                Dictionary<int, string> itemNames = new Dictionary<int, string>();
                stage = "Item";
                if (jet.TableExists("Item"))
                    foreach (DataRow r in jet.QuerySafe(cn, "SELECT [ItemCode],[Name] FROM [Item]").Rows) itemNames[(int)D(r["ItemCode"])] = S(r["Name"]);
                if (jet.TableExists("PTH") && jet.TableExists("PTL"))
                {
                    Dictionary<string, string> heads = new Dictionary<string, string>();
                    foreach (DataRow r in jet.Query(cn, null, "SELECT * FROM [PTH]").Rows) heads[Key(S(r["BT"]), S(r["Tran_Type"]), S(r["ID_NO"]))] = S(r["Acc_Code"]);
                    foreach (DataRow r in jet.Query(cn, null, "SELECT * FROM [PTL]").Rows)
                    {
                        LedgerItemSale i = new LedgerItemSale(); i.BT = S(r["BT"]).ToUpperInvariant(); i.Voucher = S(r["VchNo"]); i.Key = Key(i.BT, S(r["Tran_Type"]), S(r["ID_NO"]));
                        if (!heads.TryGetValue(i.Key, out i.Account)) continue;
                        i.ItemCode = (int)D(r["ItemCode"]); i.Name = itemNames.ContainsKey(i.ItemCode) ? itemNames[i.ItemCode] : "Item " + i.ItemCode;
                        i.Unit = S(r["Unit"]); if (i.Unit.Length == 0) i.Unit = "unit"; i.Quantity = D(r["Tran_Qty"]); i.Rate = D(r["Unit_Rate"]); i.Value = D(r["Net_Value"]);
                        i.Date = s.FyFrom; foreach (LedgerLine l in s._lines.ContainsKey(i.Account) ? s._lines[i.Account] : s.Lines) if (l.Key == i.Key) { i.Date = l.Date; break; }
                        s.ItemSales.Add(i); GetOrAdd<List<LedgerItemSale>>(s._items, i.Account).Add(i);
                    }
                }
                // Resolve AccNew's *From PA* / *From RE* placeholders from the full voucher.
                foreach (LedgerLine l in s.Lines)
                {
                    HashSet<string> parties; string contra = l.Contra;
                    if (contra.Length == 0 || contra.StartsWith("*"))
                    {
                        if (s._voucherAccounts.TryGetValue(l.Key, out parties)) contra = string.Join(", ", parties.Where(x => x != l.Account).Take(3).ToArray());
                    }
                    l.Contra = contra;
                }
            }
            stage = "Classify";
            foreach (LedgerAccount a in s.Accounts) a.Kind = s.Classify(a);
            return s;
            }
            catch (Exception ex) { throw new InvalidOperationException("Visual load failed at " + stage + ": " + ex.Message, ex); }
        }

        string Classify(LedgerAccount a)
        {
            string path = (a.GroupPath + " " + a.Name).ToUpperInvariant();
            if (path.Contains("CASH AND BANK") || path.Contains("/ BANKS") || path.Contains("CASH AND BANKS")) return "Bank & cash";
            if (_salesParties.Contains(a.Code) && _purchaseParties.Contains(a.Code)) return "Customer & supplier";
            if (_salesParties.Contains(a.Code)) return "Customer";
            LedgerAccount root; if (a.Root != null && _accounts.TryGetValue(a.Root, out root)) { if (root.Name.ToUpperInvariant().Contains("INCOME")) return "Income"; if (root.Name.ToUpperInvariant().Contains("EXPAN") || root.Name.ToUpperInvariant().Contains("EXPEN")) return "Expense"; }
            if (_purchaseParties.Contains(a.Code)) return "Supplier";
            List<LedgerLine> ll; if (_lines.TryGetValue(a.Code, out ll) && ll.Count > 0 && ll.All(x => x.BT == "RE" || x.BT == "PA" || x.BT == "CB")) return "Bank & cash";
            return "Other ledger";
        }
        public List<LedgerAccount> Search(string text, string group, bool includeGroups)
        {
            string q = (text ?? "").Trim();
            return Accounts.Where(a => (includeGroups || !a.IsGroup) &&
                (q.Length == 0 || a.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || a.Code.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) &&
                (string.IsNullOrEmpty(group) || a.Code == group || a.GroupPath.Contains(group))).OrderBy(a => a.Name).ToList();
        }
        public HashSet<string> Descendants(string code)
        {
            HashSet<string> result = new HashSet<string>(StringComparer.OrdinalIgnoreCase); result.Add(code);
            bool changed = true;
            while (changed) { changed = false; foreach (KeyValuePair<string, string> p in Parents) if (result.Contains(p.Value) && result.Add(p.Key)) changed = true; }
            return result;
        }
        public LedgerMetrics Compute(string code, DateTime from, DateTime to)
        {
            LedgerAccount a; if (!_accounts.TryGetValue(code, out a)) throw new ArgumentException("Unknown ledger: " + code);
            if (to < from) throw new ArgumentException("The end date is before the start date.");
            HashSet<string> codes = a.IsGroup ? Descendants(code) : new HashSet<string>(StringComparer.OrdinalIgnoreCase) { code };
            LedgerMetrics m = new LedgerMetrics(); m.Account = a; m.From = from; m.To = to; m.DataTo = DataTo;
            List<LedgerLine> all = Lines.Where(x => codes.Contains(x.Account)).OrderBy(x => x.Date).ThenBy(x => x.BT).ThenBy(x => x.Voucher).ToList();
            m.Opening = a.IsGroup ? Accounts.Where(x => codes.Contains(x.Code) && !x.IsGroup).Sum(x => x.Opening) : a.Opening;
            m.Opening += all.Where(x => x.Date < from).Sum(x => x.Signed);
            m.Lines = all.Where(x => x.Date >= from && x.Date <= to).ToList(); m.Closing = m.Opening;
            bool customer = a.Kind == "Customer" || a.Kind == "Customer & supplier", supplier = a.Kind == "Supplier", bank = a.Kind == "Bank & cash";
            Dictionary<string, NameValue> cats = new Dictionary<string, NameValue>(), sources = new Dictionary<string, NameValue>(), dest = new Dictionary<string, NameValue>();
            HashSet<string> active = new HashSet<string>();
            foreach (LedgerLine l in m.Lines)
            {
                m.Debit += l.Signed > 0 ? l.Amount : 0; m.Credit += l.Signed < 0 ? l.Amount : 0; m.Closing += l.Signed;
                if (l.Date > m.LastActivity) m.LastActivity = l.Date; active.Add(l.Date.ToShortDateString());
                bool internalTransfer = bank && IsInternalTransfer(l);
                string category = l.IsClosing ? "Year-end transfer" : internalTransfer ? "Internal transfer" : l.BT == "SL" ? "Sales / billing" : l.BT == "PU" ? "Purchases / bills" :
                    l.BT == "JV" ? "Journal adjustment" : (l.BT == "RE" || l.BT == "PA" || l.BT == "CB") ? (bank ? (l.Signed > 0 ? "Money in" : "Money out") : (customer ? (l.Signed < 0 ? "Receipt" : "Refund / payment") : supplier ? (l.Signed > 0 ? "Payment" : "Refund / receipt") : (l.Signed > 0 ? "Debit movement" : "Credit movement"))) : l.Book;
                l.Category = category; AddName(cats, category, l.Amount, l.BT);
                string contraName = AccountName(l.Contra);
                if (l.Signed > 0) AddName(sources, contraName, l.Amount, "Debit side"); else AddName(dest, contraName, l.Amount, "Credit side");
                if (l.IsClosing) m.ClosingTransfers += l.Signed;
                else if (internalTransfer) { if (l.Signed > 0) m.TransfersIn += l.Amount; else m.TransfersOut += l.Amount; }
                else if (l.BT == "SL") m.Sales += l.Signed;
                else if (l.BT == "PU") m.Purchases -= l.Signed;
                else if (l.BT == "RE" || l.BT == "PA" || l.BT == "CB")
                {
                    if (l.Signed > 0) m.CashIn += l.Amount; else m.CashOut += l.Amount;
                    if (customer) { if (l.Signed < 0) m.Receipts += l.Amount; else m.Payments += l.Amount; }
                    else if (supplier) { if (l.Signed > 0) m.Payments += l.Amount; else m.Receipts += l.Amount; }
                }
                else if (l.BT == "JV") m.JournalNet += l.Signed;
                else m.OtherNet += l.Signed;
                if (!l.IsClosing && a.Kind == "Income") m.OperatingIncome -= l.Signed;
                if (!l.IsClosing && a.Kind == "Expense") m.OperatingExpense += l.Signed;
            }
            m.ActiveDays = active.Count; m.Categories = Ranked(cats); m.Sources = Ranked(sources); m.Destinations = Ranked(dest);
            m.Bills = Bills.Where(x => codes.Contains(x.Account) && !x.IsOpening && x.Date >= from && x.Date <= to).OrderBy(x => x.Date).ToList();
            foreach (LedgerBill b in m.Bills) { m.CurrentBilled += b.Amount; m.CurrentSettled += Math.Min(b.Amount, Math.Max(0, b.Adjusted)); m.CurrentPending += b.Pending; if (b.OverAdjusted) m.OverAdjustedBills++; m.OverAdjusted += Math.Max(0, b.Adjusted - b.Amount); int bucket = b.AgeDays <= 30 ? 0 : b.AgeDays <= 60 ? 1 : b.AgeDays <= 90 ? 2 : 3; m.Aging[bucket] += b.Pending; }
            m.Links = Links.Where(x => codes.Contains(x.Account) && x.Date >= from && x.Date <= to && !x.IsOpeningPayment).ToList();
            m.LinkedPayments = m.Links.Count; m.LinkedCash = m.Links.Sum(x => x.Amount);
            List<double> weightedDays = m.Links.Where(x => x.Amount > 0).OrderBy(x => x.Days).SelectMany(x => Enumerable.Repeat((double)x.Days, Math.Max(1, Math.Min(1000, (int)Math.Round(x.Amount))))).ToList();
            if (weightedDays.Count > 0) m.MedianPaymentDays = weightedDays[weightedDays.Count / 2];
            m.Items = ItemSales.Where(x => codes.Contains(x.Account) && x.Date >= from && x.Date <= to).OrderBy(x => x.Date).ToList();
            Dictionary<string, NameValue> mix = new Dictionary<string, NameValue>();
            foreach (IGrouping<string, LedgerItemSale> g in m.Items.GroupBy(x => x.Name + " | " + x.Unit))
            { double qty = g.Sum(x => x.Quantity), val = g.Sum(x => x.Value); string unit = g.First().Unit;
                NameValue nv = new NameValue(g.Key, Math.Abs(val), qty.ToString("N2", CultureInfo.InvariantCulture) + " " + unit + " | avg rate " + (Math.Abs(qty) > .001 ? (val / qty).ToString("N2", CultureInfo.InvariantCulture) : "n/a"));
                nv.Secondary = qty; mix[g.Key] = nv; }
            m.ItemMix = Ranked(mix);
            double cash = bank ? m.CashIn + m.CashOut : customer ? m.Receipts : m.Payments;
            m.UnlinkedCash = Math.Max(0, cash - m.LinkedCash); m.CashCoverage = (customer ? m.Sales + Math.Max(0, m.JournalNet) : m.Purchases + Math.Max(0, -m.JournalNet));
            m.CashCoverage = m.CashCoverage > .005 ? cash / m.CashCoverage * 100 : 0;
            double master = a.IsGroup ? Accounts.Where(x => codes.Contains(x.Code) && !x.IsGroup).Sum(x => x.MasterClosing) : a.MasterClosing;
            double fullClose = (a.IsGroup ? Accounts.Where(x => codes.Contains(x.Code) && !x.IsGroup).Sum(x => x.Opening) : a.Opening) + all.Sum(x => x.Signed);
            m.ReconciliationDifference = fullClose - master;
            m.CarriedDuplicates = Bills.Where(x => codes.Contains(x.Account) && x.IsOpening).GroupBy(x => x.BT + "|" + x.Book + "|" + x.Voucher).Count(g => g.Count() > 1);
            BuildMonths(m, bank, customer, supplier); BuildInsights(m, customer, supplier, bank); return m;
        }
        public string AccountName(string code)
        { LedgerAccount a; return _accounts.TryGetValue((code ?? "").Trim(), out a) ? a.Name : string.IsNullOrEmpty(code) ? "Unspecified / mixed counterparties" : code; }
        bool IsInternalTransfer(LedgerLine l)
        {
            HashSet<string> parties; if (!_voucherAccounts.TryGetValue(l.Key, out parties)) return false;
            List<LedgerAccount> others = parties.Where(x => x != l.Account && _accounts.ContainsKey(x)).Select(x => _accounts[x]).ToList();
            return others.Count > 0 && others.All(x => x.Kind == "Bank & cash");
        }
        void BuildMonths(LedgerMetrics m, bool bank, bool customer, bool supplier)
        {
            DateTime start = new DateTime(m.From.Year, m.From.Month, 1), end = new DateTime(m.To.Year, m.To.Month, 1);
            Dictionary<DateTime, LedgerMonth> map = new Dictionary<DateTime, LedgerMonth>();
            for (DateTime d = start; d <= end && map.Count < 120; d = d.AddMonths(1)) { LedgerMonth mo = new LedgerMonth(); mo.Month = d; map[d] = mo; }
            double balance = m.Opening;
            foreach (LedgerLine l in m.Lines)
            {
                LedgerMonth mo; if (!map.TryGetValue(new DateTime(l.Date.Year, l.Date.Month, 1), out mo)) continue;
                balance += l.Signed; mo.Balance = balance; mo.Entries++;
                if (l.IsClosing) { mo.Closing += l.Signed; continue; }
                if (l.Category == "Internal transfer") { mo.Transfers += l.Amount; continue; }
                if (l.BT == "SL") mo.Billing += l.Signed;
                if (l.BT == "PU") mo.Purchases -= l.Signed;
                if (l.BT == "JV") mo.Journals += l.Signed;
                if (l.Signed > 0) mo.MoneyIn += l.Amount; else mo.MoneyOut += l.Amount;
                if (bank) { if (l.Signed > 0) mo.Cash += l.Amount; else mo.Cash -= l.Amount; }
                else if (customer && l.Signed < 0 && (l.BT == "RE" || l.BT == "PA" || l.BT == "CB")) mo.Cash += l.Amount;
                else if (supplier && l.Signed > 0 && (l.BT == "RE" || l.BT == "PA" || l.BT == "CB")) mo.Cash += l.Amount;
                if (m.Account.Kind == "Income") mo.Income -= l.Signed;
                if (m.Account.Kind == "Expense") mo.Expense += l.Signed;
            }
            m.Months = map.Values.OrderBy(x => x.Month).ToList();
            // Months with no entries must carry the running balance forward; an empty month is not a zero balance.
            double carry = m.Opening;
            foreach (LedgerMonth mo in m.Months) { if (mo.Entries == 0) mo.Balance = carry; else carry = mo.Balance; }
        }
        void BuildInsights(LedgerMetrics m, bool customer, bool supplier, bool bank)
        {
            Func<double, string> money = delegate(double v) { return Helpers.FmtAmt(Math.Abs(v)); };
            if (Math.Abs(m.ReconciliationDifference) > .05) m.Insights.Add(new LedgerInsight("Balance needs reconciliation", "EFF movement differs from the account master by Rs. " + money(m.ReconciliationDifference) + ". The master balance is shown separately; do not assume the bill register is complete.", "warning"));
            if (m.CarriedDuplicates > 0) m.Insights.Add(new LedgerInsight("Carried bill references repeat", m.CarriedDuplicates + " reference groups have multiple carried-forward rows. They are excluded from current-bill totals; opening balance is shown separately.", "warning"));
            if (m.OverAdjustedBills > 0) m.Insights.Add(new LedgerInsight("Settlement exceeds bill value", m.OverAdjustedBills + " current bills have adjustments above their value. Excess is flagged, not silently treated as a payment.", "warning"));
            if (customer && m.Closing < -.005) m.Insights.Add(new LedgerInsight("Customer credit / possible advance", "Rs. " + money(m.Closing) + " is on the credit side. This is not an unpaid sales balance; check advances and unallocated receipts.", "info"));
            if (supplier && m.Closing > .005) m.Insights.Add(new LedgerInsight("Supplier debit / possible advance", "Rs. " + money(m.Closing) + " is on the debit side. It may be an advance or a reconciliation difference, not an unpaid purchase.", "info"));
            if (m.CurrentPending > .005 && Math.Abs(m.CurrentPending - Math.Abs(m.Closing)) > .05) m.Insights.Add(new LedgerInsight("Bills are not the whole balance", "Current bills pending: Rs. " + money(m.CurrentPending) + ". Ledger balance: Rs. " + money(m.Closing) + ". Opening balances, advances and non-bill adjustments explain why these must not be conflated.", "info"));
            if (m.UnlinkedCash > .005) m.Insights.Add(new LedgerInsight("Cash without a bill link", "Rs. " + money(m.UnlinkedCash) + " of cash movement has no matching PayHist allocation in this period. It is not automatically an advance.", "info"));
            if (Math.Abs(m.ClosingTransfers) > .005) m.Insights.Add(new LedgerInsight("Year-end transfers separated", "Rs. " + money(m.ClosingTransfers) + " of P&L transfer movement is excluded from operating income/expense. A zero closing balance does not mean no activity.", "info"));
            if (bank && m.TransfersIn + m.TransfersOut > .005) m.Insights.Add(new LedgerInsight("Internal transfers separated", "Rs. " + money(m.TransfersIn + m.TransfersOut) + " moved between cash/bank ledgers. The money map distinguishes these from external flows.", "info"));
            if (m.LastActivity != DateTime.MinValue && (DataTo - m.LastActivity).Days > 60 && Math.Abs(m.Closing) > .005) m.Insights.Add(new LedgerInsight("Balance resting", "No movement for " + (DataTo - m.LastActivity).Days + " days before the latest database entry, with Rs. " + money(m.Closing) + " still on the ledger.", "attention"));
            foreach (IGrouping<string, LedgerItemSale> g in m.Items.GroupBy(x => x.Name + "|" + x.Unit))
            { double min = g.Min(x => x.Rate), max = g.Max(x => x.Rate); if (g.Count() >= 3 && min > 0 && max / min > 1.2) m.Insights.Add(new LedgerInsight("Rate range: " + g.First().Name, "Recorded rates range from Rs. " + money(min) + " to Rs. " + money(max) + " per " + g.First().Unit + ". Compare quantity, period and narration before judging the change.", "attention")); }
            if (m.Lines.Count == 0) m.Insights.Add(new LedgerInsight("No activity in this period", "The opening and closing balance can still be non-zero. Change the date window or inspect another ledger.", "info"));
        }
        public LedgerOverview Overview(DateTime from, DateTime to)
        {
            LedgerOverview o = new LedgerOverview(); o.From = from; o.To = to; o.DataTo = DataTo;
            foreach (LedgerAccount a in Accounts.Where(x => !x.IsGroup))
            {
                LedgerMetrics m = Compute(a.Code, from, to); o.Ledgers.Add(m);
                if (m.Lines.Count > 0) o.ActiveLedgers++;
                if (m.Account.Kind == "Customer") { o.CustomerCount++; if (m.Closing > 0) o.Receivables += m.Closing; else o.CustomerAdvances -= m.Closing; }
                if (m.Account.Kind == "Supplier") { o.SupplierCount++; if (m.Closing < 0) o.Payables -= m.Closing; else o.SupplierAdvances += m.Closing; }
                if (m.Account.Kind == "Customer & supplier") { o.CustomerCount++; o.SupplierCount++; if (m.Closing > 0) o.Receivables += m.Closing; else o.Payables -= m.Closing; }
                if (m.Account.Kind == "Bank & cash") o.BankBalance += m.Closing;
                if (m.Account.Kind.Contains("Customer")) o.Sales += Math.Max(0, m.Sales);
                if (m.Account.Kind.Contains("Supplier")) o.Purchases += Math.Max(0, m.Purchases);
                o.Receipts += m.Receipts; o.Payments += m.Payments;
                if (m.Insights.Any(x => x.Severity == "warning" || x.Severity == "attention")) o.AttentionCount++;
            }
            Dictionary<DateTime, LedgerMonth> months = new Dictionary<DateTime, LedgerMonth>();
            for (DateTime d = new DateTime(from.Year, from.Month, 1); d <= to && months.Count < 120; d = d.AddMonths(1)) months[d] = new LedgerMonth { Month = d };
            foreach (LedgerMetrics m in o.Ledgers) foreach (LedgerMonth mo in m.Months)
                { LedgerMonth t; if (!months.TryGetValue(mo.Month, out t)) continue; if (m.Account.Kind.Contains("Customer")) { t.Billing += Math.Max(0, mo.Billing); t.Cash += mo.Cash; } if (m.Account.Kind.Contains("Supplier")) t.Purchases += Math.Max(0, mo.Purchases); t.Entries += mo.Entries; }
            o.Months = months.Values.OrderBy(x => x.Month).ToList();
            o.Insights.Add(new LedgerInsight("Balances and bills answer different questions", "Receivables/payables use the ledger's signed balance. Current-bill pending totals exclude carried-forward duplicates and never replace the ledger balance.", "info"));
            o.Insights.Add(new LedgerInsight("Customer classification follows activity", "A ledger with sales is treated as a customer even when its master-data group says creditors. This occurs in the supplied company data.", "info"));
            return o;
        }
    }
}
