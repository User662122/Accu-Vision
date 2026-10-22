using System;
using System.Collections.Generic;
using System.Data;
using System.Data.OleDb;

namespace AccuVisionRepost.Core
{
    public sealed class SeriesRow
    {
        public string BT;
        public string TranType;
        public string Name;
        public string ShortName;
        public string AccCode;       // income account for SL series
        public string AccCodeSL;
        public long No1;             // last used voucher number
        public string NetRoundoff;   // "1" = round bill net to whole rupees
        public string LineRoundOff;
        public string Display
        {
            get { return string.Format("{0}  ({1}-{2})  - next Vch No {3}", Name, BT, TranType, No1 + 1); }
        }
        public override string ToString() { return Display; }
    }

    public sealed class AccountRow
    {
        public string Code;
        public string Name;
        public double OpBal;
        public string OpDbCr;
        public double DbAmt;
        public double CrAmt;
        public double ClBal;
        public string ClDbCr;
        public string GrpFlag;
        public string AccType;
        public string SysFlagValue;   // AccMst.Sys_Flag - 'Y' = system account, cannot take entries
        public bool IsGroup { get { return string.Equals((GrpFlag ?? "").Trim(), "Y", StringComparison.OrdinalIgnoreCase); } }
        public bool SysFlag { get { return string.Equals((SysFlagValue ?? "").Trim(), "Y", StringComparison.OrdinalIgnoreCase); } }
        public string Display { get { return Code + "  " + Name; } }
        public override string ToString() { return Display; }
    }

    public sealed class ItemRow
    {
        public int ItemCode;
        public string AccCode;
        public string Name;
        public double ClSt;
        public string UnitCode;
        public string Key { get { return ItemCode + "|" + AccCode + "|" + Name; } }
        public string Display { get { return ItemCode + "  " + Name.Trim() + "  (stock " + ClSt + ")"; } }
        public override string ToString() { return Display; }
    }

    public sealed class AddOnRow
    {
        public int Code;
        public string Name;
        public string AccCodeSL;
        public string AccCode;
        public string FieldName;
        public string DefSign;
    }

    public sealed class DbProbe
    {
        public string CompanyName = "";
        public DateTime FyFrom = DateTime.MinValue;
        public DateTime FyTo = DateTime.MinValue;
        public long Uid;
        public List<SeriesRow> Series = new List<SeriesRow>();
        public Dictionary<string, long> ControlNo = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, AccountRow> Accounts = new Dictionary<string, AccountRow>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> ParentOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public List<ItemRow> Items = new List<ItemRow>();
        public Dictionary<int, ItemRow> ItemByCode = new Dictionary<int, ItemRow>();
        public Dictionary<string, double[]> PriceList = new Dictionary<string, double[]>(); // ItemCode|Acc -> [rate]
        public Dictionary<int, AddOnRow> AddOns = new Dictionary<int, AddOnRow>();
        public long EffUid2Max;
        public long PtlUid2Max;
        public long PthUid2Max;

        public AccountRow Account(string code)
        {
            AccountRow a;
            return (code != null && Accounts.TryGetValue(code.Trim(), out a)) ? a : null;
        }

        public string AccountName(string code)
        {
            AccountRow a = Account(code);
            return a == null ? "" : a.Name.Trim();
        }

