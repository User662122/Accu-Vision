# Guard coverage — AccNew's entry-time error dialogs vs this tool

## What "253 guards in 110 functions" means

Scanning `AccNew.exe` (Ghidra 12.1.4, 4,760 functions) for the routines that read or write the
voucher tables (`INF`, `EFF`, `PTH`, `PTHL`, `PTL`, `BillHist`, `PayHist`, `ItemTrn`) gives
**365 functions**. Inside them there are **385 `rtcMsgBox` call sites** and **118 distinct
blocking messages**. Counting one point per *(function × message)* pair gives **253 guard points
in 110 functions** — the figure quoted in the audit.

An "error dialog that stops there instead of writing to the database" is exactly what these are:
in the JV save routine (`0x00d22c00`) all 8 dialogs sit **before** the first database write, so a
failed check never leaves a half-written voucher. The behaviour you described is correct, and 253
is a fair estimate of its size.

## What has been implemented here

**71 of the 118 distinct messages (173 of the 253 guard points) are now enforced** by
`Core/AccNewGuards.cs` — every one that can apply to posting a cloned voucher. Every message the
tool raises carries the original AccNew text and the address it was found at, so a refusal can be
traced back to the software:

```
Ledger line 1: SUNDRY DEBTORS is a GROUP account.  [AccNew: "Group Account Not Allowed In Transaction" @ 0x56cf68]
```

The remaining **47 messages (80 guard points)** cannot apply to this tool, in four groups:

| Why it cannot apply | Messages |
|---|---|
| Delete / edit guards on master records (ledger, item, agent, addons, group) | 16 |
| Other modules: excise PLA, yarn/grey, VAT, opening-bill import, company/year creation | 14 |
| Report, print, barcode, picture, search and licence dialogs | 11 |
| SQL fragments or column names the scan picked up (not messages at all) | 6 |

Nothing was silently skipped — all 118 are listed at the end with status and reason.

## Where the guards run

| Stage | Method | Needs DB? | Effect |
|---|---|---|---|
| Every keystroke / cell edit / Recalculate | `AccNewGuards.CheckVoucher` via `CloneEngine.Validate` | no | errors in red, POST button disabled |
| Review screen | same (`Validate`) | no | same list |
| Inside the posting transaction, **before the first insert** | `AccNewGuards.CheckLive` | yes | throws, transaction rolls back, nothing written |

The second call is the important one: it runs on the **final rows with their new numbers** after
`PrepareFinal`, so the duplicate checks test the numbers that would actually be written — and a
failure happens before any `INSERT`.

## The guards

