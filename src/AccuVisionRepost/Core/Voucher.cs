using System;
using System.Collections.Generic;
using System.Data;

namespace AccuVisionRepost.Core
{
    public enum VoucherKind { Sales, Journal, BankCash, ReceiptPayment, Other }

    public static class VoucherKinds
    {
        public static VoucherKind FromBT(string bt)
        {
            string b = (bt ?? "").Trim().ToUpperInvariant();
            if (b == "SL") return VoucherKind.Sales;
            if (b == "JV") return VoucherKind.Journal;
            if (b == "CB") return VoucherKind.BankCash;
            if (b == "RE" || b == "PA") return VoucherKind.ReceiptPayment;
            return VoucherKind.Other;
        }

        public static string Title(VoucherKind k)
        {
            switch (k)
            {
                case VoucherKind.Sales: return "Sales - Multi Line (SL)";
                case VoucherKind.Journal: return "Journal Voucher (JV)";
                case VoucherKind.BankCash: return "Bank - Cash Transaction (CB)";
                case VoucherKind.ReceiptPayment: return "Bank/Cash Receipt-Payment (RE/PA)";
                default: return "Other";
            }
        }
    }

    /// <summary>One previous-entry search result (an INF header).</summary>
    public sealed class VoucherSummary
    {
        public long UId;
        public string BT;
        public string TranType;
        public string BookName;
        public string IDNo;
        public string VchNo;      // as stored (space padded)
        public DateTime VchDate;
        public string AccCode;    // INF head account
        public string AccName;
        public double Amount;     // bill net (SL) / total debit (JV/CB/RE/PA)
        public string Narration;
        public string TaxInvNo;   // sales only, filled when cheap

        public string VchNoTrim { get { return (VchNo ?? "").Trim(); } }
        public VoucherKind Kind { get { return VoucherKinds.FromBT(BT); } }
        public string Display
        {
            get
            {
                return string.Format("{0}  Vch {1}  {2}  Rs. {3}",
                    Helpers.FmtDate(VchDate), VchNoTrim, AccName, Helpers.FmtAmt(Amount));
            }
        }
    }

    /// <summary>
    /// A voucher loaded verbatim from the database: one DataTable per physical
    /// table (INF/EFF always; PTH/PTHL/PTL/ItemTrn/BillHist for sales;
    /// EFFLine/INFRem/PayHist when present). Editing happens on these rows, so
    /// every column AccNew originally wrote is preserved unless the user (or a
    /// documented recalculation rule) changes it.
    /// </summary>
    public sealed class LoadedVoucher
    {
        public Dictionary<string, DataTable> Tables = new Dictionary<string, DataTable>(StringComparer.OrdinalIgnoreCase);

        public string SrcBT;
        public string SrcTranType;
        public string SrcIDNo;
        public string SrcVchNo;
        public DateTime SrcDate;
        public long SrcUId;
        public string SrcParty;     // sales party / head account
        public VoucherKind Kind;

        /// <summary>New values being prepared.</summary>
        public DateTime NewDate;
        public string NewTranType;  // book for the new entry (defaults to source series)
        public string NewTaxInvNo;  // sales; "" = auto at post
        public int TaxInvMode;      // 0 = same as VchNo, 1 = keep source offset, 2 = manual
        public string UserId;       // INF user stamp for the new entry

        /// <summary>Extra linked tables found on the source voucher that are NOT handled. Posting is blocked while non-empty.</summary>
        public List<string> UnsupportedExtras = new List<string>();

        /// <summary>Snapshot of original numeric values used for pattern detection during recalculation (key TABLE|rowindex|COL).</summary>
        public Dictionary<string, double> Original = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        public DataTable T(string table)
        {
            DataTable dt;
            return Tables.TryGetValue(table, out dt) ? dt : null;
        }

        public DataRow Inf { get { DataTable t = T("INF"); return (t != null && t.Rows.Count > 0) ? t.Rows[0] : null; } }
        public DataRow Pth { get { DataTable t = T("PTH"); return (t != null && t.Rows.Count > 0) ? t.Rows[0] : null; } }

        public List<DataRow> Rows(string table)
        {
            List<DataRow> l = new List<DataRow>();
            DataTable t = T(table);
            if (t != null) foreach (DataRow r in t.Rows) if (r.RowState != DataRowState.Deleted) l.Add(r);
            return l;
        }

        public double EffTotal(string dbcr)
        {
            double s = 0;
            foreach (DataRow r in Rows("EFF"))
                if (string.Equals(JetDb.GetStr(r, "DbCr").Trim(), dbcr, StringComparison.OrdinalIgnoreCase))
                    s += JetDb.GetDbl(r, "Tran_amt");
            return Helpers.Round2(s);
        }

        public void SnapshotOriginal()
        {
            Original.Clear();
            foreach (KeyValuePair<string, DataTable> kv in Tables)
            {
                for (int i = 0; i < kv.Value.Rows.Count; i++)
                {
                    DataRow r = kv.Value.Rows[i];
                    foreach (DataColumn c in kv.Value.Columns)
                    {
                        object o = r[c];
                        if (o != DBNull.Value && o != null && (o is double || o is float || o is decimal || o is int || o is long || o is short))
                            Original[kv.Key + "|" + i + "|" + c.ColumnName.ToUpperInvariant()] = Convert.ToDouble(o);
                    }
                }
            }
        }

        public double Orig(string table, int rowIndex, string col)
        {
            double v;
            return Original.TryGetValue(table + "|" + rowIndex + "|" + col.ToUpperInvariant(), out v) ? v : double.NaN;
        }

        public string SourceLabel
        {
            get
            {
                return string.Format("{0}  Book type {1}  Vch No {2}  Date {3}  (ID_NO {4}, UId {5})",
                    SrcBT, SrcTranType, (SrcVchNo ?? "").Trim(), Helpers.FmtDate(SrcDate), SrcIDNo, SrcUId);
            }
        }
    }

    /// <summary>Everything the engine resolved at post/review time.</summary>
    public sealed class PostPlan
    {
        public long NewUId;
        public long NewIdNum;
        public long NewVchNum;
        public string NewIDNo;
        public string NewVchNo;
        public string NewTaxInvNo;
        public SeriesRow Series;
        public Dictionary<string, int> InsertCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, double[]> AccDeltas = new Dictionary<string, double[]>(); // acc -> [dCr, dDb] incl. ancestors
        public Dictionary<int, double> ItemNewBal = new Dictionary<int, double>();
        public List<string> Notes = new List<string>();
        public List<string> BillAdjustments = new List<string>();

        public int TotalInserts
        {
            get { int s = 0; foreach (int v in InsertCounts.Values) s += v; return s; }
        }
    }

    public sealed class PostResult
    {
        public PostPlan Plan;
        public string BackupPath;
        public string LogPath;
        public string Report;
    }
}