        public SeriesRow FindSeries(string bt, string tranType)
        {
            foreach (SeriesRow s in Series)
                if (string.Equals(s.BT, bt, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(s.TranType, (tranType ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
                    return s;
            return null;
        }

        public ItemRow Item(int code)
        {
            ItemRow it;
            return ItemByCode.TryGetValue(code, out it) ? it : null;
        }

        public string ItemName(int code)
        {
            ItemRow it = Item(code);
            return it == null ? "" : it.Name.Trim();
        }
    }

    /// <summary>Opens Accurate.Mdb and probes the state AccNew keeps (counters, balances, masters).</summary>
    public sealed class CompanyDb
    {
        public JetDb Jet { get; private set; }
        public DbProbe State { get; private set; }

        public CompanyDb(string dbPath, string Password)
        {
            Jet = new JetDb(dbPath, Password);
        }

        public string DbPath { get { return Jet.DbPath; } }

        public DbProbe Probe()
        {
            using (OleDbConnection cn = Jet.Open(false))
            {
                Jet.LoadSchema(cn);
                DbProbe p = new DbProbe();

                // Company / financial year (column names as used by SaleBillPost.vbs)
                DataTable comp = Jet.QuerySafe(cn, "SELECT * FROM [Company]");
                if (comp.Rows.Count > 0)
                {
                    DataRow r = comp.Rows[0];
                    p.CompanyName = JetDb.GetStr(r, "comp_name");
                    if (p.CompanyName == "") p.CompanyName = JetDb.GetStr(r, "Comp_Name");
                    p.FyFrom = JetDb.GetDate(r, "fin_year_from", DateTime.MinValue);
                    p.FyTo = JetDb.GetDate(r, "fin_year_upto", DateTime.MinValue);
                }

                // UID counter (single-row table named UID with column UID)
                object uidObj = Jet.Scalar(cn, null, "SELECT [UID] FROM [UID]");
                p.Uid = (uidObj == DBNull.Value) ? 0 : Convert.ToInt64(uidObj);

                // Voucher series
                DataTable tt = Jet.Query(cn, null,
                    "SELECT [No_1],[BT],[Tran_type],[Name],[ShortName],[Type_Flag],[addon_flag],[Acc_Code],[Acc_Code_SL],[Net_Roundoff],[Line_RoundOff],[PayMod] FROM [TranType]");
                foreach (DataRow r in tt.Rows)
                {
                    SeriesRow s = new SeriesRow();
                    s.BT = JetDb.GetStr(r, "BT").Trim();
                    s.TranType = JetDb.GetStr(r, "Tran_type").Trim();
                    s.Name = JetDb.GetStr(r, "Name").Trim();
                    s.ShortName = JetDb.GetStr(r, "ShortName").Trim();
                    s.AccCode = JetDb.GetStr(r, "Acc_Code").Trim();
                    s.AccCodeSL = JetDb.GetStr(r, "Acc_Code_SL").Trim();
                    s.No1 = (long)Math.Round(JetDb.GetDbl(r, "No_1"));
                    s.NetRoundoff = JetDb.GetStr(r, "Net_Roundoff").Trim();
                    s.LineRoundOff = JetDb.GetStr(r, "Line_RoundOff").Trim();
                    p.Series.Add(s);
                }

                // Control ID counters (row per voucher BT: Prog_name = JV/SL/CB/RE/PA...)
                DataTable ctrl = Jet.Query(cn, null, "SELECT * FROM [Control]");
                foreach (DataRow r in ctrl.Rows)
                {
                    string prog = JetDb.GetStr(r, "Prog_name").Trim();
                    string ser = JetDb.GetStr(r, "Series").Trim();
                    if (prog.Length > 0 && ser.Length == 0)
                        p.ControlNo[prog.ToUpperInvariant()] = (long)Math.Round(JetDb.GetDbl(r, "No_1"));
                }

                // Accounts
                DataTable acc = Jet.Query(cn, null,
                    "SELECT [Acc_code],[Name],[Op_bal],[Op_dbcr],[Db_amt],[Cr_amt],[Cl_bal],[Cl_dbcr],[Grp_flag],[Acc_type],[Sys_Flag] FROM [AccMst]");
                foreach (DataRow r in acc.Rows)
                {
                    AccountRow a = new AccountRow();
                    a.Code = JetDb.GetStr(r, "Acc_code").Trim();
                    a.Name = JetDb.GetStr(r, "Name");
                    a.OpBal = JetDb.GetDbl(r, "Op_bal");
                    a.OpDbCr = JetDb.GetStr(r, "Op_dbcr").Trim();
                    a.DbAmt = JetDb.GetDbl(r, "Db_amt");
                    a.CrAmt = JetDb.GetDbl(r, "Cr_amt");
                    a.ClBal = JetDb.GetDbl(r, "Cl_bal");
                    a.ClDbCr = JetDb.GetStr(r, "Cl_dbcr").Trim();
                    a.GrpFlag = JetDb.GetStr(r, "Grp_flag").Trim();
                    a.AccType = JetDb.GetStr(r, "Acc_type").Trim();
                    a.SysFlagValue = JetDb.GetStr(r, "Sys_Flag").Trim();
                    if (a.Code.Length > 0) p.Accounts[a.Code] = a;
                }

                // Account hierarchy (child -> parent)
                if (Jet.TableExists("AccBom"))
                {
                    DataTable bom = Jet.Query(cn, null, "SELECT [parent_code],[compon_code] FROM [AccBom]");
                    foreach (DataRow r in bom.Rows)
                    {
                        string child = JetDb.GetStr(r, "compon_code").Trim();
                        string parent = JetDb.GetStr(r, "parent_code").Trim();
                        if (child.Length > 0 && parent.Length > 0) p.ParentOf[child] = parent;
                    }
                }

                // Items (identity = ItemCode + Acc_Code + Name)
                DataTable items = Jet.Query(cn, null,
                    "SELECT [ItemCode],[Acc_Code],[Name],[Cl_St],[Unit_Code] FROM [Item]");
                foreach (DataRow r in items.Rows)
                {
                    ItemRow it = new ItemRow();
                    it.ItemCode = JetDb.GetInt(r, "ItemCode");
                    it.AccCode = JetDb.GetStr(r, "Acc_Code");
                    it.Name = JetDb.GetStr(r, "Name");
                    it.ClSt = JetDb.GetDbl(r, "Cl_St");
                    it.UnitCode = JetDb.GetStr(r, "Unit_Code").Trim();
                    p.Items.Add(it);
                    if (!p.ItemByCode.ContainsKey(it.ItemCode)) p.ItemByCode[it.ItemCode] = it;
                }

                // Price list (per item per customer)
                if (Jet.TableExists("PriseList"))
                {
                    DataTable pr = Jet.QuerySafe(cn, "SELECT [ItemCode],[Acc_Code],[Rate] FROM [PriseList]");
                    foreach (DataRow r in pr.Rows)
                    {
                        string k = JetDb.GetInt(r, "ItemCode") + "|" + JetDb.GetStr(r, "Acc_Code").Trim().ToUpperInvariant();
                        p.PriceList[k] = new double[] { JetDb.GetDbl(r, "Rate") };
                    }
                }

                // AddOns registry (4=SGST 5=CGST 6=IGST 99=ROUND OFF ...)
                if (Jet.TableExists("AddOns"))
                {
                    DataTable ao = Jet.QuerySafe(cn,
                        "SELECT [AddOns],[Name],[Acc_Code],[Acc_Code_SL],[FieldName],[def_sign] FROM [AddOns]");
                    foreach (DataRow r in ao.Rows)
                    {
                        AddOnRow a = new AddOnRow();
                        a.Code = JetDb.GetInt(r, "AddOns");
                        a.Name = JetDb.GetStr(r, "Name").Trim();
                        a.AccCode = JetDb.GetStr(r, "Acc_Code").Trim();
                        a.AccCodeSL = JetDb.GetStr(r, "Acc_Code_SL").Trim();
                        a.FieldName = JetDb.GetStr(r, "FieldName").Trim();
                        a.DefSign = JetDb.GetStr(r, "def_sign").Trim();
                        p.AddOns[a.Code] = a;
                    }
                }

                p.EffUid2Max = MaxOr0(cn, "EFF", "Uid2");
                p.PtlUid2Max = MaxOr0(cn, "PTL", "Uid2");
                p.PthUid2Max = MaxOr0(cn, "PTH", "UID2");

                State = p;
                return p;
            }
        }

        private long MaxOr0(OleDbConnection cn, string table, string col)
        {
            if (!Jet.TableExists(table) || !Jet.HasColumn(table, col)) return 0;
            object o = Jet.Scalar(cn, null, "SELECT Max([" + Jet.RealColumn(table, col) + "]) FROM [" + Jet.RealTable(table) + "]");
            return (o == DBNull.Value || o == null) ? 0 : Convert.ToInt64(o);
        }

        /// <summary>Ancestors of an account (nearest parent first), cycle-safe.</summary>
        public static List<string> Ancestors(DbProbe p, string acc)
        {
            List<string> list = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string cur = acc;
            seen.Add(cur);
            string parent;
            while (p.ParentOf.TryGetValue(cur, out parent) && parent.Length > 0 && !seen.Contains(parent))
            {
                // The root A000000000 is not itself an AccMst row in the samples;
                // stop before adding anything that is not an account row.
                if (!p.Accounts.ContainsKey(parent)) break;
                list.Add(parent);
                seen.Add(parent);
                cur = parent;
            }
            return list;
        }
    }
}
