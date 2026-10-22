using System;
using System.Collections.Generic;
using System.Data;
using System.Data.OleDb;

namespace AccuVisionRepost.Core
{
    /// <summary>
    /// Guards copied from AccNew's own voucher code.
    ///
    /// The decompilation has 253 (function x blocking-message) guard points spread over the
    /// 110 routines that touch INF/EFF/PTH/PTL/BillHist/PayHist/ItemTrn. Every guard that can
    /// apply to posting a clone is implemented here; each one names the original message and
    /// the address it was found at, so a failure can be traced back to the software.
    ///
    /// Split in two on purpose:
    ///   CheckVoucher - needs only the voucher in memory (safe to run on every keystroke)
    ///   CheckLive    - needs the database (duplicates, masters); runs inside the post transaction
    /// </summary>
    public static class AccNewGuards
    {
        // ------------------------------------------------------------------ helpers

        private static void Err(List<string> l, string msg) { if (l != null) l.Add(msg); }
        private static void Warn(List<string> l, string msg) { if (l != null) l.Add(msg); }

        private static string Tag(string accNewMessage, string address)
        {
            return "  [AccNew: \"" + accNewMessage + "\" @ " + address + "]";
        }

        private static double Amt(DataRow r, string col)
        {
            return JetDb.RowHas(r, col) ? JetDb.GetDbl(r, col) : 0;
        }

        private static string Str(DataRow r, string col)
        {
            return JetDb.RowHas(r, col) ? JetDb.GetStr(r, col).Trim() : "";
        }

        private static bool Has(DataRow r, string col) { return JetDb.RowHas(r, col); }

        // ------------------------------------------------------------------ 1. in-memory guards

