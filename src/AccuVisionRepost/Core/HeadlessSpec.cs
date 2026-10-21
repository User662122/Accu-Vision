using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace AccuVisionRepost.Core
{
    public sealed class ItemLineSpec
    {
        public string Action = "set"; // set | add | delete
        public int? LineNo;
        public string Item;           // code or name
        public double? Qty;
        public double? Nos;
        public string Unit;
        public double? Rate;
        public string Narration;
        public string AccCode;
    }

    public sealed class AddonLineSpec
    {
        public string Action = "set"; // set | add | delete
        public int? Line;
        public int? AddOns;           // 4=SGST, 5=CGST, 6=IGST, 99=ROUNDOFF, etc.
        public string Type;           // + or -
        public double? Per;
        public double? Amount;
        public double? AssVal;
        public string AccCode;
    }

    public sealed class EffLineSpec
    {
        public string Action = "set"; // set | add | delete
        public int? LineNo;
        public string Account;        // code or name
        public string DbCr;           // D or C
        public double? Amount;
        public string Narration;
        public string OtherAccount;   // Acc_Code2 code or name
    }

    public sealed class ChequeLineSpec
    {
        public string Action = "set"; // set | add | delete
        public int? RowIndex;         // 0-based row index or 1-based Line_no
        public int? LineNo;
        public string Account;
        public string OtherAccount;
        public string BankName;
        public string ChqNo;
        public string ChqDate;
    }

    public sealed class PayHistLineSpec
    {
        public string Action = "set"; // set | delete
        public int? RowIndex;         // 0-based row index
        public string BillVchNo;      // optional match by bill VchNo
        public double? AdjAmt;
        public double? DiscAmt;
    }

    /// <summary>
    /// Complete specification for selecting a source voucher and applying
    /// edits headlessly (via CLI flags, JSON spec file, or TSV/CSV batch row).
    /// </summary>
    public sealed class VoucherEditSpec
    {
        public bool Select = true;

        // ---- Source voucher selector
        public long SrcUId;
        public string SrcBT = "";
        public string SrcBook = "";
        public string SrcVchNo = "";
        public string SrcIDNo = "";

        // ---- Header / target edits
        public string NewDate = "";
        public int? PlusMonths;
        public string NewBook = "";
        public bool? BumpNarration;
        public string Narration;
        public List<string> ReplaceNarration = new List<string>();
        public string Party;
        public string TaxInvMode;     // "same" (0), "offset"/"gap" (1), "manual" (2)
        public string TaxInvNo;
        public string TaxCode;
        public bool? AutoTaxCode;
        public string PayMode;
        public int? CrDays;
        public string RefNo;
        public string RefDate;
        public string UserId;

        // ---- Quick single-value overrides (CLI & Excel/TSV batch friendly)
        public string Item;
        public double? Qty;
        public double? Nos;
        public string Unit;
        public double? Rate;
        public string ItemNarration;
        public double? SgstPer;
        public double? SgstAmt;
        public double? CgstPer;
        public double? CgstAmt;
        public double? IgstPer;
        public double? IgstAmt;
        public double? Amount;        // JV/CB/RE/PA amount override
        public string DrAccount;
        public string CrAccount;
        public string ChqNo;
        public string ChqDate;
        public string BankName;
        public bool ClearAdjustments;

        // ---- Detailed line collections
        public bool ReplaceItemLines;
        public bool ReplaceAddonLines;
        public bool ReplaceEffLines;
        public List<ItemLineSpec> ItemLines = new List<ItemLineSpec>();
        public List<AddonLineSpec> AddonLines = new List<AddonLineSpec>();
        public List<EffLineSpec> EffLines = new List<EffLineSpec>();
        public List<ChequeLineSpec> ChequeLines = new List<ChequeLineSpec>();
        public List<PayHistLineSpec> PayHistLines = new List<PayHistLineSpec>();

        public Dictionary<string, object> ToDictionary()
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["select"] = Select;
            if (SrcUId > 0) d["srcUId"] = SrcUId;
            if (!string.IsNullOrEmpty(SrcBT)) d["srcBT"] = SrcBT;
            if (!string.IsNullOrEmpty(SrcBook)) d["srcBook"] = SrcBook;
            if (!string.IsNullOrEmpty(SrcVchNo)) d["srcVchNo"] = SrcVchNo;
            if (!string.IsNullOrEmpty(SrcIDNo)) d["srcIDNo"] = SrcIDNo;

            if (!string.IsNullOrEmpty(NewDate)) d["newDate"] = NewDate;
            if (PlusMonths.HasValue) d["plusMonths"] = PlusMonths.Value;
            if (!string.IsNullOrEmpty(NewBook)) d["newBook"] = NewBook;
            if (BumpNarration.HasValue) d["bumpNarration"] = BumpNarration.Value;
            if (Narration != null) d["narration"] = Narration;
            if (ReplaceNarration.Count > 0)
            {
                List<object> rn = new List<object>();
                foreach (string s in ReplaceNarration) rn.Add(s);
                d["replaceNarration"] = rn;
            }
            if (Party != null) d["party"] = Party;
            if (TaxInvMode != null) d["taxInvMode"] = TaxInvMode;
            if (TaxInvNo != null) d["taxInvNo"] = TaxInvNo;
            if (TaxCode != null) d["taxCode"] = TaxCode;
            if (AutoTaxCode.HasValue) d["autoTaxCode"] = AutoTaxCode.Value;
            if (PayMode != null) d["payMode"] = PayMode;
            if (CrDays.HasValue) d["crDays"] = CrDays.Value;
            if (RefNo != null) d["refNo"] = RefNo;
            if (RefDate != null) d["refDate"] = RefDate;
            if (UserId != null) d["userId"] = UserId;

            if (Item != null) d["item"] = Item;
            if (Qty.HasValue) d["qty"] = Qty.Value;
            if (Nos.HasValue) d["nos"] = Nos.Value;
            if (Unit != null) d["unit"] = Unit;
            if (Rate.HasValue) d["rate"] = Rate.Value;
            if (ItemNarration != null) d["itemNarration"] = ItemNarration;
            if (SgstPer.HasValue) d["sgstPer"] = SgstPer.Value;
            if (SgstAmt.HasValue) d["sgstAmt"] = SgstAmt.Value;
            if (CgstPer.HasValue) d["cgstPer"] = CgstPer.Value;
            if (CgstAmt.HasValue) d["cgstAmt"] = CgstAmt.Value;
            if (IgstPer.HasValue) d["igstPer"] = IgstPer.Value;
            if (IgstAmt.HasValue) d["igstAmt"] = IgstAmt.Value;
            if (Amount.HasValue) d["amount"] = Amount.Value;
            if (DrAccount != null) d["drAccount"] = DrAccount;
            if (CrAccount != null) d["crAccount"] = CrAccount;
            if (ChqNo != null) d["chqNo"] = ChqNo;
            if (ChqDate != null) d["chqDate"] = ChqDate;
            if (BankName != null) d["bankName"] = BankName;
            if (ClearAdjustments) d["clearAdjustments"] = true;

            if (ReplaceItemLines) d["replaceItemLines"] = true;
            if (ItemLines.Count > 0)
            {
                List<object> l = new List<object>();
                foreach (ItemLineSpec it in ItemLines)
                {
                    Dictionary<string, object> m = new Dictionary<string, object>();
                    m["action"] = it.Action ?? "set";
                    if (it.LineNo.HasValue) m["lineNo"] = it.LineNo.Value;
                    if (it.Item != null) m["item"] = it.Item;
                    if (it.Qty.HasValue) m["qty"] = it.Qty.Value;
                    if (it.Nos.HasValue) m["nos"] = it.Nos.Value;
                    if (it.Unit != null) m["unit"] = it.Unit;
                    if (it.Rate.HasValue) m["rate"] = it.Rate.Value;
                    if (it.Narration != null) m["narration"] = it.Narration;
                    if (it.AccCode != null) m["accCode"] = it.AccCode;
                    l.Add(m);
                }
                d["itemLines"] = l;
            }

            if (ReplaceAddonLines) d["replaceAddonLines"] = true;
            if (AddonLines.Count > 0)
            {
                List<object> l = new List<object>();
                foreach (AddonLineSpec a in AddonLines)
                {
                    Dictionary<string, object> m = new Dictionary<string, object>();
                    m["action"] = a.Action ?? "set";
                    if (a.Line.HasValue) m["line"] = a.Line.Value;
                    if (a.AddOns.HasValue) m["addOns"] = a.AddOns.Value;
                    if (a.Type != null) m["type"] = a.Type;
                    if (a.Per.HasValue) m["per"] = a.Per.Value;
                    if (a.Amount.HasValue) m["amount"] = a.Amount.Value;
                    if (a.AssVal.HasValue) m["assVal"] = a.AssVal.Value;
                    if (a.AccCode != null) m["accCode"] = a.AccCode;
                    l.Add(m);
                }
                d["addonLines"] = l;
            }

            if (ReplaceEffLines) d["replaceEffLines"] = true;
            if (EffLines.Count > 0)
            {
                List<object> l = new List<object>();
                foreach (EffLineSpec e in EffLines)
                {
                    Dictionary<string, object> m = new Dictionary<string, object>();
                    m["action"] = e.Action ?? "set";
                    if (e.LineNo.HasValue) m["lineNo"] = e.LineNo.Value;
                    if (e.Account != null) m["account"] = e.Account;
                    if (e.DbCr != null) m["dbCr"] = e.DbCr;
                    if (e.Amount.HasValue) m["amount"] = e.Amount.Value;
                    if (e.Narration != null) m["narration"] = e.Narration;
                    if (e.OtherAccount != null) m["otherAccount"] = e.OtherAccount;
                    l.Add(m);
                }
                d["effLines"] = l;
            }

            if (ChequeLines.Count > 0)
            {
                List<object> l = new List<object>();
                foreach (ChequeLineSpec c in ChequeLines)
                {
                    Dictionary<string, object> m = new Dictionary<string, object>();
                    m["action"] = c.Action ?? "set";
                    if (c.RowIndex.HasValue) m["rowIndex"] = c.RowIndex.Value;
                    if (c.LineNo.HasValue) m["lineNo"] = c.LineNo.Value;
                    if (c.Account != null) m["account"] = c.Account;
                    if (c.OtherAccount != null) m["otherAccount"] = c.OtherAccount;
                    if (c.BankName != null) m["bankName"] = c.BankName;
                    if (c.ChqNo != null) m["chqNo"] = c.ChqNo;
                    if (c.ChqDate != null) m["chqDate"] = c.ChqDate;
                    l.Add(m);
                }
                d["chequeLines"] = l;
            }

            if (PayHistLines.Count > 0)
            {
                List<object> l = new List<object>();
                foreach (PayHistLineSpec p in PayHistLines)
                {
                    Dictionary<string, object> m = new Dictionary<string, object>();
                    m["action"] = p.Action ?? "set";
                    if (p.RowIndex.HasValue) m["rowIndex"] = p.RowIndex.Value;
                    if (p.BillVchNo != null) m["billVchNo"] = p.BillVchNo;
                    if (p.AdjAmt.HasValue) m["adjAmt"] = p.AdjAmt.Value;
                    if (p.DiscAmt.HasValue) m["discAmt"] = p.DiscAmt.Value;
                    l.Add(m);
                }
                d["payHistLines"] = l;
            }

            return d;
        }

        public static VoucherEditSpec FromDictionary(Dictionary<string, object> d)
        {
            VoucherEditSpec s = new VoucherEditSpec();
            if (d == null) return s;

            bool? sel = OptBool(d, "select");
            if (sel.HasValue) s.Select = sel.Value;

            long? uid = OptLong(d, "srcUId", "uid");
            if (uid.HasValue) s.SrcUId = uid.Value;
            s.SrcBT = OptStr(d, "srcBT", "bt") ?? "";
            s.SrcBook = OptStr(d, "srcBook", "srcTranType", "book") ?? "";
            s.SrcVchNo = OptStr(d, "srcVchNo", "vchNo", "vch") ?? "";
            s.SrcIDNo = OptStr(d, "srcIDNo", "idNo") ?? "";

            s.NewDate = OptStr(d, "newDate", "date") ?? "";
            s.PlusMonths = OptInt(d, "plusMonths");
            s.NewBook = OptStr(d, "newBook", "newTranType") ?? "";
            s.BumpNarration = OptBool(d, "bumpNarration");
            s.Narration = OptStr(d, "narration", "headerNarration");
            List<object> rn = OptList(d, "replaceNarration");
            if (rn != null)
                foreach (object o in rn) if (o != null) s.ReplaceNarration.Add(Convert.ToString(o));
            s.Party = OptStr(d, "party", "partyCode", "accCode");
            s.TaxInvMode = OptStr(d, "taxInvMode");
            s.TaxInvNo = OptStr(d, "taxInvNo");
            s.TaxCode = OptStr(d, "taxCode");
            s.AutoTaxCode = OptBool(d, "autoTaxCode");
            s.PayMode = OptStr(d, "payMode");
            s.CrDays = OptInt(d, "crDays", "crDay");
            s.RefNo = OptStr(d, "refNo");
            s.RefDate = OptStr(d, "refDate");
            s.UserId = OptStr(d, "userId", "user");

            s.Item = OptStr(d, "item", "itemCode");
            s.Qty = OptDbl(d, "qty", "tranQty");
            s.Nos = OptDbl(d, "nos", "tranNos");
            s.Unit = OptStr(d, "unit");
            s.Rate = OptDbl(d, "rate", "unitRate");
            s.ItemNarration = OptStr(d, "itemNarration", "lineNarration");
            s.SgstPer = OptDbl(d, "sgstPer");
            s.SgstAmt = OptDbl(d, "sgstAmt");
            s.CgstPer = OptDbl(d, "cgstPer");
            s.CgstAmt = OptDbl(d, "cgstAmt");
            s.IgstPer = OptDbl(d, "igstPer");
            s.IgstAmt = OptDbl(d, "igstAmt");
            s.Amount = OptDbl(d, "amount", "tranAmt");
            s.DrAccount = OptStr(d, "drAccount", "drAcc");
            s.CrAccount = OptStr(d, "crAccount", "crAcc");
            s.ChqNo = OptStr(d, "chqNo");
            s.ChqDate = OptStr(d, "chqDate");
            s.BankName = OptStr(d, "bankName");
            bool? ca = OptBool(d, "clearAdjustments", "clearAdj");
            if (ca.HasValue) s.ClearAdjustments = ca.Value;

            bool? ril = OptBool(d, "replaceItemLines");
            if (ril.HasValue) s.ReplaceItemLines = ril.Value;
            List<object> items = OptList(d, "itemLines", "items");
            if (items != null)
            {
                foreach (object o in items)
                {
                    Dictionary<string, object> m = o as Dictionary<string, object>;
                    if (m == null) continue;
                    ItemLineSpec it = new ItemLineSpec();
                    it.Action = OptStr(m, "action") ?? "set";
                    it.LineNo = OptInt(m, "lineNo", "line");
                    it.Item = OptStr(m, "item", "itemCode", "itemName");
                    it.Qty = OptDbl(m, "qty", "tranQty");
                    it.Nos = OptDbl(m, "nos", "tranNos");
                    it.Unit = OptStr(m, "unit");
                    it.Rate = OptDbl(m, "rate", "unitRate");
                    it.Narration = OptStr(m, "narration");
                    it.AccCode = OptStr(m, "accCode", "incomeAcc", "account");
                    s.ItemLines.Add(it);
                }
            }

            bool? ral = OptBool(d, "replaceAddonLines");
            if (ral.HasValue) s.ReplaceAddonLines = ral.Value;
            List<object> addons = OptList(d, "addonLines", "addons");
            if (addons != null)
            {
                foreach (object o in addons)
                {
                    Dictionary<string, object> m = o as Dictionary<string, object>;
                    if (m == null) continue;
                    AddonLineSpec a = new AddonLineSpec();
                    a.Action = OptStr(m, "action") ?? "set";
                    a.Line = OptInt(m, "line", "lineNo");
                    a.AddOns = OptInt(m, "addOns", "code");
                    a.Type = OptStr(m, "type", "sign");
                    a.Per = OptDbl(m, "per", "percent");
                    a.Amount = OptDbl(m, "amount", "amt");
                    a.AssVal = OptDbl(m, "assVal");
                    a.AccCode = OptStr(m, "accCode", "account");
                    s.AddonLines.Add(a);
                }
            }

            bool? rel = OptBool(d, "replaceEffLines");
            if (rel.HasValue) s.ReplaceEffLines = rel.Value;
            List<object> effs = OptList(d, "effLines", "ledgerLines", "eff");
            if (effs != null)
            {
                foreach (object o in effs)
                {
                    Dictionary<string, object> m = o as Dictionary<string, object>;
                    if (m == null) continue;
                    EffLineSpec e = new EffLineSpec();
                    e.Action = OptStr(m, "action") ?? "set";
                    e.LineNo = OptInt(m, "lineNo", "line");
                    e.Account = OptStr(m, "account", "accCode", "acc");
                    e.DbCr = OptStr(m, "dbCr", "dc", "drCr");
                    e.Amount = OptDbl(m, "amount", "amt", "tranAmt");
                    e.Narration = OptStr(m, "narration");
                    e.OtherAccount = OptStr(m, "otherAccount", "accCode2", "acc2");
                    s.EffLines.Add(e);
                }
            }

            List<object> chqs = OptList(d, "chequeLines", "effLine");
            if (chqs != null)
            {
                foreach (object o in chqs)
                {
                    Dictionary<string, object> m = o as Dictionary<string, object>;
                    if (m == null) continue;
                    ChequeLineSpec c = new ChequeLineSpec();
                    c.Action = OptStr(m, "action") ?? "set";
                    c.RowIndex = OptInt(m, "rowIndex", "index");
                    c.LineNo = OptInt(m, "lineNo", "line");
                    c.Account = OptStr(m, "account", "accCode", "acc");
                    c.OtherAccount = OptStr(m, "otherAccount", "accCode2", "acc2");
                    c.BankName = OptStr(m, "bankName", "bank");
                    c.ChqNo = OptStr(m, "chqNo", "chequeNo");
                    c.ChqDate = OptStr(m, "chqDate", "chequeDate");
                    s.ChequeLines.Add(c);
                }
            }

            List<object> phs = OptList(d, "payHistLines", "billAdjustments", "payHist");
            if (phs != null)
            {
                foreach (object o in phs)
                {
                    Dictionary<string, object> m = o as Dictionary<string, object>;
                    if (m == null) continue;
                    PayHistLineSpec p = new PayHistLineSpec();
                    p.Action = OptStr(m, "action") ?? "set";
                    p.RowIndex = OptInt(m, "rowIndex", "index");
                    p.BillVchNo = OptStr(m, "billVchNo", "vchNo", "vch");
                    p.AdjAmt = OptDbl(m, "adjAmt", "adj");
                    p.DiscAmt = OptDbl(m, "discAmt", "disc");
                    s.PayHistLines.Add(p);
                }
            }

            return s;
        }

        private static object FindKey(Dictionary<string, object> d, params string[] keys)
        {
            foreach (string k in keys)
            {
                foreach (KeyValuePair<string, object> kv in d)
                    if (string.Equals(kv.Key, k, StringComparison.OrdinalIgnoreCase))
                        return kv.Value;
            }
            return null;
        }

        private static string OptStr(Dictionary<string, object> d, params string[] keys)
        {
            object o = FindKey(d, keys);
            return o == null ? null : Convert.ToString(o);
        }

        private static double? OptDbl(Dictionary<string, object> d, params string[] keys)
        {
            object o = FindKey(d, keys);
            if (o == null) return null;
            if (o is double) return (double)o;
            if (o is int || o is long) return Convert.ToDouble(o);
            double v = Helpers.ParseDbl(Convert.ToString(o), double.NaN);
            return double.IsNaN(v) ? (double?)null : v;
        }

        private static int? OptInt(Dictionary<string, object> d, params string[] keys)
        {
            object o = FindKey(d, keys);
            if (o == null) return null;
            if (o is int) return (int)o;
            if (o is long || o is double) return Convert.ToInt32(o);
            int v = Helpers.ParseInt(Convert.ToString(o), int.MinValue);
            return v == int.MinValue ? (int?)null : v;
        }

        private static long? OptLong(Dictionary<string, object> d, params string[] keys)
        {
            object o = FindKey(d, keys);
            if (o == null) return null;
            if (o is long) return (long)o;
            if (o is int || o is double) return Convert.ToInt64(o);
            long v;
            if (long.TryParse(Convert.ToString(o).Trim(), out v)) return v;
            return null;
        }

        private static bool? OptBool(Dictionary<string, object> d, params string[] keys)
        {
            object o = FindKey(d, keys);
            if (o == null) return null;
            if (o is bool) return (bool)o;
            string s = Convert.ToString(o).Trim().ToUpperInvariant();
            if (s == "TRUE" || s == "1" || s == "YES" || s == "Y") return true;
            if (s == "FALSE" || s == "0" || s == "NO" || s == "N") return false;
            return null;
        }

        private static List<object> OptList(Dictionary<string, object> d, params string[] keys)
        {
            object o = FindKey(d, keys);
            return o as List<object>;
        }
    }

    /// <summary>
    /// Zero-dependency JSON parser and writer compatible with C# 5 / .NET 4.x.
    /// Supports comments (// and /* */) and trailing commas on input.
    /// </summary>
    public static class SimpleJson
    {
        public static string Serialize(object value, bool indent)
        {
            StringBuilder sb = new StringBuilder();
            WriteValue(sb, value, indent, 0);
            return sb.ToString();
        }

        private static void WriteIndent(StringBuilder sb, bool indent, int depth)
        {
            if (!indent) return;
            for (int i = 0; i < depth * 2; i++) sb.Append(' ');
        }

        private static void WriteValue(StringBuilder sb, object val, bool indent, int depth)
        {
            if (val == null || val == DBNull.Value)
            {
                sb.Append("null");
                return;
            }
            if (val is bool)
            {
                sb.Append(((bool)val) ? "true" : "false");
                return;
            }
            if (val is int || val is long || val is short || val is byte)
            {
                sb.Append(Convert.ToInt64(val).ToString(CultureInfo.InvariantCulture));
                return;
            }
            if (val is double || val is float || val is decimal)
            {
                double d = Convert.ToDouble(val);
                if (double.IsNaN(d) || double.IsInfinity(d)) sb.Append("null");
                else sb.Append(d.ToString("0.####", CultureInfo.InvariantCulture));
                return;
            }
            if (val is DateTime)
            {
                WriteString(sb, Helpers.FmtDateIso((DateTime)val));
                return;
            }
            if (val is string)
            {
                WriteString(sb, (string)val);
                return;
            }
            Dictionary<string, object> dict = val as Dictionary<string, object>;
            if (dict != null)
            {
                if (dict.Count == 0) { sb.Append("{}"); return; }
                sb.Append('{');
                if (indent) sb.Append("\r\n");
                int i = 0;
                foreach (KeyValuePair<string, object> kv in dict)
                {
                    if (i > 0) { sb.Append(','); if (indent) sb.Append("\r\n"); }
                    WriteIndent(sb, indent, depth + 1);
                    WriteString(sb, kv.Key);
                    sb.Append(indent ? ": " : ":");
                    WriteValue(sb, kv.Value, indent, depth + 1);
                    i++;
                }
                if (indent) { sb.Append("\r\n"); WriteIndent(sb, indent, depth); }
                sb.Append('}');
                return;
            }
            System.Collections.IEnumerable list = val as System.Collections.IEnumerable;
            if (list != null)
            {
                List<object> items = new List<object>();
                foreach (object item in list) items.Add(item);
                if (items.Count == 0) { sb.Append("[]"); return; }
                sb.Append('[');
                if (indent) sb.Append("\r\n");
                for (int i = 0; i < items.Count; i++)
                {
                    if (i > 0) { sb.Append(','); if (indent) sb.Append("\r\n"); }
                    WriteIndent(sb, indent, depth + 1);
                    WriteValue(sb, items[i], indent, depth + 1);
                }
                if (indent) { sb.Append("\r\n"); WriteIndent(sb, indent, depth); }
                sb.Append(']');
                return;
            }
            WriteString(sb, Convert.ToString(val));
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            if (s != null)
            {
                for (int i = 0; i < s.Length; i++)
                {
                    char c = s[i];
                    switch (c)
                    {
                        case '"': sb.Append("\\\""); break;
                        case '\\': sb.Append("\\\\"); break;
                        case '\b': sb.Append("\\b"); break;
                        case '\f': sb.Append("\\f"); break;
                        case '\n': sb.Append("\\n"); break;
                        case '\r': sb.Append("\\r"); break;
                        case '\t': sb.Append("\\t"); break;
                        default:
                            if (c < 32) sb.Append("\\u" + ((int)c).ToString("x4"));
                            else sb.Append(c);
                            break;
                    }
                }
            }
            sb.Append('"');
        }

        public static object Parse(string json)
        {
            if (string.IsNullOrEmpty(json)) throw new FormatException("JSON input is empty.");
            int idx = 0;
            object v = ParseValue(json, ref idx);
            SkipWs(json, ref idx);
            return v;
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length)
            {
                char c = s[i];
                if (char.IsWhiteSpace(c) || c == '\uFEFF') { i++; continue; }
                if (c == '/' && i + 1 < s.Length && s[i + 1] == '/')
                {
                    i += 2;
                    while (i < s.Length && s[i] != '\n') i++;
                    continue;
                }
                if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < s.Length && !(s[i] == '*' && s[i + 1] == '/')) i++;
                    if (i + 1 < s.Length) i += 2;
                    continue;
                }
                break;
            }
        }

        private static object ParseValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) throw new FormatException("Unexpected end of JSON input.");
            char c = s[i];
            if (c == '{') return ParseObject(s, ref i);
            if (c == '[') return ParseArray(s, ref i);
            if (c == '"') return ParseString(s, ref i);
            if (c == 't' && i + 3 < s.Length && s.Substring(i, 4) == "true") { i += 4; return true; }
            if (c == 'f' && i + 4 < s.Length && s.Substring(i, 5) == "false") { i += 5; return false; }
            if (c == 'n' && i + 3 < s.Length && s.Substring(i, 4) == "null") { i += 4; return null; }
            if (c == '-' || char.IsDigit(c)) return ParseNumber(s, ref i);
            throw new FormatException("Unexpected character '" + c + "' in JSON at position " + i + ".");
        }

        private static Dictionary<string, object> ParseObject(string s, ref int i)
        {
            Dictionary<string, object> d = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            i++; // '{'
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return d; }
            while (i < s.Length)
            {
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == '}') { i++; return d; }
                if (s[i] != '"') throw new FormatException("Expected string property name in JSON object at position " + i + ".");
                string key = ParseString(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new FormatException("Expected ':' after property '" + key + "' in JSON at position " + i + ".");
                i++;
                object val = ParseValue(s, ref i);
                d[key] = val;
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == '}') { i++; return d; }
                throw new FormatException("Expected ',' or '}' in JSON object at position " + i + ".");
            }
            throw new FormatException("Unterminated JSON object.");
        }

        private static List<object> ParseArray(string s, ref int i)
        {
            List<object> list = new List<object>();
            i++; // '['
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return list; }
            while (i < s.Length)
            {
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ']') { i++; return list; }
                list.Add(ParseValue(s, ref i));
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == ']') { i++; return list; }
                throw new FormatException("Expected ',' or ']' in JSON array at position " + i + ".");
            }
            throw new FormatException("Unterminated JSON array.");
        }

        private static string ParseString(string s, ref int i)
        {
            StringBuilder sb = new StringBuilder();
            i++; // '"'
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c == '\\')
                {
                    if (i >= s.Length) throw new FormatException("Unterminated escape sequence in JSON string.");
                    char esc = s[i++];
                    switch (esc)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (i + 4 > s.Length) throw new FormatException("Invalid \\uXXXX escape in JSON string.");
                            int code = int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                            sb.Append((char)code);
                            i += 4;
                            break;
                        default: sb.Append(esc); break;
                    }
                }
                else sb.Append(c);
            }
            throw new FormatException("Unterminated JSON string.");
        }

        private static object ParseNumber(string s, ref int i)
        {
            int start = i;
            if (s[i] == '-') i++;
            while (i < s.Length && char.IsDigit(s[i])) i++;
            bool isFloat = false;
            if (i < s.Length && s[i] == '.')
            {
                isFloat = true;
                i++;
                while (i < s.Length && char.IsDigit(s[i])) i++;
            }
            if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
            {
                isFloat = true;
                i++;
                if (i < s.Length && (s[i] == '+' || s[i] == '-')) i++;
                while (i < s.Length && char.IsDigit(s[i])) i++;
            }
            string num = s.Substring(start, i - start);
            if (!isFloat)
            {
                long lv;
                if (long.TryParse(num, NumberStyles.Integer, CultureInfo.InvariantCulture, out lv))
                {
                    if (lv >= int.MinValue && lv <= int.MaxValue) return (int)lv;
                    return lv;
                }
            }
            double dv;
            if (double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out dv)) return dv;
            throw new FormatException("Invalid number '" + num + "' in JSON.");
        }
    }

    /// <summary>TSV and CSV tabular reader/writer for Excel/Calc batch workflows.</summary>
    public static class TabularIo
    {
        public static char DetectSeparator(string path)
        {
            if (!string.IsNullOrEmpty(path) && path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                return ',';
            return '\t';
        }

        public static string JoinRow(string[] cells, char sep)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < cells.Length; i++)
            {
                if (i > 0) sb.Append(sep);
                string v = cells[i] ?? "";
                if (sep == '\t')
                {
                    sb.Append(v.Replace('\t', ' ').Replace("\r", " ").Replace("\n", " "));
                }
                else
                {
                    if (v.IndexOf(sep) >= 0 || v.IndexOf('"') >= 0 || v.IndexOf('\n') >= 0 || v.IndexOf('\r') >= 0)
                        sb.Append('"').Append(v.Replace("\"", "\"\"")).Append('"');
                    else
                        sb.Append(v);
                }
            }
            return sb.ToString();
        }

        public static List<string[]> ReadRows(string text, char sep)
        {
            List<string[]> rows = new List<string[]>();
            if (string.IsNullOrEmpty(text)) return rows;
            using (StringReader sr = new StringReader(text))
            {
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    if (line.Trim().Length == 0) continue;
                    rows.Add(SplitLine(line, sep));
                }
            }
            return rows;
        }

        public static List<string[]> ReadFile(string path, char sep)
        {
            return ReadRows(File.ReadAllText(path, Encoding.UTF8), sep);
        }

        public static void WriteFile(string path, string[] headers, List<string[]> rows, char sep)
        {
            StringBuilder sb = new StringBuilder();
            if (headers != null) sb.AppendLine(JoinRow(headers, sep));
            if (rows != null)
                foreach (string[] r in rows) sb.AppendLine(JoinRow(r, sep));
            string dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }

        private static string[] SplitLine(string line, char sep)
        {
            if (sep == '\t') return line.Split('\t');
            List<string> cols = new List<string>();
            StringBuilder cur = new StringBuilder();
            bool inQuotes = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; }
                        else inQuotes = false;
                    }
                    else cur.Append(c);
                }
                else
                {
                    if (c == '"') inQuotes = true;
                    else if (c == sep) { cols.Add(cur.ToString()); cur.Length = 0; }
                    else cur.Append(c);
                }
            }
            cols.Add(cur.ToString());
            return cols.ToArray();
        }
    }
}
