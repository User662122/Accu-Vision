# Audit — does AccuVisionRepost write an entry the same way AccNew does?

**Question being answered:** when a user clicks **New Entry**, presses **Enter** through each
field, and finally clicks **Save** in the main accounting software, what exactly runs — and does
*my* application (`AccuVisionRepost.exe`) produce the same database result?

**Short answer:** the **database write layer matches** (same tables, same columns, same numbers,
same counter bookkeeping). The **UI layer cannot match by design** — `AccuVisionRepost` has no
"New Entry" form; it *clones* an existing voucher instead of typing a blank one, so the New →
Enter → Save path maps onto Load → Edit → Recalculate → Validate → Post.

There are **6 concrete divergences** (all listed in §4, ranked); none of them changes the
accounting result on this database, and two of them would matter in other company databases.

---

## 0. What was examined (and how much to trust each source)

| # | Source | What it proves | Trust |
|---|---|---|---|
| 1 | `Accurate.Mdb` — the live company database (153 tables) | actual stored values, column names, counter values, numbering | **Highest — read directly** |
| 2 | `Analysis of Accnew.exe.zip` → `accnew_injection_kit/FINDINGS.md` + `ops_manual_session.json` | 4 entries typed by hand into AccNew, then reproduced byte-for-byte by script: 25 inserts + 32 updates gave a **byte-identical** database | **Highest — proven by byte-diff** |
| 3 | `AccNew_DECOMPILATION_COMPLETE_SINGLE.zip` (Ghidra 12.1.4, 4,760 functions, MSVBVM60) | which function does what, validation order, error messages, SQL | High for *behaviour*, medium for *names* (VB6 strips procedure names) |
| 4 | `AccuVisionRepost` source (this repo) | what your app actually executes | **Exact** |

AccNew is a **VB6 native** executable. Native compilation removes procedure names, so functions
are identified here by Ghidra addresses (`0x00d22c00`) plus the strings and SQL each one contains.
That is why every claim below cites the string address that proves it.

---

# Part 1 — The main accounting software (AccNew / "Accurate")

## 1.1 What runs when "New Entry" is opened

The voucher window is **one VB6 form** that holds the ledger grid, the Dr/Cr amounts, the date and
the voucher number. Every handler of that form is bound through a 48-entry **method table** at
`0x004b5405 … 0x004b569c` (13-byte stride). Opening a new entry is not a single function; it is
three cooperating handlers:

| Step | Function | What it does | Proof |
|---|---|---|---|
| Form opens | `0x00d133c0` (10,588 bytes) | Connects the DB (`Provider=Microsoft.Jet.OLEDB.4.0 … Jet OLEDB:Database Password=honda757acc`), loads `Control`, `TranType`, `AccMst` (`where grp_flag <> 'Y'`), applies the logged-in user's **form permissions** (`select * from passline … and form_caption = '…' and user_name = '…'`) and the tooltip table | strings `0x4f9b08 PWD=`, `0x4f8630 select * from control`, `0x4f9924 select * from AccMst`, `0x4f79b0 where grp_flag <> 'Y'`, `0x4f9c78 Restriction` |
| New voucher number | `0x00d19860` (7,423 bytes) | Reads the next number for the book from the **period series** first, then the plain series: `select * from trantypeser where bt='…' and tran_type='…' and ser_val='…'` (`ser_val` = `yyyymm`), falling back to `TranType.No_1`; formats with `#########0` | strings `0x4fa50c/0x4f8768 trantypeser`, `0x4fa07 … ser_val`, `0x4f9d48 vchno`, `0x4f21d8 no_1` |
| Mode / button captions | mode routine reached from the same table | Sets the command-button captions for Add / Edit / Inquiry: `"INQ."`, `"Edit"`, `"Save Entry"` and enables/disables fields | `0x4f574c INQ.`, `0x4f58b8 Edit`, `0x4f5bf0 Save Entry` |

> **Honest caveat.** Because VB6 native strips names, I can prove *what* each handler does but not
> which `.frm` line it came from. The table slot for **Save** is unambiguous
> (`0x004b5634 → 0x00d22c00`, see §1.5). The New-entry slot is `0x00d19860` (next-number handler)
> with form init `0x00d133c0`; both are provable by the SQL they run, not by their names.

## 1.2 What that calls (call tree of a new entry)

```
form init  0x00d133c0
 ├── Jet/JRO connect  →  "…Accurate.Mdb;Jet OLEDB:Database Password=…"
 ├── select * from control            → Counter read (Control.No_1)
 ├── select * from trantype           → series read (TranType.No_1)
 ├── select * from AccMst / AccBom    → ledger master + hierarchy
 ├── select * from passline           → user restrictions  ("Restriction")
 └── HelpToolTip lookup               → field help
new number 0x00d19860
 ├── select * from trantypeser where bt/tran_type/ser_val
 └── TranType.No_1 fallback → formats "#########0" → VchNo textbox
ledger field  (Enter / F4)
 ├── 0x00d0de10 / 0x00d104b0 / 0x00d10a20 / 0x00d11110   → rtcSendKeys "{F4}"
 ├── 0x00d12140   account picker  ("CBOACC", "{Tab}", "Do You Want To Add New Master ?")
 ├── 0x00d0df10   lookup + show closing balance (" Debit"/" Credit", "##,##,##,##0.00")
 └── 0x00d0e9e0   re-lookup + group-account test ("Group Account Not Allowed In JV")
amount field      0x00d0dac0  ("Invalid Number")
bill adjust       0x00d0a9e0  ("Excess Amt. Not Allowed", "Bill Payment Excess Bill Amt.")
Enter key         0x00d111b0  if KeyAscii = 0x0D → call form method +0x79C
Save              0x00d22c00  (JV)     0x00761290  (Bank/Cash)
```

