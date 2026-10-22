# AccuVisionRepost — clone & re-post AccNew vouchers

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

## For developers

- `src/AccuVisionRepost` — C# WinForms, .NET Framework 4.8, x86,
  `System.Data.OleDb` (Jet 4.0). Code is kept at C# 5 so the in-box Windows
  `csc.exe` can also build it (see `build.bat`).
- `docs/HOW-IT-WORKS.md` — the exact per-table posting recipe and where each
  rule came from in the decompilation.
- `docs/BANK-NOTES.md` — CB vs RE/PA evidence and the first-run verification.
- Built-in engine test on a scratch database:
  `AccuVisionRepost.exe /selftest %TEMP%` → must print `SELFTEST PASSED`.
- Built-in headless WinForms UI test:
  `AccuVisionRepost.exe /uitest %TEMP%` → must print `UITEST PASSED`.
- Read-only database probe:
  `AccuVisionRepost.exe /probe <path-to-Accurate.Mdb> [Password]`

---

## Headless / Command-Line Mode (Excel-style automation, scripts & SSH)

`AccuVisionRepost.exe` works **completely headlessly** from `cmd.exe`,
PowerShell, batch scripts, VBA/LibreOffice macros, Python, or remote SSH
sessions (e.g., PhoneFS) — with **zero GUI windows or modal popups**, full
pre-post validation, detailed error/warning reporting, JSON/TSV/CSV output,
and `KEY=VALUE` status files.

Run `AccuVisionRepost.exe --help` to see all commands and flags.

### 1. Probe Company Database (`probe`)
Inspect company name, financial year, voucher series/books, next voucher
numbers, accounts, items, add-ons, and whether `AccNew.exe` is running:
```bat
AccuVisionRepost.exe probe --db "D:\AccNew\HTEPL\00012526\Accurate.Mdb" --accounts --items --addons
```

### 2. Search Vouchers (`search`)
Search Sales (`SL`), Journal (`JV`), Bank-Cash (`CB`), or Receipt/Payment
(`RE`/`PA`) entries and print as a table, TSV (for Excel), or JSON:
```bat
AccuVisionRepost.exe search --db "D:\AccNew\HTEPL\00012526\Accurate.Mdb" --bt SL --latest-month --format tsv --out bills.tsv
```

### 3. Inspect & Export Editable JSON Spec (`inspect`)
Inspect any voucher in full detail and optionally export an editable JSON
specification:
```bat
AccuVisionRepost.exe inspect --db "D:\AccNew\HTEPL\00012526\Accurate.Mdb" --bt SL --vch 132 --export bill_132.json
```

### 4. Headless Review / Dry-Run Validation (`review`)
Clone a voucher in memory, apply edits (date, +months, party, item, qty, rate,
GST %, accounts, narration, cheque details, or `--spec file.json`), run all
validation + live database checks (including duplicate `TAX_INV_NO` and
`BillHist` pending-balance checks), and print the full review plan **without
writing to the database**:
```bat
AccuVisionRepost.exe review --db "D:\AccNew\HTEPL\00012526\Accurate.Mdb" ^
  --bt SL --vch 132 --new-date 2026-05-20 --rate 250 ^
  --status-file status.txt --json-out review.json
```

### 5. Single Headless Post (`post`)
Validate, back up `Accurate.Mdb`, post the new voucher inside a single atomic
transaction, verify the inserted rows, and write `PostLog_*.txt`:
```bat
AccuVisionRepost.exe post --db "D:\AccNew\HTEPL\00012526\Accurate.Mdb" ^
  --bt SL --vch 132 --plus-months 1 --rate 250 ^
  --status-file status.txt --json-out post.json
```

### 6. Batch / Excel / TSV Two-Step Workflow (`batch-review` & `batch-post`)
Just like the Excel/LibreOffice `BuildReview_Next` → `PerformEntry_Post`
workflow, you can export a whole month of bills to an Excel-compatible
`.tsv` or `.csv`, edit rates/dates/narrations or set `Select=N` on rows to
skip, and post the batch headlessly:
```bat
REM Step 1: Build next month's review table (TSV/CSV opens directly in Excel)
AccuVisionRepost.exe batch-review --db "D:\AccNew\HTEPL\00012526\Accurate.Mdb" ^
  --bt SL --latest-month --plus-months 1 --export-batch next_month_review.tsv

REM Step 2: Post all selected (Select=Y) rows from the reviewed TSV/CSV
AccuVisionRepost.exe batch-post --db "D:\AccNew\HTEPL\00012526\Accurate.Mdb" ^
  --batch-file next_month_review.tsv --out-batch posted_results.tsv --status-file batch_status.txt
```

### Error Reporting & Exit Codes
Every headless command reports errors and warnings to:
1. **Standard Output (`stdout`)** — full human-readable report (or structured JSON with `--json`).
2. **Standard Error (`stderr`)** — concise `ERROR:` lines whenever validation or execution fails.
3. **JSON (`--json` or `--json-out <file.json>`)** — includes `"ok": true|false`, `"exitCode"`, `"errors": [...]`, `"warnings": [...]`, and full voucher/plan details.
4. **Status File (`--status-file <file.txt>`)** — `KEY=VALUE` lines (`STATUS=OK|ERROR`, `EXIT_CODE=0|1|2|3`, `ERROR=...`, `NEW_VCH=...`, `NEW_ID_NO=...`, `BACKUP=...`) for easy parsing from `.bat` or VBA.

| Exit Code | Meaning |
|---|---|
| `0` | Success (Probe OK / Search OK / Review Valid / Posted OK / SelfTest Passed) |
| `1` | Runtime or database connection/IO error (e.g., missing `.mdb` file) |
| `2` | Invalid command-line arguments or malformed spec file |
| `3` | Validation or pre-post check error (unbalanced Dr/Cr, duplicate `TAX_INV_NO`, group account, out-of-FY date, `AccNew.exe` running, or warning under `--strict`) |
