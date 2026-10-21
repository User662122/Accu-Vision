using System;
using System.Collections.Generic;
using System.Data;
using System.Data.OleDb;
using System.Globalization;
using System.IO;
using System.Text;

namespace AccuVisionRepost.Core
{
    public sealed class SingleVoucherRunResult
    {
        public bool Ok;
        public int ExitCode;
        public string Command;
        public LoadedVoucher Voucher;
        public PostPlan Plan;
        public PostResult PostResult;
        public List<string> Errors = new List<string>();
        public List<string> Warnings = new List<string>();
        public string TextReport = "";

        public Dictionary<string, object> ToDictionary()
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["ok"] = Ok;
            d["exitCode"] = ExitCode;
            d["command"] = Command ?? "";
            if (Voucher != null)
            {
                d["srcUId"] = Voucher.SrcUId;
                d["srcBT"] = Voucher.SrcBT;
                d["srcBook"] = Voucher.NewTranType ?? Voucher.SrcTranType;
                d["srcVchNo"] = (Voucher.SrcVchNo ?? "").Trim();
                d["srcIDNo"] = Voucher.SrcIDNo;
                d["srcDate"] = Helpers.FmtDateIso(Voucher.SrcDate);
                d["newDate"] = Helpers.FmtDateIso(Voucher.NewDate);
                d["newBook"] = Voucher.NewTranType;
                d["drTotal"] = Voucher.EffTotal("D");
                d["crTotal"] = Voucher.EffTotal("C");
                if (Voucher.Kind == VoucherKind.Sales && Voucher.Pth != null)
                {
                    d["goodsAmt"] = JetDb.GetDbl(Voucher.Pth, "GoodAmt");
                    d["addAmt"] = JetDb.GetDbl(Voucher.Pth, "AddAmt");
                    d["lessAmt"] = JetDb.GetDbl(Voucher.Pth, "LessAmt");
                    d["netAmt"] = JetDb.GetDbl(Voucher.Pth, "NetAmt");
                    d["partyCode"] = JetDb.GetStr(Voucher.Pth, "Acc_Code").Trim();
                    d["taxCode"] = JetDb.GetStr(Voucher.Pth, "TaxCode").Trim();
                }
            }
            if (Plan != null)
            {
                d["newVchNo"] = (Plan.NewVchNo ?? "").Trim();
                d["newIDNo"] = Plan.NewIDNo;
                d["newUId"] = Plan.NewUId;
                if (!string.IsNullOrEmpty(Plan.NewTaxInvNo)) d["newTaxInvNo"] = Plan.NewTaxInvNo;
                d["totalInserts"] = Plan.TotalInserts;
                List<object> notes = new List<object>();
                foreach (string n in Plan.Notes) notes.Add(n);
                d["notes"] = notes;
            }
            if (PostResult != null)
            {
                d["posted"] = true;
                d["backupPath"] = PostResult.BackupPath ?? "";
                d["logPath"] = PostResult.LogPath ?? "";
            }
            else
            {
                d["posted"] = false;
            }
            List<object> errs = new List<object>();
            foreach (string e in Errors) errs.Add(e);
            d["errors"] = errs;

            List<object> warns = new List<object>();
            foreach (string w in Warnings) warns.Add(w);
            d["warnings"] = warns;