        /// <summary>
        /// Structural and arithmetic guards that AccNew enforces on a voucher. Runs on the
        /// in-memory row set, so the UI can show them while the user is still editing.
        /// </summary>
        public static void CheckVoucher(LoadedVoucher v, DbProbe st, List<string> errors, List<string> warnings)
        {
            if (v == null) return;
            DataRow inf = v.Inf;
            List<DataRow> eff = v.Rows("EFF");
            if (inf == null || eff.Count == 0) return;

            string bt = JetDb.GetStr(inf, "BT").Trim();
            string tt = JetDb.GetStr(inf, "Tran_Type").Trim();
            string vchNo = JetDb.GetStr(inf, "VchNo");
            string idNo = JetDb.GetStr(inf, "ID_NO");
            DateTime vchDate = JetDb.GetDate(inf, "VchDate", DateTime.MinValue);

            // ---- G01 identity block: every row of the voucher must agree with INF.
            // AccNew's integrity checker reports these as "INF,EFF,Not Match VCHNO," etc.
            CheckIdentity(v, bt, tt, vchNo, idNo, vchDate, errors);

            // ---- G02 INF.DbCr must be D or C  ("INF,,dbcr - Invalid character,")
            string infDc = Str(inf, "DbCr").ToUpperInvariant();
            if (infDc != "D" && infDc != "C")
                Err(errors, "Voucher header (INF) Dr/Cr is \"" + Str(inf, "DbCr") + "\" - AccNew only writes D or C." + Tag("INF,,dbcr - Invalid character,", "integrity checker"));

            // ---- G03 INF.Acc_Code must exist  ("INF,ACCMST,Invalid Acc_Code or Record missing from AccMst,")
            string infAcc = Str(inf, "Acc_Code");
            if (infAcc.Length == 0)
                Err(errors, "Voucher header has no head account (INF.Acc_Code)." + Tag("Account Not Found In Master", "0x4f5eb0"));
            else if (st != null && st.Account(infAcc) == null)
                Err(errors, "Head account " + infAcc + " does not exist in AccMst." + Tag("INF,ACCMST,Invalid Acc_Code or Record missing from AccMst,", "integrity checker"));

            // ---- G05 EFF line accounts exist, Acc_Code2 exists when used, no group/system account
            foreach (DataRow r in eff)
            {
                int line = JetDb.GetInt(r, "Line_no");
                string acc = Str(r, "Acc_Code");
                string acc2 = Str(r, "Acc_Code2");

                if (acc.Length > 0 && st != null)
                {
                    AccountRow a = st.Account(acc);
                    if (a == null)
                        Err(errors, "Ledger line " + line + ": account " + acc + " does not exist in AccMst." + Tag("EFF,ACCMST,Invalid Acc_Code or Record missing from AccMst,", "integrity checker"));
                    else
                    {
                        if (a.IsGroup)
                            Err(errors, "Ledger line " + line + ": " + a.Name.Trim() + " is a GROUP account." + Tag("Group Account Not Allowed In Transaction", "0x56cf68"));
                        if (a.SysFlag)
                            Err(errors, "Ledger line " + line + ": " + a.Name.Trim() + " is a system account (Sys_Flag = 'Y') and cannot take entries." + Tag("and sys_flag <> 'Y'", "0x4f9bb0"));
                    }
                }

                if (acc2.Length > 0 && st != null && st.Account(acc2) == null)
                    Err(errors, "Ledger line " + line + ": contra account " + acc2 + " does not exist in AccMst." + Tag("EFF,ACCMST,Invalid Acc_Code2 or Record missing from AccMst,", "integrity checker"));
            }

            // ---- G06 the two sides must agree (three messages AccNew raises for this)
            double dr = v.EffTotal("D"), cr = v.EffTotal("C");
            if (!Helpers.EqAmt(dr, cr))
                Err(errors, "Debit " + Helpers.FmtAmt(dr) + " and Credit " + Helpers.FmtAmt(cr) + " do not match (difference " + Helpers.FmtAmt(Math.Abs(dr - cr)) + ")." + Tag("Cr. And Db Not Match In EFF", "integrity checker") + Tag("DEBIT And CREDIT  Must be Equal For JV", "0x4fa7d0"));

            // ---- G07 Journal: multi-contra  ("Multi - Contra Entry  . . . Can't Save" 0x534df4)
            if (v.Kind == VoucherKind.Journal && st != null)
            {
                int bankCash = 0;
                foreach (DataRow r in eff)
                {
                    AccountRow a = st.Account(Str(r, "Acc_Code"));
                    if (a != null && (a.AccType == "03" || a.AccType == "04")) bankCash++;
                }
                if (bankCash > 2)
                    Err(errors, "Multi-contra journal (" + bankCash + " bank/cash lines) - AccNew refuses to save these." + Tag("Multi - Contra Entry  . . . Can't Save", "0x534df4"));
            }

            if (v.Kind == VoucherKind.Sales) CheckSales(v, st, inf, eff, errors, warnings);

            // ---- G20 receipts/payments: adjustment totals may not beat the voucher
            //      ("Adjust More then Receipt...Not Allowed")
            List<DataRow> ph = v.Rows("PayHist");
            if (ph.Count > 0)
            {
                double adj = 0, disc = 0, excess = 0;
                foreach (DataRow r in ph)
                {
                    adj += Amt(r, "Adj_Amt");
                    disc += Amt(r, "Disc_Amt");
                    excess += Amt(r, "Excess_Amt");
                }
                double total = Helpers.Round2(adj + disc);
                if (total > dr + 0.011)
                    Err(errors, "Bill adjustments " + Helpers.FmtAmt(total) + " exceed the voucher amount " + Helpers.FmtAmt(dr) + "." + Tag("Adjust More then Receipt...Not Allowed", "0x5354b0"));
                if (excess > 0.004)
                    Warn(warnings, "The source voucher carries an Excess amount of " + Helpers.FmtAmt(excess) + " - check that it is intended." + Tag("Confirmation For Excess Adjustment", "0x534758"));
            }
        }

