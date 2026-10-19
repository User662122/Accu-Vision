using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using AccuVisionRepost.Core;

namespace AccuVisionRepost.Ui
{
    public sealed class MainForm : Form
    {
        // ---- connection bar
        private TextBox _dbPath, _pwd, _userId;
        private Label _companyLbl, _statusLbl;

        // ---- find tab
        private ComboBox _kindCombo, _bookCombo;
        private DateTimePicker _fromDtp, _toDtp;
        private TextBox _searchTxt;
        private DataGridView _resultsGrid;
        private List<VoucherSummary> _results = new List<VoucherSummary>();

        // ---- edit tab
        private Label _srcLbl;
        private DateTimePicker _newDateDtp, _refDateDtp;
        private ComboBox _newBookCombo, _taxInvModeCombo;
        private TextBox _narrationTxt, _partyCodeTxt, _partyNameTxt, _taxCodeTxt, _payModeTxt, _crDaysTxt, _taxInvTxt, _refNoTxt, _headTxt;
        private CheckBox _autoTaxChk, _bumpNarChk;
        private DataGridView _effGrid, _itemGrid, _addonGrid, _effLineGrid, _payHistGrid;
        private TabControl _editTabs;
        private TabPage _tabEff, _tabItems, _tabAddons, _tabCheque, _tabAdj;
        private Label _totalsLbl;
        private ListBox _msgList;

        // ---- review tab
        private TextBox _reviewTxt;
        private CheckBox _backupChk, _confirmChk;
        private Label _accNewLbl;
        private Button _postBtn;

        private TabControl _tabs;
        private TabPage _tabEdit, _tabReview;

        private CompanyDb _db;
        private CloneEngine _engine;
        private LoadedVoucher _v;
        private bool _syncing;
        private object _editingOldValue;
        private string _srcHeaderNarration = "";
        private string _lastAutoNarration = "";
        private Dictionary<DataRow, string> _srcLineNarrations = new Dictionary<DataRow, string>();
        private Dictionary<DataRow, string> _lastAutoLineNarrations = new Dictionary<DataRow, string>();

        public MainForm()
        {
            Text = "AccuVisionRepost - AccNew Voucher Clone & Re-Post (Sales Multi Line / Bank / Journal)";
            Width = 1240; Height = 860;
            MinimumSize = new Size(980, 640);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9f);
            BuildUi();
            LoadSettings();
        }

        // ================================================================ layout

        private void BuildUi()
        {
            Panel top = new Panel();
            top.Dock = DockStyle.Top;
            top.Height = 86;
            top.Padding = new Padding(8);
            top.BackColor = Color.FromArgb(238, 242, 247);

            Label l1 = new Label(); l1.Text = "Company database (Accurate.Mdb):"; l1.Location = new Point(8, 10); l1.AutoSize = true;
            _dbPath = new TextBox(); _dbPath.Location = new Point(8, 30); _dbPath.Width = 560;
            Button browse = new Button(); browse.Text = "Browse..."; browse.Location = new Point(574, 28); browse.Width = 86;
            browse.Click += delegate { BrowseDb(); };
            Button find = new Button(); find.Text = "Find in folder..."; find.Location = new Point(664, 28); find.Width = 112;
            find.Click += delegate { FindDatabases(); };
            Label l2 = new Label(); l2.Text = "DB Password:"; l2.Location = new Point(786, 10); l2.AutoSize = true;
            _pwd = new TextBox(); _pwd.Location = new Point(786, 30); _pwd.Width = 130; _pwd.Text = "honda757acc";
            Label l3 = new Label(); l3.Text = "Operator (UserId):"; l3.Location = new Point(926, 10); l3.AutoSize = true;
            _userId = new TextBox(); _userId.Location = new Point(926, 30); _userId.Width = 90;
            Button connect = new Button(); connect.Text = "CONNECT"; connect.Location = new Point(1026, 26); connect.Width = 110; connect.Height = 30;
            connect.Font = new Font(Font, FontStyle.Bold);
            connect.Click += delegate { ConnectDb(); };
            _companyLbl = new Label(); _companyLbl.Location = new Point(8, 58); _companyLbl.AutoSize = true;
            _companyLbl.Font = new Font(Font, FontStyle.Bold);
            _companyLbl.Text = "Not connected. Select Accurate.Mdb of the company + financial year you want to post into.";
            _statusLbl = new Label(); _statusLbl.Location = new Point(700, 58); _statusLbl.AutoSize = true; _statusLbl.ForeColor = Color.DarkRed;

            top.Controls.AddRange(new Control[] { l1, _dbPath, browse, find, l2, _pwd, l3, _userId, connect, _companyLbl, _statusLbl });
            Controls.Add(top);

            _tabs = new TabControl();
            _tabs.Dock = DockStyle.Fill;
            Controls.Add(_tabs);
            _tabs.BringToFront(); // Fill control must be top of z-order (docked last)

            TabPage tabFind = new TabPage("1. Find Previous Entry");
            _tabEdit = new TabPage("2. Edit New Entry");
            _tabReview = new TabPage("3. Review & Post");
            _tabs.TabPages.Add(tabFind);
            _tabs.TabPages.Add(_tabEdit);
            _tabs.TabPages.Add(_tabReview);
            _tabs.Selected += delegate(object s, TabControlEventArgs e)
            {
                if (e.TabPage == _tabReview) RefreshReview();
            };

            BuildFindTab(tabFind);
            BuildEditTab(_tabEdit);
            BuildReviewTab(_tabReview);
        }

        private void BuildFindTab(TabPage tab)
        {
            int y = 12;
            Label l = new Label(); l.Text = "Voucher:"; l.Location = new Point(10, y + 3); l.AutoSize = true; tab.Controls.Add(l);
            _kindCombo = new ComboBox(); _kindCombo.Location = new Point(70, y); _kindCombo.Width = 270; _kindCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            _kindCombo.Items.AddRange(new object[] {
                "Sales - Multi Line (SL)", "Bank - Cash Transaction (CB)", "Bank/Cash Receipt-Payment (RE/PA)", "Journal Voucher (JV)", "Any of the above" });
            _kindCombo.SelectedIndex = 0;
            _kindCombo.SelectedIndexChanged += delegate { FillBookCombo(); };
            tab.Controls.Add(_kindCombo);

            Label l2 = new Label(); l2.Text = "Book:"; l2.Location = new Point(356, y + 3); l2.AutoSize = true; tab.Controls.Add(l2);
            _bookCombo = new ComboBox(); _bookCombo.Location = new Point(400, y); _bookCombo.Width = 340; _bookCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            tab.Controls.Add(_bookCombo);

            Label l3 = new Label(); l3.Text = "From:"; l3.Location = new Point(756, y + 3); l3.AutoSize = true; tab.Controls.Add(l3);
            _fromDtp = new DateTimePicker(); _fromDtp.Location = new Point(800, y); _fromDtp.Width = 120; _fromDtp.Format = DateTimePickerFormat.Custom; _fromDtp.CustomFormat = "dd-MM-yyyy";
            tab.Controls.Add(_fromDtp);
            Label l4 = new Label(); l4.Text = "To:"; l4.Location = new Point(930, y + 3); l4.AutoSize = true; tab.Controls.Add(l4);
            _toDtp = new DateTimePicker(); _toDtp.Location = new Point(958, y); _toDtp.Width = 120; _toDtp.Format = DateTimePickerFormat.Custom; _toDtp.CustomFormat = "dd-MM-yyyy";
            tab.Controls.Add(_toDtp);

            y += 36;
            Button prevMonth = new Button(); prevMonth.Text = "Previous month"; prevMonth.Location = new Point(70, y); prevMonth.Width = 130;
            prevMonth.Click += delegate
            {
                DateTime first = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1).AddMonths(-1);
                _fromDtp.Value = first;
                _toDtp.Value = first.AddMonths(1).AddDays(-1);
            };
            tab.Controls.Add(prevMonth);