            d["report"] = TextReport ?? "";
            return d;
        }
    }

    public sealed class BatchRunResult
    {
        public bool Ok;
        public int ExitCode;
        public string Command;
        public int TotalRows;
        public int SelectedRows;
        public int SucceededRows;
        public int FailedRows;
        public int SkippedRows;
        public string BackupPath = "";
        public List<SingleVoucherRunResult> Items = new List<SingleVoucherRunResult>();
        public List<string> Errors = new List<string>();
        public List<string> Warnings = new List<string>();
        public string TextReport = "";

        public Dictionary<string, object> ToDictionary()
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["ok"] = Ok;
            d["exitCode"] = ExitCode;
            d["command"] = Command ?? "";
            d["totalRows"] = TotalRows;
            d["selectedRows"] = SelectedRows;
            d["succeededRows"] = SucceededRows;
            d["failedRows"] = FailedRows;
            d["skippedRows"] = SkippedRows;
            if (!string.IsNullOrEmpty(BackupPath)) d["backupPath"] = BackupPath;

            List<object> errs = new List<object>();
            foreach (string e in Errors) errs.Add(e);
            d["errors"] = errs;

            List<object> warns = new List<object>();
            foreach (string w in Warnings) warns.Add(w);
            d["warnings"] = warns;

            List<object> items = new List<object>();
            foreach (SingleVoucherRunResult it in Items) items.Add(it.ToDictionary());
            d["items"] = items;
            d["report"] = TextReport ?? "";
            return d;
        }
    }

    /// <summary>
    /// Headless execution engine: resolves source vouchers, applies CLI/JSON/TSV
    /// edits, runs full recalculation and live-DB validation, formats reports,
    /// exports/imports batch tables (Excel Review_Post compatible), and executes
    /// single or batch postings without any GUI dependency.
    /// </summary>
    public static class HeadlessEngine
    {
        // ================================================================ RESOLUTION

        public static AccountRow ResolveAccount(DbProbe probe, string codeOrName, bool requireLeaf, string context, List<string> errors)
        {
            string q = (codeOrName ?? "").Trim();
            if (q.Length == 0)
            {
                errors.Add(context + ": account code/name is blank.");
                return null;
            }
            AccountRow exact = probe.Account(q);
            if (exact != null)
            {
                if (requireLeaf && exact.IsGroup)
                {
                    errors.Add(context + ": account " + exact.Code + " (" + exact.Name.Trim() + ") is a GROUP account - AccNew does not allow group accounts in vouchers.");
                    return null;
                }
                return exact;
            }

            string norm = Helpers.NormName(q);
            List<AccountRow> exactNameMatches = new List<AccountRow>();
            List<AccountRow> subMatches = new List<AccountRow>();
            foreach (AccountRow a in probe.Accounts.Values)
            {
                if (requireLeaf && a.IsGroup) continue;
                string an = Helpers.NormName(a.Name);
                if (an == norm) exactNameMatches.Add(a);
                else if (an.Contains(norm)) subMatches.Add(a);
            }

            if (exactNameMatches.Count == 1) return exactNameMatches[0];
            if (exactNameMatches.Count > 1)
            {
                errors.Add(context + ": multiple accounts have the exact name '" + q + "' (" + JoinAccountSample(exactNameMatches) + "). Specify the exact account code.");
                return null;
            }
            if (subMatches.Count == 1) return subMatches[0];
            if (subMatches.Count > 1)
            {
                errors.Add(context + ": account '" + q + "' matches " + subMatches.Count + " accounts (" + JoinAccountSample(subMatches) + "). Specify the exact account code.");
                return null;
            }

            // Check if it matched a group account by name when requireLeaf was true
            if (requireLeaf)
            {
                foreach (AccountRow a in probe.Accounts.Values)
                {
                    if (a.IsGroup && Helpers.NormName(a.Name) == norm)
                    {
                        errors.Add(context + ": '" + a.Name.Trim() + "' (" + a.Code + ") is a GROUP account - AccNew does not allow group accounts in vouchers.");
                        return null;
                    }
                }
            }

            errors.Add(context + ": account '" + q + "' was not found in AccMst.");
            return null;
        }

        private static string JoinAccountSample(List<AccountRow> list)
        {
            List<string> s = new List<string>();
            for (int i = 0; i < list.Count && i < 5; i++)
                s.Add(list[i].Code + "=" + list[i].Name.Trim());
            if (list.Count > 5) s.Add("...");
            return string.Join(", ", s.ToArray());
        }

        public static ItemRow ResolveItem(DbProbe probe, string codeOrName, string context, List<string> errors)
        {
            string q = (codeOrName ?? "").Trim();
            if (q.Length == 0)
            {
                errors.Add(context + ": item code/name is blank.");
                return null;
            }
            int code;
            if (int.TryParse(q, out code))
            {
                ItemRow byCode = probe.Item(code);
                if (byCode != null) return byCode;
            }

            string norm = Helpers.NormName(q);
            List<ItemRow> exactMatches = new List<ItemRow>();
            List<ItemRow> subMatches = new List<ItemRow>();
            foreach (ItemRow it in probe.Items)
            {
                string nm = Helpers.NormName(it.Name);
                if (nm == norm) exactMatches.Add(it);
                else if (nm.Contains(norm)) subMatches.Add(it);
            }
            if (exactMatches.Count >= 1) return exactMatches[0];
            if (subMatches.Count == 1) return subMatches[0];
            if (subMatches.Count > 1)
            {
                List<string> sample = new List<string>();
                for (int i = 0; i < subMatches.Count && i < 5; i++)
                    sample.Add(subMatches[i].ItemCode + "=" + subMatches[i].Name.Trim());
                errors.Add(context + ": item '" + q + "' matches multiple items (" + string.Join(", ", sample.ToArray()) + "). Specify the numeric ItemCode.");
                return null;
            }
            errors.Add(context + ": item '" + q + "' does not exist in the Item master.");
            return null;
        }

        public static SeriesRow ResolveSeries(DbProbe probe, string bt, string bookCodeOrName, List<string> errors)
        {
            string q = (bookCodeOrName ?? "").Trim();
            if (q.Length == 0) return null;
            SeriesRow byCode = probe.FindSeries(bt, q);
            if (byCode != null) return byCode;

            // Try 2-digit zero-padded code (e.g. "1" -> "01")
            int num;
            if (int.TryParse(q, out num))
            {
                byCode = probe.FindSeries(bt, num.ToString("00", CultureInfo.InvariantCulture));
                if (byCode != null) return byCode;
            }

            string norm = Helpers.NormName(q);
            List<SeriesRow> matches = new List<SeriesRow>();
            foreach (SeriesRow s in probe.Series)
            {
                if (!string.IsNullOrEmpty(bt) && !string.Equals(s.BT, bt, StringComparison.OrdinalIgnoreCase)) continue;
                if (Helpers.NormName(s.Name) == norm || string.Equals(s.ShortName, q, StringComparison.OrdinalIgnoreCase))
                    return s;
                if (Helpers.NormName(s.Name).Contains(norm))
                    matches.Add(s);
            }
            if (matches.Count == 1) return matches[0];
            if (matches.Count > 1)
            {
                errors.Add("Book '" + q + "' matches multiple series for " + bt + ". Use the 2-character Tran_type code.");
                return null;
            }
            errors.Add("Book (TranType) '" + q + "' was not found" + (string.IsNullOrEmpty(bt) ? "." : (" for BT=" + bt + ".")));
            return null;
        }

        public static LoadedVoucher ResolveSourceVoucher(CompanyDb db, CloneEngine engine, VoucherEditSpec spec, List<string> errors)
        {
            if (db.State == null) db.Probe();
            if (spec.SrcUId > 0)
            {
                try
                {
                    return engine.Load(spec.SrcUId);
                }
                catch (Exception ex)
                {
                    errors.Add(ex.Message);
                    return null;
                }
            }

            string bt = (spec.SrcBT ?? "").Trim().ToUpperInvariant();
            string book = (spec.SrcBook ?? "").Trim();
            string vch = (spec.SrcVchNo ?? "").Trim();
            string idNo = (spec.SrcIDNo ?? "").Trim();

            if (vch.Length == 0 && idNo.Length == 0)
            {
                errors.Add("Specify the source voucher to load: use --uid <UId>, or --bt <SL|JV|CB|RE|PA> with --vch <VchNo> or --id-no <ID_NO>.");
                return null;
            }

            string tranType = null;
            if (book.Length > 0)
            {
                SeriesRow s = ResolveSeries(db.State, bt, book, errors);
                if (s == null) return null;
                tranType = s.TranType;
                if (bt.Length == 0) bt = s.BT;
            }

            string[] bts = bt.Length > 0 ? new string[] { bt } : new string[] { "SL", "CB", "RE", "PA", "JV" };
            List<VoucherSummary> all = engine.Search(bts, tranType, DateTime.MinValue, DateTime.MinValue, "");
            List<VoucherSummary> hits = new List<VoucherSummary>();
            string paddedId = "";
            long idLong;
            if (idNo.Length > 0 && long.TryParse(idNo, out idLong)) paddedId = Helpers.PadId(idLong);

            foreach (VoucherSummary s in all)
            {
                if (vch.Length > 0 && !string.Equals(s.VchNoTrim, vch, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (idNo.Length > 0 && !string.Equals(s.IDNo.Trim(), idNo, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(s.IDNo.Trim(), paddedId, StringComparison.OrdinalIgnoreCase))
                    continue;
                hits.Add(s);
            }

            if (hits.Count == 0)
            {
                errors.Add("Source voucher not found matching"
                    + (bt.Length > 0 ? (" BT=" + bt) : "")
                    + (tranType != null ? (" Book=" + tranType) : "")
                    + (vch.Length > 0 ? (" VchNo=" + vch) : "")
                    + (idNo.Length > 0 ? (" ID_NO=" + idNo) : "") + ".");
                return null;
            }
            if (hits.Count > 1)
            {
                List<string> desc = new List<string>();
                foreach (VoucherSummary h in hits)
                    desc.Add(string.Format("UId={0} ({1}-{2} Vch {3} Date {4})", h.UId, h.BT, h.TranType, h.VchNoTrim, Helpers.FmtDate(h.VchDate)));
                errors.Add("Multiple source vouchers match (" + string.Join("; ", desc.ToArray()) + "). Pass --uid <UId> or --book <TranType> to pick one.");
                return null;
            }

            try
            {
                return engine.Load(hits[0].UId);
            }
            catch (Exception ex)
            {
                errors.Add(ex.Message);
                return null;
            }
        }

        // ================================================================ APPLY EDITS

        private static DateTime AddMonthsClamped(DateTime d, int months)
        {
            DateTime cur = d;
            if (months >= 0)
            {
                for (int i = 0; i < months; i++) cur = Helpers.AddOneMonthClamped(cur);
                return cur;
            }
            return d.AddMonths(months);
        }

        public static void ApplyEdits(CompanyDb db, CloneEngine engine, LoadedVoucher v, VoucherEditSpec spec, List<string> errors, List<string> warnings)
        {
            if (spec == null) spec = new VoucherEditSpec();
            DbProbe probe = db.State;
            DataRow inf = v.Inf;

            // Capture original narrations before bumping
            string srcHeaderNar = inf == null ? "" : JetDb.GetStr(inf, "Naration");
            Dictionary<DataRow, string> srcPtlNar = new Dictionary<DataRow, string>();
            foreach (DataRow r in v.Rows("PTL")) srcPtlNar[r] = JetDb.GetStr(r, "Naration");
            Dictionary<DataRow, string> srcEffNar = new Dictionary<DataRow, string>();
            foreach (DataRow r in v.Rows("EFF")) srcEffNar[r] = JetDb.GetStr(r, "Naration");

            // 1. Date
            if (spec.PlusMonths.HasValue)
            {
                v.NewDate = AddMonthsClamped(v.SrcDate.Date, spec.PlusMonths.Value);
            }
            if (!string.IsNullOrEmpty(spec.NewDate))
            {
                DateTime parsedDate;
                if (!Helpers.TryParseDate(spec.NewDate, out parsedDate))
                    errors.Add("Invalid newDate '" + spec.NewDate + "' (expected yyyy-MM-dd or dd-MM-yyyy).");
                else
                    v.NewDate = parsedDate;
            }

            // 2. Book / Series
            if (!string.IsNullOrEmpty(spec.NewBook))
            {
                SeriesRow ser = ResolveSeries(probe, v.SrcBT, spec.NewBook, errors);
                if (ser != null)
                {
                    v.NewTranType = ser.TranType;
                    engine.ApplyNewBookIncomeRemap(v, warnings);
                }
            }

            // 3. Narration month/date auto-bumping (enabled by default, like GUI Tab 2)
            bool bump = spec.BumpNarration.HasValue ? spec.BumpNarration.Value : true;
            if (bump && inf != null)
            {
                string bumpedHead = Helpers.BumpNarration(srcHeaderNar, v.SrcDate, v.NewDate);
                JetDb.SetVal(inf, "Naration", bumpedHead.Length == 0 ? (object)DBNull.Value : bumpedHead);
                foreach (KeyValuePair<DataRow, string> kv in srcPtlNar)
                {
                    if (kv.Key.RowState == DataRowState.Deleted) continue;
                    JetDb.SetVal(kv.Key, "Naration", Helpers.BumpNarration(kv.Value, v.SrcDate, v.NewDate));
                }
                if (v.Kind != VoucherKind.Sales)
                {
                    foreach (KeyValuePair<DataRow, string> kv in srcEffNar)
                    {
                        if (kv.Key.RowState == DataRowState.Deleted) continue;
                        JetDb.SetVal(kv.Key, "Naration", Helpers.BumpNarration(kv.Value, v.SrcDate, v.NewDate));
                    }
                }
            }

            // 4. Explicit narration override & find/replace
            if (spec.Narration != null && inf != null)
            {
                JetDb.SetVal(inf, "Naration", spec.Narration.Length == 0 ? (object)DBNull.Value : spec.Narration);
            }
            if (spec.ReplaceNarration != null && spec.ReplaceNarration.Count > 0)
            {
                foreach (string pair in spec.ReplaceNarration)
                {
                    int eq = (pair ?? "").IndexOf('=');
                    if (eq <= 0) continue;
                    string fromStr = pair.Substring(0, eq);
                    string toStr = pair.Substring(eq + 1);
                    if (inf != null && !JetDb.IsNull(inf, "Naration"))
                        JetDb.SetVal(inf, "Naration", JetDb.GetStr(inf, "Naration").Replace(fromStr, toStr));
                    foreach (DataRow r in v.Rows("PTL"))
                        if (!JetDb.IsNull(r, "Naration"))
                            JetDb.SetVal(r, "Naration", JetDb.GetStr(r, "Naration").Replace(fromStr, toStr));
                    foreach (DataRow r in v.Rows("EFF"))
                        if (!JetDb.IsNull(r, "Naration"))
                            JetDb.SetVal(r, "Naration", JetDb.GetStr(r, "Naration").Replace(fromStr, toStr));
                }
            }

            // 5. UserId, RefNo, RefDate
            if (!string.IsNullOrEmpty(spec.UserId))
                v.UserId = spec.UserId.Trim();
            if (v.Kind != VoucherKind.Sales && inf != null)
            {
                if (spec.RefNo != null)
                    JetDb.SetVal(inf, "RefNo", spec.RefNo.Trim().Length == 0 ? (object)DBNull.Value : spec.RefNo.Trim());
                if (spec.RefDate != null)
                {
                    string rd = spec.RefDate.Trim();
                    if (rd.Length == 0 || string.Equals(rd, "null", StringComparison.OrdinalIgnoreCase) || string.Equals(rd, "none", StringComparison.OrdinalIgnoreCase) || string.Equals(rd, "clear", StringComparison.OrdinalIgnoreCase))
                        JetDb.SetVal(inf, "RefDate", DBNull.Value);
                    else
                    {
                        DateTime parsedRef;
                        if (!Helpers.TryParseDate(rd, out parsedRef))
                            errors.Add("Invalid refDate '" + rd + "'.");
                        else
                            JetDb.SetVal(inf, "RefDate", parsedRef);
                    }
                }
            }

            // 6. Sales Party & Header fields
            if (v.Kind == VoucherKind.Sales && v.Pth != null)
            {
                if (spec.Party != null)
                {
                    AccountRow partyAcc = ResolveAccount(probe, spec.Party, true, "Party", errors);
                    string newPartyCode = partyAcc != null ? partyAcc.Code : spec.Party.Trim();
                    string oldParty = JetDb.GetStr(v.Pth, "Acc_Code").Trim();
                    JetDb.SetVal(v.Pth, "Acc_Code", newPartyCode);
                    if (oldParty.Length > 0 && !string.Equals(oldParty, newPartyCode, StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (DataRow bh in v.Rows("BillHist"))
                            if (string.Equals(JetDb.GetStr(bh, "Acc_Code").Trim(), oldParty, StringComparison.OrdinalIgnoreCase))
                                JetDb.SetVal(bh, "Acc_Code", newPartyCode);
                        foreach (DataRow e in v.Rows("EFF"))
                        {
                            if (string.Equals(JetDb.GetStr(e, "Acc_Code").Trim(), oldParty, StringComparison.OrdinalIgnoreCase))
                                JetDb.SetVal(e, "Acc_Code", newPartyCode);
                            if (string.Equals(JetDb.GetStr(e, "Acc_Code2").Trim(), oldParty, StringComparison.OrdinalIgnoreCase))
                                JetDb.SetVal(e, "Acc_Code2", newPartyCode);
                        }
                    }
                }
                if (spec.PayMode != null) JetDb.SetVal(v.Pth, "PayMode", spec.PayMode.Trim());
                if (spec.CrDays.HasValue) JetDb.SetVal(v.Pth, "cr_day", spec.CrDays.Value);

                if (!string.IsNullOrEmpty(spec.TaxInvMode))
                {
                    string m = spec.TaxInvMode.Trim().ToLowerInvariant();
                    if (m == "0" || m == "same" || m == "vch" || m == "sameasvch") v.TaxInvMode = 0;
                    else if (m == "1" || m == "offset" || m == "gap" || m == "keepoffset" || m == "keepgap") v.TaxInvMode = 1;
                    else if (m == "2" || m == "manual") v.TaxInvMode = 2;
                    else errors.Add("Invalid taxInvMode '" + spec.TaxInvMode + "' (expected 'same', 'offset', or 'manual').");
                }
                if (spec.TaxInvNo != null)
                {
                    v.NewTaxInvNo = spec.TaxInvNo.Trim();
                    if (string.IsNullOrEmpty(spec.TaxInvMode))
                        v.TaxInvMode = 2;
                }
            }
            else if (spec.Party != null)
            {
                // For non-Sales vouchers, --party updates the head account in EFF (and mirrors)
                AccountRow headAcc = ResolveAccount(probe, spec.Party, true, "Party/Head account", errors);
                if (headAcc != null && v.Rows("EFF").Count > 0)
                {
                    string oldHead = JetDb.GetStr(v.Inf, "Acc_Code").Trim();
                    if (oldHead.Length == 0) oldHead = JetDb.GetStr(v.Rows("EFF")[0], "Acc_Code").Trim();
                    foreach (DataRow e in v.Rows("EFF"))
                    {
                        if (string.Equals(JetDb.GetStr(e, "Acc_Code").Trim(), oldHead, StringComparison.OrdinalIgnoreCase))
                            JetDb.SetVal(e, "Acc_Code", headAcc.Code);
                        if (string.Equals(JetDb.GetStr(e, "Acc_Code2").Trim(), oldHead, StringComparison.OrdinalIgnoreCase))
                            JetDb.SetVal(e, "Acc_Code2", headAcc.Code);
                    }
                }
            }

            // 7. Sales Item lines (PTL)
            if (v.Kind == VoucherKind.Sales)
            {
                ApplySalesItemEdits(probe, v, spec, errors);
            }

            // 8. Sales Tax / Add-On lines (PTHL) + RecalcSales
            bool autoTax = spec.AutoTaxCode.HasValue ? spec.AutoTaxCode.Value : string.IsNullOrEmpty(spec.TaxCode);
            if (v.Kind == VoucherKind.Sales)
            {
                Dictionary<int, double> explicitAddonAmounts = ApplySalesAddonEdits(probe, v, spec, errors);
                engine.RecalcSales(v, autoTax, warnings);
                if (explicitAddonAmounts.Count > 0)
                {
                    foreach (DataRow r in v.Rows("PTHL"))
                    {
                        int code = JetDb.GetInt(r, "AddOns");
                        double forcedAmt;
                        if (explicitAddonAmounts.TryGetValue(code, out forcedAmt))
                            JetDb.SetVal(r, "Amount", Helpers.Round2(forcedAmt));
                    }
                    // Re-run RecalcSales so PTH.AddAmt, RoundOff, NetAmt, and EFF lines reflect the explicit addon amounts
                    engine.RecalcSales(v, autoTax, warnings);
                }
                if (!autoTax && !string.IsNullOrEmpty(spec.TaxCode) && v.Pth != null)
                {
                    JetDb.SetVal(v.Pth, "TaxCode", spec.TaxCode.Trim());
                    if (inf != null) JetDb.SetVal(inf, "TaxCode", spec.TaxCode.Trim());
                }
            }

            // 9. Ledger (EFF) lines edits (for JV/CB/RE/PA, or explicit EFF overrides)
            ApplyEffEdits(probe, v, spec, errors);

            // 10. Cheque / Bank (EFFLine) edits
            ApplyChequeEdits(probe, v, spec, errors);

            // 11. Bill Adjustments (PayHist) edits
            ApplyPayHistEdits(v, spec, errors);

            if (v.Kind != VoucherKind.Sales)
            {
                engine.RecalcGeneric(v, warnings);
            }

            // 12. Validate
            engine.Validate(v, errors, warnings);
        }

        private static DataRow CloneTemplateRow(DataTable t)
        {
            DataRow r = t.NewRow();
            foreach (DataRow src in t.Rows)
            {
                if (src.RowState == DataRowState.Deleted) continue;
                r.ItemArray = (object[])src.ItemArray.Clone();
                break;
            }
            return r;
        }

        private static int NextLineNum(List<DataRow> rows, string col, int minVal)
        {
            int m = minVal - 1;
            foreach (DataRow r in rows) m = Math.Max(m, JetDb.GetInt(r, col));
            return m + 1;
        }

        private static void ApplySalesItemEdits(DbProbe probe, LoadedVoucher v, VoucherEditSpec spec, List<string> errors)
        {
            DataTable ptlTable = v.T("PTL");
            if (ptlTable == null) return;

            if (spec.ReplaceItemLines && spec.ItemLines.Count > 0)
            {
                DataRow template = v.Rows("PTL").Count > 0 ? v.Rows("PTL")[0] : null;
                object[] tmplArr = template != null ? (object[])template.ItemArray.Clone() : null;
                ptlTable.Rows.Clear();
                int ln = 1;
                foreach (ItemLineSpec ls in spec.ItemLines)
                {
                    if (string.Equals(ls.Action, "delete", StringComparison.OrdinalIgnoreCase)) continue;
                    DataRow nr = ptlTable.NewRow();
                    if (tmplArr != null) nr.ItemArray = (object[])tmplArr.Clone();
                    JetDb.SetVal(nr, "Line_no", ls.LineNo.HasValue ? ls.LineNo.Value : ln);
                    ApplySingleItemLineSpec(probe, nr, ls, "Item line " + ln, errors);
                    ptlTable.Rows.Add(nr);
                    ln++;
                }
            }
            else
            {
                foreach (ItemLineSpec ls in spec.ItemLines)
                {
                    string act = (ls.Action ?? "set").Trim().ToLowerInvariant();
                    if (act == "delete" || act == "del" || act == "remove")
                    {
                        DataRow target = FindPtlLine(v, ls.LineNo);
                        if (target == null) errors.Add("Item line to delete (Line_no=" + ls.LineNo + ") not found.");
                        else ptlTable.Rows.Remove(target);
                    }
                    else if (act == "add" || act == "new")
                    {
                        DataRow nr = CloneTemplateRow(ptlTable);
                        int ln = ls.LineNo.HasValue ? ls.LineNo.Value : NextLineNum(v.Rows("PTL"), "Line_no", 1);
                        JetDb.SetVal(nr, "Line_no", ln);
                        JetDb.SetVal(nr, "Tran_Qty", 1.0);
                        JetDb.SetVal(nr, "Tran_Nos", 1.0);
                        JetDb.SetVal(nr, "Unit_Rate", 0.0);
                        JetDb.SetVal(nr, "Entry_Rate", 0.0);
                        JetDb.SetVal(nr, "Value", 0.0);
                        JetDb.SetVal(nr, "Net_Value", 0.0);
                        ApplySingleItemLineSpec(probe, nr, ls, "Added item line " + ln, errors);
                        ptlTable.Rows.Add(nr);
                    }
                    else
                    {
                        DataRow target = FindPtlLine(v, ls.LineNo);
                        if (target == null)
                        {
                            errors.Add("Item line " + (ls.LineNo.HasValue ? ls.LineNo.Value.ToString() : "1") + " not found in PTL.");
                        }
                        else
                        {
                            ApplySingleItemLineSpec(probe, target, ls, "Item line " + JetDb.GetInt(target, "Line_no"), errors);
                        }
                    }
                }
            }

            // Quick single-line overrides (apply to 1st PTL row)
            if (spec.Item != null || spec.Qty.HasValue || spec.Nos.HasValue || spec.Unit != null || spec.Rate.HasValue || spec.ItemNarration != null)
            {
                List<DataRow> ptl = v.Rows("PTL");
                if (ptl.Count == 0)
                {
                    errors.Add("Sales bill has no item lines (PTL) to apply quick item edits to.");
                }
                else
                {
                    ItemLineSpec quick = new ItemLineSpec();
                    quick.Item = spec.Item;
                    quick.Qty = spec.Qty;
                    quick.Nos = spec.Nos;
                    quick.Unit = spec.Unit;
                    quick.Rate = spec.Rate;
                    quick.Narration = spec.ItemNarration;
                    ApplySingleItemLineSpec(probe, ptl[0], quick, "Item line 1", errors);
                }
            }
        }

        private static DataRow FindPtlLine(LoadedVoucher v, int? lineNo)
        {
            List<DataRow> rows = v.Rows("PTL");
            if (rows.Count == 0) return null;
            if (!lineNo.HasValue) return rows[0];
            foreach (DataRow r in rows)
                if (JetDb.GetInt(r, "Line_no") == lineNo.Value) return r;
            if (lineNo.Value >= 1 && lineNo.Value <= rows.Count) return rows[lineNo.Value - 1];
            return null;
        }

        private static void ApplySingleItemLineSpec(DbProbe probe, DataRow r, ItemLineSpec ls, string ctx, List<string> errors)
        {
            if (!string.IsNullOrEmpty(ls.Item))
            {
                int oldCode = JetDb.GetInt(r, "ItemCode");
                ItemRow it = ResolveItem(probe, ls.Item, ctx, errors);
                if (it != null)
                {
                    JetDb.SetVal(r, "ItemCode", it.ItemCode);
                    if (it.ItemCode != oldCode && string.IsNullOrEmpty(ls.Unit) && !string.IsNullOrEmpty(it.UnitCode))
                        JetDb.SetVal(r, "Unit", it.UnitCode);
                }
                else
                {
                    int rawCode;
                    if (int.TryParse(ls.Item.Trim(), out rawCode)) JetDb.SetVal(r, "ItemCode", rawCode);
                }
            }
            if (ls.Qty.HasValue) JetDb.SetVal(r, "Tran_Qty", ls.Qty.Value);
            if (ls.Nos.HasValue) JetDb.SetVal(r, "Tran_Nos", ls.Nos.Value);
            if (ls.Unit != null) JetDb.SetVal(r, "Unit", ls.Unit.Trim());
            if (ls.Rate.HasValue) JetDb.SetVal(r, "Unit_Rate", ls.Rate.Value);
            if (ls.Narration != null) JetDb.SetVal(r, "Naration", ls.Narration);
            if (!string.IsNullOrEmpty(ls.AccCode))
            {
                AccountRow acc = ResolveAccount(probe, ls.AccCode, true, ctx + " income account", errors);
                JetDb.SetVal(r, "Acc_COde", acc != null ? acc.Code : ls.AccCode.Trim());
            }
        }

        private static Dictionary<int, double> ApplySalesAddonEdits(DbProbe probe, LoadedVoucher v, VoucherEditSpec spec, List<string> errors)
        {
            Dictionary<int, double> explicitAmounts = new Dictionary<int, double>();
            DataTable pthlTable = v.T("PTHL");
            if (pthlTable == null) return explicitAmounts;

            if (spec.ReplaceAddonLines && spec.AddonLines.Count > 0)
            {
                DataRow template = v.Rows("PTHL").Count > 0 ? v.Rows("PTHL")[0] : null;
                object[] tmplArr = template != null ? (object[])template.ItemArray.Clone() : null;
                pthlTable.Rows.Clear();
                int ln = 101;
                foreach (AddonLineSpec a in spec.AddonLines)
                {
                    if (string.Equals(a.Action, "delete", StringComparison.OrdinalIgnoreCase)) continue;
                    DataRow nr = pthlTable.NewRow();
                    if (tmplArr != null) nr.ItemArray = (object[])tmplArr.Clone();
                    int code = a.AddOns.HasValue ? a.AddOns.Value : 4;
                    FillNewAddonRow(probe, v, nr, a.Line.HasValue ? a.Line.Value : ln, code);
                    ApplySingleAddonSpec(probe, nr, a, explicitAmounts, errors);
                    pthlTable.Rows.Add(nr);
                    ln++;
                }
            }
            else
            {
                foreach (AddonLineSpec a in spec.AddonLines)
                {
                    string act = (a.Action ?? "set").Trim().ToLowerInvariant();
                    if (act == "delete" || act == "del" || act == "remove")
                    {
                        DataRow target = FindAddonRow(v, a.AddOns, a.Line);
                        if (target != null) pthlTable.Rows.Remove(target);
                    }
                    else if (act == "add" || act == "new")
                    {
                        int code = a.AddOns.HasValue ? a.AddOns.Value : 0;
                        if (code <= 0) { errors.Add("Add-on line 'add' requires addOns code (e.g. 4=SGST, 5=CGST, 6=IGST)."); continue; }
                        DataRow nr = CloneTemplateRow(pthlTable);
                        int ln = a.Line.HasValue ? a.Line.Value : NextLineNum(v.Rows("PTHL"), "Line", 101);
                        FillNewAddonRow(probe, v, nr, ln, code);
                        ApplySingleAddonSpec(probe, nr, a, explicitAmounts, errors);
                        pthlTable.Rows.Add(nr);
                    }
                    else
                    {
                        DataRow target = FindAddonRow(v, a.AddOns, a.Line);
                        if (target == null && a.AddOns.HasValue)
                        {
                            target = CloneTemplateRow(pthlTable);
                            int ln = a.Line.HasValue ? a.Line.Value : NextLineNum(v.Rows("PTHL"), "Line", 101);
                            FillNewAddonRow(probe, v, target, ln, a.AddOns.Value);
                            pthlTable.Rows.Add(target);
                        }
                        if (target == null)
                            errors.Add("Add-on line not found in PTHL (specify addOns code or line number).");
                        else
                            ApplySingleAddonSpec(probe, target, a, explicitAmounts, errors);
                    }
                }
            }

            // Quick SGST / CGST / IGST overrides
            ApplyQuickTaxOverride(probe, v, 4, spec.SgstPer, spec.SgstAmt, explicitAmounts);
            ApplyQuickTaxOverride(probe, v, 5, spec.CgstPer, spec.CgstAmt, explicitAmounts);
            ApplyQuickTaxOverride(probe, v, 6, spec.IgstPer, spec.IgstAmt, explicitAmounts);

            return explicitAmounts;
        }

        private static void ApplyQuickTaxOverride(DbProbe probe, LoadedVoucher v, int addonCode, double? per, double? amt, Dictionary<int, double> explicitAmounts)
        {
            if (!per.HasValue && !amt.HasValue) return;
            DataTable pthlTable = v.T("PTHL");
            DataRow row = FindAddonRow(v, addonCode, null);
            if (row == null)
            {
                if ((per.HasValue && Math.Abs(per.Value) < 0.0001) && (!amt.HasValue || Math.Abs(amt.Value) < 0.004))
                    return;
                row = CloneTemplateRow(pthlTable);
                int ln = NextLineNum(v.Rows("PTHL"), "Line", 101);
                FillNewAddonRow(probe, v, row, ln, addonCode);
                pthlTable.Rows.Add(row);
            }
            if (per.HasValue)
            {
                JetDb.SetVal(row, "Per", per.Value);
                if (Math.Abs(per.Value) < 0.0001 && (!amt.HasValue || Math.Abs(amt.Value) < 0.004))
                {
                    pthlTable.Rows.Remove(row);
                    return;
                }
            }
            if (amt.HasValue)
            {
                // Like SaleBillRepost.bas: if amt is 0 while per > 0, let RecalcSales compute it from % * Goods
                double curPer = JetDb.GetDbl(row, "Per");
                if (Math.Abs(amt.Value) > 0.004 || Math.Abs(curPer) < 0.0001)
                {
                    JetDb.SetVal(row, "Amount", Helpers.Round2(amt.Value));
                    explicitAmounts[addonCode] = Helpers.Round2(amt.Value);
                }
            }
        }

        private static DataRow FindAddonRow(LoadedVoucher v, int? addonCode, int? line)
        {
            foreach (DataRow r in v.Rows("PTHL"))
            {
                if (addonCode.HasValue && JetDb.GetInt(r, "AddOns") == addonCode.Value) return r;
                if (line.HasValue && JetDb.GetInt(r, "Line") == line.Value) return r;
            }
            return null;
        }

        private static void FillNewAddonRow(DbProbe probe, LoadedVoucher v, DataRow r, int line, int code)
        {
            AddOnRow def;
            probe.AddOns.TryGetValue(code, out def);
            JetDb.SetVal(r, "Line", line);
            JetDb.SetVal(r, "AddOns", code);
            JetDb.SetVal(r, "Type", (def != null && def.DefSign == "-") ? "-" : "+");
            JetDb.SetVal(r, "Per", 0.0);
            JetDb.SetVal(r, "Amount", 0.0);
            JetDb.SetVal(r, "Ass_val", v.Pth == null ? 0.0 : JetDb.GetDbl(v.Pth, "GoodAmt"));
            string acc = def != null ? def.AccCodeSL : "";
            if (code == 99)
            {
                SeriesRow s = probe.FindSeries(v.SrcBT, v.NewTranType);
                acc = s == null ? "" : s.AccCode;
            }
            JetDb.SetVal(r, "Acc_Code", acc);
            if (JetDb.RowHas(r, "Exc_Inc")) JetDb.SetVal(r, "Exc_Inc", "E");
        }

        private static void ApplySingleAddonSpec(DbProbe probe, DataRow r, AddonLineSpec a, Dictionary<int, double> explicitAmounts, List<string> errors)
        {
            if (a.AddOns.HasValue) JetDb.SetVal(r, "AddOns", a.AddOns.Value);
            if (!string.IsNullOrEmpty(a.Type)) JetDb.SetVal(r, "Type", a.Type.Trim() == "-" ? "-" : "+");
            if (a.Per.HasValue) JetDb.SetVal(r, "Per", a.Per.Value);
            if (a.AssVal.HasValue) JetDb.SetVal(r, "Ass_val", a.AssVal.Value);
            if (a.Amount.HasValue)
            {
                double curPer = JetDb.GetDbl(r, "Per");
                if (Math.Abs(a.Amount.Value) > 0.004 || Math.Abs(curPer) < 0.0001)
                {
                    JetDb.SetVal(r, "Amount", Helpers.Round2(a.Amount.Value));
                    explicitAmounts[JetDb.GetInt(r, "AddOns")] = Helpers.Round2(a.Amount.Value);
                }
            }
            if (!string.IsNullOrEmpty(a.AccCode))
            {
                AccountRow acc = ResolveAccount(probe, a.AccCode, true, "AddOn account", errors);
                JetDb.SetVal(r, "Acc_Code", acc != null ? acc.Code : a.AccCode.Trim());
            }
        }

        private static void ApplyEffEdits(DbProbe probe, LoadedVoucher v, VoucherEditSpec spec, List<string> errors)
        {
            DataTable effTable = v.T("EFF");
            if (effTable == null) return;

            // Quick Dr/Cr account & amount overrides for non-Sales vouchers
            if (v.Kind != VoucherKind.Sales)
            {
                if (!string.IsNullOrEmpty(spec.DrAccount))
                {
                    AccountRow drAcc = ResolveAccount(probe, spec.DrAccount, true, "Debit account", errors);
                    string code = drAcc != null ? drAcc.Code : spec.DrAccount.Trim();
                    SubstituteSideAccount(v, "D", code);
                }
                if (!string.IsNullOrEmpty(spec.CrAccount))
                {
                    AccountRow crAcc = ResolveAccount(probe, spec.CrAccount, true, "Credit account", errors);
                    string code = crAcc != null ? crAcc.Code : spec.CrAccount.Trim();
                    SubstituteSideAccount(v, "C", code);
                }
                if (spec.Amount.HasValue)
                {
                    double targetAmt = Helpers.Round2(spec.Amount.Value);
                    List<DataRow> eff = v.Rows("EFF");
                    double drSum = v.EffTotal("D");
                    double crSum = v.EffTotal("C");
                    foreach (DataRow r in eff)
                    {
                        string dc = JetDb.GetStr(r, "DbCr").Trim().ToUpperInvariant();
                        double cur = JetDb.GetDbl(r, "Tran_amt");
                        double sideSum = (dc == "C") ? crSum : drSum;
                        if (sideSum > 0.004 && !Helpers.EqAmt(cur, sideSum))
                            JetDb.SetVal(r, "Tran_amt", Helpers.Round2(targetAmt * (cur / sideSum)));
                        else
                            JetDb.SetVal(r, "Tran_amt", targetAmt);
                    }
                }
            }

            if (spec.ReplaceEffLines && spec.EffLines.Count > 0)
            {
                DataRow template = v.Rows("EFF").Count > 0 ? v.Rows("EFF")[0] : null;
                object[] tmplArr = template != null ? (object[])template.ItemArray.Clone() : null;
                effTable.Rows.Clear();
                int ln = 1;
                foreach (EffLineSpec es in spec.EffLines)
                {
                    if (string.Equals(es.Action, "delete", StringComparison.OrdinalIgnoreCase)) continue;
                    DataRow nr = effTable.NewRow();
                    if (tmplArr != null) nr.ItemArray = (object[])tmplArr.Clone();
                    JetDb.SetVal(nr, "Line_no", es.LineNo.HasValue ? es.LineNo.Value : ln);
                    JetDb.SetVal(nr, "Other_amt", 0.0);
                    JetDb.SetVal(nr, "Other_rate", 0.0);
                    ApplySingleEffSpec(probe, v, nr, es, "EFF line " + ln, errors);
                    effTable.Rows.Add(nr);
                    ln++;
                }
            }
            else
            {
                foreach (EffLineSpec es in spec.EffLines)
                {
                    string act = (es.Action ?? "set").Trim().ToLowerInvariant();
                    if (act == "delete" || act == "del" || act == "remove")
                    {
                        DataRow target = FindEffLine(v, es.LineNo);
                        if (target == null) errors.Add("EFF line to delete (Line_no=" + es.LineNo + ") not found.");
                        else effTable.Rows.Remove(target);
                    }
                    else if (act == "add" || act == "new")
                    {
                        DataRow nr = CloneTemplateRow(effTable);
                        int ln = es.LineNo.HasValue ? es.LineNo.Value : NextLineNum(v.Rows("EFF"), "Line_no", 1);
                        JetDb.SetVal(nr, "Line_no", ln);
                        JetDb.SetVal(nr, "Tran_amt", 0.0);
                        JetDb.SetVal(nr, "DbCr", "D");
                        JetDb.SetVal(nr, "Other_amt", 0.0);
                        JetDb.SetVal(nr, "Other_rate", 0.0);
                        if (v.Inf != null) JetDb.SetVal(nr, "Naration", JetDb.GetStr(v.Inf, "Naration"));
                        ApplySingleEffSpec(probe, v, nr, es, "Added EFF line " + ln, errors);
                        effTable.Rows.Add(nr);
                    }
                    else
                    {
                        DataRow target = FindEffLine(v, es.LineNo);
                        if (target == null)
                            errors.Add("EFF line " + (es.LineNo.HasValue ? es.LineNo.Value.ToString() : "1") + " not found.");
                        else
                            ApplySingleEffSpec(probe, v, target, es, "EFF line " + JetDb.GetInt(target, "Line_no"), errors);
                    }
                }
            }
        }

        private static void SubstituteSideAccount(LoadedVoucher v, string side, string newCode)
        {
            List<DataRow> eff = v.Rows("EFF");
            string oldCode = "";
            foreach (DataRow r in eff)
            {
                if (string.Equals(JetDb.GetStr(r, "DbCr").Trim(), side, StringComparison.OrdinalIgnoreCase))
                {
                    oldCode = JetDb.GetStr(r, "Acc_Code").Trim();
                    JetDb.SetVal(r, "Acc_Code", newCode);
                    break;
                }
            }
            if (oldCode.Length > 0 && !string.Equals(oldCode, newCode, StringComparison.OrdinalIgnoreCase))
            {
                foreach (DataRow r in eff)
                    if (string.Equals(JetDb.GetStr(r, "Acc_Code2").Trim(), oldCode, StringComparison.OrdinalIgnoreCase))
                        JetDb.SetVal(r, "Acc_Code2", newCode);
            }
        }

        private static DataRow FindEffLine(LoadedVoucher v, int? lineNo)
        {
            List<DataRow> rows = v.Rows("EFF");
            if (rows.Count == 0) return null;
            if (!lineNo.HasValue) return rows[0];
            foreach (DataRow r in rows)
                if (JetDb.GetInt(r, "Line_no") == lineNo.Value) return r;
            if (lineNo.Value >= 1 && lineNo.Value <= rows.Count) return rows[lineNo.Value - 1];
            return null;
        }

        private static void ApplySingleEffSpec(DbProbe probe, LoadedVoucher v, DataRow r, EffLineSpec es, string ctx, List<string> errors)
        {
            if (!string.IsNullOrEmpty(es.Account))
            {
                string oldAcc = JetDb.GetStr(r, "Acc_Code").Trim();
                AccountRow acc = ResolveAccount(probe, es.Account, true, ctx, errors);
                string newAcc = acc != null ? acc.Code : es.Account.Trim();
                JetDb.SetVal(r, "Acc_Code", newAcc);
                if (oldAcc.Length > 0 && !string.Equals(oldAcc, newAcc, StringComparison.OrdinalIgnoreCase))
                {
                    foreach (DataRow o in v.Rows("EFF"))
                        if (string.Equals(JetDb.GetStr(o, "Acc_Code2").Trim(), oldAcc, StringComparison.OrdinalIgnoreCase))
                            JetDb.SetVal(o, "Acc_Code2", newAcc);
                }
            }
            if (!string.IsNullOrEmpty(es.DbCr))
                JetDb.SetVal(r, "DbCr", es.DbCr.Trim().ToUpperInvariant());
            if (es.Amount.HasValue)
                JetDb.SetVal(r, "Tran_amt", Helpers.Round2(es.Amount.Value));
            if (es.Narration != null)
                JetDb.SetVal(r, "Naration", es.Narration);
            if (!string.IsNullOrEmpty(es.OtherAccount))
            {
                AccountRow acc2 = ResolveAccount(probe, es.OtherAccount, true, ctx + " other account", errors);
                JetDb.SetVal(r, "Acc_Code2", acc2 != null ? acc2.Code : es.OtherAccount.Trim());
            }
        }

        private static void ApplyChequeEdits(DbProbe probe, LoadedVoucher v, VoucherEditSpec spec, List<string> errors)
        {
            bool hasQuick = spec.ChqNo != null || spec.ChqDate != null || spec.BankName != null;
            if (!hasQuick && spec.ChequeLines.Count == 0) return;

            DataTable el = v.T("EFFLine");
            if (el == null)
            {
                errors.Add("Source voucher has no EFFLine table loaded for cheque/bank details.");
                return;
            }

            if (hasQuick)
            {
                List<DataRow> rows = v.Rows("EFFLine");
                DataRow target;
                if (rows.Count > 0) target = rows[0];
                else
                {
                    target = el.NewRow();
                    if (v.Rows("EFF").Count > 0)
                        JetDb.SetVal(target, "Line_no", JetDb.GetInt(v.Rows("EFF")[0], "Line_no"));
                    el.Rows.Add(target);
                }
                if (spec.BankName != null) JetDb.SetVal(target, "BankName", spec.BankName.Trim());
                if (spec.ChqNo != null) JetDb.SetVal(target, "ChqNo", spec.ChqNo.Trim());
                if (spec.ChqDate != null)
                {
                    DateTime dt;
                    if (!Helpers.TryParseDate(spec.ChqDate, out dt))
                        errors.Add("Invalid chqDate '" + spec.ChqDate + "'.");
                    else
                        JetDb.SetVal(target, "ChqDate", dt);
                }
            }

            foreach (ChequeLineSpec cs in spec.ChequeLines)
            {
                string act = (cs.Action ?? "set").Trim().ToLowerInvariant();
                List<DataRow> rows = v.Rows("EFFLine");
                if (act == "delete" || act == "del" || act == "remove")
                {
                    int idx = cs.RowIndex.HasValue ? cs.RowIndex.Value : 0;
                    if (idx >= 0 && idx < rows.Count) el.Rows.Remove(rows[idx]);
                }
                else if (act == "add" || act == "new")
                {
                    DataRow nr = CloneTemplateRow(el);
                    if (cs.LineNo.HasValue) JetDb.SetVal(nr, "Line_no", cs.LineNo.Value);
                    ApplySingleChequeSpec(probe, nr, cs, errors);
                    el.Rows.Add(nr);
                }
                else
                {
                    int idx = cs.RowIndex.HasValue ? cs.RowIndex.Value : 0;
                    if (idx >= 0 && idx < rows.Count)
                        ApplySingleChequeSpec(probe, rows[idx], cs, errors);
                    else
                        errors.Add("Cheque line index " + idx + " not found in EFFLine.");
                }
            }
        }

        private static void ApplySingleChequeSpec(DbProbe probe, DataRow r, ChequeLineSpec cs, List<string> errors)
        {
            if (cs.LineNo.HasValue) JetDb.SetVal(r, "Line_no", cs.LineNo.Value);
            if (!string.IsNullOrEmpty(cs.Account))
            {
                AccountRow a = ResolveAccount(probe, cs.Account, true, "Cheque line account", errors);
                JetDb.SetVal(r, "Acc_Code", a != null ? a.Code : cs.Account.Trim());
            }
            if (!string.IsNullOrEmpty(cs.OtherAccount))
            {
                AccountRow a2 = ResolveAccount(probe, cs.OtherAccount, true, "Cheque line other account", errors);
                JetDb.SetVal(r, "Acc_Code2", a2 != null ? a2.Code : cs.OtherAccount.Trim());
            }
            if (cs.BankName != null) JetDb.SetVal(r, "BankName", cs.BankName.Trim());
            if (cs.ChqNo != null) JetDb.SetVal(r, "ChqNo", cs.ChqNo.Trim());
            if (cs.ChqDate != null)
            {
                DateTime dt;
                if (!Helpers.TryParseDate(cs.ChqDate, out dt))
                    errors.Add("Invalid chqDate '" + cs.ChqDate + "'.");
                else
                    JetDb.SetVal(r, "ChqDate", dt);
            }
        }

        private static void ApplyPayHistEdits(LoadedVoucher v, VoucherEditSpec spec, List<string> errors)
        {
            DataTable ph = v.T("PayHist");
            if (spec.ClearAdjustments)
            {
                if (ph != null) ph.Rows.Clear();
                if (v.Inf != null) JetDb.SetVal(v.Inf, "Adj_Amt", 0.0);
                return;
            }
            if (spec.PayHistLines.Count == 0) return;
            if (ph == null)
            {
                errors.Add("Source voucher has no PayHist bill adjustments to edit.");
                return;
            }
            foreach (PayHistLineSpec ps in spec.PayHistLines)
            {
                List<DataRow> rows = v.Rows("PayHist");
                DataRow target = null;
                if (!string.IsNullOrEmpty(ps.BillVchNo))
                {
                    foreach (DataRow r in rows)
                        if (string.Equals(JetDb.GetStr(r, "VchNo").Trim(), ps.BillVchNo.Trim(), StringComparison.OrdinalIgnoreCase))
                        { target = r; break; }
                }
                else
                {
                    int idx = ps.RowIndex.HasValue ? ps.RowIndex.Value : 0;
                    if (idx >= 0 && idx < rows.Count) target = rows[idx];
                }
                if (target == null)
                {
                    errors.Add("PayHist bill adjustment row not found.");
                    continue;
                }
                string act = (ps.Action ?? "set").Trim().ToLowerInvariant();
                if (act == "delete" || act == "del" || act == "remove")
                {
                    ph.Rows.Remove(target);
                }
                else
                {
                    if (ps.AdjAmt.HasValue) JetDb.SetVal(target, "Adj_Amt", Helpers.Round2(ps.AdjAmt.Value));
                    if (ps.DiscAmt.HasValue) JetDb.SetVal(target, "Disc_Amt", Helpers.Round2(ps.DiscAmt.Value));
                    if (JetDb.RowHas(target, "Total_Amt"))
                        JetDb.SetVal(target, "Total_Amt", Helpers.Round2(JetDb.GetDbl(target, "Adj_Amt") + JetDb.GetDbl(target, "Disc_Amt") + JetDb.GetDbl(target, "Excess_Amt")));
                }
            }
        }

        // ================================================================ EXPORT TO SPEC

        public static VoucherEditSpec ExportToSpec(CompanyDb db, LoadedVoucher v)
        {
            VoucherEditSpec s = new VoucherEditSpec();
            s.Select = true;
            s.SrcUId = v.SrcUId;
            s.SrcBT = v.SrcBT;
            s.SrcBook = v.SrcTranType;
            s.SrcVchNo = (v.SrcVchNo ?? "").Trim();
            s.SrcIDNo = v.SrcIDNo;
            s.NewDate = Helpers.FmtDateIso(v.NewDate);
            s.NewBook = v.NewTranType;
            s.BumpNarration = true;
            if (v.Inf != null)
            {
                s.Narration = JetDb.GetStr(v.Inf, "Naration");
                s.UserId = JetDb.GetStr(v.Inf, "UserId").Trim();
                if (v.Kind != VoucherKind.Sales)
                {
                    s.RefNo = JetDb.GetStr(v.Inf, "RefNo").Trim();
                    if (!JetDb.IsNull(v.Inf, "RefDate"))
                        s.RefDate = Helpers.FmtDateIso(JetDb.GetDate(v.Inf, "RefDate", v.NewDate));
                }
            }

            if (v.Kind == VoucherKind.Sales && v.Pth != null)
            {
                s.Party = JetDb.GetStr(v.Pth, "Acc_Code").Trim();
                s.TaxInvMode = v.TaxInvMode == 2 ? "manual" : (v.TaxInvMode == 1 ? "offset" : "same");
                if (v.TaxInvMode == 2) s.TaxInvNo = v.NewTaxInvNo;
                s.TaxCode = JetDb.GetStr(v.Pth, "TaxCode").Trim();
                s.AutoTaxCode = true;
                s.PayMode = JetDb.GetStr(v.Pth, "PayMode").Trim();
                s.CrDays = JetDb.GetInt(v.Pth, "cr_day");

                foreach (DataRow r in v.Rows("PTL"))
                {
                    ItemLineSpec it = new ItemLineSpec();
                    it.Action = "set";
                    it.LineNo = JetDb.GetInt(r, "Line_no");
                    it.Item = JetDb.GetInt(r, "ItemCode").ToString(CultureInfo.InvariantCulture);
                    it.Qty = JetDb.GetDbl(r, "Tran_Qty");
                    it.Nos = JetDb.GetDbl(r, "Tran_Nos");
                    it.Unit = JetDb.GetStr(r, "Unit").Trim();
                    it.Rate = JetDb.GetDbl(r, "Unit_Rate");
                    it.Narration = JetDb.GetStr(r, "Naration");
                    it.AccCode = JetDb.GetStr(r, "Acc_COde").Trim();
                    s.ItemLines.Add(it);
                }

                foreach (DataRow r in v.Rows("PTHL"))
                {
                    int code = JetDb.GetInt(r, "AddOns");
                    if (code == 99) continue; // round-off is auto-managed
                    AddonLineSpec a = new AddonLineSpec();
                    a.Action = "set";
                    a.Line = JetDb.GetInt(r, "Line");
                    a.AddOns = code;
                    a.Type = JetDb.GetStr(r, "Type").Trim();
                    a.Per = JetDb.GetDbl(r, "Per");
                    a.Amount = JetDb.GetDbl(r, "Amount");
                    a.AssVal = JetDb.GetDbl(r, "Ass_val");
                    a.AccCode = JetDb.GetStr(r, "Acc_Code").Trim();
                    s.AddonLines.Add(a);
                }
            }
            else
            {
                foreach (DataRow r in v.Rows("EFF"))
                {
                    EffLineSpec e = new EffLineSpec();
                    e.Action = "set";
                    e.LineNo = JetDb.GetInt(r, "Line_no");
                    e.Account = JetDb.GetStr(r, "Acc_Code").Trim();
                    e.DbCr = JetDb.GetStr(r, "DbCr").Trim().ToUpperInvariant();
                    e.Amount = JetDb.GetDbl(r, "Tran_amt");
                    e.Narration = JetDb.GetStr(r, "Naration");
                    e.OtherAccount = JetDb.GetStr(r, "Acc_Code2").Trim();
                    s.EffLines.Add(e);
                }
            }

            List<DataRow> chqs = v.Rows("EFFLine");
            for (int i = 0; i < chqs.Count; i++)
            {
                DataRow r = chqs[i];
                ChequeLineSpec c = new ChequeLineSpec();
                c.Action = "set";
                c.RowIndex = i;
                c.LineNo = JetDb.GetInt(r, "Line_no");
                c.Account = JetDb.GetStr(r, "Acc_Code").Trim();
                c.OtherAccount = JetDb.GetStr(r, "Acc_Code2").Trim();
                c.BankName = JetDb.GetStr(r, "BankName").Trim();
                c.ChqNo = JetDb.GetStr(r, "ChqNo").Trim();
                if (!JetDb.IsNull(r, "ChqDate"))
                    c.ChqDate = Helpers.FmtDateIso(JetDb.GetDate(r, "ChqDate", DateTime.MinValue));
                s.ChequeLines.Add(c);
            }

            List<DataRow> phs = v.Rows("PayHist");
            for (int i = 0; i < phs.Count; i++)
            {
                DataRow r = phs[i];
                PayHistLineSpec p = new PayHistLineSpec();
                p.Action = "set";
                p.RowIndex = i;
                p.BillVchNo = JetDb.GetStr(r, "VchNo").Trim();
                p.AdjAmt = JetDb.GetDbl(r, "Adj_Amt");
                p.DiscAmt = JetDb.GetDbl(r, "Disc_Amt");
                s.PayHistLines.Add(p);
            }

            return s;
        }

        // ================================================================ FORMATTING & EXECUTION

        private static string TrimTo(string s, int n)
        {
            s = s ?? "";
            return s.Length <= n ? s : s.Substring(0, n);
        }

        public static string FormatInspectReport(CompanyDb db, LoadedVoucher v, List<string> errors, List<string> warnings)
        {
            DbProbe st = db.State;
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("VOUCHER INSPECTION");
            sb.AppendLine("  Source      : " + v.SourceLabel);
            sb.AppendLine("  Kind        : " + VoucherKinds.Title(v.Kind));
            SeriesRow ser = st.FindSeries(v.SrcBT, v.SrcTranType);
            sb.AppendLine("  Book        : " + (ser == null ? (v.SrcBT + "-" + v.SrcTranType) : ser.Display));
            if (v.Inf != null)
            {
                string headAcc = JetDb.GetStr(v.Inf, "Acc_Code").Trim();
                sb.AppendLine("  Head Account: " + headAcc + "  " + st.AccountName(headAcc));
                sb.AppendLine("  Narration   : " + JetDb.GetStr(v.Inf, "Naration"));
                sb.AppendLine("  UserId      : " + JetDb.GetStr(v.Inf, "UserId").Trim());
            }
            if (v.Kind == VoucherKind.Sales && v.Pth != null)
            {
                sb.AppendLine("  TAX Inv No  : " + JetDb.GetStr(v.Pth, "TAX_INV_NO") + "   TaxCode: " + JetDb.GetStr(v.Pth, "TaxCode").Trim()
                    + "   PayMode: " + JetDb.GetStr(v.Pth, "PayMode").Trim() + "   CrDays: " + JetDb.GetInt(v.Pth, "cr_day"));
                sb.AppendLine(string.Format("  Bill Totals : Goods Rs. {0}  +  Add Rs. {1}  -  Less Rs. {2}  =  NET Rs. {3}",
                    Helpers.FmtAmt(JetDb.GetDbl(v.Pth, "GoodAmt")),
                    Helpers.FmtAmt(JetDb.GetDbl(v.Pth, "AddAmt")),
                    Helpers.FmtAmt(JetDb.GetDbl(v.Pth, "LessAmt")),
                    Helpers.FmtAmt(JetDb.GetDbl(v.Pth, "NetAmt"))));
                sb.AppendLine();
                sb.AppendLine("ITEM LINES (PTL):");
                foreach (DataRow r in v.Rows("PTL"))
                {
                    int ic = JetDb.GetInt(r, "ItemCode");
                    sb.AppendLine(string.Format("  Line {0}: Item {1} ({2})  Qty={3}  Nos={4}  Unit={5}  Rate={6}  Value={7}  IncomeAcc={8}  Nar='{9}'",
                        JetDb.GetInt(r, "Line_no"), ic, st.ItemName(ic),
                        JetDb.GetDbl(r, "Tran_Qty"), JetDb.GetDbl(r, "Tran_Nos"),
                        JetDb.GetStr(r, "Unit").Trim(), Helpers.FmtAmt(JetDb.GetDbl(r, "Unit_Rate")),
                        Helpers.FmtAmt(JetDb.GetDbl(r, "Net_Value")), JetDb.GetStr(r, "Acc_COde").Trim(),
                        JetDb.GetStr(r, "Naration")));
                }
                sb.AppendLine();
                sb.AppendLine("TAX / ADD-ON LINES (PTHL):");
                foreach (DataRow r in v.Rows("PTHL"))
                {
                    int code = JetDb.GetInt(r, "AddOns");
                    AddOnRow ao;
                    string nm = st.AddOns.TryGetValue(code, out ao) ? ao.Name : ("AddOn " + code);
                    string acc = JetDb.GetStr(r, "Acc_Code").Trim();
                    sb.AppendLine(string.Format("  Line {0}: AddOns={1} ({2})  Sign={3}  Per={4}%  Amount={5}  AssVal={6}  Acc={7} ({8})",
                        JetDb.GetInt(r, "Line"), code, nm, JetDb.GetStr(r, "Type").Trim(),
                        JetDb.GetDbl(r, "Per"), Helpers.FmtAmt(JetDb.GetDbl(r, "Amount")),
                        Helpers.FmtAmt(JetDb.GetDbl(r, "Ass_val")), acc, st.AccountName(acc)));
                }
            }
            sb.AppendLine();
            sb.AppendLine("LEDGER LINES (EFF):");
            foreach (DataRow r in v.Rows("EFF"))
            {
                string acc = JetDb.GetStr(r, "Acc_Code").Trim();
                string acc2 = JetDb.GetStr(r, "Acc_Code2").Trim();
                sb.AppendLine(string.Format("  Line {0,3}: {1} ({2})  {3}  Rs. {4,12}  Other={5}  Nar='{6}'",
                    JetDb.GetInt(r, "Line_no"), acc, TrimTo(st.AccountName(acc), 32),
                    JetDb.GetStr(r, "DbCr").Trim(), Helpers.FmtAmt(JetDb.GetDbl(r, "Tran_amt")),
                    acc2, JetDb.GetStr(r, "Naration")));
            }
            sb.AppendLine(string.Format("  Total Debit Rs. {0}   Total Credit Rs. {1}",
                Helpers.FmtAmt(v.EffTotal("D")), Helpers.FmtAmt(v.EffTotal("C"))));

            if (v.Rows("EFFLine").Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("CHEQUE / BANK DETAILS (EFFLine):");
                foreach (DataRow r in v.Rows("EFFLine"))
                {
                    sb.AppendLine(string.Format("  Line {0}: Bank='{1}'  ChqNo='{2}'  ChqDate={3}  Acc={4}  Acc2={5}",
                        JetDb.GetInt(r, "Line_no"), JetDb.GetStr(r, "BankName").Trim(),
                        JetDb.GetStr(r, "ChqNo").Trim(),
                        JetDb.IsNull(r, "ChqDate") ? "" : Helpers.FmtDate(JetDb.GetDate(r, "ChqDate", DateTime.MinValue)),
                        JetDb.GetStr(r, "Acc_Code").Trim(), JetDb.GetStr(r, "Acc_Code2").Trim()));
                }
            }
            if (v.Rows("PayHist").Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("BILL ADJUSTMENTS (PayHist):");
                foreach (DataRow r in v.Rows("PayHist"))
                {
                    sb.AppendLine(string.Format("  Bill {0}-{1} Vch {2} (ID {3}): Adj Rs. {4}  Disc Rs. {5}",
                        JetDb.RowHas(r, "BT2") ? JetDb.GetStr(r, "BT2").Trim() : "",
                        JetDb.GetStr(r, "Tran_Type").Trim(), JetDb.GetStr(r, "VchNo").Trim(),
                        JetDb.GetStr(r, "ID_NO").Trim(),
                        Helpers.FmtAmt(JetDb.GetDbl(r, "Adj_Amt")), Helpers.FmtAmt(JetDb.GetDbl(r, "Disc_Amt"))));
                }
            }
            AppendDiagnostics(sb, errors, warnings);
            return sb.ToString();
        }

        public static string FormatReviewReport(CompanyDb db, LoadedVoucher v, PostPlan plan, List<string> errors, List<string> warnings, bool backupEnabled)
        {
            DbProbe st = db.State;
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("NEW ENTRY TO BE POSTED");
            sb.AppendLine(string.Format("  {0}   Book: {1} ({2}-{3})",
                VoucherKinds.Title(v.Kind),
                plan == null || plan.Series == null ? "?" : plan.Series.Name,
                v.SrcBT, v.NewTranType));
            if (plan != null)
            {
                sb.AppendLine(string.Format("  Date: {0}    New Vch No: {1}    New ID_NO: {2}    New UId: {3}",
                    Helpers.FmtDate(v.NewDate), (plan.NewVchNo ?? "").Trim(), plan.NewIDNo, plan.NewUId));
                if (v.Kind == VoucherKind.Sales) sb.AppendLine("  TAX Invoice No: " + plan.NewTaxInvNo);
                sb.AppendLine("  (Numbers are re-checked inside the posting transaction - if another user posts first, the next free number is used.)");
            }
            sb.AppendLine();
            sb.AppendLine("SOURCE ENTRY (unchanged)");
            sb.AppendLine("  " + v.SourceLabel);
            if (plan != null)
            {
                sb.AppendLine();
                sb.AppendLine("ROWS THAT WILL BE INSERTED");
                foreach (KeyValuePair<string, int> kv in plan.InsertCounts)
                    sb.AppendLine("  " + kv.Key + " x " + kv.Value);
            }
            sb.AppendLine();
            sb.AppendLine("LEDGER LINES OF THE NEW ENTRY");
            sb.AppendLine(string.Format("  {0,-44} {1,3} {2,16}", "Account", "D/C", "Amount"));
            foreach (DataRow r in v.Rows("EFF"))
            {
                string acc = JetDb.GetStr(r, "Acc_Code").Trim();
                sb.AppendLine(string.Format("  {0,-44} {1,3} {2,16}",
                    TrimTo(st.AccountName(acc) + " (" + acc + ")", 44),
                    JetDb.GetStr(r, "DbCr").Trim(),
                    Helpers.FmtAmt(JetDb.GetDbl(r, "Tran_amt"))));
            }
            sb.AppendLine(string.Format("  Total Dr Rs. {0}   Total Cr Rs. {1}",
                Helpers.FmtAmt(v.EffTotal("D")), Helpers.FmtAmt(v.EffTotal("C"))));

            if (v.Kind == VoucherKind.Sales && v.Pth != null)
            {
                string partyCode = JetDb.GetStr(v.Pth, "Acc_Code").Trim();
                sb.AppendLine();
                sb.AppendLine(string.Format("BILL: Goods Rs. {0} + Add Rs. {1} - Less Rs. {2} = NET Rs. {3}   Party: {4} ({5})",
                    Helpers.FmtAmt(JetDb.GetDbl(v.Pth, "GoodAmt")),
                    Helpers.FmtAmt(JetDb.GetDbl(v.Pth, "AddAmt")),
                    Helpers.FmtAmt(JetDb.GetDbl(v.Pth, "LessAmt")),
                    Helpers.FmtAmt(JetDb.GetDbl(v.Pth, "NetAmt")),
                    st.AccountName(partyCode), partyCode));
                foreach (DataRow r in v.Rows("PTL"))
                {
                    int ic = JetDb.GetInt(r, "ItemCode");
                    string stockAfter = (plan != null && plan.ItemNewBal.ContainsKey(ic)) ? plan.ItemNewBal[ic].ToString(CultureInfo.InvariantCulture) : "?";
                    sb.AppendLine(string.Format("  Item: {0}  qty {1} @ {2} = Rs. {3}   (stock after posting: {4})",
                        st.ItemName(ic), JetDb.GetDbl(r, "Tran_Qty"),
                        Helpers.FmtAmt(JetDb.GetDbl(r, "Unit_Rate")),
                        Helpers.FmtAmt(JetDb.GetDbl(r, "Net_Value")), stockAfter));
                }
            }
            if (plan != null && v.Rows("PayHist").Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("BILL ADJUSTMENTS - the bills below will be marked adjusted by this NEW entry:");
                foreach (string s in plan.BillAdjustments) sb.AppendLine("  " + s);
            }
            if (plan != null)
            {
                sb.AppendLine();
                sb.AppendLine("ACCOUNT BALANCES THAT WILL MOVE (incl. group totals): " + plan.AccDeltas.Count + " accounts");
                foreach (KeyValuePair<string, double[]> kv in plan.AccDeltas)
                {
                    if (Helpers.EqAmt(kv.Value[0], 0) && Helpers.EqAmt(kv.Value[1], 0)) continue;
                    sb.AppendLine(string.Format("  {0,-44} Cr +{1}   Dr +{2}",
                        TrimTo(st.AccountName(kv.Key), 44),
                        Helpers.FmtAmt(kv.Value[0]), Helpers.FmtAmt(kv.Value[1])));
                }
                if (plan.Notes.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("NOTES:");
                    foreach (string n in plan.Notes) sb.AppendLine("  - " + n);
                }
            }
            AppendDiagnostics(sb, errors, warnings);
            sb.AppendLine();
            sb.AppendLine(backupEnabled
                ? "A dated backup copy of Accurate.Mdb will be made before anything is written."
                : "WARNING: backup is switched OFF for this posting.");
            return sb.ToString();
        }

        private static void AppendDiagnostics(StringBuilder sb, List<string> errors, List<string> warnings)
        {
            sb.AppendLine();
            if (errors != null && errors.Count > 0)
            {
                sb.AppendLine("ERRORS (" + errors.Count + ") - CANNOT POST YET:");
                foreach (string e in errors) sb.AppendLine("  ERROR: " + e);
            }
            else
            {
                sb.AppendLine("VALIDATION: OK (0 errors)");
            }
            if (warnings != null && warnings.Count > 0)
            {
                sb.AppendLine("WARNINGS (" + warnings.Count + "):");
                foreach (string w in warnings) sb.AppendLine("  WARNING: " + w);
            }
        }

        public static SingleVoucherRunResult ExecuteSingle(
            CompanyDb db,
            CloneEngine engine,
            VoucherEditSpec spec,
            bool doPost,
            bool makeBackup,
            bool failOnWarnings,
            bool ignoreAccNewRunning)
        {
            SingleVoucherRunResult res = new SingleVoucherRunResult();
            res.Command = doPost ? "post" : "review";

            db.Probe();
            LoadedVoucher v = ResolveSourceVoucher(db, engine, spec, res.Errors);
            if (v == null)
            {
                res.Ok = false;
                res.ExitCode = 3;
                StringBuilder errSb = new StringBuilder();
                errSb.AppendLine((doPost ? "POST" : "REVIEW") + " FAILED - SOURCE VOUCHER ERROR");
                foreach (string e in res.Errors) errSb.AppendLine("  ERROR: " + e);
                res.TextReport = errSb.ToString();
                return res;
            }
            res.Voucher = v;

            ApplyEdits(db, engine, v, spec, res.Errors, res.Warnings);

            // Check live DB counters, duplicate TAX_INV_NO, and BillHist adjustments
            PostPlan plan = null;
            if (db.State.FindSeries(v.SrcBT, v.NewTranType) != null)
            {
                try
                {
                    plan = engine.BuildReviewPlan(v);
                    res.Plan = plan;
                    foreach (string le in plan.LiveDbErrors)
                        if (!res.Errors.Contains(le)) res.Errors.Add(le);
                }
                catch (Exception ex)
                {
                    res.Errors.Add("Review plan check failed: " + ex.Message);
                }
            }

            if (!ignoreAccNewRunning && CloneEngine.IsAccNewRunning())
            {
                string msg = "AccNew.exe is running. Close AccNew completely before posting, otherwise AccNew can overwrite the voucher counters when it exits.";
                if (doPost) res.Errors.Add(msg);
                else res.Warnings.Add(msg);
            }
            if (File.Exists(CloneEngine.LockFilePath(db.DbPath)))
            {
                res.Warnings.Add("Database lock file (.ldb) is present next to Accurate.Mdb - make sure AccNew is closed before posting.");
            }

            if (failOnWarnings && res.Warnings.Count > 0)
            {
                foreach (string w in res.Warnings)
                    res.Errors.Add("Strict mode (--strict): warning treated as error: " + w);
            }

            if (res.Errors.Count > 0)
            {
                res.Ok = false;
                res.ExitCode = 3;
                res.TextReport = FormatReviewReport(db, v, plan, res.Errors, res.Warnings, makeBackup);
                return res;
            }

            if (!doPost)
            {
                res.Ok = true;
                res.ExitCode = 0;
                res.TextReport = FormatReviewReport(db, v, plan, res.Errors, res.Warnings, makeBackup);
                return res;
            }

            // Perform live post
            try
            {
                PostResult pr = engine.Post(v, makeBackup, ignoreAccNewRunning);
                res.PostResult = pr;
                res.Plan = pr.Plan;
                res.Ok = true;
                res.ExitCode = 0;
                StringBuilder sb = new StringBuilder();
                sb.Append(pr.Report);
                if (res.Warnings.Count > 0)
                {
                    sb.AppendLine("Warnings (" + res.Warnings.Count + "):");
                    foreach (string w in res.Warnings) sb.AppendLine("  WARNING: " + w);
                }
                if (!string.IsNullOrEmpty(pr.LogPath))
                    sb.AppendLine("Log file: " + pr.LogPath);
                res.TextReport = sb.ToString();
                return res;
            }
            catch (Exception ex)
            {
                res.Errors.Add(ex.Message);
                res.Ok = false;
                res.ExitCode = 3;
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("POSTING FAILED - nothing was written to the database.");
                foreach (string e in res.Errors) sb.AppendLine("  ERROR: " + e);
                foreach (string w in res.Warnings) sb.AppendLine("  WARNING: " + w);
                res.TextReport = sb.ToString();
                return res;
            }
        }

        // ================================================================ BATCH EXECUTION & TSV/CSV (EXCEL COMPATIBLE)

        public static BatchRunResult ExecuteBatch(
            CompanyDb db,
            CloneEngine engine,
            List<VoucherEditSpec> specs,
            bool doPost,
            bool makeBackup,
            bool failOnWarnings,
            bool continueOnError,
            bool ignoreAccNewRunning)
        {
            BatchRunResult bres = new BatchRunResult();
            bres.Command = doPost ? "batch-post" : "batch-review";
            bres.TotalRows = specs == null ? 0 : specs.Count;

            if (specs == null || specs.Count == 0)
            {
                bres.Errors.Add("No vouchers matched or were provided for batch operation.");
                bres.Ok = false;
                bres.ExitCode = 3;
                bres.TextReport = "BATCH FAILED: No vouchers matched or were provided.\r\n";
                return bres;
            }

            db.Probe();

            // Track provisional next numbers per series & BT across the batch during review
            Dictionary<string, long> nextSeriesVch = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, long> nextControlId = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            long nextUid = db.State.Uid;

            // Phase 1: Dry-run / validate every selected row in the batch
            List<int> selectedIndices = new List<int>();
            for (int i = 0; i < specs.Count; i++)
            {
                VoucherEditSpec sp = specs[i];
                if (!sp.Select)
                {
                    bres.SkippedRows++;
                    SingleVoucherRunResult skipped = new SingleVoucherRunResult();
                    skipped.Ok = true;
                    skipped.ExitCode = 0;
                    skipped.Command = "skip";
                    skipped.TextReport = "Row " + (i + 1) + ": SKIPPED (select=false)";
                    bres.Items.Add(skipped);
                    continue;
                }
                bres.SelectedRows++;
                selectedIndices.Add(i);

                SingleVoucherRunResult itemRes = ExecuteSingle(db, engine, sp, false, makeBackup, failOnWarnings, ignoreAccNewRunning);
                if (itemRes.Plan != null && itemRes.Voucher != null)
                {
                    // Adjust provisional review numbers sequentially across the batch
                    string sKey = itemRes.Voucher.SrcBT + "|" + itemRes.Voucher.NewTranType;
                    long vchNum = itemRes.Plan.NewVchNum;
                    if (nextSeriesVch.ContainsKey(sKey) && nextSeriesVch[sKey] >= vchNum)
                        vchNum = nextSeriesVch[sKey] + 1;
                    nextSeriesVch[sKey] = vchNum;

                    string cKey = itemRes.Voucher.SrcBT.ToUpperInvariant();
                    long idNum = itemRes.Plan.NewIdNum;
                    if (nextControlId.ContainsKey(cKey) && nextControlId[cKey] >= idNum)
                        idNum = nextControlId[cKey] + 1;
                    nextControlId[cKey] = idNum;

                    if (nextUid >= itemRes.Plan.NewUId) nextUid = nextUid + 1;
                    else nextUid = itemRes.Plan.NewUId;

                    itemRes.Plan.NewVchNum = vchNum;
                    itemRes.Plan.NewVchNo = Helpers.PadVch(vchNum);
                    itemRes.Plan.NewIdNum = idNum;
                    itemRes.Plan.NewIDNo = Helpers.PadId(idNum);
                    itemRes.Plan.NewUId = nextUid;
                    if (itemRes.Voucher.Kind == VoucherKind.Sales && itemRes.Voucher.Pth != null && itemRes.Voucher.TaxInvMode != 2)
                    {
                        string srcInv = JetDb.GetStr(itemRes.Voucher.Pth, "TAX_INV_NO");
                        itemRes.Plan.NewTaxInvNo = itemRes.Voucher.TaxInvMode == 1
                            ? Helpers.TaxInvKeepOffset(srcInv, Helpers.VchNoToLong(itemRes.Voucher.SrcVchNo), vchNum)
                            : Helpers.TaxInvSameAsVch(srcInv, vchNum);
                    }
                }
                if (!itemRes.Ok)
                {
                    bres.FailedRows++;
                    foreach (string e in itemRes.Errors)
                        bres.Errors.Add("Row " + (i + 1) + " (" + DescribeSpec(sp) + "): " + e);
                }
                else
                {
                    if (!doPost) bres.SucceededRows++;
                }
                foreach (string w in itemRes.Warnings)
                    bres.Warnings.Add("Row " + (i + 1) + " (" + DescribeSpec(sp) + "): " + w);

                bres.Items.Add(itemRes);
            }

            if (bres.SelectedRows == 0)
            {
                bres.Errors.Add("No voucher is selected in the batch (all rows have select=false).");
                bres.Ok = false;
                bres.ExitCode = 3;
                bres.TextReport = "BATCH FAILED: No voucher is selected in the batch.\r\n";
                return bres;
            }

            // If dry-run review or if pre-check failed and !continueOnError
            if (!doPost || (bres.FailedRows > 0 && !continueOnError))
            {
                bres.Ok = bres.FailedRows == 0;
                bres.ExitCode = bres.Ok ? 0 : 3;
                bres.TextReport = FormatBatchSummary(db, bres, doPost);
                return bres;
            }

            // Phase 2: Live posting of valid rows
            if (makeBackup)
            {
                try
                {
                    bres.BackupPath = engine.MakeBackup();
                }
                catch (Exception ex)
                {
                    bres.Errors.Add("Could not create pre-batch database backup: " + ex.Message);
                    bres.Ok = false;
                    bres.ExitCode = 1;
                    bres.TextReport = FormatBatchSummary(db, bres, doPost);
                    return bres;
                }
            }

            bres.SucceededRows = 0;
            bres.FailedRows = 0;
            bres.Errors.Clear();
            bres.Warnings.Clear();

            for (int i = 0; i < specs.Count; i++)
            {
                VoucherEditSpec sp = specs[i];
                if (!sp.Select) continue;

                SingleVoucherRunResult postItem = ExecuteSingle(db, engine, sp, true, false, failOnWarnings, ignoreAccNewRunning);
                if (!string.IsNullOrEmpty(bres.BackupPath) && postItem.PostResult != null)
                    postItem.PostResult.BackupPath = bres.BackupPath;

                bres.Items[i] = postItem;
                if (postItem.Ok)
                {
                    bres.SucceededRows++;
                }
                else
                {
                    bres.FailedRows++;
                    foreach (string e in postItem.Errors)
                        bres.Errors.Add("Row " + (i + 1) + " (" + DescribeSpec(sp) + "): " + e);
                    if (!continueOnError) break;
                }
                foreach (string w in postItem.Warnings)
                    bres.Warnings.Add("Row " + (i + 1) + " (" + DescribeSpec(sp) + "): " + w);
            }

            bres.Ok = bres.FailedRows == 0 && bres.SucceededRows > 0;
            bres.ExitCode = bres.Ok ? 0 : 3;
            bres.TextReport = FormatBatchSummary(db, bres, true);
            return bres;
        }

        private static string DescribeSpec(VoucherEditSpec sp)
        {
            if (sp.SrcUId > 0) return "UId=" + sp.SrcUId;
            return (sp.SrcBT ?? "") + " Vch=" + (sp.SrcVchNo ?? sp.SrcIDNo ?? "?");
        }

        private static string FormatBatchSummary(CompanyDb db, BatchRunResult bres, bool doPost)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(doPost ? "BATCH POST REPORT" : "BATCH REVIEW REPORT (DRY RUN)");
            sb.AppendLine(string.Format("  Total rows: {0}   Selected: {1}   {2}: {3}   Failed: {4}   Skipped: {5}",
                bres.TotalRows, bres.SelectedRows,
                doPost ? "Posted OK" : "Ready OK",
                bres.SucceededRows, bres.FailedRows, bres.SkippedRows));
            if (!string.IsNullOrEmpty(bres.BackupPath))
                sb.AppendLine("  Database backup: " + bres.BackupPath);
            sb.AppendLine();
            sb.AppendLine(string.Format("  {0,-4} {1,-8} {2,-7} {3,-8} {4,-8} {5,-10} {6,-28} {7,12}  {8}",
                "Row", "Status", "BT-Book", "SrcVch", "NewVch", "NewDate", "Party / Head Account", "Amount", "TaxInv / Notes"));
            sb.AppendLine("  " + new string('-', 108));

            for (int i = 0; i < bres.Items.Count; i++)
            {
                SingleVoucherRunResult it = bres.Items[i];
                if (it.Command == "skip")
                {
                    sb.AppendLine(string.Format("  {0,-4} {1,-8}", i + 1, "SKIPPED"));
                    continue;
                }
                LoadedVoucher v = it.Voucher;
                string status = it.Ok ? (it.PostResult != null ? "POSTED" : "READY") : "ERROR";
                string btBook = v == null ? "?" : (v.SrcBT + "-" + v.NewTranType);
                string srcVch = v == null ? "?" : (v.SrcVchNo ?? "").Trim();
                string newVch = it.Plan == null ? "?" : (it.Plan.NewVchNo ?? "").Trim();
                string newDate = v == null ? "?" : Helpers.FmtDate(v.NewDate);
                string party = "";
                double amt = 0;
                string extra = "";
                if (v != null)
                {
                    string accCode = (v.Kind == VoucherKind.Sales && v.Pth != null)
                        ? JetDb.GetStr(v.Pth, "Acc_Code").Trim()
                        : (v.Inf != null ? JetDb.GetStr(v.Inf, "Acc_Code").Trim() : "");
                    party = TrimTo(db.State.AccountName(accCode), 28);
                    amt = (v.Kind == VoucherKind.Sales && v.Pth != null)
                        ? JetDb.GetDbl(v.Pth, "NetAmt")
                        : v.EffTotal("D");
                    if (v.Kind == VoucherKind.Sales && it.Plan != null)
                        extra = "Inv:" + it.Plan.NewTaxInvNo;
                }
                if (!it.Ok && it.Errors.Count > 0)
                    extra = "ERR: " + it.Errors[0];

                sb.AppendLine(string.Format("  {0,-4} {1,-8} {2,-7} {3,-8} {4,-8} {5,-10} {6,-28} {7,12}  {8}",
                    i + 1, status, btBook, srcVch, newVch, newDate, party, Helpers.FmtAmt(amt), extra));
            }

            AppendDiagnostics(sb, bres.Errors, bres.Warnings);
            return sb.ToString();
        }

        // ================================================================ EXCEL / CALC TSV & CSV BATCH IMPORT/EXPORT

        private static readonly string[] BatchHeaders = new string[]
        {
            "Select", "Status", "SrcUId", "BT", "SrcBook", "SrcVchNo", "SrcIDNo", "SrcDate",
            "NewVchNo", "NewIDNo", "NewUId", "NewDate", "NewBook",
            "PartyCode", "PartyName", "TaxInvMode", "TaxInvNo",
            "ItemCode", "ItemName", "Qty", "Rate", "GoodsAmt",
            "SGST_Per", "SGST_Amt", "CGST_Per", "CGST_Amt", "IGST_Per", "IGST_Amt",
            "NetAmt", "Amount", "DrAccount", "CrAccount",
            "ItemNarration", "HeaderNarration", "Errors", "Warnings"
        };

        public static void WriteBatchTable(CompanyDb db, BatchRunResult bres, List<VoucherEditSpec> specs, string path)
        {
            char sep = TabularIo.DetectSeparator(path);
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(TabularIo.JoinRow(BatchHeaders, sep));

            for (int i = 0; i < bres.Items.Count; i++)
            {
                SingleVoucherRunResult it = bres.Items[i];
                VoucherEditSpec sp = (specs != null && i < specs.Count) ? specs[i] : new VoucherEditSpec();
                LoadedVoucher v = it.Voucher;
                PostPlan pl = it.Plan;

                string status = it.Command == "skip" ? "SKIPPED" : (it.Ok ? (it.PostResult != null ? "POSTED" : "READY") : "ERROR");
                string srcUId = v != null ? v.SrcUId.ToString(CultureInfo.InvariantCulture) : (sp.SrcUId > 0 ? sp.SrcUId.ToString(CultureInfo.InvariantCulture) : "");
                string bt = v != null ? v.SrcBT : (sp.SrcBT ?? "");
                string srcBook = v != null ? v.SrcTranType : (sp.SrcBook ?? "");
                string srcVch = v != null ? (v.SrcVchNo ?? "").Trim() : (sp.SrcVchNo ?? "");
                string srcId = v != null ? v.SrcIDNo : (sp.SrcIDNo ?? "");
                string srcDate = v != null ? Helpers.FmtDateIso(v.SrcDate) : "";

                string newVch = pl != null ? (pl.NewVchNo ?? "").Trim() : "";
                string newId = pl != null ? (pl.NewIDNo ?? "") : "";
                string newUId = pl != null && it.PostResult != null ? pl.NewUId.ToString(CultureInfo.InvariantCulture) : "";
                string newDate = v != null ? Helpers.FmtDateIso(v.NewDate) : (sp.NewDate ?? "");
                string newBook = v != null ? v.NewTranType : (sp.NewBook ?? "");

                string partyCode = "";
                string partyName = "";
                string taxInvMode = "same";
                string taxInvNo = "";
                string itemCode = "";
                string itemName = "";
                string qty = "";
                string rate = "";
                string goodsAmt = "";
                string sgstPer = "", sgstAmt = "", cgstPer = "", cgstAmt = "", igstPer = "", igstAmt = "";
                string netAmt = "";
                string amount = "";
                string drAcc = "", crAcc = "";
                string itemNar = "";
                string headNar = "";

                if (v != null)
                {
                    if (v.Inf != null) headNar = JetDb.GetStr(v.Inf, "Naration");
                    if (v.Kind == VoucherKind.Sales && v.Pth != null)
                    {
                        partyCode = JetDb.GetStr(v.Pth, "Acc_Code").Trim();
                        partyName = db.State.AccountName(partyCode);
                        taxInvMode = v.TaxInvMode == 2 ? "manual" : (v.TaxInvMode == 1 ? "offset" : "same");
                        taxInvNo = pl != null ? (pl.NewTaxInvNo ?? "") : (v.NewTaxInvNo ?? "");
                        goodsAmt = Helpers.FmtNumRaw(JetDb.GetDbl(v.Pth, "GoodAmt"));
                        netAmt = Helpers.FmtNumRaw(JetDb.GetDbl(v.Pth, "NetAmt"));
                        amount = netAmt;

                        List<DataRow> ptl = v.Rows("PTL");
                        if (ptl.Count > 0)
                        {
                            int ic = JetDb.GetInt(ptl[0], "ItemCode");
                            itemCode = ic.ToString(CultureInfo.InvariantCulture);
                            itemName = db.State.ItemName(ic);
                            qty = JetDb.GetDbl(ptl[0], "Tran_Qty").ToString("0.####", CultureInfo.InvariantCulture);
                            rate = Helpers.FmtNumRaw(JetDb.GetDbl(ptl[0], "Unit_Rate"));
                            itemNar = JetDb.GetStr(ptl[0], "Naration");
                        }
                        foreach (DataRow a in v.Rows("PTHL"))
                        {
                            int code = JetDb.GetInt(a, "AddOns");
                            if (code == 4) { sgstPer = JetDb.GetDbl(a, "Per").ToString("0.####", CultureInfo.InvariantCulture); sgstAmt = Helpers.FmtNumRaw(JetDb.GetDbl(a, "Amount")); }
                            else if (code == 5) { cgstPer = JetDb.GetDbl(a, "Per").ToString("0.####", CultureInfo.InvariantCulture); cgstAmt = Helpers.FmtNumRaw(JetDb.GetDbl(a, "Amount")); }
                            else if (code == 6) { igstPer = JetDb.GetDbl(a, "Per").ToString("0.####", CultureInfo.InvariantCulture); igstAmt = Helpers.FmtNumRaw(JetDb.GetDbl(a, "Amount")); }
                        }
                    }
                    else
                    {
                        partyCode = v.Inf != null ? JetDb.GetStr(v.Inf, "Acc_Code").Trim() : "";
                        partyName = db.State.AccountName(partyCode);
                        amount = Helpers.FmtNumRaw(v.EffTotal("D"));
                        netAmt = amount;
                        foreach (DataRow e in v.Rows("EFF"))
                        {
                            string dc = JetDb.GetStr(e, "DbCr").Trim().ToUpperInvariant();
                            if (dc == "D" && drAcc.Length == 0) drAcc = JetDb.GetStr(e, "Acc_Code").Trim();
                            if (dc == "C" && crAcc.Length == 0) crAcc = JetDb.GetStr(e, "Acc_Code").Trim();
                        }
                    }
                }

                string errs = string.Join("; ", it.Errors.ToArray());
                string warns = string.Join("; ", it.Warnings.ToArray());

                string[] row = new string[]
                {
                    sp.Select ? "TRUE" : "FALSE",
                    status,
                    srcUId, bt, srcBook, srcVch, srcId, srcDate,
                    newVch, newId, newUId, newDate, newBook,
                    partyCode, partyName, taxInvMode, taxInvNo,
                    itemCode, itemName, qty, rate, goodsAmt,
                    sgstPer, sgstAmt, cgstPer, cgstAmt, igstPer, igstAmt,
                    netAmt, amount, drAcc, crAcc,
                    itemNar, headNar, errs, warns
                };
                sb.AppendLine(TabularIo.JoinRow(row, sep));
            }

            string dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }

        public static List<VoucherEditSpec> ReadBatchFile(string path, List<string> errors)
        {
            List<VoucherEditSpec> list = new List<VoucherEditSpec>();
            if (!File.Exists(path))
            {
                errors.Add("Batch file not found: " + path);
                return list;
            }
            string text = File.ReadAllText(path, Encoding.UTF8);
            string trimmed = text.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
            if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("[") || trimmed.StartsWith("{"))
            {
                try
                {
                    object parsed = SimpleJson.Parse(text);
                    List<object> arr = parsed as List<object>;
                    if (arr != null)
                    {
                        foreach (object o in arr)
                        {
                            Dictionary<string, object> d = o as Dictionary<string, object>;
                            if (d != null) list.Add(VoucherEditSpec.FromDictionary(d));
                        }
                        return list;
                    }
                    Dictionary<string, object> root = parsed as Dictionary<string, object>;
                    if (root != null)
                    {
                        object itemsObj;
                        if (root.TryGetValue("vouchers", out itemsObj) || root.TryGetValue("items", out itemsObj))
                        {
                            List<object> sub = itemsObj as List<object>;
                            if (sub != null)
                            {
                                foreach (object o in sub)
                                {
                                    Dictionary<string, object> d = o as Dictionary<string, object>;
                                    if (d != null) list.Add(VoucherEditSpec.FromDictionary(d));
                                }
                                return list;
                            }
                        }
                        list.Add(VoucherEditSpec.FromDictionary(root));
                        return list;
                    }
                }
                catch (Exception ex)
                {
                    errors.Add("Failed to parse JSON batch file '" + path + "': " + ex.Message);
                    return list;
                }
            }

            char sep = TabularIo.DetectSeparator(path);
            List<string[]> rows = TabularIo.ReadRows(text, sep);
            if (rows.Count < 2)
            {
                errors.Add("Batch table '" + path + "' has no data rows.");
                return list;
            }

            string[] header = rows[0];
            Dictionary<string, int> colMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int c = 0; c < header.Length; c++)
            {
                string h = (header[c] ?? "").Trim();
                if (h.Length > 0 && !colMap.ContainsKey(h)) colMap[h] = c;
            }

            for (int r = 1; r < rows.Count; r++)
            {
                string[] cells = rows[r];
                string status = GetCell(cells, colMap, "Status").ToUpperInvariant();
                if (status == "POSTED")
                {
                    // Like SaleBillRepost.bas PerformEntry_Post: skip rows already marked POSTED
                    VoucherEditSpec already = new VoucherEditSpec();
                    already.Select = false;
                    list.Add(already);
                    continue;
                }

                VoucherEditSpec sp = new VoucherEditSpec();
                string selStr = GetCell(cells, colMap, "Select");
                if (selStr.Length > 0)
                {
                    string u = selStr.ToUpperInvariant();
                    sp.Select = !(u == "FALSE" || u == "0" || u == "NO" || u == "N" || u == "SKIP");
                }

                long uid;
                if (long.TryParse(GetCell(cells, colMap, "SrcUId", "UId", "UID"), out uid)) sp.SrcUId = uid;
                sp.SrcBT = GetCell(cells, colMap, "BT", "SrcBT");
                sp.SrcBook = GetCell(cells, colMap, "SrcBook", "TranType", "Book");
                sp.SrcVchNo = GetCell(cells, colMap, "SrcVchNo", "VchNo");
                sp.SrcIDNo = GetCell(cells, colMap, "SrcIDNo", "ID_NO");

                sp.NewDate = GetCell(cells, colMap, "NewDate", "Date");
                sp.NewBook = GetCell(cells, colMap, "NewBook");

                string partyCode = GetCell(cells, colMap, "PartyCode", "Party");
                string partyName = GetCell(cells, colMap, "PartyName");
                if (partyCode.Length > 0) sp.Party = partyCode;
                else if (partyName.Length > 0) sp.Party = partyName;

                string tim = GetCell(cells, colMap, "TaxInvMode");
                if (tim.Length > 0) sp.TaxInvMode = tim;
                string tin = GetCell(cells, colMap, "TaxInvNo", "TAX_INV_NO");
                string previewVch = GetCell(cells, colMap, "NewVchNo");
                if (tin.Length > 0)
                {
                    bool isAutoMode = string.Equals(tim, "same", StringComparison.OrdinalIgnoreCase)
                                   || string.Equals(tim, "offset", StringComparison.OrdinalIgnoreCase)
                                   || tim == "0" || tim == "1";
                    bool matchesPreview = previewVch.Length > 0 && string.Equals(Helpers.TrailingDigits(tin).TrimStart('0'), previewVch.Trim().TrimStart('0'), StringComparison.OrdinalIgnoreCase);
                    if (!isAutoMode || !matchesPreview)
                    {
                        sp.TaxInvNo = tin;
                        sp.TaxInvMode = "manual";
                    }
                }

                string itemCode = GetCell(cells, colMap, "ItemCode", "Item");
                if (itemCode.Length > 0) sp.Item = itemCode;

                sp.Qty = OptCellDbl(cells, colMap, "Qty", "Tran_Qty");
                sp.Rate = OptCellDbl(cells, colMap, "Rate", "Unit_Rate");
                double? rowGoods = OptCellDbl(cells, colMap, "GoodsAmt");
                sp.SgstPer = OptCellDbl(cells, colMap, "SGST_Per");
                sp.SgstAmt = FilterDerivedTaxAmt(rowGoods, sp.SgstPer, OptCellDbl(cells, colMap, "SGST_Amt"));
                sp.CgstPer = OptCellDbl(cells, colMap, "CGST_Per");
                sp.CgstAmt = FilterDerivedTaxAmt(rowGoods, sp.CgstPer, OptCellDbl(cells, colMap, "CGST_Amt"));
                sp.IgstPer = OptCellDbl(cells, colMap, "IGST_Per");
                sp.IgstAmt = FilterDerivedTaxAmt(rowGoods, sp.IgstPer, OptCellDbl(cells, colMap, "IGST_Amt"));

                if (!string.Equals(sp.SrcBT, "SL", StringComparison.OrdinalIgnoreCase))
                {
                    sp.Amount = OptCellDbl(cells, colMap, "Amount", "NetAmt");
                    string dr = GetCell(cells, colMap, "DrAccount", "DrAcc");
                    string cr = GetCell(cells, colMap, "CrAccount", "CrAcc");
                    if (dr.Length > 0) sp.DrAccount = dr;
                    if (cr.Length > 0) sp.CrAccount = cr;
                }

                string itemNar = GetCellRaw(cells, colMap, "ItemNarration");
                if (itemNar != null) sp.ItemNarration = itemNar;
                string headNar = GetCellRaw(cells, colMap, "HeaderNarration", "Narration");
                if (headNar != null) sp.Narration = headNar;

                list.Add(sp);
            }
            return list;
        }

        private static string GetCell(string[] cells, Dictionary<string, int> map, params string[] names)
        {
            string s = GetCellRaw(cells, map, names);
            return s == null ? "" : s.Trim();
        }

        private static string GetCellRaw(string[] cells, Dictionary<string, int> map, params string[] names)
        {
            foreach (string n in names)
            {
                int idx;
                if (map.TryGetValue(n, out idx) && idx >= 0 && idx < cells.Length)
                    return cells[idx];
            }
            return null;
        }

        private static double? OptCellDbl(string[] cells, Dictionary<string, int> map, params string[] names)
        {
            string s = GetCell(cells, map, names);
            if (s.Length == 0) return null;
            double d = Helpers.ParseDbl(s, double.NaN);
            return double.IsNaN(d) ? (double?)null : d;
        }

        private static double? FilterDerivedTaxAmt(double? rowGoods, double? per, double? amt)
        {
            if (!amt.HasValue) return null;
            if (rowGoods.HasValue && per.HasValue && per.Value > 0.0001)
            {
                double expected = Helpers.Round2(rowGoods.Value * per.Value / 100.0);
                if (Helpers.EqAmt(amt.Value, expected))
                    return null; // percentage-derived; let RecalcSales recompute from new Goods * Per
            }
            return amt;
        }
    }
}