        private static void CheckSales(LoadedVoucher v, DbProbe st, DataRow inf, List<DataRow> eff, List<string> errors, List<string> warnings)
        {
            DataRow pth = v.Pth;
            List<DataRow> ptl = v.Rows("PTL");
            if (pth == null) return;

            // ---- G08 bill totals must add up (existing rule, kept with its message)
            double good = Amt(pth, "GoodAmt"), add = Amt(pth, "AddAmt"), less = Amt(pth, "LessAmt"), net = Amt(pth, "NetAmt");
            double chk = Helpers.Round2(good + add - less - net);
            if (!Helpers.EqAmt(chk, 0))
                Err(errors, "Bill totals do not add up: GoodAmt + AddAmt - LessAmt != NetAmt (off by " + Helpers.FmtAmt(chk) + "). Press Recalculate." + Tag("Billhist Amont Not Match with EFF", "integrity checker"));

            // ---- G09 bill header and header account must agree with INF
            //      ("INF,PTH,Acc_Code not match," / "INF,PTH,Record Not Match,")
            string pthAcc = Str(pth, "Acc_Code");
            string infAcc = Str(inf, "Acc_Code");
            if (pthAcc.Length > 0 && infAcc.Length > 0 && !string.Equals(pthAcc, infAcc, StringComparison.OrdinalIgnoreCase))
                Err(errors, "Bill party " + pthAcc + " does not match the voucher head account " + infAcc + "." + Tag("INF,PTH,Acc_Code not match,", "integrity checker"));

            // ---- G10 party line of EFF must carry the bill total
            //      ("Billhist Amont Not Match with EFF")
            double partyDr = 0;
            foreach (DataRow r in eff)
                if (string.Equals(Str(r, "DbCr"), "D", StringComparison.OrdinalIgnoreCase) && string.Equals(Str(r, "Acc_Code"), pthAcc, StringComparison.OrdinalIgnoreCase))
                    partyDr = Helpers.Round2(partyDr + Amt(r, "Tran_amt"));
            if (partyDr > 0 && !Helpers.EqAmt(partyDr, net))
                Err(errors, "The party line in EFF is " + Helpers.FmtAmt(partyDr) + " but the bill total is " + Helpers.FmtAmt(net) + "." + Tag("Billhist Amont Not Match with EFF", "integrity checker"));

            // ---- G11 Tax code required for a taxable bill  ("Tax Code Required")
            bool hasTaxAddon = false;
            foreach (DataRow r in v.Rows("PTHL"))
            {
                int code = JetDb.GetInt(r, "AddOns");
                if (code >= 4 && code <= 6 && Math.Abs(Amt(r, "Amount")) > 0.004) hasTaxAddon = true;
            }
            string taxCode = Str(pth, "TaxCode");
            if (hasTaxAddon && taxCode.Length == 0)
                Err(errors, "The bill has GST add-ons but PTH.TaxCode is blank." + Tag("Tax Code Required", "0x52c6a0"));
            if (!hasTaxAddon && taxCode.Length > 0 && !string.Equals(taxCode, "TF", StringComparison.OrdinalIgnoreCase))
                Warn(warnings, "Tax code \"" + taxCode + "\" is set but the bill carries no GST amount - AccNew would normally store TF." + Tag("Tax Code Required", "0x52c6a0"));

            // ---- G12 every item line must post to the SAME income account
            //      ("All Item Not Connect With Same A/c ..Please Check Item" /
            //       "Privious Item A/c Not Match With Currwnt A/c")
            string firstAcc = null;
            foreach (DataRow r in ptl)
            {
                string acc = Str(r, "Acc_COde");
                if (acc.Length == 0) continue;
                if (firstAcc == null) firstAcc = acc;
                else if (!string.Equals(firstAcc, acc, StringComparison.OrdinalIgnoreCase))
                {
                    Err(errors, "Item lines post to different accounts (" + firstAcc + " and " + acc + ") - all items on one bill must use the same account." + Tag("All Item Not Connect With Same A/c ..Please Check Item", "0x52c728"));
                    break;
                }
            }

            // ---- G13 item income account must exist
            if (firstAcc != null && st != null && st.Account(firstAcc) == null)
                Err(errors, "Item income account " + firstAcc + " does not exist in AccMst." + Tag("EFF,ACCMST,Invalid Acc_Code or Record missing from AccMst,", "integrity checker"));

            // ---- G14 every item line needs an item that exists in the master
            foreach (DataRow r in ptl)
            {
                int ic = JetDb.GetInt(r, "ItemCode");
                if (st != null && st.Item(ic) == null)
                    Err(errors, "Item " + ic + " (line " + JetDb.GetInt(r, "Line_no") + ") is not in the Item master." + Tag("Item Not Found In Master", "0x52b790"));
            }
        }