## 1.3 Pressing **Enter** in each field

VB6 routes Enter through `KeyPress`/`KeyDown` handlers. The ones on this form:

* `0x00d111b0` — `if (KeyAscii == 13) → (*(form + 0x79C))()`. The classic "Enter = go to the next
  control / commit the line" stub. A second Enter handler `0x00d12140` also watches
  `0x10` (Shift/Ctrl with `0x03`/`0x07`), `0x4C` (Ctrl+L) and one configured hot-key
  (`DAT_01893180`).
* `{F4}` handlers — `0x00d0de10` sends `{F4}` (and clears the combo, `-1`), `0x00d104b0`,
  `0x00d10a20`, `0x00d11110` send `{F4}` **only**; `{Home}+{End}` handlers
  (`0x00d05da0`, `0x00d18070`, `0x00d189e0`, `0x00d18a80`) select the whole field on entry.
* **Ledger field, on Enter:**
  * `0x00d0df10` — `select * from accmst where name = '<typed>'`; if the user lacks permission →
    `"Restriction"`; if not found → `"Account Not Found In Master"`; if found it loads
    `cl_bal` / `cl_dbcr` and renders it as `" Debit"` / `" Credit"` with `##,##,##,##0.00`.
  * `0x00d0e9e0` — second pass: lookup again, then `parent_code = '<code>'` against **AccBom**;
    if the account has children it is a group → `"Group Account Not Allowed In JV"`.
    It also fills address fields (`address_1/2/3`, `City`) for party accounts.
* **Amount field, on Enter:** `0x00d0dac0` → `"Invalid Number"` when the text does not parse.
  For bill-wise adjustment `0x00d0a9e0` → `"Excess Amt. Not Allowed"` /
  `"Bill Payment Excess Bill Amt."`.
* **Date field:** formatted with `yyyymmdd` and `dd/mm/yyyy`
  (`0x4f53b8`, `0x4fbf98`); bad input → `"Invalid Date"` (`0x4f1ca8`).

## 1.4 How the ledger, Dr/Cr, date and voucher number are validated

| Rule | Where enforced | Evidence (string address) |
|---|---|---|
| Ledger must exist in `AccMst` | account field + save | `Account Not Found In Master` `0x4f5eb0`, `Account Name Not Found In Master` `0x4f5e18` |
| Ledger must be a **leaf** (not a group) | account field | `Group Account Not Allowed In JV` `0x4f9ce0`; SQL `where acc_code not in (select parent_code from accbom)` `0x4f9b34`, `and sys_flag <> 'Y'` `0x4f9bb0` |
| Dr total must equal Cr total | **save** | `DEBIT And CREDIT  Must be Equal For JV` `0x4fa7d0`, `Balance Not Equal` `0x4fa824` |
| Voucher number required | save | `Voucher No. Required` `0x4fa84c` |
| Voucher number must not already exist | save | `Voucher No. Already Exist` `0x4fa90c` + `Select * from inf where bt = 'JV' and vchno = '…' and tran_type = '…'` `0x4f87a0` |
| Book/series (Tran_Type) required & valid | save | `Transaction Type Required` `0x4fa87c`, `Invalid Transaction Type` `0x4f9eac`, `Invalid Transaction Type Data Not Saves` `0x4faa94` |
| Date inside the accounting period | save | `A/c Period : ` `0x528b9c` + ` To ` `0x4fbfb4`, `Invalid Date` `0x4f1ca8` |
| Special JVs cannot be edited | save | `Interest JV Can Not Add/Edit` `0x534ac8`, `Auto JV Can Not Add/Edit` `0x534a90` (in `0x00d10550` / `0x00d11f90`) |
| Multi-contra JV cannot be saved | save | `Multi - Contra Entry  . . . Can't Save` `0x534df4` |
| User permission per form / per action | field + save | `Restriction` `0x4f9c78` (+ `passline` query) |
| Unsaved-changes guard on leaving | form | `Last Transaction Not Saved` `0x4fa568`, `Save Last Transaction` `0x4fa5d4`, `Do You Want To Save Current Entry ?` `0x4f5ba4` |

## 1.5 What runs on **Save**, and in what order

The Save button is bound to **`0x00d22c00`** for a Journal Voucher and **`0x00761290`** for a
Bank-Cash transaction (the source of the two is the same code with a different book type; a third
near-copy `0x0174b370` is bound to the "new" JV form). The strings appear in this code order,
which *is* the execution order:

