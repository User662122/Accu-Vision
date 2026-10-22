using System;
using System.Collections.Generic;
using System.Data;
using System.Data.OleDb;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace AccuVisionRepost.Core
{
    /// <summary>
    /// The clone-edit-repost engine.
    ///
    /// Design rule (learned from the AccNew decompilation, FINDINGS.md and
    /// SaleBillRepost.bas on the owner's Drive): a voucher is a GROUP of rows
    /// glued by BT + Tran_Type + ID_NO + VchNo + UId. Instead of synthesising
    /// every column from scratch, this engine copies the source voucher's rows
    /// verbatim (all columns), applies the user's edits, recomputes only the
    /// documented totals, allocates fresh numbers from AccNew's own counters
    /// (UID.UID, Control.No_1, TranType.No_1, per-table max Uid2) inside one
    /// transaction, then replays AccNew's side effects (AccMst leaf + AccBom
    /// ancestors, Item stock mirrors + ItemTrn chain, PriseList).
    /// </summary>
    public sealed class CloneEngine
    {
        private readonly CompanyDb _db;
        public CloneEngine(CompanyDb db) { _db = db; }
        private JetDb Jet { get { return _db.Jet; } }
        private DbProbe St { get { return _db.State; } }

        // ================================================================ SEARCH

        public List<VoucherSummary> Search(string[] bts, string tranType, DateTime fromDate, DateTime toDate, string searchText)
        {
            if (St == null) throw new InvalidOperationException("Probe the database first.");
            string btList = "'" + string.Join("','", bts) + "'";
            List<VoucherSummary> list = new List<VoucherSummary>();
            Dictionary<long, double> drTotals = new Dictionary<long, double>();
            HashSet<long> itemMatchUids = new HashSet<long>();
            string text = (searchText ?? "").Trim();

            using (OleDbConnection cn = Jet.Open(false))
            {
                DataTable dr = Jet.QuerySafe(cn,
                    "SELECT [UId], Sum([Tran_amt]) AS s FROM [EFF] WHERE [DbCr]='D' AND [BT] IN (" + btList + ") GROUP BY [UId]");
                foreach (DataRow r in dr.Rows)
                    drTotals[Convert.ToInt64(JetDb.GetDbl(r, "UId"))] = JetDb.GetDbl(r, "s");

                if (text.Length > 0 && Jet.TableExists("PTL"))
                {
                    DataTable im = Jet.QuerySafe(cn,
                        "SELECT l.[Uid] AS u FROM [PTL] l, [Item] i WHERE l.[ItemCode]=i.[ItemCode] AND i.[Name] LIKE ?",
                        "%" + text + "%");
                    foreach (DataRow r in im.Rows) itemMatchUids.Add(Convert.ToInt64(JetDb.GetDbl(r, "u")));
                }

                string sql = "SELECT TOP 3000 * FROM [INF] WHERE [BT] IN (" + btList + ")";
                List<object> pars = new List<object>();
                if (!string.IsNullOrEmpty(tranType)) { sql += " AND [Tran_Type]=?"; pars.Add(tranType); }
                if (fromDate != DateTime.MinValue) { sql += " AND [VchDate]>=?"; pars.Add(fromDate.Date); }
                if (toDate != DateTime.MinValue) { sql += " AND [VchDate]<=?"; pars.Add(toDate.Date); }
                sql += " ORDER BY [VchDate] DESC, [Tran_Type], [VchNo] DESC";
                DataTable inf = Jet.Query(cn, null, sql, pars.ToArray());

                foreach (DataRow r in inf.Rows)
                {
                    VoucherSummary s = new VoucherSummary();
                    s.UId = Convert.ToInt64(JetDb.GetDbl(r, "UId"));
                    s.BT = JetDb.GetStr(r, "BT").Trim();
                    s.TranType = JetDb.GetStr(r, "Tran_Type").Trim();
                    SeriesRow ser = St.FindSeries(s.BT, s.TranType);
                    s.BookName = ser == null ? (s.BT + "-" + s.TranType) : ser.Name;
                    s.IDNo = JetDb.GetStr(r, "ID_NO");
                    s.VchNo = JetDb.GetStr(r, "VchNo");
                    s.VchDate = JetDb.GetDate(r, "VchDate", DateTime.MinValue);
                    s.AccCode = JetDb.GetStr(r, "Acc_Code").Trim();
                    s.AccName = St.AccountName(s.AccCode);
                    double amt = JetDb.GetDbl(r, "tran_amt");
                    if (Math.Abs(amt) < 0.004)
                    {
                        double t;
                        if (drTotals.TryGetValue(s.UId, out t)) amt = t;
                    }
                    s.Amount = amt;
                    s.Narration = JetDb.GetStr(r, "Naration");
                    list.Add(s);
                }
            }

            if (text.Length > 0)
            {
                string nt = Helpers.NormName(text);
                List<VoucherSummary> filtered = new List<VoucherSummary>();
                foreach (VoucherSummary s in list)
                {
                    bool hit = Helpers.NormName(s.AccName).Contains(nt)
                               || Helpers.NormName(s.Narration).Contains(nt)
                               || Helpers.NormName(s.BookName).Contains(nt)
                               || s.VchNoTrim == text.Trim()
                               || s.IDNo.Trim() == text.Trim()
                               || itemMatchUids.Contains(s.UId);
                    if (hit) filtered.Add(s);
                }
                list = filtered;
            }
            return list;
        }

        // ================================================================ LOAD

        private DataTable LoadByIdentity(OleDbConnection cn, string table, string bt, string tranType, string idNo, string vchNo, long uid)
        {
            if (!Jet.TableExists(table)) return null;
            string rt = Jet.RealTable(table);
            List<string> where = new List<string>();
            List<object> pars = new List<object>();

            string uidCol = null;
            foreach (string cand in new string[] { "UId", "UID", "Uid" })
                if (Jet.HasColumn(table, cand)) { uidCol = Jet.RealColumn(table, cand); break; }

            if (Jet.HasColumn(table, "BT")) { where.Add("[BT]=?"); pars.Add(bt); }
            if (uidCol != null && !string.Equals(table, "PTHL", StringComparison.OrdinalIgnoreCase))
            {
                // UID is the strongest link (PTHL also has it, but keep composite there for clarity)
                where.Add("[" + uidCol + "]=?");
                pars.Add(uid);
            }
            else
            {
                if (Jet.HasColumn(table, "Tran_Type")) { where.Add("[Tran_Type]=?"); pars.Add(tranType); }
                if (Jet.HasColumn(table, "ID_NO")) { where.Add("[ID_NO]=?"); pars.Add(idNo); }
                if (Jet.HasColumn(table, "VchNo")) { where.Add("[VchNo]=?"); pars.Add(vchNo); }
                else if (Jet.HasColumn(table, "Doc_No")) { where.Add("[Doc_No]=?"); pars.Add(vchNo); }
            }
            string order = "";
            if (Jet.HasColumn(table, "Line_no")) order = " ORDER BY [Line_no]";
            else if (Jet.HasColumn(table, "Line")) order = " ORDER BY [Line]";
            string sql = "SELECT * FROM [" + rt + "]" + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where.ToArray()) : "") + order;
            return Jet.Query(cn, null, sql, pars.ToArray());
        }

        private DataTable EmptySchema(OleDbConnection cn, string table)
        {
            if (!Jet.TableExists(table)) return null;
            return Jet.Query(cn, null, "SELECT * FROM [" + Jet.RealTable(table) + "] WHERE 1=0");
        }

        private int CountByIdentity(OleDbConnection cn, string table, string bt, string tranType, string idNo, string vchNo, long uid)
        {
            DataTable t = LoadByIdentity(cn, table, bt, tranType, idNo, vchNo, uid);
            return t == null ? 0 : t.Rows.Count;
        }

        public LoadedVoucher Load(long uid)
        {
            using (OleDbConnection cn = Jet.Open(false))
            {
                if (!Jet.HasSchema) Jet.LoadSchema(cn);
                DataTable inf = Jet.Query(cn, null, "SELECT * FROM [INF] WHERE [UId]=?", uid);
                if (inf.Rows.Count == 0) throw new InvalidOperationException("Voucher UId " + uid + " not found in INF.");
                if (inf.Rows.Count > 1) throw new InvalidOperationException("More than one INF row has UId " + uid + " - database needs checking first.");

                DataRow h = inf.Rows[0];
                LoadedVoucher v = new LoadedVoucher();
                v.SrcBT = JetDb.GetStr(h, "BT").Trim();
                v.SrcTranType = JetDb.GetStr(h, "Tran_Type").Trim();
                v.SrcIDNo = JetDb.GetStr(h, "ID_NO");
                v.SrcVchNo = JetDb.GetStr(h, "VchNo");
                v.SrcDate = JetDb.GetDate(h, "VchDate", DateTime.MinValue);
                v.SrcUId = uid;
                v.SrcParty = JetDb.GetStr(h, "Acc_Code").Trim();
                v.Kind = VoucherKinds.FromBT(v.SrcBT);
                v.NewTranType = v.SrcTranType;
                v.UserId = JetDb.GetStr(h, "UserId");
                v.Tables["INF"] = inf;

                string[] BT = new string[] { v.SrcBT };
                Func<string, DataTable> load = delegate(string t)
                {
                    return LoadByIdentity(cn, t, v.SrcBT, v.SrcTranType, v.SrcIDNo, v.SrcVchNo, uid);
                };

                v.Tables["EFF"] = load("EFF");

                if (v.Kind == VoucherKind.Sales)
                {
                    v.Tables["PTH"] = load("PTH");
                    v.Tables["PTHL"] = load("PTHL");
                    v.Tables["PTL"] = load("PTL");
                    v.Tables["ItemTrn"] = load("ItemTrn");
                    v.Tables["BillHist"] = load("BillHist");
                    // schemas needed even when empty (e.g. tax-free bill has no PTHL)
                    foreach (string t in new string[] { "PTH", "PTHL", "PTL", "ItemTrn", "BillHist" })
                        if (v.Tables[t] == null) v.Tables[t] = EmptySchema(cn, t);
                }

                DataTable effLine = load("EFFLine");
                if (effLine != null && effLine.Rows.Count > 0) v.Tables["EFFLine"] = effLine;
                DataTable infRem = load("INFRem");
                if (infRem != null && infRem.Rows.Count > 0) v.Tables["INFRem"] = infRem;

                // PayHist: linked by Our_Tran_Type + Our_VchNo (our receipt/payment)
                if (Jet.TableExists("PayHist") && Jet.HasColumn("PayHist", "Our_VchNo"))
                {
                    DataTable phAll = Jet.QuerySafe(cn,
                        "SELECT * FROM [PayHist] WHERE [Our_Tran_Type]=?", v.SrcTranType);
                    DataTable ph = phAll.Clone();
                    foreach (DataRow r in phAll.Rows)
                    {
                        if (string.Equals(JetDb.GetStr(r, "Our_VchNo").Trim(), v.SrcVchNo.Trim(), StringComparison.OrdinalIgnoreCase)
                            && (!JetDb.RowHas(r, "BT") || string.Equals(JetDb.GetStr(r, "BT").Trim(), v.SrcBT, StringComparison.OrdinalIgnoreCase)))
                            ph.ImportRow(r);
                    }
                    if (ph.Rows.Count > 0) v.Tables["PayHist"] = ph;
                }

                // ---- discovery: any other populated linked table we do not handle?
                string[] candidates = new string[] { "PTLL", "PTHL1", "PTL_PO", "PTH_PO", "PTHL_PO", "TDS", "JVXREF", "CHH", "CHL" };
                foreach (string t in candidates)
                {
                    if (!Jet.TableExists(t)) continue;
                    int n = 0;
                    try { n = CountByIdentity(cn, t, v.SrcBT, v.SrcTranType, v.SrcIDNo, v.SrcVchNo, uid); }
                    catch { n = 0; }
                    if (n > 0) v.UnsupportedExtras.Add(t + " (" + n + " row(s))");
                }

                v.SnapshotOriginal();
                v.Original["EFFTOTAL|D"] = v.EffTotal("D");
                v.Original["EFFTOTAL|C"] = v.EffTotal("C");
                double adj = 0, disc = 0;
                foreach (DataRow r in v.Rows("PayHist"))
                {
                    adj += JetDb.GetDbl(r, "Adj_Amt");
                    disc += JetDb.GetDbl(r, "Disc_Amt");
                }
                v.Original["PAYHIST|ADJ"] = adj;
                v.Original["PAYHIST|ADJDISC"] = adj + disc;

                // sensible default new date: same day next month (monthly re-post),
                // falling back to today when that leaves the financial year.
                DateTime def = Helpers.AddOneMonthClamped(v.SrcDate.Date);
                if (St != null && St.FyTo != DateTime.MinValue && (def > St.FyTo || def < St.FyFrom)) def = DateTime.Today;
                v.NewDate = def;
                return v;
            }
        }

        // ================================================================ RECALCULATION

        private static DataRow TemplateClone(DataTable t, DataRow template)
        {
            DataRow r = t.NewRow();
            if (template != null) r.ItemArray = (object[])template.ItemArray.Clone();
            return r;
        }

        public void ApplyNewBookIncomeRemap(LoadedVoucher v, List<string> warnings)
        {
            SeriesRow series = St.FindSeries(v.SrcBT, v.NewTranType);
            if (v.Kind != VoucherKind.Sales || series == null) return;
            string newIncome = (series.AccCode ?? "").Trim();
            if (newIncome.Length == 0) return;
            List<DataRow> ptl = v.Rows("PTL");
            if (ptl.Count == 0) return;
            string oldIncome = JetDb.GetStr(ptl[0], "Acc_COde").Trim();
            foreach (DataRow r in ptl)
                if (!string.Equals(JetDb.GetStr(r, "Acc_COde").Trim(), oldIncome, StringComparison.OrdinalIgnoreCase))
                {
                    warnings.Add("Item lines use more than one income account; book change did not remap income accounts.");
                    return;
                }
            if (string.Equals(oldIncome, newIncome, StringComparison.OrdinalIgnoreCase)) return;
            foreach (DataRow r in ptl) JetDb.SetVal(r, "Acc_COde", newIncome);
            foreach (DataRow r in v.Rows("PTHL"))
                if (JetDb.GetInt(r, "AddOns") == 99) JetDb.SetVal(r, "Acc_Code", newIncome);
            foreach (DataRow r in v.Rows("EFF"))
            {
                if (string.Equals(JetDb.GetStr(r, "Acc_Code").Trim(), oldIncome, StringComparison.OrdinalIgnoreCase))
                    JetDb.SetVal(r, "Acc_Code", newIncome);
                if (string.Equals(JetDb.GetStr(r, "Acc_Code2").Trim(), oldIncome, StringComparison.OrdinalIgnoreCase))
                    JetDb.SetVal(r, "Acc_Code2", newIncome);
            }
        }

        public void RecalcSales(LoadedVoucher v, bool autoTaxCode, List<string> warnings)
        {
            SeriesRow series = St.FindSeries(v.SrcBT, v.NewTranType);
            DataRow pth = v.Pth;
            if (pth == null) { warnings.Add("PTH header missing - cannot recalculate sales totals."); return; }

            // ---- item lines
            DataTable ptlTable = v.T("PTL");
            List<DataRow> ptl = v.Rows("PTL");
            double goods = 0, totQty = 0, totNos = 0, grossSum = 0;
            for (int i = 0; i < ptl.Count; i++)
            {
                DataRow r = ptl[i];
                int idx = ptlTable.Rows.IndexOf(r);
                double qty = JetDb.GetDbl(r, "Tran_Qty");
                double rate = JetDb.GetDbl(r, "Unit_Rate");
                string ratePer = JetDb.GetStr(r, "Rate_Per").Trim().ToUpperInvariant();
                double val;
                if (ratePer == "Q" || ratePer == "")
                {
                    val = Helpers.Round2(qty * rate);
                }
                else
                {
                    double oQty = v.Orig("PTL", idx, "Tran_Qty");
                    double oRate = v.Orig("PTL", idx, "Unit_Rate");
                    double oNet = v.Orig("PTL", idx, "Net_Value");
                    if (!double.IsNaN(oQty) && !double.IsNaN(oRate) && Math.Abs(oQty * oRate) > 0.0001 && !double.IsNaN(oNet))
                        val = Helpers.Round2(qty * rate * (oNet / (oQty * oRate)));
                    else
                        val = Helpers.Round2(qty * rate);
                    warnings.Add("Item line " + (i + 1) + " uses Rate_Per='" + ratePer + "' - value kept proportional to the source line. Please verify.");
                }
                JetDb.SetVal(r, "Value", val);
                JetDb.SetVal(r, "Net_Value", val);
                double oNos = v.Orig("PTL", idx, "Tran_Nos");
                double oQty2 = v.Orig("PTL", idx, "Tran_Qty");
                if (!double.IsNaN(oNos) && Helpers.EqAmt(oNos, oQty2) && Helpers.EqAmt(JetDb.GetDbl(r, "Tran_Nos"), oNos))
                    JetDb.SetVal(r, "Tran_Nos", qty);
                double oGross = v.Orig("PTL", idx, "Gross_qty");
                if (!double.IsNaN(oGross) && Helpers.EqAmt(oGross, oQty2) && Helpers.EqAmt(JetDb.GetDbl(r, "Gross_qty"), oGross))
                    JetDb.SetVal(r, "Gross_qty", qty);
                double oEntry = v.Orig("PTL", idx, "Entry_Rate");
                double oRate2 = v.Orig("PTL", idx, "Unit_Rate");
                if (!double.IsNaN(oEntry) && Helpers.EqAmt(oEntry, oRate2))
                    JetDb.SetVal(r, "Entry_Rate", rate);

                goods += val;
                totQty += JetDb.GetDbl(r, "Tran_Qty");
                totNos += JetDb.GetDbl(r, "Tran_Nos");
                grossSum += JetDb.GetDbl(r, "Gross_qty");
            }
            goods = Helpers.Round2(goods);

            // ---- add-ons (PTHL)
            DataTable pthlTable = v.T("PTHL");
            double origGoods = v.Orig("PTH", 0, "GoodAmt");
            DataRow roRow = null;
            List<DataRow> addn = v.Rows("PTHL");
            foreach (DataRow r in addn)
                if (JetDb.GetInt(r, "AddOns") == 99 && roRow == null) roRow = r;

            foreach (DataRow r in addn)
            {
                if (JetDb.GetInt(r, "AddOns") == 99) continue;
                int idx = pthlTable.Rows.IndexOf(r);
                double oAss = v.Orig("PTHL", idx, "Ass_val");
                double oAmt = v.Orig("PTHL", idx, "Amount");
                double oPer = v.Orig("PTHL", idx, "Per");
                if (!double.IsNaN(oAss) && !double.IsNaN(origGoods) && Helpers.EqAmt(oAss, origGoods))
                    JetDb.SetVal(r, "Ass_val", goods);
                double curAmt = JetDb.GetDbl(r, "Amount");
                double curPer = JetDb.GetDbl(r, "Per");
                bool amtUntouched = !double.IsNaN(oAmt) && Helpers.EqAmt(curAmt, oAmt);
                bool perChanged = !double.IsNaN(oPer) && !Helpers.EqAmt(curPer, oPer);
                bool wasPercentDerived = !double.IsNaN(oAmt) && !double.IsNaN(oAss) && !double.IsNaN(oPer)
                                          && Helpers.EqAmt(oAmt, Helpers.Round2(oAss * oPer / 100.0));
                if (amtUntouched && (wasPercentDerived || perChanged))
                    JetDb.SetVal(r, "Amount", Helpers.Round2(JetDb.GetDbl(r, "Ass_val") * curPer / 100.0));
            }

            double addPlusEx = 0, lessAmt = 0;
            foreach (DataRow r in v.Rows("PTHL"))
            {
                if (JetDb.GetInt(r, "AddOns") == 99) continue;
                if (JetDb.GetStr(r, "Type").Trim() == "-") lessAmt += JetDb.GetDbl(r, "Amount");
                else addPlusEx += JetDb.GetDbl(r, "Amount");
            }
            addPlusEx = Helpers.Round2(addPlusEx); lessAmt = Helpers.Round2(lessAmt);
            double rawEx = Helpers.Round2(goods + addPlusEx - lessAmt);

            string income = "";
            if (series != null && series.AccCode.Trim().Length > 0) income = series.AccCode.Trim();
            if (income.Length == 0 && ptl.Count > 0) income = JetDb.GetStr(ptl[0], "Acc_COde").Trim();

            bool autoRound = series != null && series.NetRoundoff == "1";
            double roAmt = 0;
            if (autoRound)
            {
                double netTarget = Math.Round(rawEx, 0);
                roAmt = Helpers.Round2(netTarget - rawEx);
                if (Math.Abs(roAmt) < 0.004)
                {
                    if (roRow != null) roRow.Delete();
                    roRow = null;
                }
                else
                {
                    if (roRow == null && pthlTable != null)
                    {
                        DataRow template = addn.Count > 0 ? addn[0] : null;
                        roRow = TemplateClone(pthlTable, template);
                        int line = 101;
                        foreach (DataRow r in v.Rows("PTHL"))
                            if (JetDb.GetInt(r, "AddOns") != 99) line = Math.Max(line, JetDb.GetInt(r, "Line") + 1);
                        JetDb.SetVal(roRow, "Line", line);
                        JetDb.SetVal(roRow, "AddOns", 99);
                        JetDb.SetVal(roRow, "Type", "+");
                        JetDb.SetVal(roRow, "Per", 0.0);
                        if (JetDb.RowHas(roRow, "Exc_Inc")) JetDb.SetVal(roRow, "Exc_Inc", "E");
                        pthlTable.Rows.Add(roRow);
                    }
                    if (roRow != null)
                    {
                        JetDb.SetVal(roRow, "Amount", roAmt);
                        JetDb.SetVal(roRow, "Ass_val", rawEx);
                        JetDb.SetVal(roRow, "Acc_Code", income);
                        JetDb.SetVal(roRow, "Type", "+");
                    }
                }
            }
            else if (roRow != null)
            {
                roAmt = JetDb.GetStr(roRow, "Type").Trim() == "-" ? -JetDb.GetDbl(roRow, "Amount") : JetDb.GetDbl(roRow, "Amount");
            }

            double addAmt = Helpers.Round2(addPlusEx + roAmt);
            double net = Helpers.Round2(goods + addAmt - lessAmt);

            if (autoTaxCode)
            {
                double pctSum = 0; bool anyTax = false;
                foreach (DataRow r in v.Rows("PTHL"))
                {
                    int code = JetDb.GetInt(r, "AddOns");
                    if ((code == 4 || code == 5 || code == 6) && Math.Abs(JetDb.GetDbl(r, "Amount")) > 0.004)
                    {
                        anyTax = true;
                        pctSum += JetDb.GetDbl(r, "Per");
                    }
                }
                string tc = anyTax ? ("G" + (int)Math.Round(pctSum)) : "TF";
                JetDb.SetVal(pth, "TaxCode", tc);
            }

            JetDb.SetVal(pth, "GoodAmt", goods);
            JetDb.SetVal(pth, "AddAmt", addAmt);
            JetDb.SetVal(pth, "LessAmt", lessAmt);
            JetDb.SetVal(pth, "NetAmt", net);
            JetDb.SetVal(pth, "Tran_Qty", Helpers.Round2(totQty));
            JetDb.SetVal(pth, "Tran_Nos", Helpers.Round2(totNos));

            // ---- EFF: party(0) / income(1) / add-ons(101+) - verified pattern
            DataRow inf = v.Inf;
            string party = JetDb.GetStr(pth, "Acc_Code").Trim();
            string headerNar = inf == null ? "" : JetDb.GetStr(inf, "Naration");
            DataTable effTable = v.T("EFF");
            List<DataRow> eff = v.Rows("EFF");
            Func<int, DataRow> findLine = delegate(int ln)
            {
                foreach (DataRow r in v.Rows("EFF")) if (JetDb.GetInt(r, "Line_no") == ln) return r;
                return null;
            };
            Func<int, DataRow> ensureLine = delegate(int ln)
            {
                DataRow r = findLine(ln);
                if (r != null) return r;
                DataRow template = v.Rows("EFF").Count > 0 ? v.Rows("EFF")[0] : null;
                r = TemplateClone(effTable, template);
                JetDb.SetVal(r, "Line_no", ln);
                JetDb.SetVal(r, "Other_amt", 0.0);
                JetDb.SetVal(r, "Other_rate", 0.0);
                if (JetDb.RowHas(r, "Check_Sys")) JetDb.SetVal(r, "Check_Sys", null);
                effTable.Rows.Add(r);
                return r;
            };

            DataRow partyLine = ensureLine(0);
            JetDb.SetVal(partyLine, "Acc_Code", party);
            JetDb.SetVal(partyLine, "Acc_Code2", income);
            JetDb.SetVal(partyLine, "DbCr", "D");
            JetDb.SetVal(partyLine, "Tran_amt", net);
            JetDb.SetVal(partyLine, "Naration", headerNar);
            JetDb.SetVal(partyLine, "Gross_qty", 0.0);

            // add-on EFF lines mirror PTHL (except 99, merged into income)
            HashSet<int> addonLines = new HashSet<int>();
            foreach (DataRow r in v.Rows("PTHL"))
            {
                int code = JetDb.GetInt(r, "AddOns");
                if (code == 99) continue;
                int ln = JetDb.GetInt(r, "Line");
                if (ln < 100) continue;
                addonLines.Add(ln);
                DataRow e = ensureLine(ln);
                JetDb.SetVal(e, "Acc_Code", JetDb.GetStr(r, "Acc_Code").Trim());
                JetDb.SetVal(e, "Acc_Code2", party);
                JetDb.SetVal(e, "DbCr", JetDb.GetStr(r, "Type").Trim() == "-" ? "D" : "C");
                JetDb.SetVal(e, "Tran_amt", JetDb.GetDbl(r, "Amount"));
                AddOnRow ao;
                string nar = (St.AddOns.TryGetValue(code, out ao) && ao.Name.Length > 0) ? ao.Name : JetDb.GetStr(e, "Naration");
                JetDb.SetVal(e, "Naration", nar);
                JetDb.SetVal(e, "Gross_qty", 0.0);
            }
            foreach (DataRow e in v.Rows("EFF"))
            {
                int ln = JetDb.GetInt(e, "Line_no");
                if (ln >= 100 && !addonLines.Contains(ln)) e.Delete();
            }

            List<DataRow> others = new List<DataRow>();
            foreach (DataRow e in v.Rows("EFF"))
            {
                int ln = JetDb.GetInt(e, "Line_no");
                if (ln != 0 && ln < 100) others.Add(e);
            }
            if (others.Count == 0) others.Add(ensureLine(1));
            if (others.Count == 1)
            {
                DataRow inc = others[0];
                JetDb.SetVal(inc, "Acc_Code", income);
                JetDb.SetVal(inc, "Acc_Code2", party);
                JetDb.SetVal(inc, "DbCr", "C");
                JetDb.SetVal(inc, "Tran_amt", Helpers.Round2(goods + roAmt));
                JetDb.SetVal(inc, "Naration", headerNar);
                JetDb.SetVal(inc, "Gross_qty", Helpers.Round2(grossSum));
            }
            else
            {
                warnings.Add("This bill has " + others.Count + " income/other ledger lines - amounts were NOT auto-balanced. Edit the ledger lines so Debit = Credit.");
            }

            if (inf != null)
            {
                JetDb.SetVal(inf, "tran_amt", net);
                JetDb.SetVal(inf, "Acc_Code", party);
                JetDb.SetVal(inf, "DbCr", "D");
                JetDb.SetVal(inf, "TaxCode", JetDb.GetStr(pth, "TaxCode"));
                JetDb.SetVal(inf, "Adj_Amt", 0.0);
            }
        }

        public void RecalcGeneric(LoadedVoucher v, List<string> warnings)
        {
            DataRow inf = v.Inf;
            if (inf == null) return;
            List<DataRow> eff = v.Rows("EFF");
            if (eff.Count == 0) return;

            string pref = JetDb.GetStr(inf, "DbCr").Trim().ToUpperInvariant();
            if (pref != "D" && pref != "C") pref = "D";
            DataRow head = null;
            foreach (DataRow r in eff)
                if (string.Equals(JetDb.GetStr(r, "DbCr").Trim(), pref, StringComparison.OrdinalIgnoreCase)) { head = r; break; }
            if (head == null) head = eff[0];
            JetDb.SetVal(inf, "Acc_Code", JetDb.GetStr(head, "Acc_Code").Trim());
            JetDb.SetVal(inf, "DbCr", JetDb.GetStr(head, "DbCr").Trim());

            double srcInfAmt = v.Orig("INF", 0, "tran_amt");
            double srcDr = v.Original.ContainsKey("EFFTOTAL|D") ? v.Original["EFFTOTAL|D"] : double.NaN;
            double srcCr = v.Original.ContainsKey("EFFTOTAL|C") ? v.Original["EFFTOTAL|C"] : double.NaN;
            if (!double.IsNaN(srcInfAmt))
            {
                if (Helpers.EqAmt(srcInfAmt, 0)) JetDb.SetVal(inf, "tran_amt", 0.0);
                else if (!double.IsNaN(srcDr) && Helpers.EqAmt(srcInfAmt, srcDr)) JetDb.SetVal(inf, "tran_amt", v.EffTotal("D"));
                else if (!double.IsNaN(srcCr) && Helpers.EqAmt(srcInfAmt, srcCr)) JetDb.SetVal(inf, "tran_amt", v.EffTotal("C"));
                else warnings.Add("INF amount (" + Helpers.FmtAmt(srcInfAmt) + ") does not match the source Dr/Cr totals - left unchanged. Please verify.");
            }

            // PayHist -> INF Adj_Amt pattern (source INF.Adj == sum(Adj) or sum(Adj+Disc))
            List<DataRow> ph = v.Rows("PayHist");
            if (ph.Count > 0)
            {
                double srcAdjInf = v.Orig("INF", 0, "Adj_Amt");
                double sA = v.Original.ContainsKey("PAYHIST|ADJ") ? v.Original["PAYHIST|ADJ"] : double.NaN;
                double sAD = v.Original.ContainsKey("PAYHIST|ADJDISC") ? v.Original["PAYHIST|ADJDISC"] : double.NaN;
                double nA = 0, nd = 0;
                foreach (DataRow r in ph) { nA += JetDb.GetDbl(r, "Adj_Amt"); nd += JetDb.GetDbl(r, "Disc_Amt"); }
                if (!double.IsNaN(srcAdjInf) && !double.IsNaN(sAD) && Helpers.EqAmt(srcAdjInf, sAD))
                    JetDb.SetVal(inf, "Adj_Amt", Helpers.Round2(nA + nd));
                else if (!double.IsNaN(srcAdjInf) && !double.IsNaN(sA) && Helpers.EqAmt(srcAdjInf, sA))
                    JetDb.SetVal(inf, "Adj_Amt", Helpers.Round2(nA));
                else
                    warnings.Add("Bill adjustments exist, but INF.Adj_Amt does not follow the source pattern - left unchanged. Check the bill-adjustment tab.");
            }

            FillMissingContra(v);
        }

        /// <summary>
        /// AccNew stores the "other side" account on EVERY ledger line (EFF.Acc_Code2):
        /// in live JV 439/440 line 1 is A with Acc_Code2 = B and line 2 is B with
        /// Acc_Code2 = A. Cloned lines already carry it, but a line added by hand in the
        /// grid starts blank - fill those from the first line on the opposite Dr/Cr side,
        /// which is the 2-line rule and the natural generalisation for longer vouchers.
        /// Sales are left alone (their lines are role-coded 0/1/101+ and use other rules).
        /// </summary>
        public void FillMissingContra(LoadedVoucher v)
        {
            if (v == null || v.Kind == VoucherKind.Sales) return;
            List<DataRow> eff = v.Rows("EFF");
            if (eff.Count < 2) return;

            string firstDr = "", firstCr = "";
            foreach (DataRow r in eff)
            {
                string acc = JetDb.GetStr(r, "Acc_Code").Trim();
                if (acc.Length == 0) continue;
                string dc = JetDb.GetStr(r, "DbCr").Trim().ToUpperInvariant();
                if (dc == "D" && firstDr.Length == 0) firstDr = acc;
                else if (dc == "C" && firstCr.Length == 0) firstCr = acc;
            }

            foreach (DataRow r in eff)
            {
                if (JetDb.GetStr(r, "Acc_Code2").Trim().Length > 0) continue;
                string dc = JetDb.GetStr(r, "DbCr").Trim().ToUpperInvariant();
                string contra = (dc == "D") ? firstCr : firstDr;
                if (contra.Length > 0) JetDb.SetVal(r, "Acc_Code2", contra);
            }
        }

        // ================================================================ VALIDATION

        public void Validate(LoadedVoucher v, List<string> errors, List<string> warnings)
        {
            foreach (string x in v.UnsupportedExtras)
                errors.Add("This source voucher also has rows in " + x + ", which this tool does not clone yet. Posting is blocked so nothing is lost.");

            SeriesRow series = St.FindSeries(v.SrcBT, v.NewTranType);
            if (series == null) errors.Add("Book (TranType) " + v.SrcBT + "-" + v.NewTranType + " was not found. Choose a book from the list.");

            // AccNew refuses these three cases itself, so a clone that reproduced one
            // could never be opened/re-saved in AccNew either. Wording follows the
            // original messages (Auto JV 0x534a90, Interest JV 0x534ac8, multi-contra 0x534df4).
            string seriesName = (series != null && series.Name != null) ? series.Name.Trim().ToUpperInvariant() : "";
            if (seriesName.Contains("AUTO JV"))
                errors.Add("AccNew does not allow adding or editing an AUTO JV voucher (\"Auto JV Can Not Add/Edit\"). Pick a normal Journal Voucher book.");
            if (seriesName.Contains("INTEREST JV"))
                errors.Add("AccNew does not allow adding or editing an Interest JV voucher (\"Interest JV Can Not Add/Edit\").");

            DateTime d = v.NewDate.Date;
            if (St.FyFrom != DateTime.MinValue && (d < St.FyFrom.Date || d > St.FyTo.Date))
                errors.Add("New date " + Helpers.FmtDate(d) + " is outside the financial year (" + Helpers.FmtDate(St.FyFrom) + " to " + Helpers.FmtDate(St.FyTo) + ").");

            List<DataRow> eff = v.Rows("EFF");
            if (eff.Count < 2) errors.Add("At least 2 ledger (EFF) lines are required.");
            foreach (DataRow r in eff)
            {
                string acc = JetDb.GetStr(r, "Acc_Code").Trim();
                string dc = JetDb.GetStr(r, "DbCr").Trim().ToUpperInvariant();
                double amt = JetDb.GetDbl(r, "Tran_amt");
                if (acc.Length == 0) errors.Add("EFF line " + JetDb.GetInt(r, "Line_no") + ": account is blank.");
                else
                {
                    AccountRow a = St.Account(acc);
                    if (a == null) errors.Add("EFF line " + JetDb.GetInt(r, "Line_no") + ": account " + acc + " does not exist in AccMst.");
                    else if (a.IsGroup) errors.Add("EFF line " + JetDb.GetInt(r, "Line_no") + ": " + a.Name.Trim() + " is a GROUP account - AccNew does not allow group accounts in vouchers.");
                }
                if (dc != "D" && dc != "C") errors.Add("EFF line " + JetDb.GetInt(r, "Line_no") + ": Dr/Cr must be D or C.");
                if (amt < -0.004) errors.Add("EFF line " + JetDb.GetInt(r, "Line_no") + ": amount cannot be negative.");
                if (Helpers.EqAmt(amt, 0)) warnings.Add("EFF line " + JetDb.GetInt(r, "Line_no") + " has zero amount.");
            }
            double dr = v.EffTotal("D"), cr = v.EffTotal("C");
            if (!Helpers.EqAmt(dr, cr)) errors.Add("Ledger lines do not balance: Debit " + Helpers.FmtAmt(dr) + " vs Credit " + Helpers.FmtAmt(cr) + " (difference " + Helpers.FmtAmt(Math.Abs(dr - cr)) + ").");
            if (Helpers.EqAmt(dr, 0)) errors.Add("Voucher amount is zero.");

            if (v.Kind == VoucherKind.Sales)
            {
                if (v.Pth == null) errors.Add("Sales header (PTH) is missing on the source bill.");
                List<DataRow> ptl = v.Rows("PTL");
                if (ptl.Count == 0) errors.Add("Sales bill has no item lines (PTL).");
                foreach (DataRow r in ptl)
                {
                    int ic = JetDb.GetInt(r, "ItemCode");
                    if (St.Item(ic) == null) errors.Add("Item " + ic + " does not exist in the Item master.");
                    if (JetDb.GetDbl(r, "Tran_Qty") < 0 || JetDb.GetDbl(r, "Unit_Rate") < 0)
                        errors.Add("Item line " + JetDb.GetInt(r, "Line_no") + ": qty/rate cannot be negative.");
                    if (JetDb.GetStr(r, "chno").Trim().Length > 0)
                        warnings.Add("Item line " + JetDb.GetInt(r, "Line_no") + " is linked to a challan (" + JetDb.GetStr(r, "chno").Trim() + ") - the clone will carry the same challan reference. Verify this is intended.");
                }
                if (v.Pth != null)
                {
                    double chk = Helpers.Round2(JetDb.GetDbl(v.Pth, "GoodAmt") + JetDb.GetDbl(v.Pth, "AddAmt") - JetDb.GetDbl(v.Pth, "LessAmt") - JetDb.GetDbl(v.Pth, "NetAmt"));
                    if (!Helpers.EqAmt(chk, 0)) errors.Add("Bill totals do not add up: GoodAmt + AddAmt - LessAmt != NetAmt (off by " + Helpers.FmtAmt(chk) + "). Press Recalculate.");
                    if (v.TaxInvMode == 2 && (v.NewTaxInvNo ?? "").Trim().Length == 0)
                        errors.Add("TAX Invoice No is blank (manual mode).");
                }
            }

            if (v.Kind == VoucherKind.Journal)
            {
                // "Multi - Contra Entry  . . . Can't Save" (0x534df4): a contra journal that
                // moves money between more than one pair of bank/cash accounts is refused.
                int contraLines = 0;
                foreach (DataRow r in eff)
                {
                    AccountRow a = St.Account(JetDb.GetStr(r, "Acc_Code").Trim());
                    if (a != null && (a.AccType == "03" || a.AccType == "04")) contraLines++;
                }
                if (contraLines > 2)
                    errors.Add("This is a multi-contra journal (" + contraLines + " bank/cash lines). AccNew refuses to save it (\"Multi - Contra Entry . . . Can't Save\").");
            }

            if (v.Kind == VoucherKind.BankCash)
            {
                bool hasBankOrCash = false;
                foreach (DataRow r in eff)
                {
                    AccountRow a = St.Account(JetDb.GetStr(r, "Acc_Code").Trim());
                    if (a != null && (a.AccType == "03" || a.AccType == "04")) hasBankOrCash = true;
                }
                if (!hasBankOrCash)
                    warnings.Add("No ledger line uses a Bank/Cash type account (Acc_type 03/04), which is unusual for a Bank-Cash Transaction. Please verify the accounts.");
            }

            foreach (DataRow r in v.Rows("PayHist"))
            {
                if (JetDb.GetDbl(r, "Adj_Amt") < -0.004) errors.Add("A bill adjustment (PayHist) has a negative Adj_Amt.");
            }

            ValidateTextLengths(v, errors);
        }

        private void ValidateTextLengths(LoadedVoucher v, List<string> errors)
        {
            foreach (KeyValuePair<string, DataTable> kv in v.Tables)
            {
                if (kv.Value == null) continue;
                foreach (DataRow r in kv.Value.Rows)
                {
                    if (r.RowState == DataRowState.Deleted) continue;
                    foreach (DataColumn c in kv.Value.Columns)
                    {
                        if (c.DataType != typeof(string)) continue;
                        int max = Jet.ColumnMaxLen(kv.Key, c.ColumnName);
                        object o = r[c];
                        if (max > 0 && o is string && ((string)o).Length > max)
                            errors.Add(kv.Key + "." + c.ColumnName + " is longer than the column allows (" + ((string)o).Length + " > " + max + " chars). Shorten it.");
                    }
                }
            }
        }

        // ================================================================ PLAN (review numbers)

        private sealed class Counters
        {
            public long SeriesNo;   // TranType.No_1 (or TranTypeSer.No_1 when a period row exists)
            public string SerVal;   // null = plain series, else the TranTypeSer period ('yyyyMM') that was used
            public long ControlNo;  // Control.No_1 snapshot
            public long Uid;        // UID.UID snapshot
            public long Eff2, Ptl2, Pth2;
        }

        private Counters ReadCounters(OleDbConnection cn, OleDbTransaction tx, LoadedVoucher v)
        {
            Counters c = new Counters();

            // AccNew's next-number routine (0x00d19860) asks the PERIOD series first:
            //     select * from trantypeser where bt = ? and tran_type = ? and ser_val = 'yyyyMM'
            // and only falls back to TranType.No_1 when no period row exists. The period
            // row is what gets bumped in that case, so remember which one was read.
            c.SerVal = null;
            string serVal = v.NewDate.Year.ToString("0000") + v.NewDate.Month.ToString("00");
            if (Jet.TableExists("TranTypeSer") && Jet.HasColumn("TranTypeSer", "Ser_Val") && Jet.HasColumn("TranTypeSer", "No_1"))
            {
                DataTable ts = Jet.Query(cn, tx,
                    "SELECT [No_1] FROM [TranTypeSer] WHERE [BT]=? AND [Tran_type]=? AND [Ser_Val]=?",
                    v.SrcBT, v.NewTranType, serVal);
                if (ts.Rows.Count > 1)
                    throw new InvalidOperationException("TranTypeSer rows for " + v.SrcBT + "-" + v.NewTranType + " period " + serVal + ": " + ts.Rows.Count + " (expected 1).");
                if (ts.Rows.Count == 1)
                {
                    c.SerVal = serVal;
                    c.SeriesNo = (long)Math.Round(JetDb.GetDbl(ts.Rows[0], "No_1"));
                }
            }

            if (c.SerVal == null)
            {
                DataTable s = Jet.Query(cn, tx, "SELECT [No_1] FROM [TranType] WHERE [BT]=? AND [Tran_type]=?", v.SrcBT, v.NewTranType);
                if (s.Rows.Count != 1) throw new InvalidOperationException("TranType series " + v.SrcBT + "-" + v.NewTranType + " rows found: " + s.Rows.Count + " (expected 1).");
                c.SeriesNo = (long)Math.Round(JetDb.GetDbl(s.Rows[0], "No_1"));
            }

            DataTable ct = Jet.Query(cn, tx, "SELECT [No_1],[Series] FROM [Control] WHERE [Prog_name]=?", v.SrcBT);
            bool found = false;
            foreach (DataRow r in ct.Rows)
            {
                if (JetDb.GetStr(r, "Series").Trim().Length == 0)
                {
                    if (found) throw new InvalidOperationException("More than one Control row for " + v.SrcBT + " with blank Series.");
                    c.ControlNo = (long)Math.Round(JetDb.GetDbl(r, "No_1"));
                    found = true;
                }
            }
            if (!found) throw new InvalidOperationException("Control counter row for " + v.SrcBT + " (blank Series) not found.");

            object u = Jet.Scalar(cn, tx, "SELECT [UID] FROM [UID]");
            c.Uid = (u == DBNull.Value) ? 0 : Convert.ToInt64(u);

            c.Eff2 = MaxTx(cn, tx, "EFF", "Uid2");
            c.Ptl2 = MaxTx(cn, tx, "PTL", "Uid2");
            c.Pth2 = MaxTx(cn, tx, "PTH", "UID2");
            return c;
        }

        private long MaxTx(OleDbConnection cn, OleDbTransaction tx, string table, string col)
        {
            if (!Jet.TableExists(table) || !Jet.HasColumn(table, col)) return 0;
            object o = Jet.Scalar(cn, tx, "SELECT Max([" + Jet.RealColumn(table, col) + "]) FROM [" + Jet.RealTable(table) + "]");
            return (o == DBNull.Value || o == null) ? 0 : Convert.ToInt64(o);
        }

        private bool VchExists(OleDbConnection cn, OleDbTransaction tx, string bt, string tt, string vchNo)
        {
            object o = Jet.Scalar(cn, tx, "SELECT Count(*) FROM [INF] WHERE [BT]=? AND [Tran_Type]=? AND [VchNo]=?", bt, tt, vchNo);
            return Convert.ToInt64(o) > 0;
        }
        private bool IdExists(OleDbConnection cn, OleDbTransaction tx, string bt, string idNo)
        {
            object o = Jet.Scalar(cn, tx, "SELECT Count(*) FROM [INF] WHERE [BT]=? AND [ID_NO]=?", bt, idNo);
            return Convert.ToInt64(o) > 0;
        }
        private bool UidExists(OleDbConnection cn, OleDbTransaction tx, long uid)
        {
            object o = Jet.Scalar(cn, tx, "SELECT Count(*) FROM [INF] WHERE [UId]=?", uid);
            return Convert.ToInt64(o) > 0;
        }

        public Dictionary<string, double[]> ComputeAccDeltas(LoadedVoucher v)
        {
            Dictionary<string, double[]> delta = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
            Action<string, double, double> add = delegate(string acc, double dcr, double ddb)
            {
                string cur = acc;
                HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                while (!string.IsNullOrEmpty(cur) && St.Accounts.ContainsKey(cur) && !seen.Contains(cur))
                {
                    double[] d;
                    if (!delta.TryGetValue(cur, out d)) { d = new double[] { 0, 0 }; delta[cur] = d; }
                    d[0] = Helpers.Round2(d[0] + dcr);
                    d[1] = Helpers.Round2(d[1] + ddb);
                    seen.Add(cur);
                    string parent;
                    cur = St.ParentOf.TryGetValue(cur, out parent) ? parent : null;
                }
            };
            foreach (DataRow r in v.Rows("EFF"))
            {
                string acc = JetDb.GetStr(r, "Acc_Code").Trim();
                double amt = JetDb.GetDbl(r, "Tran_amt");
                if (JetDb.GetStr(r, "DbCr").Trim().Equals("D", StringComparison.OrdinalIgnoreCase)) add(acc, 0, amt);
                else add(acc, amt, 0);
            }
            return delta;
        }

        /// <summary>Returns error text or null; fills human-readable lines.</summary>
        private string CheckBillAdjustments(OleDbConnection cn, OleDbTransaction tx, LoadedVoucher v, List<string> lines)
        {
            foreach (DataRow r in v.Rows("PayHist"))
            {
                string bt2 = JetDb.RowHas(r, "BT2") ? JetDb.GetStr(r, "BT2").Trim() : "";
                string billTt = JetDb.GetStr(r, "Tran_Type").Trim();
                string billVch = JetDb.GetStr(r, "VchNo");
                string billId = JetDb.GetStr(r, "ID_NO");
                double delta = Helpers.Round2(JetDb.GetDbl(r, "Adj_Amt") + JetDb.GetDbl(r, "Disc_Amt"));
                DataTable bh = Jet.Query(cn, tx,
                    "SELECT [Tran_amt],[Adj_Amt],[Acc_Code] FROM [BillHist] WHERE [BT]=? AND [Tran_Type]=? AND [ID_NO]=? AND [VchNo]=?",
                    bt2, billTt, billId, billVch);
                if (bh.Rows.Count == 0)
                    return "Bill adjustment refers to " + bt2 + " Vch " + billVch.Trim() + " (ID " + billId + ") but that bill was not found in BillHist. It may be an OPENING bill (OpBill/OpPayHist), which this version does not adjust. Posting stopped.";
                double billAmt = JetDb.GetDbl(bh.Rows[0], "Tran_amt");
                double already = JetDb.GetDbl(bh.Rows[0], "Adj_Amt");
                if (already + delta > billAmt + 0.011)
                    return "Bill " + bt2 + " Vch " + billVch.Trim() + ": adjustment " + Helpers.FmtAmt(delta) + " would exceed the pending amount " + Helpers.FmtAmt(billAmt - already) + " (bill " + Helpers.FmtAmt(billAmt) + ", already adjusted " + Helpers.FmtAmt(already) + "). Reduce the Adj. Amount.";
                lines.Add(string.Format("{0} Vch {1}: bill Rs. {2}, already adjusted Rs. {3}, this entry adjusts Rs. {4}, new pending Rs. {5}",
                    bt2, billVch.Trim(), Helpers.FmtAmt(billAmt), Helpers.FmtAmt(already), Helpers.FmtAmt(delta), Helpers.FmtAmt(billAmt - already - delta)));
            }
            return null;
        }

        public PostPlan BuildReviewPlan(LoadedVoucher v)
        {
            PostPlan plan = new PostPlan();
            plan.Series = St.FindSeries(v.SrcBT, v.NewTranType);
            using (OleDbConnection cn = Jet.Open(false))
            {
                Counters c = ReadCounters(cn, null, v);
                long vchNum = c.SeriesNo + 1;
                while (VchExists(cn, null, v.SrcBT, v.NewTranType, Helpers.PadVch(vchNum))) vchNum++;
                long idNum = c.ControlNo + 1;
                while (IdExists(cn, null, v.SrcBT, Helpers.PadId(idNum))) idNum++;
                long uidNum = c.Uid + 1;
                while (UidExists(cn, null, uidNum)) uidNum++;
                plan.NewVchNum = vchNum; plan.NewIdNum = idNum; plan.NewUId = uidNum;
                plan.NewVchNo = Helpers.PadVch(vchNum);
                plan.NewIDNo = Helpers.PadId(idNum);
                if (vchNum != c.SeriesNo + 1) plan.Notes.Add("Series counter was behind existing vouchers; first free Vch No " + vchNum + " will be used.");
                if (c.SerVal != null) plan.Notes.Add("Voucher number comes from the period series TranTypeSer (" + c.SerVal + "), exactly as AccNew takes it.");
                if (idNum != c.ControlNo + 1) plan.Notes.Add("Control ID counter was behind existing entries; first free ID_NO " + plan.NewIDNo + " will be used.");

                if (v.Kind == VoucherKind.Sales && v.Pth != null)
                {
                    string srcInv = JetDb.GetStr(v.Pth, "TAX_INV_NO");
                    // Pth row still carries source TAX_INV_NO until Prepare; use original from a fresh read of the row's current value
                    if (v.TaxInvMode == 2) plan.NewTaxInvNo = (v.NewTaxInvNo ?? "").Trim();
                    else if (v.TaxInvMode == 1) plan.NewTaxInvNo = Helpers.TaxInvKeepOffset(srcInv, Helpers.VchNoToLong(v.SrcVchNo), vchNum);
                    else plan.NewTaxInvNo = Helpers.TaxInvSameAsVch(srcInv, vchNum);

                    if ((plan.NewTaxInvNo ?? "").Length == 0)
                    {
                        plan.LiveDbErrors.Add("TAX Invoice No came out blank. Type or pass a TAX Invoice No.");
                    }
                    else if (Jet.TableExists("PTH"))
                    {
                        object dup = Jet.Scalar(cn, null, "SELECT Count(*) FROM [PTH] WHERE [BT]='SL' AND [TAX_INV_NO]=?", plan.NewTaxInvNo);
                        if (Convert.ToInt64(dup) > 0)
                            plan.LiveDbErrors.Add("TAX Invoice No '" + plan.NewTaxInvNo + "' already exists in this database. Change it (TAX Inv mode = Manual).");
                    }
                }

                foreach (KeyValuePair<string, DataTable> kv in v.Tables)
                {
                    if (kv.Value == null) continue;
                    if (string.Equals(kv.Key, "ItemTrn", StringComparison.OrdinalIgnoreCase)) continue;
                    int n = v.Rows(kv.Key).Count;
                    if (n > 0) plan.InsertCounts[kv.Key] = n;
                }
                if (v.Kind == VoucherKind.Sales)
                {
                    plan.InsertCounts["ItemTrn"] = v.Rows("PTL").Count;
                    // item balances from a fresh read
                    Dictionary<int, double> running = new Dictionary<int, double>();
                    foreach (DataRow r in v.Rows("PTL"))
                    {
                        int ic = JetDb.GetInt(r, "ItemCode");
                        double cur;
                        if (!running.TryGetValue(ic, out cur))
                        {
                            DataRow dbRow = ResolveItemDbRow(cn, null, ic);
                            cur = dbRow == null ? 0 : JetDb.GetDbl(dbRow, "Cl_St");
                        }
                        cur = Helpers.Round2(cur - JetDb.GetDbl(r, "Tran_Qty"));
                        running[ic] = cur;
                    }
                    foreach (KeyValuePair<int, double> kv in running) plan.ItemNewBal[kv.Key] = kv.Value;
                }

                plan.AccDeltas = ComputeAccDeltas(v);
                if (v.Rows("PayHist").Count > 0)
                {
                    string err = CheckBillAdjustments(cn, null, v, plan.BillAdjustments);
                    if (err != null)
                    {
                        plan.LiveDbErrors.Add(err);
                        plan.Notes.Add("BILL ADJUSTMENT WARNING: " + err);
                    }
                }
                return plan;
            }
        }

        private DataRow ResolveItemDbRow(OleDbConnection cn, OleDbTransaction tx, int itemCode)
        {
            DataTable t = Jet.Query(cn, tx, "SELECT * FROM [Item] WHERE [ItemCode]=?", itemCode);
            if (t.Rows.Count == 0) return null;
            if (t.Rows.Count == 1) return t.Rows[0];
            ItemRow want = St.Item(itemCode);
            if (want != null)
                foreach (DataRow r in t.Rows)
                    if (string.Equals(JetDb.GetStr(r, "Name").Trim(), want.Name.Trim(), StringComparison.OrdinalIgnoreCase)
                        && string.Equals(JetDb.GetStr(r, "Acc_Code").Trim(), want.AccCode.Trim(), StringComparison.OrdinalIgnoreCase))
                        return r;
            return t.Rows[0];
        }

        // ================================================================ PREPARE FINAL ROWS

        private static DataTable CopyTable(DataTable src)
        {
            DataTable t = src.Clone();
            foreach (DataRow r in src.Rows)
            {
                if (r.RowState == DataRowState.Deleted) continue;
                t.Rows.Add((object[])r.ItemArray.Clone());
            }
            return t;
        }

        private void SetIdentity(DataRow r, string bt, string tt, string idNo, string vchNo)
        {
            JetDb.SetVal(r, "BT", bt);
            JetDb.SetVal(r, "Tran_Type", tt);
            JetDb.SetVal(r, "ID_NO", idNo);
            if (JetDb.RowHas(r, "VchNo")) JetDb.SetVal(r, "VchNo", vchNo);
            if (JetDb.RowHas(r, "Doc_No")) JetDb.SetVal(r, "Doc_No", vchNo);
        }

        private void MoveDateIfSame(DataRow r, string col, DateTime srcDate, DateTime newDate)
        {
            if (!JetDb.RowHas(r, col) || JetDb.IsNull(r, col)) return;
            DateTime cur = JetDb.GetDate(r, col, DateTime.MinValue);
            if (cur.Date == srcDate.Date) JetDb.SetVal(r, col, newDate.Date);
        }

        private Dictionary<string, DataTable> PrepareFinal(LoadedVoucher v, PostPlan plan, OleDbConnection cn, OleDbTransaction tx,
            Dictionary<int, double> finalItemBal, Dictionary<int, DataRow> itemDbRows, List<string> notes)
        {
            FillMissingContra(v);   // any line still without an "other side" gets one before we copy

            Dictionary<string, DataTable> f = new Dictionary<string, DataTable>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, DataTable> kv in v.Tables)
            {
                if (kv.Value == null) continue;
                if (string.Equals(kv.Key, "ItemTrn", StringComparison.OrdinalIgnoreCase)) continue; // rebuilt below
                f[kv.Key] = CopyTable(kv.Value);
            }
            DateTime now = DateTime.Now;
            now = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second);
            string bt = v.SrcBT, tt = v.NewTranType;

            Counters counters = ReadCounters(cn, tx, v);
            long eff2 = counters.Eff2, ptl2 = counters.Ptl2, pth2 = counters.Pth2;

            // INF
            DataRow inf = f["INF"].Rows[0];
            SetIdentity(inf, bt, tt, plan.NewIDNo, plan.NewVchNo);
            JetDb.SetVal(inf, "VchDate", v.NewDate.Date);
            MoveDateIfSame(inf, "RefDate", v.SrcDate, v.NewDate);
            JetDb.SetVal(inf, "Entry_Date", now);
            JetDb.SetVal(inf, "UId", plan.NewUId);
            if (!string.IsNullOrEmpty((v.UserId ?? "").Trim())) JetDb.SetVal(inf, "UserId", Helpers.PadUser(v.UserId));
            if (v.Kind == VoucherKind.Sales) JetDb.SetVal(inf, "RefNo", plan.NewVchNo);

            // EFF (allocate Uid2 in Line_no order, like the app)
            List<DataRow> effRows = new List<DataRow>();
            foreach (DataRow r in f["EFF"].Rows) effRows.Add(r);
            effRows.Sort(delegate(DataRow a, DataRow b) { return JetDb.GetInt(a, "Line_no").CompareTo(JetDb.GetInt(b, "Line_no")); });
            // AccNew numbers journal/bank ledger lines 1..n (live data: 1, 2). Deleting a
            // line in the grid leaves a gap, so renumber on the way out. Sales keep their
            // role-coded numbering (0 = party, 1 = income, 101+ = taxes), and vouchers with
            // cheque/bank detail (EFFLine) are renumbered only when nothing points at the old
            // numbers - EFFLine.Line_no links each detail row to its ledger line.
            bool renumber = v.Kind != VoucherKind.Sales && v.Rows("EFFLine").Count == 0;
            int lineNo = 1;
            foreach (DataRow r in effRows)
            {
                SetIdentity(r, bt, tt, plan.NewIDNo, plan.NewVchNo);
                JetDb.SetVal(r, "VchDate", v.NewDate.Date);
                JetDb.SetVal(r, "UId", plan.NewUId);
                if (renumber) JetDb.SetVal(r, "Line_no", lineNo);
                eff2++;
                JetDb.SetVal(r, "Uid2", eff2);
                lineNo++;
            }

            if (v.Kind == VoucherKind.Sales)
            {
                // PTH
                DataRow pth = f["PTH"].Rows[0];
                string srcRefNo = JetDb.GetStr(pth, "RefNo");
                string srcRefNoP = JetDb.GetStr(pth, "RefNoP");
                SetIdentity(pth, bt, tt, plan.NewIDNo, plan.NewVchNo);
                JetDb.SetVal(pth, "VchDate", v.NewDate.Date);
                MoveDateIfSame(pth, "RefDate", v.SrcDate, v.NewDate);
                MoveDateIfSame(pth, "TAX_INV_DATE", v.SrcDate, v.NewDate);
                MoveDateIfSame(pth, "GstDate", v.SrcDate, v.NewDate);
                if (srcRefNo == v.SrcVchNo) JetDb.SetVal(pth, "RefNo", plan.NewVchNo);
                if (srcRefNoP.Trim() == v.SrcVchNo.Trim()) JetDb.SetVal(pth, "RefNoP", plan.NewVchNum.ToString());
                JetDb.SetVal(pth, "TAX_INV_NO", plan.NewTaxInvNo);
                JetDb.SetVal(pth, "Comm_paid", null);
                JetDb.SetVal(pth, "Comm_paid_Date", null);
                JetDb.SetVal(pth, "UID", plan.NewUId);
                pth2++;
                JetDb.SetVal(pth, "UID2", pth2);

                foreach (DataRow r in f["PTHL"].Rows)
                {
                    SetIdentity(r, bt, tt, plan.NewIDNo, plan.NewVchNo);
                    JetDb.SetVal(r, "UID", plan.NewUId);
                }

                List<DataRow> ptlRows = new List<DataRow>();
                foreach (DataRow r in f["PTL"].Rows) ptlRows.Add(r);
                ptlRows.Sort(delegate(DataRow a, DataRow b) { return JetDb.GetInt(a, "Line_no").CompareTo(JetDb.GetInt(b, "Line_no")); });
                foreach (DataRow r in ptlRows)
                {
                    SetIdentity(r, bt, tt, plan.NewIDNo, plan.NewVchNo);
                    JetDb.SetVal(r, "Uid", plan.NewUId);
                    ptl2++;
                    JetDb.SetVal(r, "Uid2", ptl2);
                }

                // ItemTrn rebuilt from the edited PTL, chaining on live stock
                DataTable itemTrnSchema = v.T("ItemTrn");
                DataTable newTrn = itemTrnSchema.Clone();
                Dictionary<int, double> running = new Dictionary<int, double>();
                Dictionary<int, DataRow> srcByLine = new Dictionary<int, DataRow>();
                foreach (DataRow r in v.Rows("ItemTrn")) srcByLine[JetDb.GetInt(r, "Line_no")] = r;
                int li = 0;
                foreach (DataRow ptl in ptlRows)
                {
                    int ic = JetDb.GetInt(ptl, "ItemCode");
                    double cur;
                    if (!running.TryGetValue(ic, out cur))
                    {
                        DataRow dbRow = ResolveItemDbRow(cn, tx, ic);
                        if (dbRow == null) throw new InvalidOperationException("Item " + ic + " not found in the Item master at posting time.");
                        itemDbRows[ic] = dbRow;
                        cur = JetDb.GetDbl(dbRow, "Cl_St");
                    }
                    double qty = JetDb.GetDbl(ptl, "Tran_Qty");
                    double newBal = Helpers.Round2(cur - qty);
                    running[ic] = newBal;

                    DataRow template;
                    srcByLine.TryGetValue(li, out template);
                    if (template != null && JetDb.GetInt(template, "ItemCode") != ic) template = null;
                    DataRow nr = newTrn.NewRow();
                    if (template != null) nr.ItemArray = (object[])template.ItemArray.Clone();
                    JetDb.SetVal(nr, "ItemCode", ic);
                    JetDb.SetVal(nr, "Tran_Date", v.NewDate.Date);
                    JetDb.SetVal(nr, "Doc_Date", v.NewDate.Date);
                    JetDb.SetVal(nr, "Old_Bal", cur);
                    JetDb.SetVal(nr, "Tran_Qty", qty);
                    JetDb.SetVal(nr, "New_Bal", newBal);
                    JetDb.SetVal(nr, "Tran_Qty1", JetDb.RowHas(nr, "Tran_Qty1") && template == null ? (object)0.0 : JetDb.GetRaw(nr, "Tran_Qty1"));
                    JetDb.SetVal(nr, "Line_no", li);
                    JetDb.SetVal(nr, "Tran_Qty_NOS", JetDb.GetDbl(ptl, "Tran_Nos"));
                    JetDb.SetVal(nr, "UId", plan.NewUId);
                    JetDb.SetVal(nr, "BT", bt);
                    JetDb.SetVal(nr, "Doc_No", plan.NewVchNo);
                    JetDb.SetVal(nr, "Acc_Code", JetDb.GetStr(ptl, "Acc_COde").Trim());
                    JetDb.SetVal(nr, "Tran_Type", tt);
                    JetDb.SetVal(nr, "ID_NO", plan.NewIDNo);
                    if (template == null)
                    {
                        JetDb.SetVal(nr, "New_Vch_No", null);
                        JetDb.SetVal(nr, "BT_DISP", null);
                        JetDb.SetVal(nr, "Check_Sys", null);
                    }
                    newTrn.Rows.Add(nr);
                    li++;
                }
                f["ItemTrn"] = newTrn;
                foreach (KeyValuePair<int, double> kv in running) finalItemBal[kv.Key] = kv.Value;

                // BillHist (fresh bill: nothing adjusted yet)
                if (f.ContainsKey("BillHist") && f["BillHist"].Rows.Count > 0)
                {
                    DataRow bh = f["BillHist"].Rows[0];
                    string bhRef = JetDb.GetStr(bh, "RefNo");
                    SetIdentity(bh, bt, tt, plan.NewIDNo, plan.NewVchNo);
                    JetDb.SetVal(bh, "VchDate", v.NewDate.Date);
                    MoveDateIfSame(bh, "RefDate", v.SrcDate, v.NewDate);
                    if (bhRef == v.SrcVchNo) JetDb.SetVal(bh, "RefNo", plan.NewVchNo);
                    JetDb.SetVal(bh, "Acc_Code", JetDb.GetStr(pth, "Acc_Code").Trim());
                    JetDb.SetVal(bh, "Tran_amt", JetDb.GetDbl(pth, "NetAmt"));
                    JetDb.SetVal(bh, "Adj_Amt", 0.0);
                    JetDb.SetVal(bh, "UID", plan.NewUId);
                    JetDb.SetVal(bh, "UID2", 0);
                    JetDb.SetVal(bh, "Excess_Amt", 0.0);
                    JetDb.SetVal(bh, "op_flag", null);
                    JetDb.SetVal(bh, "Print_flag", null);
                    JetDb.SetVal(bh, "Check_Sys", null);
                }
                else if (f.ContainsKey("BillHist"))
                {
                    notes.Add("Source bill had no BillHist row; none was created. Outstanding reports may need a check.");
                }
            }

            // EFFLine (cheque/bank detail per EFF line) - fresh entry is NOT cleared in a bank statement yet
            DataTable el;
            if (f.TryGetValue("EFFLine", out el))
            {
                foreach (DataRow r in el.Rows)
                {
                    SetIdentity(r, bt, tt, plan.NewIDNo, plan.NewVchNo);
                    if (JetDb.RowHas(r, "BankDate")) JetDb.SetVal(r, "BankDate", null);
                    foreach (string uc in new string[] { "UId", "UID", "Uid" })
                        if (JetDb.RowHas(r, uc) && Convert.ToInt64(JetDb.GetDbl(r, uc)) == v.SrcUId) JetDb.SetVal(r, uc, plan.NewUId);
                }
            }

            DataTable rem;
            if (f.TryGetValue("INFRem", out rem))
                foreach (DataRow r in rem.Rows) SetIdentity(r, bt, tt, plan.NewIDNo, plan.NewVchNo);

            DataTable ph;
            if (f.TryGetValue("PayHist", out ph))
            {
                foreach (DataRow r in ph.Rows)
                {
                    JetDb.SetVal(r, "Our_Tran_Type", tt);
                    JetDb.SetVal(r, "Our_VchNo", plan.NewVchNo);
                    if (JetDb.RowHas(r, "BT") && string.Equals(JetDb.GetStr(r, "BT").Trim(), v.SrcBT, StringComparison.OrdinalIgnoreCase))
                        JetDb.SetVal(r, "BT", bt);
                    foreach (string cname in new string[] { "Our_ID_NO", "Our_Id_No", "ID_NO2" })
                        if (JetDb.RowHas(r, cname) && JetDb.GetStr(r, cname) == v.SrcIDNo) JetDb.SetVal(r, cname, plan.NewIDNo);
                    foreach (string uc in new string[] { "UId", "UID", "Uid" })
                        if (JetDb.RowHas(r, uc) && Convert.ToInt64(JetDb.GetDbl(r, uc)) == v.SrcUId) JetDb.SetVal(r, uc, plan.NewUId);
                    MoveDateIfSame(r, "VchDate", v.SrcDate, v.NewDate);
                    MoveDateIfSame(r, "Our_VchDate", v.SrcDate, v.NewDate);
                }
            }

            return f;
        }

        // ================================================================ POST

        public static bool IsAccNewRunning()
        {
            try { return Process.GetProcessesByName("AccNew").Length > 0; }
            catch { return false; }
        }

        public static string LockFilePath(string dbPath)
        {
            return Path.Combine(Path.GetDirectoryName(dbPath), Path.GetFileNameWithoutExtension(dbPath) + ".ldb");
        }

        public string MakeBackup()
        {
            string dbDir = Path.GetDirectoryName(Jet.DbPath);
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string baseName = Path.GetFileNameWithoutExtension(Jet.DbPath) + "_BACKUP_" + stamp;
            string ext = Path.GetExtension(Jet.DbPath);
            string[] dirs = new string[]
            {
                Path.Combine(dbDir, "AccuVision_Backups"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AccuVisionRepost", "Backups")
            };
            Exception last = null;
            foreach (string dir in dirs)
            {
                try
                {
                    Directory.CreateDirectory(dir);
                    string dest = Path.Combine(dir, baseName + ext);
                    int suffix = 1;
                    while (File.Exists(dest))
                    {
                        dest = Path.Combine(dir, baseName + "_" + suffix + ext);
                        suffix++;
                    }
                    File.Copy(Jet.DbPath, dest, false);
                    FileInfo a = new FileInfo(Jet.DbPath);
                    FileInfo b = new FileInfo(dest);
                    if (a.Length != b.Length) throw new IOException("Backup size mismatch (" + b.Length + " vs " + a.Length + ").");
                    return dest;
                }
                catch (Exception ex) { last = ex; }
            }
            throw new InvalidOperationException("Could not create a database backup: " + (last == null ? "" : last.Message));
        }

        public PostResult Post(LoadedVoucher v, bool makeBackup)
        {
            return Post(v, makeBackup, false);
        }

        public PostResult Post(LoadedVoucher v, bool makeBackup, bool ignoreAccNewRunning)
        {
            _db.Probe(); // freshest account/hierarchy maps before anything is computed
            List<string> errors = new List<string>(), warnings = new List<string>();
            Validate(v, errors, warnings);
            if (errors.Count > 0)
                throw new InvalidOperationException("Please correct these first:\r\n- " + string.Join("\r\n- ", errors.ToArray()));

            if (!ignoreAccNewRunning && IsAccNewRunning())
                throw new InvalidOperationException("AccNew.exe is running. Close AccNew completely before posting, otherwise AccNew can overwrite the voucher counters when it exits.");

            string backup = makeBackup ? MakeBackup() : "";

            PostPlan plan = BuildReviewPlan(v);

            using (OleDbConnection cn = Jet.Open(true))
            {
                if (!Jet.HasSchema) Jet.LoadSchema(cn);
                OleDbTransaction tx = cn.BeginTransaction();
                try
                {
                    // allocate inside the transaction (never trust the preview)
                    Counters c = ReadCounters(cn, tx, v);
                    long vchNum = c.SeriesNo + 1;
                    while (VchExists(cn, tx, v.SrcBT, v.NewTranType, Helpers.PadVch(vchNum))) vchNum++;
                    long idNum = c.ControlNo + 1;
                    while (IdExists(cn, tx, v.SrcBT, Helpers.PadId(idNum))) idNum++;
                    long uidNum = c.Uid + 1;
                    while (UidExists(cn, tx, uidNum)) uidNum++;
                    plan.NewVchNum = vchNum; plan.NewVchNo = Helpers.PadVch(vchNum);
                    plan.NewIdNum = idNum; plan.NewIDNo = Helpers.PadId(idNum);
                    plan.NewUId = uidNum;

                    if (v.Kind == VoucherKind.Sales && v.Pth != null)
                    {
                        string srcInv = JetDb.GetStr(v.Pth, "TAX_INV_NO");
                        if (v.TaxInvMode == 2) plan.NewTaxInvNo = (v.NewTaxInvNo ?? "").Trim();
                        else if (v.TaxInvMode == 1) plan.NewTaxInvNo = Helpers.TaxInvKeepOffset(srcInv, Helpers.VchNoToLong(v.SrcVchNo), vchNum);
                        else plan.NewTaxInvNo = Helpers.TaxInvSameAsVch(srcInv, vchNum);
                        if ((plan.NewTaxInvNo ?? "").Length == 0)
                            throw new InvalidOperationException("TAX Invoice No came out blank. Type it in the header.");
                        object dup = Jet.Scalar(cn, tx, "SELECT Count(*) FROM [PTH] WHERE [BT]='SL' AND [TAX_INV_NO]=?", plan.NewTaxInvNo);
                        if (Convert.ToInt64(dup) > 0)
                            throw new InvalidOperationException("TAX Invoice No '" + plan.NewTaxInvNo + "' already exists in this database. Change it in the header (TAX Inv mode = Manual). Nothing was posted.");
                    }

                    List<string> billLines = new List<string>();
                    if (v.Rows("PayHist").Count > 0)
                    {
                        string berr = CheckBillAdjustments(cn, tx, v, billLines);
                        if (berr != null) throw new InvalidOperationException(berr + " Nothing was posted.");
                        plan.BillAdjustments = billLines;
                    }

                    Dictionary<int, double> finalBal = new Dictionary<int, double>();
                    Dictionary<int, DataRow> itemRows = new Dictionary<int, DataRow>();
                    List<string> notes = new List<string>();
                    Dictionary<string, DataTable> f = PrepareFinal(v, plan, cn, tx, finalBal, itemRows, notes);
                    plan.Notes.AddRange(notes);

                    plan.InsertCounts.Clear();
                    Action<string> insAll = delegate(string table)
                    {
                        DataTable t;
                        if (!f.TryGetValue(table, out t) || t == null) return;
                        int n = 0;
                        foreach (DataRow r in t.Rows) { Jet.InsertRow(cn, tx, table, r); n++; }
                        if (n > 0) plan.InsertCounts[table] = n;
                    };

                    // insert order follows the manual session (INF first per entry)
                    insAll("INF");
                    if (v.Kind == VoucherKind.Sales)
                    {
                        insAll("PTH"); insAll("PTHL"); insAll("PTL"); insAll("EFF"); insAll("ItemTrn"); insAll("BillHist");
                    }
                    else
                    {
                        insAll("EFF"); insAll("EFFLine"); insAll("INFRem"); insAll("PayHist");
                    }

                    // ---- AccMst balances (leaf + AccBom ancestors) from the final EFF
                    LoadedVoucher effOnly = new LoadedVoucher();
                    effOnly.Tables["EFF"] = f["EFF"];
                    Dictionary<string, double[]> deltas = ComputeAccDeltas(effOnly);
                    plan.AccDeltas = deltas;
                    foreach (KeyValuePair<string, double[]> kv in deltas)
                    {
                        DataTable a = Jet.Query(cn, tx, "SELECT [Db_amt],[Cr_amt],[Cl_bal],[Cl_dbcr] FROM [AccMst] WHERE [Acc_code]=?", kv.Key);
                        if (a.Rows.Count != 1) throw new InvalidOperationException("AccMst account " + kv.Key + " rows: " + a.Rows.Count + " (expected 1). Posting stopped.");
                        double dCr = kv.Value[0], dDb = kv.Value[1];
                        if (Helpers.EqAmt(dCr, 0) && Helpers.EqAmt(dDb, 0)) continue;
                        double newCr = Helpers.Round2(JetDb.GetDbl(a.Rows[0], "Cr_amt") + dCr);
                        double newDb = Helpers.Round2(JetDb.GetDbl(a.Rows[0], "Db_amt") + dDb);
                        string clSide = JetDb.GetStr(a.Rows[0], "Cl_dbcr").Trim().ToUpperInvariant();
                        if (clSide != "D" && clSide != "C") clSide = "D";
                        double newCl = Helpers.Round2(JetDb.GetDbl(a.Rows[0], "Cl_bal") + (clSide == "D" ? (dDb - dCr) : (dCr - dDb)));
                        string newSide = clSide;
                        if (newCl < -0.004) { newCl = Math.Abs(newCl); newSide = (clSide == "D") ? "C" : "D"; }
                        else if (newCl < 0) newCl = 0;
                        int n = Jet.Exec(cn, tx, "UPDATE [AccMst] SET [Db_amt]=?,[Cr_amt]=?,[Cl_bal]=?,[Cl_dbcr]=? WHERE [Acc_code]=?",
                            newDb, newCr, newCl, newSide, kv.Key);
                        if (n != 1) throw new InvalidOperationException("AccMst update for " + kv.Key + " affected " + n + " rows. Posting stopped.");
                    }

                    // ---- item stock mirrors + price list
                    if (v.Kind == VoucherKind.Sales)
                    {
                        string party = JetDb.GetStr(f["PTH"].Rows[0], "Acc_Code").Trim();
                        foreach (KeyValuePair<int, double> kv in finalBal)
                        {
                            DataRow dbRow = itemRows[kv.Key];
                            string whereAcc = JetDb.IsNull(dbRow, "Acc_Code") ? "[Acc_Code] IS NULL" : "[Acc_Code]=?";
                            List<object> vals = new List<object>() { kv.Value, kv.Value, kv.Value, kv.Value, kv.Value, kv.Key };
                            if (!JetDb.IsNull(dbRow, "Acc_Code")) vals.Add(JetDb.GetRaw(dbRow, "Acc_Code"));
                            vals.Add(JetDb.GetRaw(dbRow, "Name"));
                            int n = Jet.Exec(cn, tx,
                                "UPDATE [Item] SET [Cl_St]=?,[QtyPcsPur]=?,[QtyPcsClStock]=?,[QtyMtrPur]=?,[QtyMtrClStock]=? WHERE [ItemCode]=? AND " + whereAcc + " AND [Name]=?",
                                vals.ToArray());
                            if (n != 1) throw new InvalidOperationException("Item stock update for item " + kv.Key + " affected " + n + " rows. Posting stopped.");
                            plan.ItemNewBal[kv.Key] = kv.Value;
                        }

                        Dictionary<int, double> lastRate = new Dictionary<int, double>();
                        foreach (DataRow r in f["PTL"].Rows)
                            lastRate[JetDb.GetInt(r, "ItemCode")] = JetDb.GetDbl(r, "Unit_Rate");
                        foreach (KeyValuePair<int, double> kv in lastRate)
                        {
                            if (!Jet.TableExists("PriseList")) break;
                            int n = 0;
                            try
                            {
                                n = Jet.Exec(cn, tx, "UPDATE [PriseList] SET [Rate]=?,[Lstdate]=? WHERE [ItemCode]=? AND [Acc_Code]=?",
                                    kv.Value, v.NewDate.Date, kv.Key, party);
                            }
                            catch (Exception ex) { plan.Notes.Add("PriseList update skipped (" + ex.Message + ")."); }
                            if (n == 0) plan.Notes.Add("PriseList: no existing price row for item " + kv.Key + " / party " + party + " - AccNew price memory not updated (the bill itself is correct).");
                        }
                    }

                    // ---- PayHist bill side-effects
                    DataTable phFinal;
                    if (f.TryGetValue("PayHist", out phFinal))
                    {
                        foreach (DataRow r in phFinal.Rows)
                        {
                            string bt2 = JetDb.GetStr(r, "BT2").Trim();
                            string billTt = JetDb.GetStr(r, "Tran_Type").Trim();
                            string billVch = JetDb.GetStr(r, "VchNo");
                            string billId = JetDb.GetStr(r, "ID_NO");
                            double delta = Helpers.Round2(JetDb.GetDbl(r, "Adj_Amt") + JetDb.GetDbl(r, "Disc_Amt"));
                            int n = Jet.Exec(cn, tx,
                                "UPDATE [BillHist] SET [Adj_Amt]=[Adj_Amt]+? WHERE [BT]=? AND [Tran_Type]=? AND [ID_NO]=? AND [VchNo]=?",
                                delta, bt2, billTt, billId, billVch);
                            if (n != 1) throw new InvalidOperationException("BillHist adjustment update for " + bt2 + " Vch " + billVch.Trim() + " affected " + n + " rows. Posting stopped.");
                            int ni = Jet.Exec(cn, tx,
                                "UPDATE [INF] SET [Adj_Amt]=[Adj_Amt]+? WHERE [BT]=? AND [Tran_Type]=? AND [ID_NO]=? AND [VchNo]=?",
                                delta, bt2, billTt, billId, billVch);
                            if (ni != 1) plan.Notes.Add("Bill " + bt2 + " Vch " + billVch.Trim() + ": BillHist was adjusted, but its INF row was not found (opening bill?). Check outstanding reports.");
                        }
                    }

                    // ---- counters (guarded by the values read inside this transaction)
                    int nUp;
                    if (c.SerVal != null)
                    {
                        // the number came from the period series - bump that same row
                        nUp = Jet.Exec(cn, tx, "UPDATE [TranTypeSer] SET [No_1]=? WHERE [BT]=? AND [Tran_type]=? AND [Ser_Val]=? AND [No_1]=?",
                            (double)vchNum, v.SrcBT, v.NewTranType, c.SerVal, (double)c.SeriesNo);
                        if (nUp != 1) throw new InvalidOperationException("Period voucher counter (TranTypeSer " + c.SerVal + ") changed while posting (someone else posted?). Nothing was saved - reload and try again.");
                    }
                    else
                    {
                        nUp = Jet.Exec(cn, tx, "UPDATE [TranType] SET [No_1]=? WHERE [BT]=? AND [Tran_type]=? AND [No_1]=?",
                            (double)vchNum, v.SrcBT, v.NewTranType, (double)c.SeriesNo);
                        if (nUp != 1) throw new InvalidOperationException("Voucher series counter changed while posting (someone else posted?). Nothing was saved - reload and try again.");
                    }
                    nUp = Jet.Exec(cn, tx, "UPDATE [Control] SET [No_1]=? WHERE [Prog_name]=? AND [No_1]=? AND ([Series]='' OR [Series] IS NULL)",
                        (double)idNum, v.SrcBT, (double)c.ControlNo);
                    if (nUp != 1) throw new InvalidOperationException("Control ID counter update affected " + nUp + " rows. Nothing was saved.");
                    nUp = Jet.Exec(cn, tx, "UPDATE [UID] SET [UID]=? WHERE [UID]=?", uidNum, c.Uid);
                    if (nUp != 1) throw new InvalidOperationException("UID counter update affected " + nUp + " rows. Nothing was saved.");

                    tx.Commit();
                }
                catch
                {
                    try { tx.Rollback(); } catch { }
                    throw;
                }
            }

            _db.Probe();
            LoadedVoucher verify = Load(plan.NewUId);
            StringBuilder rep = new StringBuilder();
            rep.AppendLine("POSTED OK");
            rep.AppendLine("Source : " + v.SourceLabel);
            rep.AppendLine(string.Format("New    : {0} Book type {1}  Vch No {2}  Date {3}  ID_NO {4}  UId {5}",
                v.SrcBT, v.NewTranType, plan.NewVchNo.Trim(), Helpers.FmtDate(v.NewDate), plan.NewIDNo, plan.NewUId));
            if (v.Kind == VoucherKind.Sales) rep.AppendLine("TAX Invoice No: " + plan.NewTaxInvNo);
            rep.AppendLine("Inserted rows: " + plan.TotalInserts + " (" + JoinCounts(plan.InsertCounts) + ")");
            rep.AppendLine("Ledger check : Debit Rs. " + Helpers.FmtAmt(verify.EffTotal("D")) + " = Credit Rs. " + Helpers.FmtAmt(verify.EffTotal("C")));
            rep.AppendLine("Accounts updated (incl. groups): " + plan.AccDeltas.Count);
            foreach (KeyValuePair<int, double> kv in plan.ItemNewBal)
                rep.AppendLine("Item " + kv.Key + " (" + St.ItemName(kv.Key) + ") new stock " + kv.Value);
            foreach (string s in plan.BillAdjustments) rep.AppendLine("Bill adjustment: " + s);
            foreach (string s in plan.Notes) rep.AppendLine("Note: " + s);
            if (backup.Length > 0) rep.AppendLine("Database backup (before posting): " + backup);

            string log = WriteLog(rep.ToString());

            PostResult result = new PostResult();
            result.Plan = plan;
            result.BackupPath = backup;
            result.LogPath = log;
            result.Report = rep.ToString();
            return result;
        }

        private static string JoinCounts(Dictionary<string, int> d)
        {
            List<string> parts = new List<string>();
            foreach (KeyValuePair<string, int> kv in d) parts.Add(kv.Key + " x" + kv.Value);
            return string.Join(", ", parts.ToArray());
        }

        private string WriteLog(string text)
        {
            try
            {
                string dir = Path.Combine(Path.GetDirectoryName(Jet.DbPath), "AccuVision_Backups");
                Directory.CreateDirectory(dir);
                string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string p = Path.Combine(dir, "PostLog_" + stamp + ".txt");
                int suffix = 1;
                while (File.Exists(p))
                {
                    p = Path.Combine(dir, "PostLog_" + stamp + "_" + suffix + ".txt");
                    suffix++;
                }
                File.WriteAllText(p, text);
                return p;
            }
            catch { return ""; }
        }
    }
}