        /// <summary>
        /// "INF,EFF,Not Match VCHNO," and friends: every physical row of ONE voucher must carry
        /// the same BT / Tran_Type / ID_NO / VchNo / VchDate as its INF row. AccNew's own
        /// database checker calls these out by table name; a clone that mixed them would show up
        /// in the reports as an orphan row.
        /// </summary>
        private static void CheckIdentity(LoadedVoucher v, string bt, string tt, string vchNo, string idNo, DateTime vchDate, List<string> errors)
        {
            foreach (KeyValuePair<string, DataTable> kv in v.Tables)
            {
                string table = kv.Key;
                if (kv.Value == null) continue;
                // these two are linked to OTHER vouchers on purpose
                if (string.Equals(table, "PayHist", StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(table, "INFRem", StringComparison.OrdinalIgnoreCase)) continue;

                foreach (DataRow r in v.Rows(table))
                {
                    if (!Has(r, "VchNo")) continue;
                    string rBt = Str(r, "BT"), rTt = Str(r, "Tran_Type"), rVch = JetDb.GetStr(r, "VchNo"), rId = Str(r, "ID_NO");
                    if (rBt.Length > 0 && bt.Length > 0 && !string.Equals(rBt, bt, StringComparison.OrdinalIgnoreCase))
                        Err(errors, table + " row has BT '" + rBt + "' but the voucher is '" + bt + "'." + Tag("INF," + table + ",Tran_Type not match,", "integrity checker"));
                    if (rTt.Length > 0 && tt.Length > 0 && !string.Equals(rTt, tt, StringComparison.OrdinalIgnoreCase))
                        Err(errors, table + " row has Tran_Type '" + rTt + "' but the voucher is '" + tt + "'." + Tag("INF," + table + ",Tran_Type not match,", "integrity checker"));
                    if (rVch.Length > 0 && vchNo.Length > 0 && rVch != vchNo)
                        Err(errors, table + " row has VchNo '" + rVch.Trim() + "' but the voucher is '" + vchNo.Trim() + "'." + Tag("INF," + table + ",VchNo not match,", "integrity checker"));
                    if (rId.Length > 0 && idNo.Length > 0 && rId != idNo)
                        Err(errors, table + " row has ID_NO '" + rId + "' but the voucher is '" + idNo + "'." + Tag("INF," + table + ",Duplicate ID_NO,", "integrity checker"));

                    DateTime d = JetDb.GetDate(r, "VchDate", DateTime.MinValue);
                    if (d != DateTime.MinValue && vchDate != DateTime.MinValue && d.Date != vchDate.Date)
                        Err(errors, table + " row is dated " + Helpers.FmtDate(d) + " but the voucher is " + Helpers.FmtDate(vchDate) + "." + Tag("INF," + table + ",VchDate not match,", "integrity checker"));
                }
            }
        }

        // ------------------------------------------------------------------ 2. live-database guards

        /// <summary>
        /// Guards that need the database. Call inside the posting transaction, before any insert,
        /// so a failure aborts with nothing written.
        /// </summary>
        public static void CheckLive(LoadedVoucher v, DbProbe st, JetDb jet, OleDbConnection cn, OleDbTransaction tx,
            List<string> errors, List<string> warnings)
        {
            if (v == null || jet == null) return;
            DataRow inf = v.Inf;
            if (inf == null) return;

            string bt = JetDb.GetStr(inf, "BT").Trim();
            string tt = JetDb.GetStr(inf, "Tran_Type").Trim();
            DateTime vchDate = v.NewDate.Date;

            // ---- G21 duplicate voucher number for the book (already enforced during allocation,
            //      repeated here so the reason is explicit when it fires)
            object dupVch = jet.Scalar(cn, tx, "SELECT Count(*) FROM [INF] WHERE [BT]=? AND [Tran_Type]=? AND [VchNo]=?",
                bt, tt, JetDb.GetStr(inf, "VchNo"));
            if (Convert.ToInt64(dupVch) > 0)
                Err(errors, "Voucher No " + JetDb.GetStr(inf, "VchNo").Trim() + " already exists for book " + bt + "-" + tt + "." + Tag("Voucher No. Already Exist", "0x4fa90c"));

            // ---- G22 duplicate ID_NO  ("INF,,Duplicate ID_NO," / "Activity - Duplicate ID_NO")
            object dupId = jet.Scalar(cn, tx, "SELECT Count(*) FROM [INF] WHERE [BT]=? AND [ID_NO]=?",
                bt, JetDb.GetStr(inf, "ID_NO"));
            if (Convert.ToInt64(dupId) > 0)
                Err(errors, "ID_NO " + JetDb.GetStr(inf, "ID_NO") + " already exists for book " + bt + "." + Tag("INF,,Duplicate ID_NO,", "integrity checker"));

            // ---- G23 duplicate UId
            object dupUid = jet.Scalar(cn, tx, "SELECT Count(*) FROM [INF] WHERE [UId]=?", JetDb.GetLong(inf, "UId"));
            if (Convert.ToInt64(dupUid) > 0)
                Err(errors, "UId " + JetDb.GetLong(inf, "UId") + " is already used by another voucher." + Tag("Activity - Duplicate ID_NO", "0x54afd0") );

            // ---- G24 counter must not be behind the highest number actually in use
            //      ("INF,CONTROL,Max. No. not match with CONTROL file,")
            if (st != null && st.ControlNo.ContainsKey(bt))
            {
                long ctrl = st.ControlNo[bt];
                object maxId = jet.Scalar(cn, tx, "SELECT Max([ID_NO]) FROM [INF] WHERE [BT]=?", bt);
                if (maxId != null && maxId != DBNull.Value)
                {
                    long used;
                    if (long.TryParse(Convert.ToString(maxId).Trim(), out used) && used > ctrl)
                        Warn(warnings, "Control.No_1 for " + bt + " is " + ctrl + " but ID_NO " + used + " is already in use - AccNew would allocate past it." + Tag("INF,CONTROL,Max. No. not match with CONTROL file,", "integrity checker"));
                }
            }

            // ---- G25 tax code must exist in the TaxCode master
            //      ("INF,TAXCODE,Invalid TaxCode or missing from TaxCode Master for,")
            if (v.Kind == VoucherKind.Sales && v.Pth != null)
            {
                string taxCode = Str(v.Pth, "TaxCode");
                if (taxCode.Length > 0 && jet.TableExists("TaxCode") && jet.HasColumn("TaxCode", "TaxCode"))
                {
                    object t = jet.Scalar(cn, tx, "SELECT Count(*) FROM [TaxCode] WHERE [TaxCode]=?", taxCode);
                    if (Convert.ToInt64(t) == 0)
                        Err(errors, "Tax code \"" + taxCode + "\" is not in the TaxCode master." + Tag("INF,TAXCODE,Invalid TaxCode or missing from TaxCode Master for,", "integrity checker"));
                }

                // ---- G26 tax invoice number must be unique  ("Duplicate TAX INVOICE" / "TAX Invoice No Already Exist")
                string taxInv = Str(v.Pth, "TAX_INV_NO");
                if (taxInv.Length > 0)
                {
                    object d = jet.Scalar(cn, tx, "SELECT Count(*) FROM [PTH] WHERE [BT]=? AND [TAX_INV_NO]=?", bt, taxInv);
                    if (Convert.ToInt64(d) > 0)
                        Err(errors, "TAX Invoice No \"" + taxInv + "\" already exists in this database." + Tag("Duplicate TAX INVOICE", "0x56a6e4"));

                    // ---- G27 same invoice number must not appear twice inside one voucher
                    if (v.Rows("PTH").Count > 1)
                        Warn(warnings, "The voucher has more than one PTH row - check the invoice numbers." + Tag("Activity - Duplicate Voucher No", "0x54b0ec"));
                }
            }

            // ---- G28 duplicate cheque number  ("Duplicate Chq. No")
            //      WARNING, not an error: re-posting a bank entry deliberately keeps the source
            //      cheque number (a bounced / re-issued cheque is re-entered with the same number),
            //      and AccNew raises this interactively where the operator can confirm it. The
            //      clone must not refuse what the software itself would let through.
            if (jet.TableExists("EFFLine") && jet.HasColumn("EFFLine", "ChqNo"))
            {
                foreach (DataRow r in v.Rows("EFFLine"))
                {
                    string chq = Str(r, "ChqNo");
                    if (chq.Length == 0) continue;
                    DataTable dup = jet.Query(cn, tx, "SELECT [BT],[Tran_Type],[VchNo] FROM [EFFLine] WHERE [ChqNo]=?", chq);
                    if (dup.Rows.Count > 0)
                    {
                        string where = Str(dup.Rows[0], "BT") + " Vch " + JetDb.GetStr(dup.Rows[0], "VchNo").Trim();
                        Warn(warnings, "Cheque No " + chq + " already appears in this database (" + dup.Rows.Count
                            + " line(s), e.g. " + where + "). AccNew asks for confirmation on this - check it is intended."
                            + Tag("Duplicate Chq. No", "0x56db78"));
                    }
                }
            }

            // ---- G29 challan reference must exist ("Invalid Challan No / Line")
            if (jet.TableExists("CHH") && jet.HasColumn("CHH", "CHNO") && v.Rows("PTL").Count > 0)
            {
                foreach (DataRow r in v.Rows("PTL"))
                {
                    string chno = Str(r, "chno");
                    if (chno.Length == 0) continue;
                    object d = jet.Scalar(cn, tx, "SELECT Count(*) FROM [CHH] WHERE [CHNO]=?", chno);
                    if (Convert.ToInt64(d) == 0)
                        Err(errors, "Item line " + JetDb.GetInt(r, "Line_no") + " points at challan " + chno + ", which is not in CHH." + Tag("Invalid Challan No / Line", "0x568d98"));
                }
            }

            // ---- G30 bill adjustments: the bill must exist, belong to this party, not be
            //      over-adjusted, and not be dated after the payment.
            //      ("Party Can Not Change If Bill Adjusted", "Bill Less Then Adjusted Value",
            //       "Bill Date Must Be Less Then Payment Date")
            foreach (DataRow r in v.Rows("PayHist"))
            {
                string bt2 = Str(r, "BT2");
                string billTt = Str(r, "Tran_Type");
                string billVch = JetDb.GetStr(r, "VchNo");
                string billId = Str(r, "ID_NO");
                double delta = Helpers.Round2(Amt(r, "Adj_Amt") + Amt(r, "Disc_Amt"));

                DataTable bh = jet.Query(cn, tx,
                    "SELECT [Tran_amt],[Adj_Amt],[Acc_Code],[VchDate] FROM [BillHist] WHERE [BT]=? AND [Tran_Type]=? AND [ID_NO]=? AND [VchNo]=?",
                    bt2, billTt, billId, billVch);
                if (bh.Rows.Count == 0)
                {
                    bool opening = billVch.Trim().StartsWith("O-", StringComparison.OrdinalIgnoreCase);
                    Err(errors, "Bill adjustment points at " + bt2 + " Vch " + billVch.Trim() + " (ID " + billId + ") but that bill is not in BillHist"
                        + (opening ? " - it is an OPENING bill (VchNo starts with O-), which this version does not adjust." : ". Check the bill selection.")
                        + Tag("Bill Less Then Adjusted Value", "0x5438c0"));
                    continue;
                }

                double billAmt = JetDb.GetDbl(bh.Rows[0], "Tran_amt");
                double already = JetDb.GetDbl(bh.Rows[0], "Adj_Amt");
                string billAcc = Str(bh.Rows[0], "Acc_Code");
                DateTime billDate = JetDb.GetDate(bh.Rows[0], "VchDate", DateTime.MinValue);

                if (already + delta > billAmt + 0.011)
                    Err(errors, "Bill " + bt2 + " Vch " + billVch.Trim() + ": adjusting " + Helpers.FmtAmt(delta) + " would exceed the pending " + Helpers.FmtAmt(billAmt - already) + "." + Tag("Bill Less Then Adjusted Value", "0x5438c0"));

                if (billAcc.Length > 0 && billAcc != Str(inf, "Acc_Code"))
                    Err(errors, "Bill " + bt2 + " Vch " + billVch.Trim() + " belongs to account " + billAcc + " but this voucher is for " + Str(inf, "Acc_Code") + " - the party cannot change once a bill is adjusted." + Tag("Party Can Not Change If Bill Adjusted", "0x5437c8"));

                if (billDate != DateTime.MinValue && billDate.Date > vchDate)
                    Err(errors, "Bill " + bt2 + " Vch " + billVch.Trim() + " is dated " + Helpers.FmtDate(billDate) + ", after the voucher date " + Helpers.FmtDate(vchDate) + "." + Tag("Bill Date Must Be Less Then Payment Date", "0x543930"));
            }

            // ---- G32 agent / transport codes must exist in their masters
            //      ("INF,AGENTMST,Invalid Agent code or missing from AgentMst," /
            //       "Duplicate for Transporter")
            if (jet.TableExists("AgentMst") && jet.HasColumn("AgentMst", "AgentCode"))
            {
                foreach (DataRow r in v.Rows("PTH"))
                {
                    string agent = Str(r, "AgentCode");
                    if (agent.Length == 0 || agent == "0") continue;
                    object d = jet.Scalar(cn, tx, "SELECT Count(*) FROM [AgentMst] WHERE [AgentCode]=?", agent);
                    if (Convert.ToInt64(d) == 0)
                        Err(errors, "Agent code " + agent + " is not in the AgentMst master." + Tag("INF,AGENTMST,Invalid Agent code or missing from AgentMst,", "integrity checker"));
                }
            }
            if (jet.TableExists("Transport") && jet.HasColumn("Transport", "TransportCode"))
            {
                foreach (DataRow r in v.Rows("PTH"))
                {
                    string tr = Str(r, "TransportCode");
                    if (tr.Length == 0 || tr == "0") continue;
                    object d = jet.Scalar(cn, tx, "SELECT Count(*) FROM [Transport] WHERE [TransportCode]=?", tr);
                    if (Convert.ToInt64(d) == 0)
                        Err(errors, "Transport code " + tr + " is not in the Transport master." + Tag("Duplicate for Transporter", "0x514920"));
                }
            }

            // ---- G31 duplicate PayHist rows inside the voucher  ("PAYHIST,,Duplicate Record,")
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (DataRow r in v.Rows("PayHist"))
            {
                string key = Str(r, "BT2") + "|" + Str(r, "Tran_Type") + "|" + JetDb.GetStr(r, "VchNo").Trim() + "|" + Str(r, "ID_NO");
                if (!seen.Add(key))
                    Err(errors, "The same bill (" + key.Replace("|", " ") + ") appears twice in the adjustment list." + Tag("PAYHIST,,Duplicate Record,", "integrity checker"));
            }
        }
    }
}