| # | Step | Evidence |
|---|---|---|
| 1 | Format the voucher date, compare it with the **A/c Period** | `yyyymmdd` `0x4f53b8`, `dd/mm/yyyy` `0x4fbf98`, `A/c Period : ` + ` To ` |
| 2 | Confirm — `"Do You Want To Save Current Entry ?"` (caption `"Save Entry"`) | `0x4f5ba4`, `0x4f5bf0` |
| 3 | Block Interest JV | `Interest JV Can Not Add/Edit` `0x534ac8` |
| 4 | **Dr = Cr** test (two-level: `Balance Not Equal`, then the JV-specific message) | `0x4fa824`, `0x4fa7d0` |
| 5 | `Voucher No. Required` / `Transaction Type Required` | `0x4fa84c`, `0x4fa87c` |
| 6 | Permission (`Restriction`) | `0x4f9c78` |
| 7 | Block multi-contra | `0x534df4` |
| 8 | **Duplicate voucher** — `Select * from inf where bt = 'JV' and vchno = '…' and tran_type = '…' order by bt,vchno` → `Voucher No. Already Exist` | `0x4f87a0`, `0x4f8fe8`, `0x4fa90c` |
| 9 | **Counters** — `no_1` read/compare/write (8 references: `TranType.No_1` and `Control.No_1`) | `0x4f21d8` ×8 |
| 10 | **AccMst** — `Db_amt` / `Cr_amt` += line amount; `tran_amt`; `op_bal` / `op_dbcr` used for the side; `cl_bal`; `cl_dbcr` flip | `db_amt 0x4f25bc`, `cr_amt 0x4f25d0`, `tran_amt 0x4f2498`, `op_bal 0x4f25e4`, `op_dbcr 0x4f25f8`, `cl_bal 0x4f260c`, `cl_dbcr 0x4f2620` |
| 11 | **Group roll-up** up `AccBom` to the root `A000000000` | `parent_code 0x4f2634`, `A000000000 0x4f2650` |
| 12 | Delete the stored child rows for this entry id (JV → `infrem`, `billhist`; CB → `infrem`, `effline`) | `delete * from infrem` `0x4fa9ac`, `delete * from billhist` `0x529694`, `delete * from effline` `0x4fa9dc` |
| 13 | Reset the form to Inquiry (`"INQ."`, `11100111111`) and re-read by `tran_type` + `vchno` | `0x4f574c`, `0x4f2158` |

**About the INSERTs.** The decompiled image contains **no literal `INSERT INTO` text**, and none of
the strings above is an insert. AccNew writes the INF/EFF (and PTH/PTL/…) rows through a bound
recordset, not through SQL text. The insert set itself is nevertheless proven: the byte-diff of
`FINDINGS.md` §3/§9 shows exactly which rows a manual save creates — 25 row inserts for 4 entries,
in a specific order.

## 1.6 Tables and columns a manual save touches

Per `FINDINGS.md` §3 (byte-verified) and re-checked against the live `Accurate.Mdb`:

**Journal Voucher (`BT='JV'`)**

| Table | Rows | Columns written |
|---|---|---|
| `INF` | 1 | `VchDate, RefDate, tran_amt, Entry_Date, Adj_Amt, UId, BT, ID_NO, Acc_Code, VchNo, RefNo, Naration, Tran_Type, DbCr, UserId` |
| `EFF` | 2+ | `VchDate, Tran_amt, Line_no (1..n), UId, Uid2, BT, ID_NO, VchNo, Acc_Code, Acc_Code2, DbCr, Naration, Tran_Type` |
| `AccMst` | leaf + every `AccBom` ancestor | `Db_amt, Cr_amt, Cl_bal, Cl_dbcr` |
| `TranType` | 1 | `No_1` (voucher series) |
| `Control` | 1 | `No_1` (`Prog_name='JV'`, blank `Series`) |
| `UID` | 1 | `UID` |

**Sales bill (`BT='SL'`)** adds `PTH`, `PTHL`, `PTL`, `ItemTrn`, `BillHist` and updates
`Item` (`Cl_St, QtyPcsPur, QtyPcsClStock, QtyMtrPur, QtyMtrClStock`) and `PriseList`
(`Rate, Lstdate`). **Bank/Cash (`BT='CB'`)** is the JV set plus `EFFLine` instead of `BillHist`.

