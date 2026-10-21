# How AccuVisionRepost writes an entry (and why it matches AccNew)

Evidence base, in order of trust:

1. **Byte-verified injection recipes** ("Analysis of Accnew.exe" kit:
   `FINDINGS.md`, `bulk_build.py`, `ops_manual_session.json`,
   `state_before.json`, `updates.json`) — a manual JV 345/346 and Sales 132
   in AccNew were diffed byte-for-byte against injected JV 347 / Sales 133.
2. **The full AccNew decompilation** (`AccNew_DECOMPILATION_COMPLETE_SINGLE.zip`)
   — VB6 strings/xrefs for form logic (`frmBankCash` save `00761290`,
   `frmRecPay2006`, `frmSale2006`, table censuses).
3. **`SaleBillRepost.bas`** — the owner's own user-tested Excel/VBA repost
   tool (behaviour for TAX_INV_NO, narration month-bumping, warnings).
   Where it conflicts with (1), (1) wins.

## Core idea: verbatim clone, not synthesis

A voucher is a group of rows glued by `BT + Tran_Type + ID_NO + VchNo + UId`
across physical tables. The engine loads **every column** of the source
voucher's rows (via runtime schema introspection, so it adapts to the actual
`.mdb`), applies the user's edits, recomputes only the documented totals,
and inserts with fresh identity values. Anything nobody explicitly changes
comes out byte-identical to what AccNew wrote for the source entry.

Tables cloned per voucher kind:

| Voucher | BT | Tables |
|---|---|---|
| Sales (Multi Line) | `SL` | `INF`, `PTH`, `PTHL`, `PTL`, `EFF`, `ItemTrn` (rebuilt), `BillHist` |
| Journal | `JV` | `INF`, `EFF` (+ `EFFLine`/`INFRem` if present) |
| Bank-Cash Transaction | `CB` | `INF`, `EFF` (+ `EFFLine`/`INFRem` if present) |
| Bank/Cash Receipt-Payment | `RE` / `PA` | `INF`, `EFF` (+ `EFFLine`/`INFRem`/`PayHist` if present) |

Insert order matches the manual session: INF first, then (Sales)
PTH/PTHL/PTL/EFF/ItemTrn/BillHist, else EFF/EFFLine/INFRem/PayHist.

**Discovery guard:** on load, the source voucher is probed for rows in
`PTLL`, `PTHL1`, `PTL_PO`, `PTH_PO`, `PTHL_PO`, `TDS`, `JVXREF`, `CHH`, `CHL`
(tables this version does not write). If any exist, posting is blocked with a
clear message instead of silently dropping data.

## Number allocation (AccNew's own counters)

All re-read **inside the posting transaction** on an exclusive connection,
bumped past any existing value, and written back with old-value guards
(`UPDATE ... WHERE No_1=<old>` must hit exactly 1 row, else rollback):

- `INF.UId = UID.UID + 1`
- `ID_NO` = 10-digit zero-pad of `Control.No_1 + 1` (row `Prog_name=<BT>`, blank `Series`)
- `VchNo` = 9-char space-padded `TranType.No_1 + 1` for `(BT, Tran_type)`
- `EFF.Uid2`, `PTL.Uid2`, `PTH.UID2` = table `MAX()+1` sequentially
  (EFF allocated in `Line_no` order); `BillHist.UID2 = 0`
- `INF.Entry_Date = now`; other date fields move to the new voucher date
  **only** when they equalled the source voucher date (so e.g. a differing
  RefDate is preserved). Sales: `INF.RefDate = VchDate`, `INF.RefNo = VchNo`,
  `PTH.TAX_INV_DATE = VchDate`.

Sales `TAX_INV_NO`: three modes (header dropdown). *Same as Vch No*
(default, replaces the trailing digits of the source invoice number with the
new voucher number at the same width — SaleBillRepost.bas `BumpInvoiceNo`),
*Keep source gap* (preserves the source `TAX_INV_NO − VchNo` offset seen in
live data, e.g. 132→134), *Manual*. Posting refuses a TAX_INV_NO that
already exists.

## Sales recalculation (mirrors frmSale2006)

- `PTL.Value = PTL.Net_Value = qty × rate` (`Rate_Per='Q'`/blank; other
  `Rate_Per` values scale proportionally with a warning). `Tran_Nos` /
  `Gross_qty` / `Entry_Rate` follow qty/rate only where the source had them
  equal (pattern-detected via the original-value snapshot).
- `PTHL`: `Ass_val` follows goods where the source equalled goods; `Amount`
  recomputed `Ass_val × Per/100` (2dp) where the source amount was
  percentage-derived and the user hasn't overridden it.
