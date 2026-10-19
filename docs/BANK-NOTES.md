# "Transaction → Bank" — what it is in AccNew, and what to verify first

## Two different things hide behind the word *Bank*

From the decompilation (`frmBankCash` / menu strings) there is **no voucher
type with BT `BK`**. The Bank menu points at two different families, and
AccuVisionRepost supports both:

1. **Bank-Cash Transaction** — menu *Transaction → Bank* opens `frmBankCash`
   ("Bank-Cash Transaction", save routine `0x00761290`), voucher type **`CB`**
   with TranTypes **01 Withdrawal / 02 Deposit / 03 Transfer**. It moves
   money between cash and bank ledgers (`Acc_type 03` = cash, `04` = bank).
   Data: `INF + EFF`, plus per-line cheque/bank detail in **`EFFLine`**
   (bankname, chqno, chqdate, bankdate…) and extra remarks in **`INFRem`**
   when used. The cloned entry is inserted with `BankDate` cleared — a fresh
   entry is unreconciled until you clear it in AccNew's bank reconciliation.
2. **Bank/Cash Receipt & Payment** — `frmRecPay2006`: receipts **`RE`**
   (e.g. 02 Bank Receipt) and payments **`PA`** (e.g. 05 Bank Payment) —
   money received from / paid to parties, usually with **bill adjustments**
   (`PayHist`). Cloning replays those adjustments against the live bills
   (`BillHist.Adj_Amt`, bill INF `Adj_Amt`), so the same party outstanding
   moves as in AccNew. Over-adjusting a bill is blocked.

Both are ordinary `INF/EFF` vouchers at the ledger level, which is why the
clone engine handles them through the same path as JV, with the side tables
above attached when present.

> ⚠️ **Bill adjustments caveat:** cloning a Receipt/Payment **re-applies its
> adjustments** (the bills named in `PayHist` get the same Adj amounts
> again). That's what "exactly like a new entry typed in AccNew" means, but
> check the *Bill Adjustments* tab and the Review screen list before
> posting, and edit/remove adjustments you don't want to repeat.

## First-run verification checklist (do this once, on a COPY)

The JV and Sales recipes were already byte-verified against manual AccNew
entries by the earlier injection kit. The Bank families were derived from
the decompiled `frmBankCash`/`frmRecPay2006` logic and the table census —
so before live use, verify on a copied company folder:

1. Copy the company-year folder; point both *AccuVisionRepost* and *AccNew*
   at the copy.
2. Post one **CB Deposit** clone (edit amount + cheque no), one **Bank
   Receipt (RE)** clone *without* bill adjustment, and one **Bank Payment
   (PA)** clone *with* a bill adjustment.
3. In AccNew on the copy, check each:
   - Voucher opens in its normal screen, amounts/cheque details correct.
   - Day book / ledger postings come right; trial balance unaffected
     beyond the entry.
   - For CB: entry appears as *unreconciled* in Bank Reconciliation.
   - For RE/PA with adjustment: party outstanding for the adjusted bill
     reduced by exactly the Adj amount; bill history shows the adjustment
     under the **new** voucher number.
   - Pass one more **manual** entry of the same book in AccNew after the
     cloned one — its voucher number must continue from the clone's number
     (proves the counters were advanced correctly).
4. Repeat the same checks for one **Sales (Multi Line)** clone (check GST
   amounts, stock of the item, sales register / GSTR figures, party
   outstanding) and one **JV** clone.
5. If any existing entry you want to clone is refused with *"also has rows
   in …"* (an extra table like `PTLL`/`TDS` is attached), note the voucher
   number and table — that table needs to be added to the clone list before
   that particular entry family can be re-posted safely.

After these pass on the copy, use the tool on the live database with the
built-in backup always on.
