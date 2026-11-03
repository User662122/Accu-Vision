# AccuVisionRepost — clone & re-post AccNew vouchers

[![Build AccuVisionRepost.exe](https://github.com/User662122/Accu-Vision/actions/workflows/build.yml/badge.svg?branch=arena/7e336d4d-accu-vision)](https://github.com/User662122/Accu-Vision/actions/workflows/build.yml)

A small Windows desktop app that writes entries **directly into your existing
AccNew company database** (`Accurate.Mdb`), exactly the way AccNew itself
saves them.

**What it does:** find a previous entry (any book, any date in that company
year), load it, edit whatever you need (date, party, accounts, items, qty,
rate, tax %, narrations, cheque details…), and post it as a **brand-new
entry** — new voucher number, new ID number, balances, stock and bill
adjustments updated exactly as if you had typed the entry in AccNew.

**Vouchers supported**

1. **Transaction → Sales → Multiple Line** (`SL`) — incl. GST add-ons,
   round-off, TAX invoice number, stock, price-list memory, outstanding bill.
2. **Transaction → Bank** (`Bank-Cash Transaction`, `CB` — Withdrawal /
   Deposit / Transfer, incl. cheque/bank details) and **Bank/Cash
   Receipt & Payment** (`RE` / `PA`, incl. bill-wise adjustments).
3. **Transaction → Journal Voucher** (`JV`).

The original entry is **never modified or deleted**. Only a new entry is added.

---

## Getting the program (.exe)

**Easiest — download the ready exe from GitHub:**

1. Open this repository on GitHub → **Actions** tab.
2. Open the latest *Build AccuVisionRepost.exe* run.
3. Download the **AccuVisionRepost** artifact and unzip it. You get
   `AccuVisionRepost.exe` (+ `.config`).
4. Copy the exe anywhere (Desktop is fine) and double-click.

**Or build it yourself on your Windows PC (no Visual Studio needed):**

1. Download/clone this repo, then double-click **`build.bat`**.
2. The exe is created in the `dist\` folder.

Requirements: Windows with .NET Framework 4.8 (pre-installed on Windows 10/11).
The exe is 32-bit on purpose — the Jet (.mdb) driver is 32-bit only.

---

## How to use (step by step)

1. **Close AccNew completely.** (The app refuses to post while AccNew is
   running — AccNew caches voucher numbers in memory and could collide.)
2. Start `AccuVisionRepost.exe`.
3. At the top, select the company database **of the year you want to post
   into**: use *Browse…* for one `Accurate.Mdb`, or *Find in folder…* and
   pick your AccNew data folder to list all company-year databases.
   db password is `honda757acc` (prefilled). Optionally type your AccNew
   operator name in *UserId*. Click **CONNECT** and confirm the company name
   and financial year shown are correct.
4. **Tab 1 – Find Previous Entry:** choose voucher type (Sales / Bank /
   Journal), book, date range and/or text (party name, item, narration,
   voucher number). Click SEARCH, then double-click the entry you want to copy.
5. **Tab 2 – Edit New Entry:** the entry is loaded with a sensible new date
   (same day next month) and month references inside narrations already
   updated. Edit anything:
   - *Sales:* date, book, party (double-click to pick), item lines
     (qty/rate/narration), tax % in *Tax / Add-Ons*, TAX Invoice No mode
     (same as Vch No / keep gap / manual), header narration. Goods, GST,
     round-off, bill net and ledger lines are recalculated automatically,
     exactly like the Sales form.
   - *Journal / Bank:* accounts (double-click a code cell to search by
     name), Dr/Cr, amounts, narrations; add/delete lines; cheque number,
     bank name and cheque date in *Cheque / Bank Details*; bill adjustments
     in *Bill Adjustments* (receipts/payments).
   Errors (unbalanced voucher, group account, unknown item…) are shown in
   red below and posting stays disabled until they are fixed.
6. **Tab 3 – Review & Post:** shows the new voucher number, everything that
   will be inserted, every balance and stock that will move, and any bills
   that will be marked adjusted. Tick the confirmation box and click
   **POST NEW ENTRY INTO ACCNEW DATABASE**.
7. Open AccNew and check the new voucher (Transaction → modify/find, enter
   the new Vch No). Done.

**Safety built in**

- A dated backup copy of `Accurate.Mdb` is made first into
  `AccuVision_Backups\` next to the database.
- Posting happens in one database transaction — if anything fails halfway,
  **nothing** is written.
- New numbers (Vch No / ID_NO / UId) are re-checked inside that transaction,
  so an entry posted from another PC a second earlier cannot collide.
- Duplicated TAX Invoice Numbers are refused.
- The software's own save guards are enforced too: Dr = Cr, voucher number required
  and not already used, the date inside the company's A/c period, no group accounts,
  and the special vouchers AccNew itself refuses (Auto JV, Interest JV, multi-contra).
- Voucher numbers follow the same source AccNew uses: the period series
  (`TranTypeSer` for the new date's month) when the company keeps one, otherwise the
  book's `TranType.No_1`.
- Details are written the way AccNew writes them: the 5-character operator name in
  `INF.UserId`, the contra account on every ledger line (`EFF.Acc_Code2`) and ledger
  lines numbered 1..n.
- `docs/ENTRY-FLOW-AUDIT.md` traces the whole New Entry → Enter → Save path in the
  original software, function by function, and compares it with this tool.
- `docs/GUARDS.md` lists all 118 entry-time error messages found in the software's
  voucher routines, shows which 71 are enforced here (and where), and why the rest
  cannot apply. Each message the tool refuses with quotes the original text and the
  address it came from.
- Every posting writes a `PostLog_*.txt` report next to the backups.

**Undo:** if you ever post something wrong and notice immediately, close
AccNew, then restore by copying the backup `Accurate_BACKUP_*.mdb` from
`AccuVision_Backups\` back over `Accurate.Mdb` (only safe if nothing else
was entered in AccNew afterwards). Otherwise, pass a reversal entry in AccNew.

---

## First time? Please test on a copy first

This tool was built from a decompilation of AccNew plus byte-verified
posting recipes, but your live books deserve a test run:

1. Copy the whole company-year folder to a testing location.
2. Connect to the copied `Accurate.Mdb`, post one Sales, one Journal and one
   Bank entry.
3. Open **the copied database in AccNew** (point AccNew to the copied folder)
   and verify: voucher displays and re-saves normally, ledger day book,
   party outstanding, stock, GSTR/sales register.
4. Only then use it on the live database. See `docs/BANK-NOTES.md` for the
   detailed checklist.

---

## Desktop-only entry workflow

Load, edit, review and post entries through the desktop tabs. The former
Excel/script automation commands, batch posting and silent UI/auto-confirm
mode have been removed. Ledger Stories and its read-only exports remain.
Only scratch-database developer tests and the read-only `visual-report`
command remain available outside the desktop UI.

## For developers

- `src/AccuVisionRepost` — C# WinForms, .NET Framework 4.8, x86,
  `System.Data.OleDb` (Jet 4.0). Code is kept at C# 5 so the in-box Windows
  `csc.exe` can also build it (see `build.bat`).
- `docs/HOW-IT-WORKS.md` — the exact per-table posting recipe and where each
  rule came from in the decompilation.
- `docs/BANK-NOTES.md` — CB vs RE/PA evidence and the first-run verification.
- Built-in engine test on a scratch database:
  `AccuVisionRepost.exe /selftest %TEMP%` → must print `SELFTEST PASSED`.

### Continuous integration

`.github/workflows/build.yml` builds the app on `windows-latest` with MSBuild
(Release, x86 — the Jet OLEDB provider is 32-bit only) and then runs
`AccuVisionRepost.exe /selftest <temp dir>` on a scratch database it creates
itself. The job **fails the build** if the engine prints anything other than
`SELFTEST PASSED`, so the green tick on the commit means:

- the sources compile with no errors under the in-box toolchain, and
- the posting engine wrote JV / SL / CB vouchers that read back exactly as the
  self-test expects.

The self-test's verdict lines are also published as check-run annotations
(`gh api /repos/User662122/Accu-Vision/commits/<sha>/check-runs`), so the result
is readable without downloading the job log.

---

## Ledger Stories (read-only visualization)

The **4. Ledger Stories** tab adds a visual, question-first view for every ledger:

- Company overview: receivables, payables, advances/credits, sales vs purchases, monthly billing/cash trend, largest balances and attention list.
- Customers/tenants: billed sales vs receipts, item/service mix, current-year bill settlement, aging, opening vs current balance, unallocated cash and rate ranges.
- Suppliers: purchases vs payments, partial settlements, advances and pending bills.
- Banks/cash: money in/out, internal transfers separated from external flows, counterparties and balance trail.
- Income/expense: operating activity separated from year-end P&L transfers, counterparties, journals and monthly movement.
- Every ledger: balance bridge, monthly activity heatmap, source-voucher evidence and PNG/CSV/HTML export.

The visual engine opens Jet with `Mode=Read` and never posts or edits. Carried-forward duplicate bill references are flagged and excluded from current-bill totals; the account-master/opening balance remains authoritative. Bill settlement status is shown as stored in `BillHist` (including discounts in `Adj_Amt`), not inferred by blindly summing opening registers.

Developer checks and read-only ledger reports (no voucher posting automation):

```bat
AccuVisionRepost.exe /visualtest %TEMP%
AccuVisionRepost.exe visual-report --db "C:\path\Accurate.Mdb" --pwd **** --account A000001522 --out "C:\reports\lotus"
```

`visual-report` writes `<out>.json` and `<out>.png` without changing the database.