- Round-off: if the target series has `Net_Roundoff='1'`, net is rounded to
  whole rupees (banker's rounding, as VB `Round`) and the difference written
  to the AddOns-99 `PTHL` row (`Type '+'`), created/removed as needed.
- `TaxCode`: `G<ΣPer of addons 4/5/6 with amount>` or `TF` (auto mode).
- `PTH`: `GoodAmt/AddAmt/LessAmt/NetAmt`, `Tran_Qty/Tran_Nos` totals.
- `EFF` roles rebuilt per verified pattern: line 0 party Dr Net; one income
  line Cr = goods + round-off with `Gross_qty = Σ PTL Gross_qty`; add-on
  lines 101+ mirror PTHL (`'+' → Cr`, `'-' → Dr`, narration = add-on name).
  Round-off gets **no** own EFF line (merged into income — the one place
  SaleBillPost.vbs deviates from the app).
- `ItemTrn` rebuilt from final PTL against **live** `Item.Cl_St`
  (Old_Bal → New_Bal chain, 0-based Line_no, Doc_No = new VchNo), then
  `Item.Cl_St / QtyPcsPur / QtyPcsClStock / QtyMtrPur / QtyMtrClStock` all
  set to the final New_Bal (item row by ItemCode+Acc_Code+Name identity,
  exactly one row or rollback). `PriseList(item, party)` Rate/Lstdate
  updated only when a row already exists (noted otherwise).

## Journal / Bank recalculation

- `INF.Acc_Code`/`DbCr` = first EFF line matching the source INF side;
  `INF.tran_amt` follows the source pattern (0 (JV) / ΣDr / ΣCr),
  pattern-detected; same for `INF.Adj_Amt` vs Σ(Adj) or Σ(Adj+Disc) of
  `PayHist`. Unrecognised patterns are left alone with a warning.
- ΣDr = ΣCr, positive amounts, leaf (non-group) accounts only — enforced.

## Balance & bill side effects (from the final EFF only)

Per EFF line, the leaf account **and every `AccBom` ancestor** get
`Db_amt += dr`, `Cr_amt += cr`, and
`Cl_bal += Cl_dbcr='D' ? (dDb−dCr) : (dCr−dDb)`, flipping `Cl_dbcr` only when
the balance crosses zero. (Numerically verified against `updates.json`; the
Op-based recompute agrees exactly.)

`PayHist` (receipts/payments): each row adds `Adj+Disc` to the target bill's
`BillHist.Adj_Amt` and to the bill INF's `Adj_Amt`. Posting is blocked when
the bill isn't in `BillHist` (opening bills / OpPayHist path unsupported) or
the adjustment would exceed the pending amount.

## Things deliberately reset on the clone (a fresh entry, not a photocopy)

Sales `PTH.Comm_paid*` cleared; `BillHist.Adj_Amt/Excess_Amt = 0`,
`op_flag/Print_flag/Check_Sys` cleared; Sales `INF.Adj_Amt = 0`;
`EFFLine.BankDate` cleared (not yet bank-reconciled); `INF.Entry_Date = now`.

## Safety transaction

Posting requires: AccNew not running (it caches counters and can overwrite
them on exit) → optional timestamped backup of the whole `.mdb` →
exclusive connection → one `OleDbTransaction` → post-insert re-load
verification and a `PostLog_*.txt` report. Any exception rolls the whole
thing back.

Empty strings become `Null` on write (Jet `AllowZeroLength` + app
behaviour), except the proven `PTL.Remark1/Remark2/chno/ID_NO1` which the
manual session shows stored as `""`. Narrations are never trimmed.

## Headless architecture (`HeadlessSpec`, `HeadlessEngine`, `HeadlessCli`, `MainForm.HeadlessMode`)

1. **Console + GUI single binary (`<OutputType>Exe</OutputType>`)**:
   - When invoked with CLI arguments (`probe`, `search`, `inspect`, `review`, `post`, `batch-review`, `batch-post`, `/selftest`, `/uitest`), `Program.Main` runs `HeadlessCli.Run(args)` directly on standard `stdout`/`stderr` so `cmd.exe`, PowerShell, SSH, Python `subprocess`, and VBA `WScript.Shell.Run(..., bWaitOnReturn:=True)` wait synchronously and receive the exact exit code and redirected output streams.
   - When launched with no arguments (or `/gui`), `Program.Main` hides the console window via `ShowWindow(GetConsoleWindow(), SW_HIDE)` and opens `MainForm`.
2. **Pre-post live database checks in `BuildReviewPlan` & `ValidateForPost`**:
   - Both `review` (dry-run) and `post` check `PTH` for duplicate/blank `TAX_INV_NO`, check `BillHist` for missing or over-adjusted bills on `PayHist` rows, and verify `AccNew.exe` process status so headless dry-runs catch every error that would block a live post.
3. **Headless WinForms mode (`MainForm.HeadlessMode = true`)**:
   - All `MessageBox.Show` and modal picker calls in `MainForm` and `LookupDialog` are routed through non-blocking helpers that log to `HeadlessMessages`/`HeadlessErrors` and auto-confirm prompts, allowing the full WinForms UI to be driven and tested headlessly (`/uitest`).