Live-database spot check (this company's `Accurate.Mdb`):

```
UID                    single row, UID = 1433
Control  JV  (Series '')  No_1 = 5007      ← last ID_NO used = 0000005007
Control  SL  (Series '')  No_1 = 1907
Control  CB  (Series '')  No_1 = 13
TranType JV/01            No_1 = 451   Name = 'JOUNRAL VOUCHER'   Net_Roundoff = 1
TranTypeSer               0 rows  ← the period-series table is unused in this company
INF      1059 rows, 449 of them JV;  EFF 2535 rows
JV Vch 439 → EFF Line_no 1,2 · Acc_Code2 mirrors the contra · Uid2 26290/26291
JV Vch 439 → INF tran_amt = 0.0   |   JV Vch 440 → INF tran_amt = 75000.0
```

That last line matters: **`INF.tran_amt` for a JV is not always 0** — 167 of the 449 JVs store
`0.0` and 282 store an amount. AccNew's behaviour depends on the entry/form vintage, so a clone
must *preserve the source pattern* rather than hard-code either value. Your app does exactly that
(`CloneEngine.RecalcGeneric`).

---

# Part 2 — Your application (AccuVisionRepost)

## 2.1 There is no "New Entry" button — here is the mapping

Your app is a **clone-and-repost** engine. Its UI (`Ui/MainForm.cs`, `BuildUi`) has these actions:

| Your UI | Handler | Engine call |
|---|---|---|
| `BROWSE…` / `FIND IN FOLDER…` / `CONNECT` | `BrowseDb` 547, `FindDatabases` 557, `ConnectDb` 607 | `CompanyDb.Probe()` |
| `SEARCH PREVIOUS ENTRIES` | `DoSearch` 680 | `CloneEngine.Search` 33 |
| `LOAD SELECTED ENTRY FOR EDITING →` | `LoadSelected` 701 → `LoadVoucherIntoUi` 725 | **`CloneEngine.Load` 155** ← *this is your "New Entry"* |
| edit a grid cell + Enter | `EffCellEdited` 900 / `ItemCellEdited` 930 / `AddonCellEdited` 953 | `RecalcSales` 287 / `RecalcGeneric` 539 |
| `RECALCULATE` | `RecalcAndRefresh` 1211 | same |
| `REVIEW & POST →` | `RefreshReview` 1280 | `BuildReviewPlan` 798 + `Validate` 586 |
| `POST NEW ENTRY INTO AccNEW DATABASE` | `DoPost` 1395 | **`CloneEngine.Post` 1168** |

So "New Entry → Enter in each field → Save" becomes
**Load → edit (Enter) → Recalculate → Validate → Post**.

## 2.2 What `Load` does (`CloneEngine.Load`, line 155)

1. `SELECT * FROM [INF] WHERE [UId]=?` — must be exactly 1 row.
2. Loads the whole voucher group by identity `BT + Tran_Type + ID_NO + VchNo + UId`:
   `EFF`; for Sales also `PTH, PTHL, PTL, ItemTrn, BillHist`; plus `EFFLine`, `INFRem`,
   `PayHist` (PayHist is linked by `Our_Tran_Type` + `Our_VchNo`).
3. **Discovery guard** — if the source also has rows in `PTLL, PTHL1, PTL_PO, PTH_PO, PTHL_PO,
   TDS, JVXREF, CHH, CHL`, the voucher is flagged and posting is refused (line 228-238).
   This is the equivalent of AccNew's "this version of the tool can't reproduce that entry" — good.
4. Snapshots every numeric cell (`SnapshotOriginal`) so recalculation can follow the original
   *pattern* instead of guessing.
5. Default new date = same day next month, clamped, or today if that leaves the FY.

## 2.3 Pressing Enter in each field (your app)

Your UI is a `DataGridView`; there is no per-field VB6-style handler. The equivalent of
"Enter in a field" is **`CellEndEdit`**:

* `EffCellEdited(row, col)` — writes `Acc_Code, DbCr, Tran_amt, Naration, Acc_Code2` into the
  underlying `EFF` DataRow; if the account code changed it **re-points every `Acc_Code2` that
  referred to the old account** (mirror-maintenance); refreshes the name column; then
  `RecalcAndRefresh(false)`.
* `ItemCellEdited(row, col)` — `ItemCode, Tran_Qty, Tran_Nos, Unit, Unit_Rate, Naration, Acc_COde`;
  auto-fills `Unit` from `Item` master when the item changes; `RecalcAndRefresh(true)`.
* `AddonCellEdited(row, col)` — `Type, Per, Amount, Add_val`; recalculates the bill.
* Header fields — `SyncHeader` 1112 (`Naration`, `RefNo`, `RefDate`, `Pth.Acc_Code/PayMode/cr_day/
  TaxCode`, TAX-invoice mode, `UserId`), `PartyCodeEdited` 1143, `OnBookChanged` 1169,
  `OnNewDateChanged` 1182.
* Every one of those ends in `RecalcAndRefresh` → `UpdateTotals` (1236) → `CloneEngine.Validate`
  → errors listed in red → `UpdatePostEnabled` (1389) disables the POST button.

## 2.4 What `Validate` checks (`CloneEngine.Validate`, line 586)

| Check | Message on failure |
|---|---|
| unsupported linked tables present | "…which this tool does not clone yet. Posting is blocked…" |
| book/series exists in `TranType` | "Book (TranType) … was not found." |
| **date inside FY** — `St.FyFrom/FyTo` from `Company.fin_year_from/fin_year_upto` | "New date … is outside the financial year (…) (…)." |
| ≥ 2 `EFF` lines | "At least 2 ledger (EFF) lines are required." |
| account not blank / exists in `AccMst` | "account is blank" / "account … does not exist in AccMst." |
| **group account rejected** (`Grp_flag='Y'`) | "… is a GROUP account - AccNew does not allow group accounts in vouchers." |
| Dr/Cr is `D` or `C` | "Dr/Cr must be D or C." |
| amount ≥ 0 / non-zero | "amount cannot be negative." / warning "has zero amount." |
| **Dr total = Cr total** | "Ledger lines do not balance: Debit … vs Credit … (difference …)." |
| voucher not zero | "Voucher amount is zero." |
| Sales: `PTH` present, `PTL` non-empty, item exists, qty/rate ≥ 0 | "Sales header (PTH) is missing…", "Item … does not exist…" |
| Sales: `GoodAmt + AddAmt − LessAmt = NetAmt` | "Bill totals do not add up… Press Recalculate." |
| Sales: TAX invoice not blank (manual mode) | "TAX Invoice No is blank (manual mode)." |
| BankCash: at least one `acc_type 03/04` line | warning only |
| `PayHist.Adj_Amt` ≥ 0 | "A bill adjustment (PayHist) has a negative Adj_Amt." |
| **text length ≤ column width** (introspected from the schema) | "…is longer than the column allows…" |

Note the deliberate difference from AccNew: your **date check uses the Company table**
(`fin_year_from` / `fin_year_upto`, live values `2026-04-01` → `2027-03-31`), which is the same
period AccNew's `A/c Period :` check enforces.

## 2.5 What `Post` does, in order (`CloneEngine.Post`, line 1168)

1. `_db.Probe()` — refresh masters so validation uses the newest data.
2. `Validate(...)` → abort with the error list if anything fails.
3. `IsAccNewRunning()` → refuse if `AccNew.exe` is alive (it caches counters).
4. `MakeBackup()` (optional) — `AccuVision_Backups\Accurate_BACKUP_*.mdb`.
5. `BuildReviewPlan` — numbers, deltas, item balances for the review screen.
6. **Exclusive** OleDb connection, `BeginTransaction`.
7. **Allocate inside the transaction** (`ReadCounters` 693):
   * `VchNo` = `TranTypeSer.No_1 + 1` for the **new date's `yyyyMM`** when that period row exists,
     else `TranType.No_1 + 1`; bumped while `INF` already has it (9-char right aligned). The row
     that was read is the row that gets bumped.
   * `ID_NO = Control.No_1 + 1`, bumped while it exists (10-digit zero pad)
   * `UId   = UID.UID + 1`, bumped while it exists
   * `EFF.Uid2`, `PTL.Uid2`, `PTH.UID2` from `MAX()+1`
8. Sales only: compute `TAX_INV_NO` (same-as-vch / keep-offset / manual) and **refuse a duplicate**
   (`SELECT Count(*) FROM [PTH] WHERE [BT]='SL' AND [TAX_INV_NO]=?`).
9. `CheckBillAdjustments` (774) — every `PayHist` bill must exist in `BillHist` and not be
   over-adjusted.
10. `PrepareFinal` (921) — rewrite every row's identity block and dates, rebuild `ItemTrn`
    against live stock, reset `BillHist`/`Comm_paid`/`Adj_Amt`, clear `EFFLine.BankDate`.
11. **Insert**, in AccNew's order: `INF` → (`SL`: `PTH, PTHL, PTL, EFF, ItemTrn, BillHist`)
    or (`JV/CB/RE/PA`: `EFF, EFFLine, INFRem, PayHist`). `Jet.InsertRow` copies **every column of
    the table schema** with parameter placeholders (line 313).
12. **`AccMst`** — `ComputeAccDeltas` (745) walks leaf + all `AccBom` ancestors, then per account:
    `Db_amt += dDb`, `Cr_amt += dCr`,
    `Cl_bal += Cl_dbcr='D' ? (dDb−dCr) : (dCr−dDb)`, flip `Cl_dbcr` if the balance crosses zero.
    `UPDATE … WHERE [Acc_code]=?` must hit exactly 1 row.
13. **Sales side-effects** — `Item` five mirror columns (must hit 1 row); `PriseList.Rate/Lstdate`
    (only if the row already exists); `PayHist` → `BillHist.Adj_Amt += Adj+Disc` and the bill's
    `INF.Adj_Amt += Adj+Disc`.
14. **Counters with old-value guards** (this is *stronger* than AccNew):
    `UPDATE TranType SET No_1=? WHERE BT=? AND Tran_type=? AND No_1=<old>` → must be 1 row;
    same for `Control` and `UID`; otherwise rollback.
15. `tx.Commit()`; any exception → `tx.Rollback()` (nothing is written).
16. Re-`Load(plan.NewUId)` and verify, then write `PostLog_*.txt`.

## 2.6 Tables and columns your app writes

| Voucher | Inserted | Updated |
|---|---|---|
| **JV** | `INF`, `EFF`, `EFFLine`*, `INFRem`* | `AccMst` (leaf + ancestors), `TranType.No_1`, `Control.No_1`, `UID.UID` |
| **CB** | `INF`, `EFF`, `EFFLine`*, `INFRem`* | same as JV |
| **RE / PA** | `INF`, `EFF`, `EFFLine`*, `INFRem`*, `PayHist` | + `BillHist.Adj_Amt`, bill `INF.Adj_Amt` |
| **SL** | `INF`, `PTH`, `PTHL`, `PTL`, `EFF`, `ItemTrn`, `BillHist` | + `Item` (5 cols), `PriseList` |

\* only when the source voucher has those rows.

That is **exactly the table set** that `FINDINGS.md` §3/§9 measured from manually typed entries.

---

# Part 3 — Side-by-side

| Behaviour | AccNew (main software) | AccuVisionRepost | Verdict |
|---|---|---|---|
| Identity block `BT+Tran_Type+ID_NO+VchNo+UId` on every row | yes | `SetIdentity` (905) | ✅ |
| `VchNo` 9-char right aligned | `'      345'` | `Helpers.PadVch` = `PadLeft(9,' ')` | ✅ |
| `ID_NO` 10-digit zero pad | `'0000006305'` | `Helpers.PadId` = `PadLeft(10,'0')` | ✅ |
| `UId` from `UID` table +1 | yes | `ReadCounters` | ✅ |
| `Control.No_1` (blank series) for ID | yes | `SELECT [No_1],[Series] FROM [Control] WHERE [Prog_name]=?` | ✅ |
| `TranType.No_1` for voucher series | yes | same | ✅ |
| Period series `TranTypeSer` (`ser_val=yyyyMM`) | **yes** (`0x00d19860`) | yes — read + bumped (`ReadCounters` / `Post`) | ✅ fixed §4.1 |
| `EFF.Uid2 / PTL.Uid2 / PTH.UID2` = max+1 | yes | `MaxTx()` | ✅ |
| `BillHist.UID2 = 0` | yes | explicit | ✅ |
| Dr = Cr enforced | `0x4fa7d0` | `Validate` | ✅ |
| Group account rejected | `0x4f9ce0` | `Validate` | ✅ |
| Voucher duplicate rejected | `0x4fa90c` | `VchExists` in tx | ✅ |
| Date inside A/c Period | `A/c Period :` check | `Company.fin_year_*` | ✅ |
| **Auto JV / Interest JV / Multi-contra guards** | yes | yes — `Validate` | ✅ fixed §4.2 |
| `AccMst` leaf + ancestor roll-up, side flip | yes | `ComputeAccDeltas` | ✅ |
| `Item` five mirror columns | yes | explicit UPDATE | ✅ |
| `PriseList` rate memory | yes | update-if-exists | ✅ (with note) |
| `INF.Entry_Date` = now, `RefDate` preserved unless equal to source date | yes | `MoveDateIfSame` | ✅ |
| `INF.tran_amt` JV = source pattern | 0 or ΣDr depending on entry | pattern-detected | ✅ |
| Empty string → `NULL` except `PTL.Remark1/2/chno/ID_NO1` | yes | `KeepEmptyString` | ✅ |
| `UserId` padded to 5 (`'ACC  '`) | yes | `Helpers.PadUser` | ✅ fixed §4.3 |
| `EFF.Acc_Code2` contra for **every** line | yes | `FillMissingContra` before insert | ✅ fixed §4.4 |
| `EFF.Line_no` renumbered 1..n | yes | renumbered in `PrepareFinal` | ✅ fixed §4.5 |
| Delete child rows by `id_no` before writing | yes (edit semantics) | not needed for new IDs | ✅ equivalent |
| All-or-nothing save | Jet transaction | `OleDbTransaction` + rollback | ✅ |

---

# Part 4 — Findings, ranked

> **Status: 4.1 – 4.5 are fixed in this branch** (code in `src/AccuVisionRepost`). 4.6 is left
> open on purpose — see the note there.

### 4.1 `TranTypeSer` (period voucher series) is not supported — *medium*
AccNew's next-number routine (`0x00d19860`) queries
`select * from trantypeser where bt='…' and tran_type='…' and ser_val='…'` (with `ser_val` =
`yyyymm`) **before** falling back to `TranType.No_1`. Your app only reads `TranType.No_1`
(`CloneEngine.ReadCounters`).

In the database supplied, `TranTypeSer` has **0 rows**, so today the result is identical. In a
company that uses monthly / yearly series, your app would take a voucher number from the wrong
series and could then collide — but it would hit its own `VchExists` guard and roll back rather
than corrupt anything.

*Fix (applied):* `CloneEngine.ReadCounters` now asks `TranTypeSer` first using the **new voucher
date's** `yyyyMM`; when a period row is found it is the one read *and* the one bumped
(`UPDATE [TranTypeSer] SET [No_1]=? … AND [Ser_Val]=? AND [No_1]=<old>`), otherwise the plain
`TranType.No_1` path is used unchanged. The review screen shows which one was used. Two period rows
for the same book+month is treated as a data error and blocks posting, like the existing
"TranType rows found: N" check.

### 4.2 Special-voucher guards missing — *medium*
AccNew refuses to add/edit `Auto JV` (`Auto JV Can Not Add/Edit`), `Interest JV`
(`Interest JV Can Not Add/Edit`, `0x534ac8`) and multi-contra JVs
(`Multi - Contra Entry . . . Can't Save`, `0x534df4`). `CloneEngine.Validate` has no such rule, so
those vouchers can be cloned. Because a clone posts a *new* voucher rather than editing the
protected one, the accounting impact is limited — but AccNew itself would refuse to save the
result if it is again an Interest/Auto JV.

*Fix (applied):* `CloneEngine.Validate` now refuses (a) a series whose `TranType.Name` contains
`AUTO JV`, (b) one containing `INTEREST JV`, and (c) a Journal voucher with **more than two**
bank/cash lines (`Acc_type` 03/04) — the multi-contra case. Messages quote AccNew's own wording.

*Caveat, stated openly:* VB6 static analysis gave the exact **messages** but not the exact
**predicates**. (a) and (b) are read from the series name, which is how AccNew labels those books;
(c) assumes "multi-contra" = more than one pair of bank/cash accounts. If you know a voucher that
AccNew accepts but the tool now refuses, send it — the predicate is one line to adjust.

### 4.3 `INF.UserId` is not padded — *cosmetic*
AccNew stores a 5-character operator name (`'ACC  '`, `'OMY  '` — checked in live data and in
`FINDINGS.md` §3.1). Your app writes whatever the user typed, unpadded.

*Fix (applied):* `Helpers.PadUser` (trim, pad right to 5, never truncate) is used in
`PrepareFinal` when writing `INF.UserId`.

### 4.4 `EFF.Acc_Code2` on a newly added line — *low*
AccNew fills the "other side" account on every line (verified: `Acc_Code2` mirrors the contra in
live JV 439/440). Your `AddLedgerLine` (988) creates a blank row; `EffCellEdited` only writes
`Acc_Code2` from the grid cell, which starts empty. Cloned lines are fine; a **manually added**
line has no contra.

*Fix (applied):* new `CloneEngine.FillMissingContra` runs at the end of `RecalcGeneric` (so the
grid shows it immediately) and again at the start of `PrepareFinal` (authoritative). Any line with a
blank `Acc_Code2` gets the first account on the **opposite** Dr/Cr side — the verified 2-line rule,
generalised. Existing values are never overwritten; Sales vouchers are excluded because their line
roles (0 = party, 1 = income, 101+ = tax) follow different rules.

### 4.5 `Line_no` gaps after deleting a line — *low*
AccNew numbers JV lines `1..n` (live data: 1, 2). `DeleteEffGridLine` (1002) removes the row
without renumbering, so 1, 3 can be written.

*Fix (applied):* `PrepareFinal` renumbers non-Sales `EFF` lines `1..n` in current display order
(they are already sorted by `Line_no` for `Uid2` allocation). Skipped when the voucher has
`EFFLine` (cheque/bank detail) rows, because `EFFLine.Line_no` points at the ledger line it
describes and renumbering could re-target it.

### 4.6 `PriseList` row missing → price memory not updated — *low, already reported*
`Post` updates `PriseList` only when a row for `(ItemCode, party)` already exists, and writes a
note otherwise. AccNew creates the row when the item is first sold to that customer. The bill
itself is correct; only the "last rate" memory is skipped for a brand-new customer/item pair.

*Not fixed on purpose:* creating that row means inserting into a live master table whose required
columns are not fully proven by the byte-diff (the verified session only ever *updated* existing
rows). A wrong insert would be worse than a skipped one. If you want it, the safe way is to capture
one real "first sale to a new customer" in AccNew and add that row to the recipe — then it becomes
a 10-line change.

---

# Part 5 — "There are around 267 guards that show an error and stop before writing"

The **behaviour** you describe is correct and is now measurable: the voucher forms validate
everything first and only then write, so a failed check leaves the database untouched. The exact
**number** depends on what you count as a guard, and 267 does not fall out of any single measure I
can reproduce from the binary. Here is everything I measured, so you can pick the definition:

| Measure | Count |
|---|---|
| `rtcMsgBox` call sites in the whole `AccNew.exe` | **1,751** (in 1,015 functions) |
| Distinct strings that look like an error/validation message | **619** |
| Distinct *blocking-style* messages (`Not Found`, `Required`, `Invalid`, `Can't`, `Already Exist`, `Must be`, `Not Allowed`, `Excess`, `Duplicate` …) anywhere | **329** |
| Functions that touch voucher tables (`inf`, `eff`, `pth`, `ptl`, `billhist`, `payhist`, `itemtrn`) | **365** |
| Message boxes inside those functions | **385** |
| Distinct blocking messages used by those functions | **118** |
| **(function × blocking message) guard points inside voucher code** | **253** in 110 functions |
| Message boxes in the Journal-Voucher window alone (`0xd00000–0xd33000`) | **35** in 21 functions |

So: **253** if you count "a specific error message raised by a specific voucher routine" — the
closest thing to your 267; **329** if you count every blocking message in the program; **1,751** if
you count every dialog. Add the master-entry forms (ledger, item, party, tax code …) and the figure
moves again — which is why an exact "267" cannot be confirmed or refuted without your definition.

What *is* exactly verifiable is the thing that matters: **where the guards sit relative to the
writes.** The Journal-Voucher save routine `0x00d22c00` contains exactly **8** dialog call sites:

| # | Call site | Dialog | Stops the save? |
|---|---|---|---|
| 1 | `0xd2303e` | date outside `A/c Period : … To …` | yes |
| 2 | `0xd2313e` | `Do You Want To Save Current Entry ?` | confirmation, not an error |
| 3 | `0xd23459` | `Interest JV Can Not Add/Edit` | yes |
| 4 | `0xd2361f` | `Balance Not Equal` / `DEBIT And CREDIT  Must be Equal For JV` | yes |
| 5 | `0xd23764` | `Voucher No. Required` | yes |
| 6 | `0xd23919` | `Transaction Type Required` | yes |
| 7 | `0xd23b17` | `Restriction` (user permission) | yes |
| 8 | `0xd2418d` | `Voucher No. Already Exist` | yes |

The **first database write in that function** is the counter update at `0xd24248` (`no_1`) — i.e.
*after* all eight. The balance updates (`AccMst`, `0xd2514f` onwards), the parent roll-up
(`0xd25ec1`) and the child-row deletes (`0xd260fe`) come later still. The Bank/Cash save
`0x00761290` has **6** dialogs with the same structure (first write after them), and the second JV
variant `0x0174b370` has **8**.

That is the property your tool relies on, and it holds: **AccNew never writes half a voucher and
then complains.** It validates, then writes. Your `Post()` mirrors this (validate → one
transaction → commit/rollback), so a failed check in either program leaves `Accurate.Mdb`
unchanged.

**These guards are now implemented** — see [`GUARDS.md`](GUARDS.md). 71 of the 118 distinct
messages (173 of the 253 guard points) are enforced in `Core/AccNewGuards.cs`; the remaining 47
are delete/edit guards on masters, other modules, print/search dialogs or SQL fragments, each
listed with its reason. Every data-dependent guard was run against all 2,535 `EFF` lines, 243
bills and 136 `PayHist` rows in the supplied database and none of them rejects a voucher AccNew
itself created.

## Appendix A — Address map used above

| Address | Role | Proven by |
|---|---|---|
| `0x00d133c0` | voucher form init: DB connect, masters, permissions | connect string, `select * from control/trantype/AccMst`, `passline` |
| `0x00d19860` | next voucher number (TranTypeSer → TranType) | `select * from trantypeser … ser_val`, `no_1`, `#########0` |
| `0x00d0de10` `0x00d104b0` `0x00d10a20` `0x00d11110` | `{F4}` ledger lookup key handlers | `rtcSendKeys("{F4}")` |
| `0x00d12140` | account picker dialog | `CBOACC`, `{Tab}`, `Do You Want To Add New Master ?` |
| `0x00d0df10` | ledger lookup + show closing balance | `name = '`, `cl_bal`, `cl_dbcr`, ` Debit`/` Credit` |
| `0x00d0e9e0` | ledger re-validation, group check | `parent_code = '`, `Group Account Not Allowed In JV` |
| `0x00d0dac0` | amount parse | `Invalid Number` |
| `0x00d0a9e0` | bill adjustment limits | `Excess Amt. Not Allowed` |
| `0x00d111b0` | Enter (0x0D) → form method +0x79C | `if (*param_2 == 0xd)` |
| `0x00d11240` | find existing voucher | `Voucher Not Found` |
| `0x00d11bc0` `0x00d10550` `0x00d11f90` | Interest / Auto JV guards | `Interest JV Can Not Add/Edit`, `Auto JV Can Not Add/Edit` |
| `0x00d16350` | unsaved-changes guard on close | `Last Transaction Not Saved`, `Save Last Transaction` |
| `0x00d18460` | ID lookup | `ID-No. Not Found` |
| `0x00d1c500` | set mode Inquiry | `INQ.`, `11100111111` |
| `0x00d1c7d0` | edit existing (bill-2-bill warning) | `Jv Contain Bill2Bill Adj. Data…` |
| `0x00d1dce0` | delete voucher (reverses AccMst) | `Do You Want To Delete Current Record`, `db_amt`, `cl_bal`, `parent_code` |
| **`0x00d22c00`** | **SAVE — Journal Voucher** | `where bt = 'JV'`, full validation set |
| **`0x00761290`** | **SAVE — Bank / Cash** | `where bt = 'CB'`, `delete * from effline` |
| `0x0174b370` | SAVE — the second (newer) JV form | same 44 strings as `0x00d22c00` |

## Appendix B — How to reproduce this audit

```bash
# 1. the decompilation (4,760 functions, Ghidra 12.1.4)
unzip AccNew_DECOMPILATION_COMPLETE_SINGLE.zip -d accnew
#    accnew/AccNew_Resume_Output/{function_index,strings,string_xrefs,callgraph}.csv
#    accnew/AccNew_Resume_Output/functions/*.c        (4,550 files)

# 2. find the validation messages and who uses them
grep -i "debit\|voucher\|balance" accnew/AccNew_Resume_Output/strings.csv
#    …then look the string address up in string_xrefs.csv → function_address

# 3. read the live database
pip install access_parser
python3 -c "from access_parser import AccessParser; db=AccessParser('Accurate.Mdb'); print(len(db.catalog))"

# 4. the byte-level proof of what a manual save writes
unzip "Analysis of Accnew.exe.zip" -d analysis && less analysis/accnew_injection_kit/FINDINGS.md
```

**Bottom line:** your write path is faithful. The differences are all "UI-level" or "rare-data"
differences — the six items in §4 — not differences in how a voucher, its balances and its
counters end up in `Accurate.Mdb`.