            Label l5 = new Label(); l5.Text = "Search (party / item / narration / Vch No):"; l5.Location = new Point(220, y + 3); l5.AutoSize = true; tab.Controls.Add(l5);
            _searchTxt = new TextBox(); _searchTxt.Location = new Point(472, y); _searchTxt.Width = 300;
            _searchTxt.KeyDown += delegate(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) DoSearch(); };
            tab.Controls.Add(_searchTxt);

            Button searchBtn = new Button(); searchBtn.Text = "SEARCH PREVIOUS ENTRIES"; searchBtn.Location = new Point(790, y - 2); searchBtn.Width = 220; searchBtn.Height = 28;
            searchBtn.Font = new Font(Font, FontStyle.Bold);
            searchBtn.Click += delegate { DoSearch(); };
            tab.Controls.Add(searchBtn);

            _resultsGrid = MakeGrid(false);
            _resultsGrid.Location = new Point(10, y + 40);
            _resultsGrid.Size = new Size(1180, 560);
            _resultsGrid.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            _resultsGrid.ReadOnly = true;
            _resultsGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            AddCol(_resultsGrid, "Date", 100, true);
            AddCol(_resultsGrid, "Book", 210, true);
            AddCol(_resultsGrid, "Vch No", 80, true);
            AddCol(_resultsGrid, "Party / Head Account", 300, true);
            AddCol(_resultsGrid, "Amount (Rs.)", 130, true);
            AddCol(_resultsGrid, "Narration", 330, true);
            _resultsGrid.CellDoubleClick += delegate(object s, DataGridViewCellEventArgs e) { LoadSelected(); };
            tab.Controls.Add(_resultsGrid);

            Button loadBtn = new Button(); loadBtn.Text = "LOAD SELECTED ENTRY FOR EDITING  ->";
            loadBtn.Location = new Point(790, y + 610); loadBtn.Width = 400; loadBtn.Height = 32;
            loadBtn.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            loadBtn.Font = new Font(Font, FontStyle.Bold);
            loadBtn.Click += delegate { LoadSelected(); };
            tab.Controls.Add(loadBtn);

            Label note = new Label();
            note.Text = "Tip: entries from ANY period of this company-year database can be loaded. To re-post into a different year, connect to that year's Accurate.Mdb first - voucher numbers always come from the connected database.";
            note.Location = new Point(10, y + 612); note.Size = new Size(760, 32);
            note.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            tab.Controls.Add(note);
        }

        private void BuildEditTab(TabPage tab)
        {
            Panel head = new Panel();
            head.Dock = DockStyle.Top; head.Height = 188; head.Padding = new Padding(8);
            head.BackColor = Color.FromArgb(244, 246, 250);
            tab.Controls.Add(head);

            _srcLbl = new Label(); _srcLbl.Location = new Point(8, 6); _srcLbl.AutoSize = true;
            _srcLbl.Font = new Font(Font, FontStyle.Bold);
            _srcLbl.Text = "No entry loaded yet - use tab 1.";
            head.Controls.Add(_srcLbl);

            int y = 32;
            Label l = new Label(); l.Text = "New Date:"; l.Location = new Point(8, y + 3); l.AutoSize = true; head.Controls.Add(l);
            _newDateDtp = new DateTimePicker(); _newDateDtp.Location = new Point(80, y); _newDateDtp.Width = 120;
            _newDateDtp.Format = DateTimePickerFormat.Custom; _newDateDtp.CustomFormat = "dd-MM-yyyy";
            _newDateDtp.ValueChanged += delegate { OnNewDateChanged(); };
            head.Controls.Add(_newDateDtp);
            Button plusM = new Button(); plusM.Text = "+1 month"; plusM.Location = new Point(206, y - 2); plusM.Width = 80;
            plusM.Click += delegate { if (_v != null) _newDateDtp.Value = Helpers.AddOneMonthClamped(_newDateDtp.Value); };
            head.Controls.Add(plusM);
            Button today = new Button(); today.Text = "Today"; today.Location = new Point(290, y - 2); today.Width = 60;
            today.Click += delegate { _newDateDtp.Value = DateTime.Today; };
            head.Controls.Add(today);

            Label lb = new Label(); lb.Text = "Book for new entry:"; lb.Location = new Point(370, y + 3); lb.AutoSize = true; head.Controls.Add(lb);
            _newBookCombo = new ComboBox(); _newBookCombo.Location = new Point(496, y); _newBookCombo.Width = 330; _newBookCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            _newBookCombo.SelectedIndexChanged += delegate { OnBookChanged(); };
            head.Controls.Add(_newBookCombo);

            _bumpNarChk = new CheckBox(); _bumpNarChk.Text = "Auto-update month/dates inside narration"; _bumpNarChk.Location = new Point(844, y); _bumpNarChk.AutoSize = true; _bumpNarChk.Checked = true;
            head.Controls.Add(_bumpNarChk);

            y += 32;
            Label ln = new Label(); ln.Text = "Narration:"; ln.Location = new Point(8, y + 3); ln.AutoSize = true; head.Controls.Add(ln);
            _narrationTxt = new TextBox(); _narrationTxt.Location = new Point(80, y); _narrationTxt.Width = 746;
            _narrationTxt.Leave += delegate { SyncHeader(); RecalcAndRefresh(); };
            head.Controls.Add(_narrationTxt);
            _headTxt = new TextBox(); _headTxt.Location = new Point(844, y); _headTxt.Width = 350; _headTxt.ReadOnly = true;
            head.Controls.Add(_headTxt);

            y += 32;
            Label lp = new Label(); lp.Name = "partyLbl"; lp.Text = "Party:"; lp.Location = new Point(8, y + 3); lp.AutoSize = true; head.Controls.Add(lp);
            _partyCodeTxt = new TextBox(); _partyCodeTxt.Name = "partyCode"; _partyCodeTxt.Location = new Point(80, y); _partyCodeTxt.Width = 110;
            _partyCodeTxt.Leave += delegate { PartyCodeEdited(); };
            head.Controls.Add(_partyCodeTxt);
            _partyNameTxt = new TextBox(); _partyNameTxt.Name = "partyName"; _partyNameTxt.Location = new Point(194, y); _partyNameTxt.Width = 290; _partyNameTxt.ReadOnly = true;
            head.Controls.Add(_partyNameTxt);
            Button pickP = new Button(); pickP.Name = "partyPick"; pickP.Text = "Pick..."; pickP.Location = new Point(488, y - 2); pickP.Width = 60;
            pickP.Click += delegate
            {
                AccountRow a = LookupDialog.PickAccount(this, _db.State, _partyNameTxt.Text);
                if (a != null) { _partyCodeTxt.Text = a.Code; PartyCodeEdited(); }
            };
            head.Controls.Add(pickP);

            Label lt = new Label(); lt.Name = "taxInvLbl"; lt.Text = "TAX Inv No:"; lt.Location = new Point(566, y + 3); lt.AutoSize = true; head.Controls.Add(lt);
            _taxInvModeCombo = new ComboBox(); _taxInvModeCombo.Name = "taxInvMode"; _taxInvModeCombo.Location = new Point(648, y); _taxInvModeCombo.Width = 170; _taxInvModeCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            _taxInvModeCombo.Items.AddRange(new object[] { "Same as new Vch No", "Keep source gap", "Manual" });
            _taxInvModeCombo.SelectedIndex = 0;
            _taxInvModeCombo.SelectedIndexChanged += delegate
            {
                if (_v != null) _v.TaxInvMode = _taxInvModeCombo.SelectedIndex;
                _taxInvTxt.Enabled = _taxInvModeCombo.SelectedIndex == 2;
                UpdateTaxInvPreview();
            };
            head.Controls.Add(_taxInvModeCombo);
            _taxInvTxt = new TextBox(); _taxInvTxt.Name = "taxInv"; _taxInvTxt.Location = new Point(822, y); _taxInvTxt.Width = 130; _taxInvTxt.Enabled = false;
            head.Controls.Add(_taxInvTxt);

            Label lref = new Label(); lref.Name = "refLbl"; lref.Text = "Ref No / Date:"; lref.Location = new Point(964, y + 3); lref.AutoSize = true; head.Controls.Add(lref);
            _refNoTxt = new TextBox(); _refNoTxt.Name = "refNo"; _refNoTxt.Location = new Point(1056, y); _refNoTxt.Width = 80;
            head.Controls.Add(_refNoTxt);
            _refDateDtp = new DateTimePicker(); _refDateDtp.Name = "refDate"; _refDateDtp.Location = new Point(1140, y); _refDateDtp.Width = 110;
            _refDateDtp.Format = DateTimePickerFormat.Custom; _refDateDtp.CustomFormat = "dd-MM-yyyy"; _refDateDtp.ShowCheckBox = true;
            head.Controls.Add(_refDateDtp);

            y += 32;
            Label ltc = new Label(); ltc.Name = "taxCodeLbl"; ltc.Text = "Tax Code:"; ltc.Location = new Point(8, y + 3); ltc.AutoSize = true; head.Controls.Add(ltc);
            _taxCodeTxt = new TextBox(); _taxCodeTxt.Name = "taxCode"; _taxCodeTxt.Location = new Point(80, y); _taxCodeTxt.Width = 70;
            head.Controls.Add(_taxCodeTxt);
            _autoTaxChk = new CheckBox(); _autoTaxChk.Name = "autoTax"; _autoTaxChk.Text = "Auto"; _autoTaxChk.Location = new Point(156, y); _autoTaxChk.AutoSize = true; _autoTaxChk.Checked = true;
            head.Controls.Add(_autoTaxChk);
            Label lpm = new Label(); lpm.Name = "payLbl"; lpm.Text = "Pay Mode:"; lpm.Location = new Point(220, y + 3); lpm.AutoSize = true; head.Controls.Add(lpm);
            _payModeTxt = new TextBox(); _payModeTxt.Name = "payMode"; _payModeTxt.Location = new Point(292, y); _payModeTxt.Width = 60;
            head.Controls.Add(_payModeTxt);
            Label lcr = new Label(); lcr.Name = "crLbl"; lcr.Text = "Credit days:"; lcr.Location = new Point(366, y + 3); lcr.AutoSize = true; head.Controls.Add(lcr);
            _crDaysTxt = new TextBox(); _crDaysTxt.Name = "crDays"; _crDaysTxt.Location = new Point(446, y); _crDaysTxt.Width = 50;
            head.Controls.Add(_crDaysTxt);

            Button recalc = new Button(); recalc.Text = "RECALCULATE"; recalc.Location = new Point(540, y - 2); recalc.Width = 140;
            recalc.Click += delegate { SyncAllGrids(); SyncHeader(); RecalcAndRefresh(); };
            head.Controls.Add(recalc);
            Button toReview = new Button(); toReview.Text = "REVIEW & POST  ->"; toReview.Location = new Point(690, y - 2); toReview.Width = 170;
            toReview.Font = new Font(Font, FontStyle.Bold);
            toReview.Click += delegate { SyncAllGrids(); SyncHeader(); RecalcAndRefresh(); _tabs.SelectedTab = _tabReview; };
            head.Controls.Add(toReview);

            // messages + totals at bottom
            Panel bottom = new Panel();
            bottom.Dock = DockStyle.Bottom; bottom.Height = 112;
            tab.Controls.Add(bottom);
            _totalsLbl = new Label(); _totalsLbl.Dock = DockStyle.Top; _totalsLbl.Height = 24; _totalsLbl.Font = new Font(Font, FontStyle.Bold);
            bottom.Controls.Add(_totalsLbl);
            _msgList = new ListBox(); _msgList.Dock = DockStyle.Fill;
            bottom.Controls.Add(_msgList);
            _msgList.BringToFront();

            _editTabs = new TabControl();
            _editTabs.Dock = DockStyle.Fill;
            tab.Controls.Add(_editTabs);
            _editTabs.BringToFront(); // Fill control must be top of z-order (docked last)

            _tabEff = new TabPage("Ledger Lines");
            _tabItems = new TabPage("Item Lines (Sales)");
            _tabAddons = new TabPage("Tax / Add-Ons (Sales)");
            _tabCheque = new TabPage("Cheque / Bank Details");
            _tabAdj = new TabPage("Bill Adjustments");

            _effGrid = BuildEffGrid(_tabEff);
            _itemGrid = BuildItemGrid(_tabItems);
            _addonGrid = BuildAddonGrid(_tabAddons);
            _effLineGrid = MakeGrid(true); _effLineGrid.Dock = DockStyle.Fill; _tabCheque.Controls.Add(_effLineGrid);
            AddToolbar(_tabCheque, "Add cheque line", "Delete cheque line", AddEffLine, DeleteEffLine);
            _payHistGrid = MakeGrid(true); _payHistGrid.Dock = DockStyle.Fill; _tabAdj.Controls.Add(_payHistGrid);
            AddToolbar(_tabAdj, null, "Remove this adjustment", null, DeletePayHist);

            _editTabs.TabPages.Add(_tabEff);
        }

        private void BuildReviewTab(TabPage tab)
        {
            _reviewTxt = new TextBox();
            _reviewTxt.Location = new Point(10, 10);
            _reviewTxt.Size = new Size(1180, 480);
            _reviewTxt.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            _reviewTxt.Multiline = true;
            _reviewTxt.ScrollBars = ScrollBars.Both;
            _reviewTxt.ReadOnly = true;
            _reviewTxt.Font = new Font("Consolas", 9.5f);
            tab.Controls.Add(_reviewTxt);

            _accNewLbl = new Label(); _accNewLbl.Location = new Point(10, 500); _accNewLbl.AutoSize = true; _accNewLbl.Font = new Font(Font, FontStyle.Bold);
            _accNewLbl.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            tab.Controls.Add(_accNewLbl);
            Button recheck = new Button(); recheck.Text = "Re-check AccNew closed + refresh numbers"; recheck.Location = new Point(620, 496); recheck.Width = 300;
            recheck.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            recheck.Click += delegate { RefreshReview(); };
            tab.Controls.Add(recheck);

            _backupChk = new CheckBox(); _backupChk.Text = "Take a dated backup copy of Accurate.Mdb before posting (recommended)";
            _backupChk.Location = new Point(10, 528); _backupChk.AutoSize = true; _backupChk.Checked = true;
            _backupChk.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            tab.Controls.Add(_backupChk);

            _confirmChk = new CheckBox(); _confirmChk.Text = "I have checked the date, accounts, items and amounts above, and AccNew is closed. Post this as a NEW entry.";
            _confirmChk.Location = new Point(10, 552); _confirmChk.AutoSize = true;
            _confirmChk.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _confirmChk.CheckedChanged += delegate { UpdatePostEnabled(); };
            tab.Controls.Add(_confirmChk);

            _postBtn = new Button(); _postBtn.Text = "POST NEW ENTRY INTO AccNew DATABASE";
            _postBtn.Location = new Point(10, 584); _postBtn.Width = 560; _postBtn.Height = 44;
            _postBtn.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            _postBtn.Font = new Font(Font.FontFamily, 12f, FontStyle.Bold);
            _postBtn.BackColor = Color.FromArgb(198, 239, 206);
            _postBtn.Enabled = false;
            _postBtn.Click += delegate { DoPost(); };
            tab.Controls.Add(_postBtn);
        }

        // ================================================================ grid builders

        private static DataGridView MakeGrid(bool editable)
        {
            DataGridView g = new DataGridView();
            g.AllowUserToAddRows = false;
            g.AllowUserToDeleteRows = false;
            g.AllowUserToResizeRows = false;
            g.RowHeadersWidth = 24;
            g.AutoGenerateColumns = false;
            g.EditMode = editable ? DataGridViewEditMode.EditOnKeystrokeOrF2 : DataGridViewEditMode.EditProgrammatically;
            g.BackgroundColor = Color.White;
            return g;
        }

        private static void AddCol(DataGridView g, string header, int width, bool readOnly)
        {
            DataGridViewTextBoxColumn c = new DataGridViewTextBoxColumn();
            c.HeaderText = header; c.Name = header; c.Width = width; c.ReadOnly = readOnly;
            g.Columns.Add(c);
        }

        private void AddToolbar(TabPage tab, string addText, string delText, Action onAdd, Action onDelete)
        {
            Panel p = new Panel(); p.Dock = DockStyle.Bottom; p.Height = 34; tab.Controls.Add(p); p.SendToBack(); // edge-docked: must be carved before the Fill grid
            int x = 4;
            if (addText != null)
            {
                Button b = new Button(); b.Text = addText; b.Location = new Point(x, 4); b.Width = 180;
                b.Click += delegate { if (onAdd != null) onAdd(); };
                p.Controls.Add(b); x += 188;
            }
            if (delText != null)
            {
                Button b = new Button(); b.Text = delText; b.Location = new Point(x, 4); b.Width = 200;
                b.Click += delegate { if (onDelete != null) onDelete(); };
                p.Controls.Add(b);
            }
        }

        private DataGridView BuildEffGrid(TabPage tab)
        {
            DataGridView g = MakeGrid(true);
            g.Dock = DockStyle.Fill;
            AddCol(g, "Line", 50, true);
            AddCol(g, "Account Code", 120, false);
            AddCol(g, "Account Name", 330, true);
            DataGridViewComboBoxColumn dc = new DataGridViewComboBoxColumn();
            dc.HeaderText = "Dr/Cr"; dc.Name = "DrCr"; dc.Width = 60;
            dc.Items.AddRange(new object[] { "D", "C" });
            dc.FlatStyle = FlatStyle.Standard;
            g.Columns.Add(dc);
            AddCol(g, "Amount", 130, false);
            AddCol(g, "Narration", 330, false);
            AddCol(g, "Other A/c", 120, false);
            AddCol(g, "Other A/c Name", 220, true);
            g.CellBeginEdit += delegate(object s, DataGridViewCellCancelEventArgs e) { _editingOldValue = g.Rows[e.RowIndex].Cells[e.ColumnIndex].Value; };
            g.CellEndEdit += delegate(object s, DataGridViewCellEventArgs e) { EffCellEdited(e.RowIndex, e.ColumnIndex); };
            g.CellDoubleClick += delegate(object s, DataGridViewCellEventArgs e)
            {
                if (e.RowIndex < 0) return;
                if (e.ColumnIndex == 1 || e.ColumnIndex == 2 || e.ColumnIndex == 6 || e.ColumnIndex == 7)
                {
                    int codeCol = (e.ColumnIndex <= 2) ? 1 : 6;
                    AccountRow a = LookupDialog.PickAccount(this, _db.State, Convert.ToString(g.Rows[e.RowIndex].Cells[codeCol + 1].Value));
                    if (a != null)
                    {
                        _editingOldValue = g.Rows[e.RowIndex].Cells[codeCol].Value;
                        g.Rows[e.RowIndex].Cells[codeCol].Value = a.Code;
                        EffCellEdited(e.RowIndex, codeCol);
                    }
                }
            };
            tab.Controls.Add(g);
            AddToolbar(tab, "Add ledger line", "Delete ledger line", AddLedgerLine, DeleteEffGridLine);
            return g;
        }

        private DataGridView BuildItemGrid(TabPage tab)
        {
            DataGridView g = MakeGrid(true);
            g.Dock = DockStyle.Fill;
            AddCol(g, "Line", 50, true);
            AddCol(g, "Item Code", 90, false);
            AddCol(g, "Item Name", 300, true);
            AddCol(g, "Qty", 90, false);
            AddCol(g, "Nos", 80, false);
            AddCol(g, "Unit", 60, false);
            AddCol(g, "Rate", 120, false);
            AddCol(g, "Value", 130, true);
            AddCol(g, "Narration", 300, false);
            AddCol(g, "Income A/c", 120, false);
            g.CellEndEdit += delegate(object s, DataGridViewCellEventArgs e) { ItemCellEdited(e.RowIndex, e.ColumnIndex); };
            g.CellDoubleClick += delegate(object s, DataGridViewCellEventArgs e)
            {
                if (e.RowIndex < 0) return;
                if (e.ColumnIndex == 1 || e.ColumnIndex == 2)
                {
                    ItemRow it = LookupDialog.PickItem(this, _db.State, Convert.ToString(g.Rows[e.RowIndex].Cells[2].Value));
                    if (it != null)
                    {
                        g.Rows[e.RowIndex].Cells[1].Value = it.ItemCode;
                        ItemCellEdited(e.RowIndex, 1);
                    }
                }
                if (e.ColumnIndex == 9)
                {
                    AccountRow a = LookupDialog.PickAccount(this, _db.State, "");
                    if (a != null) { g.Rows[e.RowIndex].Cells[9].Value = a.Code; ItemCellEdited(e.RowIndex, 9); }
                }
            };
            tab.Controls.Add(g);
            AddToolbar(tab, "Add item line", "Delete item line", AddItemLine, DeleteItemLine);
            return g;
        }

        private DataGridView BuildAddonGrid(TabPage tab)
        {
            DataGridView g = MakeGrid(true);
            g.Dock = DockStyle.Fill;
            AddCol(g, "Line", 50, true);
            AddCol(g, "Add-On", 70, true);
            AddCol(g, "Name", 220, true);
            AddCol(g, "+ / -", 50, false);
            AddCol(g, "%", 80, false);
            AddCol(g, "Amount", 130, false);
            AddCol(g, "On (Ass. Value)", 130, false);
            AddCol(g, "Account Code", 120, false);
            AddCol(g, "Account Name", 280, true);
            g.CellEndEdit += delegate(object s, DataGridViewCellEventArgs e) { AddonCellEdited(e.RowIndex, e.ColumnIndex); };
            g.CellDoubleClick += delegate(object s, DataGridViewCellEventArgs e)
            {
                if (e.RowIndex >= 0 && (e.ColumnIndex == 7 || e.ColumnIndex == 8))
                {
                    AccountRow a = LookupDialog.PickAccount(this, _db.State, Convert.ToString(g.Rows[e.RowIndex].Cells[8].Value));
                    if (a != null) { g.Rows[e.RowIndex].Cells[7].Value = a.Code; AddonCellEdited(e.RowIndex, 7); }
                }
            };
            tab.Controls.Add(g);
            AddToolbar(tab, "Add tax / charge", "Delete tax / charge", AddAddonLine, DeleteAddonLine);
            return g;
        }

        // ================================================================ connection

        private void BrowseDb()
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = "Select Accurate.Mdb (company financial-year database)";
                d.Filter = "AccNew database (Accurate.Mdb)|Accurate.Mdb|Access database (*.mdb)|*.mdb|All files (*.*)|*.*";
                if (d.ShowDialog() == DialogResult.OK) _dbPath.Text = d.FileName;
            }
        }

        private void FindDatabases()
        {
            using (FolderBrowserDialog d = new FolderBrowserDialog())
            {
                d.Description = "Pick the AccNew data root folder (for example the HTEPL folder). All company-year Accurate.Mdb files below it will be listed.";
                if (d.ShowDialog() != DialogResult.OK) return;
                List<string> found = new List<string>();
                ScanDb(d.SelectedPath, 0, found);
                if (found.Count == 0)
                {
                    MessageBox.Show(this, "No Accurate.Mdb found under that folder.", "Find database", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                string picked = PickFromList("Company databases found", found);
                if (picked != null) _dbPath.Text = picked;
            }
        }

        private void ScanDb(string dir, int depth, List<string> found)
        {
            if (depth > 4 || found.Count > 300) return;
            try
            {
                foreach (string f in Directory.GetFiles(dir, "*.Mdb"))
                    if (Path.GetFileName(f).Equals("Accurate.Mdb", StringComparison.OrdinalIgnoreCase)) found.Add(f);
                foreach (string sub in Directory.GetDirectories(dir)) ScanDb(sub, depth + 1, found);
            }
            catch { }
        }

        private string PickFromList(string title, List<string> items)
        {
            using (Form f = new Form())
            {
                f.Text = title; f.Width = 820; f.Height = 420; f.StartPosition = FormStartPosition.CenterParent;
                ListBox lb = new ListBox(); lb.Dock = DockStyle.Fill;
                lb.Items.AddRange(items.ToArray());
                string result = null;
                lb.DoubleClick += delegate { result = lb.SelectedItem as string; f.DialogResult = DialogResult.OK; f.Close(); };
                Button ok = new Button(); ok.Text = "Select"; ok.Dock = DockStyle.Bottom;
                ok.Click += delegate { result = lb.SelectedItem as string; f.DialogResult = DialogResult.OK; f.Close(); };
                f.Controls.Add(lb); f.Controls.Add(ok);
                return f.ShowDialog(this) == DialogResult.OK ? result : null;
            }
        }

        private void ConnectDb()
        {
            string path = _dbPath.Text.Trim();
            if (path.Length == 0 || !File.Exists(path))
            {
                MessageBox.Show(this, "First choose the Accurate.Mdb file.", "Connect", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try
            {
                Cursor = Cursors.WaitCursor;
                _db = new CompanyDb(path, _pwd.Text);
                DbProbe p = _db.Probe();
                _engine = new CloneEngine(_db);
                _companyLbl.Text = p.CompanyName + "    FY " + Helpers.FmtDate(p.FyFrom) + " to " + Helpers.FmtDate(p.FyTo)
                                     + "    Accounts " + p.Accounts.Count + "  Items " + p.Items.Count;
                if (p.FyFrom != DateTime.MinValue) { _fromDtp.Value = p.FyFrom; _toDtp.Value = p.FyTo; }
                FillBookCombo();
                UpdateStatus();
                SaveSettings();
                MessageBox.Show(this, "Connected to:\r\n" + p.CompanyName + "\r\nFinancial year " + Helpers.FmtDate(p.FyFrom) + " to " + Helpers.FmtDate(p.FyTo),
                    "Connected", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                _db = null; _engine = null;
                MessageBox.Show(this, "Could not open the database:\r\n\r\n" + ex.Message +
                    "\r\n\r\nCheck: (1) path is correct, (2) Password is honda757acc, (3) this is a 32-bit process (exe must be x86), (4) the file is not open exclusively elsewhere.",
                    "Connect failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { Cursor = Cursors.Default; }
        }

        private void UpdateStatus()
        {
            bool running = CloneEngine.IsAccNewRunning();
            bool locked = _db != null && File.Exists(CloneEngine.LockFilePath(_db.DbPath));
            _statusLbl.Text = running ? "AccNew is RUNNING - close it before posting."
                : (locked ? "Note: database lock file is present - make sure AccNew is closed before posting." : "AccNew appears closed. Posting is safe.");
            _statusLbl.ForeColor = running ? Color.DarkRed : Color.DarkGreen;
            _accNewLbl.Text = _statusLbl.Text;
            _accNewLbl.ForeColor = _statusLbl.ForeColor;
        }

        private string[] SelectedBTs()
        {
            switch (_kindCombo.SelectedIndex)
            {
                case 0: return new string[] { "SL" };
                case 1: return new string[] { "CB" };
                case 2: return new string[] { "RE", "PA" };
                case 3: return new string[] { "JV" };
                default: return new string[] { "SL", "CB", "RE", "PA", "JV" };
            }
        }

        private void FillBookCombo()
        {
            _bookCombo.Items.Clear();
            _bookCombo.Items.Add("(All books)");
            _bookCombo.SelectedIndex = 0;
            if (_db == null || _db.State == null) return;
            string[] bts = SelectedBTs();
            foreach (SeriesRow s in _db.State.Series)
            {
                bool ok = false;
                foreach (string b in bts) if (b.Equals(s.BT, StringComparison.OrdinalIgnoreCase)) ok = true;
                if (ok) _bookCombo.Items.Add(s);
            }
        }

        // ================================================================ search + load

        private void DoSearch()
        {
            if (_engine == null) { MessageBox.Show(this, "Connect to the database first.", "Search", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            try
            {
                Cursor = Cursors.WaitCursor;
                SeriesRow book = _bookCombo.SelectedItem as SeriesRow;
                _results = _engine.Search(SelectedBTs(), book == null ? null : book.TranType, _fromDtp.Value, _toDtp.Value, _searchTxt.Text);
                _resultsGrid.Rows.Clear();
                foreach (VoucherSummary s in _results)
                {
                    int i = _resultsGrid.Rows.Add(Helpers.FmtDate(s.VchDate), s.BookName, s.VchNoTrim, s.AccName, Helpers.FmtAmt(s.Amount), s.Narration);
                    _resultsGrid.Rows[i].Tag = s;
                }
                if (_results.Count == 0)
                    MessageBox.Show(this, "No previous entries match. Check the period, book and search text.", "Search", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex) { ShowError(ex); }
            finally { Cursor = Cursors.Default; }
        }

        private void LoadSelected()
        {
            if (_resultsGrid.SelectedRows.Count == 0) return;
            VoucherSummary s = _resultsGrid.SelectedRows[0].Tag as VoucherSummary;
            if (s == null) return;
            try
            {
                Cursor = Cursors.WaitCursor;
                LoadedVoucher v = _engine.Load(s.UId);
                v.NewTaxInvNo = "";
                if (!string.IsNullOrEmpty(_userId.Text.Trim())) v.UserId = _userId.Text.Trim();
                _v = v;
                LoadVoucherIntoUi();
                _tabs.SelectedTab = _tabEdit;
                if (v.UnsupportedExtras.Count > 0)
                    MessageBox.Show(this,
                        "This source entry also has rows in table(s) this version does not clone: " + string.Join(", ", v.UnsupportedExtras.ToArray()) +
                        "\r\nYou can view it, but POSTING is blocked so nothing is silently left behind. Pick another entry, or get this table added (see docs/BANK-NOTES.md - First-run verification).",
                        "Extra linked data", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch (Exception ex) { ShowError(ex); }
            finally { Cursor = Cursors.Default; }
        }

        private void LoadVoucherIntoUi()
        {
            _syncing = true;
            try
            {
                _srcLbl.Text = "Source: " + _v.SourceLabel + "   ->   posting as a NEW " + VoucherKinds.Title(_v.Kind);
                _newDateDtp.Value = _v.NewDate;

                _newBookCombo.Items.Clear();
                foreach (SeriesRow s in _db.State.Series)
                    if (s.BT.Equals(_v.SrcBT, StringComparison.OrdinalIgnoreCase))
                    {
                        int i = _newBookCombo.Items.Add(s);
                        if (s.TranType.Equals(_v.SrcTranType, StringComparison.OrdinalIgnoreCase)) _newBookCombo.SelectedIndex = i;
                    }

                DataRow inf = _v.Inf;
                _srcHeaderNarration = JetDb.GetStr(inf, "Naration");
                _lastAutoNarration = Helpers.BumpNarration(_srcHeaderNarration, _v.SrcDate, _v.NewDate);
                JetDb.SetVal(inf, "Naration", _lastAutoNarration.Length == 0 ? (object)DBNull.Value : _lastAutoNarration);
                _narrationTxt.Text = _lastAutoNarration;
                _refNoTxt.Text = JetDb.IsNull(inf, "RefNo") ? "" : JetDb.GetStr(inf, "RefNo").Trim();
                if (!JetDb.IsNull(inf, "RefDate")) { _refDateDtp.Checked = true; _refDateDtp.Value = JetDb.GetDate(inf, "RefDate", _v.NewDate); }
                else _refDateDtp.Checked = false;

                _srcLineNarrations.Clear();
                _lastAutoLineNarrations.Clear();
                foreach (DataRow r in _v.Rows("PTL"))
                {
                    string src = JetDb.GetStr(r, "Naration");
                    _srcLineNarrations[r] = src;
                    string bumped = Helpers.BumpNarration(src, _v.SrcDate, _v.NewDate);
                    _lastAutoLineNarrations[r] = bumped;
                    JetDb.SetVal(r, "Naration", bumped);
                }

                bool isSales = _v.Kind == VoucherKind.Sales;
                SetSalesHeaderVisible(isSales);
                if (isSales && _v.Pth != null)
                {
                    _partyCodeTxt.Text = JetDb.GetStr(_v.Pth, "Acc_Code").Trim();
                    _partyNameTxt.Text = _db.State.AccountName(_partyCodeTxt.Text);
                    _taxCodeTxt.Text = JetDb.GetStr(_v.Pth, "TaxCode").Trim();
                    _payModeTxt.Text = JetDb.GetStr(_v.Pth, "PayMode").Trim();
                    _crDaysTxt.Text = JetDb.GetInt(_v.Pth, "cr_day").ToString();
                    _taxInvModeCombo.SelectedIndex = 0;
                    _taxInvTxt.Text = "";
                    _headTxt.Text = "Source TAX Inv No: " + JetDb.GetStr(_v.Pth, "TAX_INV_NO");
                }
                else
                {
                    _headTxt.Text = "Head A/c: " + JetDb.GetStr(inf, "Acc_Code").Trim() + "  " + _db.State.AccountName(JetDb.GetStr(inf, "Acc_Code").Trim());
                }

                // inner tabs per kind
                _editTabs.TabPages.Clear();
                _editTabs.TabPages.Add(_tabEff);
                if (isSales) { _editTabs.TabPages.Add(_tabItems); _editTabs.TabPages.Add(_tabAddons); }
                if (_v.T("EFFLine") != null) _editTabs.TabPages.Add(_tabCheque);
                if (_v.T("PayHist") != null) _editTabs.TabPages.Add(_tabAdj);

                FillEffGrid();
                if (isSales) { FillItemGrid(); FillAddonGrid(); }
                BindBoundGrid(_effLineGrid, _v.T("EFFLine"));
                BindBoundGrid(_payHistGrid, _v.T("PayHist"));
            }
            finally { _syncing = false; }
            RecalcAndRefresh();
            UpdateTaxInvPreview();
        }

        private void SetSalesHeaderVisible(bool sales)
        {
            string[] names = new string[] { "partyLbl", "partyCode", "partyName", "partyPick", "taxInvLbl", "taxInvMode", "taxInv", "taxCodeLbl", "taxCode", "autoTax", "payLbl", "payMode", "crLbl", "crDays" };
            foreach (Control c in _narrationTxt.Parent.Controls)
            {
                foreach (string n in names)
                    if (c.Name == n) c.Visible = sales;
            }
        }

        private void BindBoundGrid(DataGridView g, DataTable t)
        {
            g.AutoGenerateColumns = true;
            g.DataSource = null;
            if (t == null) return;
            g.DataSource = new BindingSource(t, "");
            g.AutoResizeColumns(DataGridViewAutoSizeColumnsMode.AllCells);
            foreach (DataGridViewColumn c in g.Columns) if (c.Width > 220) c.Width = 220;
        }

        // ================================================================ grids fill

        private void FillEffGrid()
        {
            _syncing = true;
            try
            {
                _effGrid.Rows.Clear();
                List<DataRow> rows = _v.Rows("EFF");
                rows.Sort(delegate(DataRow a, DataRow b) { return JetDb.GetInt(a, "Line_no").CompareTo(JetDb.GetInt(b, "Line_no")); });
                foreach (DataRow r in rows)
                {
                    string acc = JetDb.GetStr(r, "Acc_Code").Trim();
                    string acc2 = JetDb.GetStr(r, "Acc_Code2").Trim();
                    int i = _effGrid.Rows.Add(
                        JetDb.GetInt(r, "Line_no"), acc, _db.State.AccountName(acc),
                        JetDb.GetStr(r, "DbCr").Trim().ToUpperInvariant(),
                        Helpers.FmtNumRaw(JetDb.GetDbl(r, "Tran_amt")),
                        JetDb.GetStr(r, "Naration"), acc2, _db.State.AccountName(acc2));
                    _effGrid.Rows[i].Tag = r;
                }
                bool sales = _v.Kind == VoucherKind.Sales;
                // sales ledger lines are produced by the recalculation; keep narration editable
                foreach (DataGridViewRow gr in _effGrid.Rows)
                {
                    for (int c = 0; c < gr.Cells.Count; c++)
                        gr.Cells[c].ReadOnly = sales && c != 5;
                }
                _effGrid.Columns[3].ReadOnly = sales;
            }
            finally { _syncing = false; }
        }

        private void FillItemGrid()
        {
            _syncing = true;
            try
            {
                _itemGrid.Rows.Clear();
                foreach (DataRow r in _v.Rows("PTL"))
                {
                    int ic = JetDb.GetInt(r, "ItemCode");
                    int i = _itemGrid.Rows.Add(
                        JetDb.GetInt(r, "Line_no"), ic, _db.State.ItemName(ic),
                        JetDb.GetDbl(r, "Tran_Qty"), JetDb.GetDbl(r, "Tran_Nos"),
                        JetDb.GetStr(r, "Unit"), JetDb.GetDbl(r, "Unit_Rate"),
                        Helpers.FmtNumRaw(JetDb.GetDbl(r, "Net_Value")),
                        JetDb.GetStr(r, "Naration"), JetDb.GetStr(r, "Acc_COde").Trim());
                    _itemGrid.Rows[i].Tag = r;
                }
            }
            finally { _syncing = false; }
        }

        private void FillAddonGrid()
        {
            _syncing = true;
            try
            {
                _addonGrid.Rows.Clear();
                foreach (DataRow r in _v.Rows("PTHL"))
                {
                    int code = JetDb.GetInt(r, "AddOns");
                    AddOnRow ao;
                    string name = _db.State.AddOns.TryGetValue(code, out ao) ? ao.Name : ("AddOn " + code);
                    string acc = JetDb.GetStr(r, "Acc_Code").Trim();
                    int i = _addonGrid.Rows.Add(
                        JetDb.GetInt(r, "Line"), code, name, JetDb.GetStr(r, "Type").Trim(),
                        JetDb.GetDbl(r, "Per"), JetDb.GetDbl(r, "Amount"), JetDb.GetDbl(r, "Ass_val"),
                        acc, _db.State.AccountName(acc));
                    _addonGrid.Rows[i].Tag = r;
                }
            }
            finally { _syncing = false; }
        }

        // ================================================================ grid edits

        private string Cell(DataGridView g, int row, int col)
        {
            object o = g.Rows[row].Cells[col].Value;
            return o == null ? "" : Convert.ToString(o);
        }

        private void EffCellEdited(int row, int col)
        {
            if (_syncing || _v == null || row < 0) return;
            DataGridViewRow gr = _effGrid.Rows[row];
            DataRow r = gr.Tag as DataRow;
            if (r == null) return;
            string oldAcc = Convert.ToString(_editingOldValue ?? "").Trim();

            JetDb.SetVal(r, "Acc_Code", Cell(_effGrid, row, 1).Trim());
            JetDb.SetVal(r, "DbCr", Cell(_effGrid, row, 3).Trim().ToUpperInvariant());
            JetDb.SetVal(r, "Tran_amt", Helpers.Round2(Helpers.ParseDbl(Cell(_effGrid, row, 4), 0)));
            JetDb.SetVal(r, "Naration", Cell(_effGrid, row, 5));
            JetDb.SetVal(r, "Acc_Code2", Cell(_effGrid, row, 6).Trim());

            if (col == 1 && oldAcc.Length > 0)
            {
                string newAcc = JetDb.GetStr(r, "Acc_Code").Trim();
                if (!newAcc.Equals(oldAcc, StringComparison.OrdinalIgnoreCase))
                {
                    // mirror accounts follow an account substitution (clone semantics)
                    foreach (DataRow o in _v.Rows("EFF"))
                        if (JetDb.GetStr(o, "Acc_Code2").Trim().Equals(oldAcc, StringComparison.OrdinalIgnoreCase))
                            JetDb.SetVal(o, "Acc_Code2", newAcc);
                }
            }
            gr.Cells[2].Value = _db.State.AccountName(JetDb.GetStr(r, "Acc_Code").Trim());
            gr.Cells[7].Value = _db.State.AccountName(JetDb.GetStr(r, "Acc_Code2").Trim());
            RecalcAndRefresh(false);
        }

        private void ItemCellEdited(int row, int col)
        {
            if (_syncing || _v == null || row < 0) return;
            DataRow r = _itemGrid.Rows[row].Tag as DataRow;
            if (r == null) return;
            int oldItem = JetDb.GetInt(r, "ItemCode");
            JetDb.SetVal(r, "ItemCode", Helpers.ParseInt(Cell(_itemGrid, row, 1), oldItem));
            JetDb.SetVal(r, "Tran_Qty", Helpers.ParseDbl(Cell(_itemGrid, row, 3), 0));
            JetDb.SetVal(r, "Tran_Nos", Helpers.ParseDbl(Cell(_itemGrid, row, 4), 0));
            JetDb.SetVal(r, "Unit", Cell(_itemGrid, row, 5));
            JetDb.SetVal(r, "Unit_Rate", Helpers.ParseDbl(Cell(_itemGrid, row, 6), 0));
            JetDb.SetVal(r, "Naration", Cell(_itemGrid, row, 8));
            JetDb.SetVal(r, "Acc_COde", Cell(_itemGrid, row, 9).Trim());
            int ic = JetDb.GetInt(r, "ItemCode");
            if (col == 1 && ic != oldItem)
            {
                ItemRow it = _db.State.Item(ic);
                if (it != null && it.UnitCode.Length > 0) JetDb.SetVal(r, "Unit", it.UnitCode);
            }
            _itemGrid.Rows[row].Cells[2].Value = _db.State.ItemName(ic);
            RecalcAndRefresh(true);
        }

        private void AddonCellEdited(int row, int col)
        {
            if (_syncing || _v == null || row < 0) return;
            DataRow r = _addonGrid.Rows[row].Tag as DataRow;
            if (r == null) return;
            string type = Cell(_addonGrid, row, 3).Trim();
            JetDb.SetVal(r, "Type", type == "-" ? "-" : "+");
            JetDb.SetVal(r, "Per", Helpers.ParseDbl(Cell(_addonGrid, row, 4), 0));
            JetDb.SetVal(r, "Amount", Helpers.Round2(Helpers.ParseDbl(Cell(_addonGrid, row, 5), 0)));
            JetDb.SetVal(r, "Ass_val", Helpers.ParseDbl(Cell(_addonGrid, row, 6), 0));
            JetDb.SetVal(r, "Acc_Code", Cell(_addonGrid, row, 7).Trim());
            _addonGrid.Rows[row].Cells[8].Value = _db.State.AccountName(Cell(_addonGrid, row, 7).Trim());
            RecalcAndRefresh(true);
        }

        // add / delete line helpers
        private DataRow CloneNewLine(DataTable t)
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

        private int NextLineNo(IEnumerable<DataRow> rows, string col, int min)
        {
            int m = min - 1;
            foreach (DataRow r in rows) m = Math.Max(m, JetDb.GetInt(r, col));
            return m + 1;
        }

        private void AddLedgerLine()
        {
            if (_v == null) return;
            DataTable t = _v.T("EFF");
            DataRow r = CloneNewLine(t);
            JetDb.SetVal(r, "Line_no", NextLineNo(_v.Rows("EFF"), "Line_no", 1));
            JetDb.SetVal(r, "Tran_amt", 0.0);
            JetDb.SetVal(r, "DbCr", "D");
            JetDb.SetVal(r, "Naration", _narrationTxt.Text);
            t.Rows.Add(r);
            FillEffGrid();
            RecalcAndRefresh(false);
        }

        private void DeleteEffGridLine()
        {
            if (_v == null || _effGrid.SelectedRows.Count == 0) return;
            DataRow r = _effGrid.SelectedRows[0].Tag as DataRow;
            if (r != null) _v.T("EFF").Rows.Remove(r);
            FillEffGrid();
            RecalcAndRefresh(false);
        }

        private void AddItemLine()
        {
            if (_v == null) return;
            DataTable t = _v.T("PTL");
            DataRow r = CloneNewLine(t);
            JetDb.SetVal(r, "Line_no", NextLineNo(_v.Rows("PTL"), "Line_no", 1));
            JetDb.SetVal(r, "Tran_Qty", 0.0);
            JetDb.SetVal(r, "Tran_Nos", 0.0);
            JetDb.SetVal(r, "Unit_Rate", 0.0);
            JetDb.SetVal(r, "Entry_Rate", 0.0);
            JetDb.SetVal(r, "Value", 0.0);
            JetDb.SetVal(r, "Net_Value", 0.0);
            t.Rows.Add(r);
            _srcLineNarrations[r] = "";
            FillItemGrid();
            RecalcAndRefresh(true);
        }

        private void DeleteItemLine()
        {
            if (_v == null || _itemGrid.SelectedRows.Count == 0) return;
            DataRow r = _itemGrid.SelectedRows[0].Tag as DataRow;
            if (r != null) _v.T("PTL").Rows.Remove(r);
            FillItemGrid();
            RecalcAndRefresh(true);
        }

        private void AddAddonLine()
        {
            if (_v == null) return;
            List<string> choices = new List<string>();
            foreach (AddOnRow ao in _db.State.AddOns.Values) choices.Add(ao.Code + "  " + ao.Name);
            choices.Sort();
            string pick = PickFromList("Choose tax / charge (AddOns master)", choices);
            if (pick == null) return;
            int code = Helpers.ParseInt(pick.Substring(0, pick.IndexOf(' ')), 0);
            AddOnRow def;
            if (!_db.State.AddOns.TryGetValue(code, out def)) return;
            DataTable t = _v.T("PTHL");
            DataRow r = CloneNewLine(t);
            int line = 101;
            foreach (DataRow o in _v.Rows("PTHL")) line = Math.Max(line, JetDb.GetInt(o, "Line") + 1);
            JetDb.SetVal(r, "Line", line);
            JetDb.SetVal(r, "AddOns", code);
            JetDb.SetVal(r, "Type", def.DefSign == "-" ? "-" : "+");
            JetDb.SetVal(r, "Per", 0.0);
            JetDb.SetVal(r, "Amount", 0.0);
            JetDb.SetVal(r, "Ass_val", _v.Pth == null ? 0 : JetDb.GetDbl(_v.Pth, "GoodAmt"));
            string acc = def.AccCodeSL;
            if (code == 99)
            {
                SeriesRow s = _db.State.FindSeries(_v.SrcBT, _v.NewTranType);
                acc = s == null ? "" : s.AccCode;
            }
            JetDb.SetVal(r, "Acc_Code", acc);
            if (JetDb.RowHas(r, "Exc_Inc")) JetDb.SetVal(r, "Exc_Inc", "E");
            t.Rows.Add(r);
            FillAddonGrid();
            RecalcAndRefresh(true);
        }

        private void DeleteAddonLine()
        {
            if (_v == null || _addonGrid.SelectedRows.Count == 0) return;
            DataRow r = _addonGrid.SelectedRows[0].Tag as DataRow;
            if (r != null) _v.T("PTHL").Rows.Remove(r);
            FillAddonGrid();
            RecalcAndRefresh(true);
        }

        private void AddEffLine()
        {
            DataTable t = _v == null ? null : _v.T("EFFLine");
            if (t == null) return;
            DataRow r = CloneNewLine(t);
            if (JetDb.RowHas(r, "Line_no") && _v.Rows("EFF").Count > 0)
                JetDb.SetVal(r, "Line_no", JetDb.GetInt(_v.Rows("EFF")[0], "Line_no"));
            if (JetDb.RowHas(r, "BankDate")) JetDb.SetVal(r, "BankDate", null);
            t.Rows.Add(r);
        }

        private void DeleteEffLine()
        {
            if (_effLineGrid.SelectedRows.Count == 0) return;
            DataRowView rv = _effLineGrid.SelectedRows[0].DataBoundItem as DataRowView;
            if (rv != null) rv.Row.Delete();
        }

        private void DeletePayHist()
        {
            if (_payHistGrid.SelectedRows.Count == 0) return;
            DataRowView rv = _payHistGrid.SelectedRows[0].DataBoundItem as DataRowView;
            if (rv != null)
            {
                rv.Row.Delete();
                RecalcAndRefresh(false);
            }
        }

        // ================================================================ header sync + recalc

        private void SyncHeader()
        {
            if (_v == null) return;
            DataRow inf = _v.Inf;
            _v.NewDate = _newDateDtp.Value.Date;
            JetDb.SetVal(inf, "Naration", _narrationTxt.Text.Length == 0 ? (object)DBNull.Value : _narrationTxt.Text);
            if (!(_v.Kind == VoucherKind.Sales))
            {
                string rn = _refNoTxt.Text.Trim();
                if (rn.Length > 0) JetDb.SetVal(inf, "RefNo", rn);
                if (_refDateDtp.Checked) JetDb.SetVal(inf, "RefDate", _refDateDtp.Value.Date);
            }
            if (_v.Kind == VoucherKind.Sales && _v.Pth != null)
            {
                JetDb.SetVal(_v.Pth, "Acc_Code", _partyCodeTxt.Text.Trim());
                JetDb.SetVal(_v.Pth, "PayMode", _payModeTxt.Text.Trim());
                JetDb.SetVal(_v.Pth, "cr_day", Helpers.ParseInt(_crDaysTxt.Text, 0));
                if (!_autoTaxChk.Checked) JetDb.SetVal(_v.Pth, "TaxCode", _taxCodeTxt.Text.Trim());
                _v.TaxInvMode = _taxInvModeCombo.SelectedIndex;
                _v.NewTaxInvNo = _taxInvTxt.Text.Trim();
            }
            if (!string.IsNullOrEmpty(_userId.Text.Trim())) _v.UserId = _userId.Text.Trim();
        }

        private void SyncAllGrids()
        {
            // cell edits are committed on CellEndEdit; force any pending edit to finish
            try { _effGrid.EndEdit(); _itemGrid.EndEdit(); _addonGrid.EndEdit(); _effLineGrid.EndEdit(); _payHistGrid.EndEdit(); }
            catch { }
        }

        private void PartyCodeEdited()
        {
            if (_syncing || _v == null) return;
            string code = _partyCodeTxt.Text.Trim();
            _partyNameTxt.Text = _db.State.AccountName(code);
            if (_v.Pth != null)
            {
                string oldParty = JetDb.GetStr(_v.Pth, "Acc_Code").Trim();
                if (oldParty.Length > 0 && !oldParty.Equals(code, StringComparison.OrdinalIgnoreCase))
                {
                    JetDb.SetVal(_v.Pth, "Acc_Code", code);
                    foreach (DataRow bh in _v.Rows("BillHist"))
                        if (JetDb.GetStr(bh, "Acc_Code").Trim().Equals(oldParty, StringComparison.OrdinalIgnoreCase))
                            JetDb.SetVal(bh, "Acc_Code", code);
                    foreach (DataRow e in _v.Rows("EFF"))
                    {
                        if (JetDb.GetStr(e, "Acc_Code").Trim().Equals(oldParty, StringComparison.OrdinalIgnoreCase))
                            JetDb.SetVal(e, "Acc_Code", code);
                        if (JetDb.GetStr(e, "Acc_Code2").Trim().Equals(oldParty, StringComparison.OrdinalIgnoreCase))
                            JetDb.SetVal(e, "Acc_Code2", code);
                    }
                }
            }
            RecalcAndRefresh(true);
        }

        private void OnBookChanged()
        {
            if (_syncing || _v == null) return;
            SeriesRow s = _newBookCombo.SelectedItem as SeriesRow;
            if (s == null) return;
            _v.NewTranType = s.TranType;
            List<string> w = new List<string>();
            _engine.ApplyNewBookIncomeRemap(_v, w);
            SyncHeader();
            RecalcAndRefresh(true);
            UpdateTaxInvPreview();
        }

        private void OnNewDateChanged()
        {
            if (_syncing || _v == null) return;
            _v.NewDate = _newDateDtp.Value.Date;
            if (_bumpNarChk.Checked)
            {
                string nb = Helpers.BumpNarration(_srcHeaderNarration, _v.SrcDate, _v.NewDate);
                if (_narrationTxt.Text == _lastAutoNarration)
                {
                    _narrationTxt.Text = nb;
                }
                _lastAutoNarration = nb;
                foreach (KeyValuePair<DataRow, string> kv in _srcLineNarrations)
                {
                    if (kv.Key.RowState == DataRowState.Deleted) continue;
                    string lb = Helpers.BumpNarration(kv.Value, _v.SrcDate, _v.NewDate);
                    string last;
                    if (_lastAutoLineNarrations.TryGetValue(kv.Key, out last) && JetDb.GetStr(kv.Key, "Naration") == last)
                        JetDb.SetVal(kv.Key, "Naration", lb);
                    _lastAutoLineNarrations[kv.Key] = lb;
                }
                SyncHeader();
                RecalcAndRefresh(true);
            }
            UpdateTaxInvPreview();
        }

        private void RecalcAndRefresh() { RecalcAndRefresh(true); }

        private void RecalcAndRefresh(bool refillItems)
        {
            if (_v == null || _engine == null) return;
            if (_syncing) return;
            List<string> recalcWarnings = new List<string>();
            try
            {
                _syncing = true;
                if (_v.Kind == VoucherKind.Sales) _engine.RecalcSales(_v, _autoTaxChk.Checked, recalcWarnings);
                else _engine.RecalcGeneric(_v, recalcWarnings);
            }
            catch (Exception ex) { recalcWarnings.Add("Recalculation note: " + ex.Message); }
            finally { _syncing = false; }

            FillEffGrid();
            if (_v.Kind == VoucherKind.Sales && refillItems) { FillItemGrid(); FillAddonGrid(); }
            if (_v.Kind == VoucherKind.Sales && _v.Pth != null && _autoTaxChk.Checked)
                _taxCodeTxt.Text = JetDb.GetStr(_v.Pth, "TaxCode").Trim();

            string headName = _db.State.AccountName(JetDb.GetStr(_v.Inf, "Acc_Code").Trim());
            if (_v.Kind != VoucherKind.Sales) _headTxt.Text = "Head A/c: " + JetDb.GetStr(_v.Inf, "Acc_Code").Trim() + "  " + headName;

            UpdateTotals(recalcWarnings);
        }

        private void UpdateTotals(List<string> recalcWarnings)
        {
            double dr = _v.EffTotal("D"), cr = _v.EffTotal("C");
            if (_v.Kind == VoucherKind.Sales && _v.Pth != null)
            {
                _totalsLbl.Text = string.Format("Goods Rs. {0}  +  Add Rs. {1}  -  Less Rs. {2}  =  NET Rs. {3}      |      Ledger: Dr Rs. {4} / Cr Rs. {5}",
                    Helpers.FmtAmt(JetDb.GetDbl(_v.Pth, "GoodAmt")), Helpers.FmtAmt(JetDb.GetDbl(_v.Pth, "AddAmt")),
                    Helpers.FmtAmt(JetDb.GetDbl(_v.Pth, "LessAmt")), Helpers.FmtAmt(JetDb.GetDbl(_v.Pth, "NetAmt")),
                    Helpers.FmtAmt(dr), Helpers.FmtAmt(cr));
            }
            else
            {
                _totalsLbl.Text = string.Format("Total Debit Rs. {0}      Total Credit Rs. {1}      Difference Rs. {2}",
                    Helpers.FmtAmt(dr), Helpers.FmtAmt(cr), Helpers.FmtAmt(Math.Abs(dr - cr)));
            }
            _totalsLbl.ForeColor = Helpers.EqAmt(dr, cr) ? Color.DarkGreen : Color.DarkRed;

            List<string> errors = new List<string>(), warnings = new List<string>(recalcWarnings);
            try { _engine.Validate(_v, errors, warnings); }
            catch (Exception ex) { errors.Add(ex.Message); }
            _msgList.Items.Clear();
            foreach (string e in errors) _msgList.Items.Add("ERROR: " + e);
            foreach (string w in warnings) _msgList.Items.Add("Warning: " + w);
            if (errors.Count == 0 && warnings.Count == 0) _msgList.Items.Add("All checks passed - open Review & Post.");
            _postBtn.Tag = errors;
            UpdatePostEnabled();
        }

        private void UpdateTaxInvPreview()
        {
            if (_v == null || _db == null) return;
            SeriesRow s = _db.State.FindSeries(_v.SrcBT, _v.NewTranType);
            if (s == null) return;
            long nextVch = s.No1 + 1;
            if (_taxInvModeCombo.SelectedIndex == 2) return;
            string srcInv = _v.Pth == null ? "" : JetDb.GetStr(_v.Pth, "TAX_INV_NO");
            // Pth TAX_INV_NO on the loaded (unedited-identity) row is still the source number
            _taxInvTxt.Text = _taxInvModeCombo.SelectedIndex == 1
                ? Helpers.TaxInvKeepOffset(srcInv, Helpers.VchNoToLong(_v.SrcVchNo), nextVch)
                : Helpers.TaxInvSameAsVch(srcInv, nextVch);
        }

        // ================================================================ review + post

        private void RefreshReview()
        {
            UpdateStatus();
            if (_v == null || _engine == null)
            {
                _reviewTxt.Text = "Load and edit an entry first (tabs 1 and 2).";
                _postBtn.Enabled = false;
                return;
            }
            try
            {
                SyncAllGrids(); SyncHeader();
                List<string> wr = new List<string>();
                if (_v.Kind == VoucherKind.Sales) _engine.RecalcSales(_v, _autoTaxChk.Checked, wr); else _engine.RecalcGeneric(_v, wr);
                _db.Probe(); // fresh counters for a truthful preview
                PostPlan plan = _engine.BuildReviewPlan(_v);
                _taxInvTxt.Text = _v.Kind == VoucherKind.Sales && _v.TaxInvMode != 2 ? plan.NewTaxInvNo : _taxInvTxt.Text;

                List<string> errors = new List<string>(), warnings = new List<string>();
                _engine.Validate(_v, errors, warnings);
                _postBtn.Tag = errors;

                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                sb.AppendLine("NEW ENTRY TO BE POSTED");
                sb.AppendLine(string.Format("  {0}   Book: {1} ({2}-{3})", VoucherKinds.Title(_v.Kind), plan.Series == null ? "?" : plan.Series.Name, _v.SrcBT, _v.NewTranType));
                sb.AppendLine(string.Format("  Date: {0}    New Vch No: {1}    New ID_NO: {2}    New UId: {3}", Helpers.FmtDate(_v.NewDate), plan.NewVchNo.Trim(), plan.NewIDNo, plan.NewUId));
                if (_v.Kind == VoucherKind.Sales) sb.AppendLine("  TAX Invoice No: " + plan.NewTaxInvNo);
                sb.AppendLine(string.Format("  (Numbers are re-checked inside the posting transaction - if another user posts first, the next free number is used.)"));
                sb.AppendLine();
                sb.AppendLine("SOURCE ENTRY (unchanged)");
                sb.AppendLine("  " + _v.SourceLabel);
                sb.AppendLine();
                sb.AppendLine("ROWS THAT WILL BE INSERTED");
                foreach (KeyValuePair<string, int> kv in plan.InsertCounts) sb.AppendLine("  " + kv.Key + " x " + kv.Value);
                sb.AppendLine();
                sb.AppendLine("LEDGER LINES OF THE NEW ENTRY");
                sb.AppendLine(string.Format("  {0,-44} {1,3} {2,16}", "Account", "D/C", "Amount"));
                foreach (DataRow r in _v.Rows("EFF"))
                {
                    string acc = JetDb.GetStr(r, "Acc_Code").Trim();
                    sb.AppendLine(string.Format("  {0,-44} {1,3} {2,16}", TrimTo(_db.State.AccountName(acc) + " (" + acc + ")", 44),
                        JetDb.GetStr(r, "DbCr").Trim(), Helpers.FmtAmt(JetDb.GetDbl(r, "Tran_amt"))));
                }
                sb.AppendLine(string.Format("  Total Dr Rs. {0}   Total Cr Rs. {1}", Helpers.FmtAmt(_v.EffTotal("D")), Helpers.FmtAmt(_v.EffTotal("C"))));
                if (_v.Kind == VoucherKind.Sales && _v.Pth != null)
                {
                    sb.AppendLine();
                    sb.AppendLine(string.Format("BILL: Goods Rs. {0} + Add Rs. {1} - Less Rs. {2} = NET Rs. {3}   Party: {4}",
                        Helpers.FmtAmt(JetDb.GetDbl(_v.Pth, "GoodAmt")), Helpers.FmtAmt(JetDb.GetDbl(_v.Pth, "AddAmt")),
                        Helpers.FmtAmt(JetDb.GetDbl(_v.Pth, "LessAmt")), Helpers.FmtAmt(JetDb.GetDbl(_v.Pth, "NetAmt")),
                        _partyNameTxt.Text));
                    foreach (DataRow r in _v.Rows("PTL"))
                        sb.AppendLine(string.Format("  Item: {0}  qty {1} @ {2} = Rs. {3}   (stock after posting: {4})",
                            _db.State.ItemName(JetDb.GetInt(r, "ItemCode")), JetDb.GetDbl(r, "Tran_Qty"),
                            Helpers.FmtAmt(JetDb.GetDbl(r, "Unit_Rate")), Helpers.FmtAmt(JetDb.GetDbl(r, "Net_Value")),
                            plan.ItemNewBal.ContainsKey(JetDb.GetInt(r, "ItemCode")) ? plan.ItemNewBal[JetDb.GetInt(r, "ItemCode")].ToString() : "?"));
                }
                if (_v.Rows("PayHist").Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("BILL ADJUSTMENTS - the bills below will be marked adjusted by this NEW entry:");
                    foreach (string s in plan.BillAdjustments) sb.AppendLine("  " + s);
                }
                sb.AppendLine();
                sb.AppendLine("ACCOUNT BALANCES THAT WILL MOVE (incl. group totals): " + plan.AccDeltas.Count + " accounts");
                foreach (KeyValuePair<string, double[]> kv in plan.AccDeltas)
                {
                    if (Helpers.EqAmt(kv.Value[0], 0) && Helpers.EqAmt(kv.Value[1], 0)) continue;
                    sb.AppendLine(string.Format("  {0,-44} Cr +{1}   Dr +{2}", TrimTo(_db.State.AccountName(kv.Key), 44), Helpers.FmtAmt(kv.Value[0]), Helpers.FmtAmt(kv.Value[1])));
                }
                if (plan.Notes.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("NOTES:");
                    foreach (string n in plan.Notes) sb.AppendLine("  - " + n);
                }
                sb.AppendLine();
                if (errors.Count > 0)
                {
                    sb.AppendLine("CANNOT POST YET:");
                    foreach (string e in errors) sb.AppendLine("  ERROR: " + e);
                }
                if (warnings.Count > 0)
                {
                    sb.AppendLine("WARNINGS (posting still allowed - please read):");
                    foreach (string w in warnings) sb.AppendLine("  - " + w);
                }
                sb.AppendLine();
                sb.AppendLine(_backupChk.Checked
                    ? "A dated backup copy of Accurate.Mdb will be made before anything is written."
                    : "WARNING: backup is switched OFF for this posting.");
                _reviewTxt.Text = sb.ToString();
                UpdatePostEnabled();
            }
            catch (Exception ex)
            {
                _reviewTxt.Text = "Could not build the review:\r\n" + ex.Message;
                _postBtn.Enabled = false;
            }
        }

        private static string TrimTo(string s, int n)
        {
            s = s ?? "";
            return s.Length <= n ? s : s.Substring(0, n);
        }

        private void UpdatePostEnabled()
        {
            List<string> errors = _postBtn.Tag as List<string>;
            _postBtn.Enabled = _v != null && _confirmChk.Checked && (errors == null || errors.Count == 0);
        }

        private void DoPost()
        {
            if (_v == null) return;
            DialogResult ask = MessageBox.Show(this,
                "Post this NEW entry into the LIVE database?\r\n\r\n" + _dbPath.Text +
                "\r\n\r\nA backup will be taken first" + (_backupChk.Checked ? "." : " - NO, backup is OFF!") +
                "\r\nThe source entry is not changed or deleted.",
                "Confirm posting", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (ask != DialogResult.Yes) return;
            try
            {
                Cursor = Cursors.WaitCursor;
                SyncAllGrids(); SyncHeader();
                PostResult res = _engine.Post(_v, _backupChk.Checked);
                _reviewTxt.Text = res.Report + "\r\nLog file: " + res.LogPath +
                    "\r\n\r\nNow open AccNew and check this voucher: Transaction > modify/find Vch No " + res.Plan.NewVchNo.Trim() + ".";
                MessageBox.Show(this,
                    "Posted OK!\r\n\r\nNew Vch No: " + res.Plan.NewVchNo.Trim() + "\r\nID_NO: " + res.Plan.NewIDNo +
                    (_v.Kind == VoucherKind.Sales ? "\r\nTAX Invoice No: " + res.Plan.NewTaxInvNo : "") +
                    "\r\n\r\nOpen AccNew and verify this voucher. The backup taken before posting is at:\r\n" + res.BackupPath,
                    "Posted", MessageBoxButtons.OK, MessageBoxIcon.Information);
                _confirmChk.Checked = false;
                _postBtn.Tag = null;
                _postBtn.Enabled = false;
                _v = null;
                _srcLbl.Text = "Posted. Load the next entry from tab 1.";
                FillBookCombo();
                UpdateStatus();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "POSTING FAILED - nothing was written to the database.\r\n\r\n" + ex.Message,
                    "Posting failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                UpdateStatus();
            }
            finally { Cursor = Cursors.Default; }
        }

        private void ShowError(Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        // ================================================================ settings

        private string SettingsPath()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AccuVisionRepost");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "settings.txt");
        }

        private void SaveSettings()
        {
            try
            {
                File.WriteAllText(SettingsPath(),
                    "DbPath=" + _dbPath.Text.Trim() + "\r\nUserId=" + _userId.Text.Trim() + "\r\n");
            }
            catch { }
        }

        private void LoadSettings()
        {
            try
            {
                string p = SettingsPath();
                if (!File.Exists(p)) return;
                foreach (string line in File.ReadAllLines(p))
                {
                    int i = line.IndexOf('=');
                    if (i < 0) continue;
                    string k = line.Substring(0, i).Trim(), val = line.Substring(i + 1).Trim();
                    if (k == "DbPath") _dbPath.Text = val;
                    if (k == "UserId") _userId.Text = val;
                }
            }
            catch { }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            SaveSettings();
            base.OnFormClosed(e);
        }
    }
}