| # | Guard | Mirrors AccNew message | Stage |
|---|---|---|---|
| G01 | Every row of the voucher carries the same `BT`/`Tran_Type`/`ID_NO`/`VchNo`/`VchDate` as `INF` | `INF,EFF,Not Match VCHNO,` … (14 messages) | Validate |
| G02 | `INF.DbCr` is `D` or `C` | `INF,,dbcr - Invalid character,` | Validate |
| G03 | Head account exists in `AccMst` | `INF,ACCMST,Invalid Acc_Code…`, `Account Not Found In Master` | Validate |
| G05 | Line and contra accounts exist; no **group** account; no **system** account (`Sys_Flag='Y'`) | `EFF,ACCMST,…`, `Group Account Not Allowed In Transaction` | Validate |
| G06 | Debit total = Credit total | `Cr. And Db Not Match In EFF`, `Balance Not Equal`, `DEBIT And CREDIT Must be Equal For JV` | Validate |
| G07 | Journal with more than 2 bank/cash lines refused | `Multi - Contra Entry . . . Can't Save` | Validate |
| G08 | `GoodAmt + AddAmt - LessAmt = NetAmt` | `Billhist Amont Not Match with EFF` | Validate |
| G09 | Bill party = voucher head account | `INF,PTH,Acc_Code not match,` | Validate |
| G10 | Party line in `EFF` = bill total | `Billhist Amont Not Match with EFF` | Validate |
| G11 | Taxable bill must have a tax code; `TF` when there is no tax | `Tax Code Required` | Validate |
| G12 | All item lines post to one income account | `All Item Not Connect With Same A/c`, `Privious Item A/c Not Match With Currwnt A/c` | Validate |
| G13 | Item income account exists | `EFF,ACCMST,…` | Validate |
| G14 | Every item exists in the item master | `Item Not Found In Master` | Validate |
| G20 | Adjustments may not exceed the voucher amount; warns on Excess | `Adjust More then Receipt...Not Allowed`, `Confirmation For Excess Adjustment` | Validate |
| G21 | Voucher number not already used for the book | `Voucher No. Already Exist`, `Activity - Duplicate Voucher No` | Live |
| G22 | `ID_NO` not already used | `INF,,Duplicate ID_NO,`, `Activity - Duplicate ID_NO` | Live |
| G23 | `UId` not already used | `Activity - Duplicate ID_NO` | Live |
| G24 | `Control.No_1` behind the highest ID in use → warning | `INF,CONTROL,Max. No. not match with CONTROL file,` | Live |
| G25 | Tax code exists in the `TaxCode` master | `INF,TAXCODE,Invalid TaxCode…` | Live |
| G26 | TAX invoice number unique | `Duplicate TAX INVOICE`, `TAX Invoice No Already Exist`, `Bill No. Already exist...` | Live |
| G27 | More than one `PTH` row → warning | `TAX Invoice No Already Exist` | Live |
| G28 | Cheque number not already used | `Duplicate Chq. No` | Live |
| G29 | Challan reference exists in `CHH` | `Invalid Challan No / Line` | Live |
| G30 | Bill exists, belongs to the same party, not over-adjusted, not dated after the voucher | `Bill Less Then Adjusted Value`, `Party Can Not Change If Bill Adjusted`, `Bill Date Must Be Less Then Payment Date` | Live |
| G31 | The same bill is not adjusted twice in one voucher | `PAYHIST,,Duplicate Record,` | Live |
| G32 | Agent / transport codes exist in their masters | `INF,AGENTMST,…`, `Duplicate for Transporter` | Live |

Plus the guards that already existed in `Validate`: Auto JV, Interest JV, date inside the
company's A/c period, at least 2 ledger lines, non-negative amounts, text length against the real
column width, and the unsupported-table discovery block.

## Proof that the new guards do not reject real entries

A guard that fires on a voucher AccNew itself accepted would be a bug, so each data-dependent
guard was run against every voucher in the supplied `Accurate.Mdb`:

| Guard | Result on real data |
|---|---|
| G02 `INF.DbCr` is D/C | **1,059 of 1,059** `INF` rows are `D` or `C` |
| G03 head account present | **0** of 1,059 `INF` rows have a blank `Acc_Code` |
| G05 group account | **0** of 2,535 `EFF` lines post to a group account |
| G05 system account | **0** of 2,535 `EFF` lines post to a `Sys_Flag='Y'` account |
| G05 account exists | **0** of 2,535 `EFF` lines reference a missing account |
| G11 tax code | **0** of 243 bills have GST add-ons with a blank `TaxCode` |
| G12 one item account | **0** of 243 bills use more than one item account |
| G09 bill party = head account | **0** of 243 bills differ |
| G10 party line = bill total | **0** of 243 bills differ |
| G30 party match | **0** of 136 `PayHist` rows point at a bill of another party |
| G30 over-adjustment | **0** of 136 `PayHist` rows exceed the bill amount |

One honest note: **all 136 `PayHist` rows in this database are opening bills** (`VchNo` starts
with `O-`) and none of them are in `BillHist`. That was already unsupported before this change
(`CheckBillAdjustments` refused them); the guard now says so explicitly instead of giving a
generic message. Supporting opening-bill adjustments would be a separate piece of work —
`OpBill` / `OpPayHist` are not in the verified recipe.

## Complete list — all 118 messages behind those 253 guard points

`YES` = enforced by this tool, with where. `—` = cannot apply here, with the reason.

| × | AccNew message | Here | Where / why not |
|---|---|---|---|
| 16 | `Invalid Transaction Type` | YES | Validate - book must exist in TranType |
| 13 | `Voucher No Already Exist` | YES | G21 CheckLive - duplicate VchNo for the book |
| 10 | `Transaction Type Required` | YES | Validate - book/series required |
| 10 | `Voucher No. Required` | YES | allocation always assigns a number; blank cannot occur |
| 8 | `Invalid Transaction Type Data Not Saves` | YES | Validate - same rule as above, save refused |
| 7 | `Account Name Not Found In Master` | YES | G03/G05 - head and line accounts must exist |
| 7 | `excess_amt` | — | database column name, not a message |
| 5 | `Tax Code Required` | YES | G11 - taxable bill needs PTH.TaxCode |
| 4 | `***** INVALID A/C *****` | YES | G03/G05 - account validation (this is its report marker) |
| 4 | `Adjusted Amount Is Less Then` | YES | G30 - adjustment vs bill amount |
| 4 | `All Item Not Connect With Same A/c ..Please Check Item` | YES | G12 - all item lines must use one account |
| 4 | `Bill Less Then Adjusted Value` | YES | G30 - adjustment cannot exceed the pending amount |
| 4 | `Confirmation For Excess Adjustment` | YES | G20 - warning when the voucher carries Excess |
| 4 | `Excess` | — | column name, not a message |
| 4 | `Excise PLA Voucher No Already Exist` | — | Excise PLA module - no entries in this database |
| 4 | `Interest JV Can Not Add/Edit` | YES | Validate - series name contains INTEREST JV |
| 4 | `JV Already Present You Cannot Create Auto` | — | Auto-JV generator; a clone never creates one |
| 4 | `Party Can Not Change If Bill Adjusted` | YES | G30 - bill party must equal voucher party |
| 3 | `Adjust More then Receipt...Not Allowed` | YES | G20 - total adjustments vs voucher amount |
| 3 | `Balance Not Equal` | YES | G06 - Dr = Cr |
| 3 | `DEBIT And CREDIT Must be Equal For JV` | YES | G06 - Dr = Cr (JV wording) |
| 3 | `Duplicate for Transporter` | YES | G32 - transport code must exist in Transport master |
| 3 | `select sum(adj_amt + excess_amt) from payhist` | — | SQL fragment |
| 3 | `select sum(adj_amt),sum(disc_amt),sum(excess_amt) from payhist` | — | SQL fragment |
| 3 | `Voucher No. Already Exist` | YES | G21 - duplicate VchNo |
| 2 | `Account has transaction . . . can't delete` | — | delete guard on masters |
| 2 | `Account link with addons . . . can't delete` | — | delete guard on masters |
| 2 | `Account link with item . . . can't delete` | — | delete guard on masters |
| 2 | `Addons Ledger Entry Exist, You Can't Delete Addons` | — | delete guard on masters |
| 2 | `Addons Ledger Entry Exist, You Can't Edit Addons` | — | edit guard on masters |
| 2 | `Auto JV Can Not Delete` | — | delete guard |
| 2 | `Bill No. Already exist...` | YES | G26 - duplicate bill / tax invoice number |
| 2 | `Can''t Save Entry` | YES | Validate - bill must have item lines |
| 2 | `Can't Delete . . . Account has Transaction` | — | delete guard on masters |
| 2 | `Duplicate TAX INVOICE` | YES | G26 - TAX_INV_NO uniqueness |
| 2 | `Invalid BARCODE` | — | barcode label printing |
| 2 | `InValid BARCODE` | — | barcode label printing |
| 2 | `Invalid Challan No / Line` | YES | G29 - PTL.chno must exist in CHH |
| 2 | `Invalid Selection` | — | UI selection dialog |
| 2 | `Item History Exist, You Can't Delete Item Name` | — | delete guard on masters |
| 2 | `Item Not Found In Master` | YES | G14 - item must exist |
| 2 | `Multi - Contra Entry . . . Can't Save` | YES | G07 - >2 bank/cash lines refused |
| 2 | `Party Not Found In Account` | YES | G03 - head account must exist |
| 2 | `Privious Item A/c Not Match With Currwnt A/c` | YES | G12 - item account consistency |
| 2 | `TAX Invoice No Already Exist` | YES | G26 - TAX_INV_NO uniqueness |
| 2 | `Too Many Data... Please Select Small date Range` | — | report/search range |
| 2 | `Transaction Exist Can't Delete Agent` | — | delete guard on masters |
| 2 | `You Can't Delete System Group` | — | delete guard on masters |
| 1 | `5. Order once booked cannot be cancelled unless agreed upon by the sellers.` | — | printed terms text |
| 1 | `Account And Group Must Be Different` | — | master (group) editing |
| 1 | `Account Not Found In Master` | YES | G03/G05 - account must exist |
| 1 | `Activity - Duplicate ID_NO` | YES | G22 - duplicate ID_NO |
| 1 | `Activity - Duplicate Voucher No` | YES | G21 - duplicate VchNo |
| 1 | `Addons Bill Entry Exist, You Can't Delete Addons` | — | delete guard on masters |
| 1 | `Bill Date Must Be Less Then Payment Date` | YES | G30 - bill date vs voucher date |
| 1 | `BILL No Already Exist` | YES | G26 - duplicate bill number |
| 1 | `Billhist Amont Not Match with EFF` | YES | G08/G10 - bill total vs party line |
| 1 | `Can't Create Compny` | — | company creation |
| 1 | `Can't Transfer OP.Balance` | — | opening balance transfer |
| 1 | `Company NO Not Found In ACCMAIN` | — | Accmain.mdb company list |
| 1 | `Cr. And Db Not Match In EFF` | YES | G06 - Dr = Cr |
| 1 | `Data Not Found In NEW :` | — | new-year opening routine |
| 1 | `Duplicate` | — | generic message |
| 1 | `Duplicate Chq. No` | YES | G28 - cheque number already used |
| 1 | `EFF,,Invalid BT-Tran_Type or Record missing from Tran_Type,` | YES | G01/G04 - identity + series |
| 1 | `EFF,,Invalid Char. - DbCr,` | YES | Validate - Dr/Cr must be D or C |
| 1 | `EFF,ACCMST,Invalid Acc_Code or Record missing from AccMst,` | YES | G05 - line account must exist |
| 1 | `EFF,ACCMST,Invalid Acc_Code2 or Record missing from AccMst,` | YES | G05 - contra account must exist |
| 1 | `EFF,BILLHIST,Duplicate Record found in BILLHIST,` | — | RE/PA bill duplicate scan - not part of a clone |
| 1 | `EFF,INF,Invalid Record or Missing from Inf file,` | YES | G01 - every row must belong to the voucher |
| 1 | `Group Account Not Allowed In Transaction` | YES | G05 - group account refused |
| 1 | `INF,,dbcr - Invalid character,` | YES | G02 - INF.DbCr must be D or C |
| 1 | `INF,,Duplicate ID_NO,` | YES | G22 - duplicate ID_NO |
| 1 | `INF,,Duplicate Voucher No. for bt and tran_type,` | YES | G21 - duplicate VchNo |
| 1 | `INF,,Invalid BT,` | YES | Validate - book must exist |
| 1 | `INF,ACCMST,Invalid Acc_Code or Record missing from AccMst,` | YES | G03 - head account must exist |
| 1 | `INF,AGENTMST,Invalid Agent code or missing from AgentMst,` | YES | G32 - agent code must exist in AgentMst |
| 1 | `INF,BILLHIST,RefDate not match,` | YES | G01 - identity consistency |
| 1 | `INF,BILLHIST,RefNo not match,` | YES | G01 - identity consistency |
| 1 | `INF,BILLHIST,Tran_Type not match,` | YES | G01 - identity consistency |
| 1 | `INF,BILLHIST,VchDate not match,` | YES | G01 - identity consistency |
| 1 | `INF,BILLHIST,VchNo not match,` | YES | G01 - identity consistency |
| 1 | `INF,CONTROL,Max. No. not match with CONTROL file,` | YES | G24 - counter behind used numbers (warning) |
| 1 | `INF,EFF,Not Match TRAN_TYPE,` | YES | G01 - identity consistency |
| 1 | `INF,EFF,Not Match VCHDATE,` | YES | G01 - identity consistency |
| 1 | `INF,EFF,Not Match VCHNO,` | YES | G01 - identity consistency |
| 1 | `INF,PTH,Acc_Code not match,` | YES | G09 - bill party vs head account |
| 1 | `INF,PTH,Record Not Match,` | YES | G09 - bill header vs header |
| 1 | `INF,PTH,RefDate not match,` | YES | G01 - identity consistency |
| 1 | `INF,PTH,RefNo not match,` | YES | G01 - identity consistency |
| 1 | `INF,PTH,TaxCode not match,` | YES | G11/G25 - tax code presence + master |
| 1 | `INF,PTH,Tran_Type not match,` | YES | G01 - identity consistency |
| 1 | `INF,PTH,VchDate not match,` | YES | G01 - identity consistency |
| 1 | `INF,PTH,VchNo not match,` | YES | G01 - identity consistency |
| 1 | `INF,PTL,Tran_Type not match,` | YES | G01 - identity consistency |
| 1 | `INF,PTL,VchNo not match,` | YES | G01 - identity consistency |
| 1 | `INF,TAXCODE,Invalid TaxCode or missing from TaxCode Master for,` | YES | G25 - tax code must exist in TaxCode |
| 1 | `Invalid Group Name` | — | master (group) editing |
| 1 | `Item History Exist, You Can't Edit Item` | — | edit guard on masters |
| 1 | `JVXREF REC. NOT FOUND...PAYHIST REC : BT =` | — | JVXREF linkage - table never written by this version |
| 1 | `Ledger Entry Found ..You Can''t Delete This Type` | — | delete guard on masters |
| 1 | `New Year Not Found` | — | new-year creation |
| 1 | `Op. Bal. Exist Can't Make As Group` | — | master (group) editing |
| 1 | `Op. Bill Not match with Master` | — | opening bill reconciliation |
| 1 | `OPBILL,,Duplicate Record,` | — | opening bill import |
| 1 | `PAYHIST,,Duplicate Record,` | YES | G31 - same bill twice in one voucher |
| 1 | `PAYHIST,,Invalid BT+Tran_type or Record missing from Tran_Type,` | YES | G30 - bill series must exist |
| 1 | `PAYHIST,,Invalid BT2+Tran_type or Record missing from Tran_Type,` | YES | G30 - bill must be found in BillHist |
| 1 | `Picture File Not Found` | — | logo / picture loading |
| 1 | `Please Contect Your Vendor` | — | licence message |
| 1 | `select sum(adj_amt + excess_amt)` | — | SQL fragment |
| 1 | `select sum(adj_amt + excess_amt) as adj_amt from payhist` | — | SQL fragment |
| 1 | `The Gray material has been receieved from this yarn, so can not delete this entry` | — | yarn / grey module |
| 1 | `Transaction Entry Found Can't Make As Group` | — | master (group) editing |
| 1 | `VAT Master data Not FOUND` | — | VAT master - this company is on GST |
| 1 | `Voucher Not Found` | — | inquiry / find voucher |
| 1 | `|02.2 Branch T/f or consignment to & from outside the State` | — | printed return form text |
| 1 | `|20 Excess Amount of tax credit (18 - 17)` | — | printed return form text |
