using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using AccuVisionRepost.Core;

namespace AccuVisionRepost.Ui
{
    /// <summary>Dependency-free, interactive visual explanations of every AccNew ledger.</summary>
    public sealed class LedgerVisualControl : UserControl
    {
        static readonly Color Ink = Color.FromArgb(24, 40, 62), Muted = Color.FromArgb(95, 111, 132), Paper = Color.FromArgb(244, 247, 251);
        static readonly Color Teal = Color.FromArgb(0, 139, 125), Blue = Color.FromArgb(55, 105, 210), Amber = Color.FromArgb(224, 151, 35), Red = Color.FromArgb(206, 78, 83), Violet = Color.FromArgb(125, 91, 181);
        readonly TextBox _search = new TextBox();
        readonly ComboBox _scope = new ComboBox(), _ledger = new ComboBox();
        readonly DateTimePicker _from = new DateTimePicker(), _to = new DateTimePicker();
        readonly CheckBox _groups = new CheckBox();
        readonly Label _status = new Label();
        Label _title = new Label(), _subtitle = new Label();
        readonly Panel _canvas = new Panel();
        readonly ToolTip _tip = new ToolTip();
        LedgerSnapshot _snapshot;
        LedgerOverview _overview;
        LedgerMetrics _current;
        string _path, _password;
        bool _loading, _showOverview = true;
        int _loadGeneration;
        public event Action<string> OpenVoucherRequested;

        public LedgerVisualControl()
        {
            Dock = DockStyle.Fill; BackColor = Paper; Font = new Font("Segoe UI", 9f);
            BuildUi();
        }
        public bool IsBusy { get { return _loading; } }
        public string CurrentAccount { get { return _current == null ? null : _current.Account.Code; } }
        public LedgerMetrics CurrentMetrics { get { return _current; } }
        void BuildUi()
        {
            Panel bar = new Panel { Dock = DockStyle.Top, Height = 108, BackColor = Color.White, Padding = new Padding(14, 8, 14, 4) };
            Label heading = new Label { Text = "LEDGER STORIES", Font = new Font("Segoe UI", 13f, FontStyle.Bold), ForeColor = Ink, Location = new Point(14, 7), AutoSize = true };
            Label read = new Label { Text = "READ-ONLY  |  every ledger, not just a table", Location = new Point(218, 14), AutoSize = true, ForeColor = Teal, Font = new Font("Segoe UI", 8.5f, FontStyle.Bold) };
            Button refresh = MakeButton("Refresh data", 100); refresh.Location = new Point(1040, 8); refresh.Anchor = AnchorStyles.Top | AnchorStyles.Right; refresh.Click += delegate { Reload(); };
            Button overview = MakeButton("Company overview", 142); overview.Location = new Point(886, 8); overview.Anchor = AnchorStyles.Top | AnchorStyles.Right; overview.Click += delegate { ShowOverview(); };
            Button export = MakeButton("Export view...", 110); export.Location = new Point(764, 8); export.Anchor = AnchorStyles.Top | AnchorStyles.Right; export.Click += delegate { Export(); };
            Label ls = new Label { Text = "Ledger / code", Location = new Point(14, 43), AutoSize = true, ForeColor = Muted };
            _search.Location = new Point(14, 65); _search.Width = 180; _tip.SetToolTip(_search, "Search every ledger by name or account code"); _search.TextChanged += delegate { FillLedgers(); };
            _scope.Location = new Point(204, 65); _scope.Width = 145; _scope.DropDownStyle = ComboBoxStyle.DropDownList;
            _scope.Items.AddRange(new object[] { "All ledgers", "Customers", "Suppliers", "Bank & cash", "Income", "Expense", "Needs attention", "With a balance" }); _scope.SelectedIndex = 0; _scope.SelectedIndexChanged += delegate { FillLedgers(); };
            _groups.Text = "Groups"; _groups.Location = new Point(357, 67); _groups.Width = 70; _groups.CheckedChanged += delegate { FillLedgers(); };
            _ledger.Location = new Point(435, 65); _ledger.Width = 340; _ledger.DropDownStyle = ComboBoxStyle.DropDownList; _ledger.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _ledger.SelectedIndexChanged += delegate { LedgerChoice c = _ledger.SelectedItem as LedgerChoice; if (c != null && !_loading) SelectLedger(c.Code); };
            Label lf = new Label { Text = "From", Location = new Point(790, 43), AutoSize = true, ForeColor = Muted, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            Label lt = new Label { Text = "Through", Location = new Point(918, 43), AutoSize = true, ForeColor = Muted, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            _from.Location = new Point(790, 65); _from.Width = 118; _from.Format = DateTimePickerFormat.Short; _from.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _to.Location = new Point(918, 65); _to.Width = 118; _to.Format = DateTimePickerFormat.Short; _to.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            Button apply = MakeButton("Apply", 80); apply.Location = new Point(1048, 64); apply.Anchor = AnchorStyles.Top | AnchorStyles.Right; apply.Click += delegate { RenderSelection(); };
            _status.Location = new Point(14, 90); _status.Size = new Size(1130, 17); _status.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right; _status.ForeColor = Muted; _status.Text = "Connect to a company database to explore its ledger stories.";
            bar.Controls.AddRange(new Control[] { heading, read, export, overview, refresh, ls, _search, _scope, _groups, _ledger, lf, lt, _from, _to, apply, _status });
            _canvas.Dock = DockStyle.Fill; _canvas.AutoScroll = true; _canvas.Padding = new Padding(18); _canvas.Resize += delegate { if (_snapshot != null && !_loading) RenderSelection(); };
            Controls.Add(_canvas); Controls.Add(bar); _canvas.BringToFront();
        }
        static Button MakeButton(string text, int width) { return new Button { Text = text, Width = width, Height = 28, FlatStyle = FlatStyle.Flat, BackColor = Color.White, ForeColor = Ink, Cursor = Cursors.Hand }; }
        public void Connect(string path, string password)
        {
            _path = path; _password = password; _snapshot = null; _current = null; _overview = null; _showOverview = true;
            Reload();
        }
        public void Disconnect()
        {
            _loadGeneration++; _snapshot = null; _current = null; _overview = null; _path = null; _canvas.Controls.Clear();
            _status.Text = "Connect to a company database to explore its ledger stories.";
        }
        void Reload()
        {
            if (string.IsNullOrEmpty(_path) || _loading) return;
            int gen = ++_loadGeneration; _loading = true; _status.Text = "Reading a consistent, read-only snapshot..."; _canvas.Controls.Clear();
            string path = _path, pwd = _password;
            BackgroundWorker bw = new BackgroundWorker();
            bw.DoWork += delegate(object sender, DoWorkEventArgs e) { LedgerSnapshot s = LedgerSnapshot.Load(path, pwd); LedgerOverview o = s.Overview(s.FyFrom, s.FyTo); e.Result = new object[] { s, o }; };
            bw.RunWorkerCompleted += delegate(object sender, RunWorkerCompletedEventArgs e)
            {
                if (gen != _loadGeneration || IsDisposed) return;
                _loading = false;
                if (e.Error != null) { _status.Text = "Visuals could not read the database: " + e.Error.Message; return; }
                object[] result = (object[])e.Result; _snapshot = (LedgerSnapshot)result[0]; _overview = (LedgerOverview)result[1];
                _from.Value = _snapshot.FyFrom; _to.Value = _snapshot.FyTo; FillLedgers(); ShowOverview();
            };
            bw.RunWorkerAsync();
        }
        sealed class LedgerChoice
        {
            public string Code, Text;
            public LedgerChoice(string code, string text) { Code = code; Text = text; }
            public override string ToString() { return Text; }
        }
        void FillLedgers()
        {
            if (_snapshot == null) return;
            string previous = CurrentAccount; string q = _search.Text.Trim(); string scope = Convert.ToString(_scope.SelectedItem);
            IEnumerable<LedgerAccount> list = _snapshot.Search(q, null, _groups.Checked);
            if (scope == "Customers") list = list.Where(a => a.Kind.Contains("Customer"));
            else if (scope == "Suppliers") list = list.Where(a => a.Kind.Contains("Supplier"));
            else if (scope != "All ledgers") list = list.Where(a => a.Kind == scope);
            if (scope == "Needs attention" || scope == "With a balance")
            {
                HashSet<string> codes = new HashSet<string>(_overview.Ledgers.Where(m => scope == "Needs attention" ? m.Insights.Any(i => i.Severity == "warning" || i.Severity == "attention") : Math.Abs(m.Closing) > .005).Select(m => m.Account.Code));
                list = list.Where(a => codes.Contains(a.Code));
            }
            List<LedgerAccount> sorted = list.OrderByDescending(a => a.Entries).ThenBy(a => a.Name).ToList();
            _ledger.BeginUpdate(); _ledger.Items.Clear(); int select = -1;
            for (int i = 0; i < sorted.Count; i++) { LedgerAccount a = sorted[i]; _ledger.Items.Add(new LedgerChoice(a.Code, a.Name + "  [" + a.Code + "]")); if (a.Code == previous) select = i; }
            _ledger.EndUpdate(); if (select >= 0) _ledger.SelectedIndex = select;
            _status.Text = _snapshot.Company + "  |  " + sorted.Count + " matching ledgers  |  data through " + Helpers.FmtDate(_snapshot.DataTo) + "  |  loaded " + _snapshot.LoadedAt.ToShortTimeString() + "  |  READ-ONLY";
        }
        public void SelectLedger(string code)
        {
            if (_snapshot == null) return;
            _showOverview = false;
            for (int i = 0; i < _ledger.Items.Count; i++) if (((LedgerChoice)_ledger.Items[i]).Code == code) { if (_ledger.SelectedIndex != i) _ledger.SelectedIndex = i; break; }
            RenderSelection();
        }
        public void ShowOverview() { _showOverview = true; RenderSelection(); }
        void RenderSelection()
        {
            if (_snapshot == null || _loading) return;
            try
            {
                DateTime from = _from.Value.Date, to = _to.Value.Date;
                if (to < from) { _status.Text = "The end date must be on or after the start date."; return; }
                _canvas.SuspendLayout(); _canvas.Controls.Clear(); _canvas.AutoScrollPosition = new Point(0, 0);
                int width = Math.Max(720, _canvas.ClientSize.Width - 42), y = 14;
                if (_showOverview)
                {
                    _overview = _snapshot.Overview(from, to); _current = null;
                    AddHeader(ref y, width, "See where the money is stuck.", _snapshot.Company + "  |  " + Helpers.FmtDate(from) + " - " + Helpers.FmtDate(to) + "  |  click a ledger below to read its story");
                    AddCards(ref y, width, new string[] { "CUSTOMER BALANCES", "SUPPLIER BALANCES", "CUSTOMER CREDITS", "BANK & CASH", "LEDGERS TO REVIEW" }, new double[] { _overview.Receivables, _overview.Payables, _overview.CustomerAdvances, _overview.BankBalance, _overview.AttentionCount }, new string[] { "Debit-side customer balances", "Credit-side supplier balances", "Possible advances / unallocated cash", "Signed ledger balances", _overview.ActiveLedgers + " active in this period" }, new Color[] { Blue, Amber, Violet, Teal, Red }, new int[] { 0, 0, 0, 0, 2 });
                    AddSection(ref y, width, "Sales, purchases and customer collections", "Monthly activity. Internal bank transfers and year-end transfers are not sales.", 270, delegate(VisualChart c) { c.SetupOverview(_overview); });
                    int half = (width - 14) / 2;
                    int left = 18, right = 18 + half + 14, rowY = y;
                    AddPanelAt(left, rowY, half, 320, "Who owes the company?", "Largest debit-side customer balances", delegate(VisualChart c) { c.SetupRank(_overview.Ledgers.Where(m => m.Account.Kind.Contains("Customer") && m.Closing > 0).OrderByDescending(m => m.Closing).Take(8).Select(m => new NameValue(m.Account.Name, m.Closing, m.Account.Code)).ToList(), Blue, false); c.Tag = "rank-customer"; });
                    AddPanelAt(right, rowY, width - half - 14, 320, "What the company owes", "Largest credit-side supplier balances", delegate(VisualChart c) { c.SetupRank(_overview.Ledgers.Where(m => m.Account.Kind.Contains("Supplier") && m.Closing < 0).OrderByDescending(m => -m.Closing).Take(8).Select(m => new NameValue(m.Account.Name, -m.Closing, m.Account.Code)).ToList(), Amber, false); c.Tag = "rank-supplier"; });
                    y += 336;
                    AddSection(ref y, width, "Balance radar", "Right = larger absolute balance; higher = longer since last movement; bubble size = activity. Click a bubble to investigate.", 330, delegate(VisualChart c) { c.SetupRadar(_overview.Ledgers.Where(m => Math.Abs(m.Closing) > .005).ToList(), _snapshot.DataTo); });
                    AddInsights(ref y, width, _overview.Insights);
                    AddLedgerButtons(ref y, width, _overview.Ledgers.Where(m => m.Lines.Count > 0).OrderByDescending(m => m.Debit + m.Credit).Take(24).ToList());
                }
                else
                {
                    LedgerChoice choice = _ledger.SelectedItem as LedgerChoice; if (choice == null) { _showOverview = true; _canvas.ResumeLayout(); RenderSelection(); return; }
                    _current = _snapshot.Compute(choice.Code, from, to); LedgerMetrics m = _current;
                    string story = m.Account.Kind == "Customer" ? "What was billed, what came back, and what is still waiting." : m.Account.Kind == "Supplier" ? "What was purchased, what was paid, and what remains." : m.Account.Kind == "Bank & cash" ? "Where money came from, where it went, and what stayed." : m.Account.Kind == "Income" || m.Account.Kind == "Expense" ? "The operating pattern, without year-end closing noise." : "Follow the movement, the counterparties and the balance.";
                    AddHeader(ref y, width, m.Account.Name, m.Account.Kind + "  |  " + m.Account.Code + "  |  " + m.Account.GroupPath + "\n" + story);
                    bool party = m.Account.Kind.Contains("Customer") || m.Account.Kind.Contains("Supplier");
                    bool bank = m.Account.Kind == "Bank & cash";
                    string[] labels = party ? new string[] { m.Account.Kind.Contains("Supplier") ? "PURCHASE BILLS" : "SALES / BILLING", m.Account.Kind.Contains("Supplier") ? "PAID TO PARTY" : "RECEIVED FROM PARTY", "CURRENT BILLS PENDING", "LEDGER BALANCE", "CASH / BILLING" } : bank ? new string[] { "MONEY IN", "MONEY OUT", "INTERNAL TRANSFERS", "CLOSING BALANCE", "ACTIVE DAYS" } : new string[] { "DEBIT MOVEMENT", "CREDIT MOVEMENT", "YEAR-END TRANSFER", "LEDGER BALANCE", "ACTIVE DAYS" };
                    double[] vals = party ? new double[] { m.Account.Kind.Contains("Supplier") ? m.Purchases : m.Sales, m.Account.Kind.Contains("Supplier") ? m.Payments : m.Receipts, m.CurrentPending, Math.Abs(m.Closing), m.CashCoverage } : bank ? new double[] { m.CashIn, m.CashOut, m.TransfersIn + m.TransfersOut, Math.Abs(m.Closing), m.ActiveDays } : new double[] { m.Debit, m.Credit, Math.Abs(m.ClosingTransfers), Math.Abs(m.Closing), m.ActiveDays };
                    string[] notes = party ? new string[] { "Posted voucher movement", "Actual cash-book movement", "Current-year bills only; see reconciliation", m.BalanceLabel, "Can exceed 100%: old bills / advances" } : bank ? new string[] { "Includes internal transfers", "Includes internal transfers", "Separated in the money map", m.BalanceLabel, "Distinct transaction dates" } : new string[] { "All posted debit lines", "All posted credit lines", "Excluded from operating trend", m.BalanceLabel, "Distinct transaction dates" };
                    AddCards(ref y, width, labels, vals, notes, new Color[] { Blue, Teal, Amber, Violet, Teal }, party ? new int[] { 0, 0, 0, 0, 1 } : new int[] { 0, 0, 0, 0, 2 });
                    AddInsights(ref y, width, m.Insights);
                    AddSection(ref y, width, party ? "Billing versus cash, month by month" : bank ? "Money in, money out and the balance" : "The operating rhythm", party ? "Bars compare billing and cash. The line follows the signed ledger balance. Cash can pay old bills, so it need not equal this month's billing." : "Year-end transfers are marked separately; zero balance does not mean zero activity.", 290, delegate(VisualChart c) { c.SetupMonthly(m); });
                    AddSection(ref y, width, "How the opening balance became the closing balance", "A signed bridge, not a claim that every receipt belongs to a current-year bill.", 245, delegate(VisualChart c) { c.SetupBridge(m); });
                    int half2 = (width - 14) / 2, row = y;
                    AddPanelAt(18, row, half2, 330, "What is being bought / sold", "Item value blocks; quantities and average rates stay within their unit. Click a block for evidence.", delegate(VisualChart c) { c.SetupItems(m.ItemMix.Take(12).ToList()); });
                    AddPanelAt(18 + half2 + 14, row, width - half2 - 14, 330, "Current bill settlement", "Each lane is a bill: settled portion + still pending. Opening references are excluded. Status is as stored in the database.", delegate(VisualChart c) { c.SetupBills(m.Bills.OrderByDescending(b => b.Pending).ThenByDescending(b => b.Date).Take(10).ToList(), m.DataTo); });
                    y += 346;
                    AddSection(ref y, width, "The money map", "Debit-side sources on the left, this ledger in the middle, credit-side destinations on the right. Width encodes value; these are posting relationships, not assumed bank transfers.", 350, delegate(VisualChart c) { c.SetupFlow(m); });
                    AddSection(ref y, width, "When activity happens", "Each cell is a month and activity type. Darker means more posted value. Blank means no entry, not zero balance.", 250, delegate(VisualChart c) { c.SetupHeatmap(m); });
                    AddSection(ref y, width, "Evidence, when you want it", "Click a transaction to inspect the original voucher. This is supporting evidence, not the main interface.", 260, delegate(VisualChart c) { c.SetupEvidence(m.Lines.OrderByDescending(l => l.Date).ThenByDescending(l => l.Amount).Take(30).ToList(), _snapshot); });
                }
                _canvas.ResumeLayout(true);
            }
            catch (Exception ex) { _canvas.ResumeLayout(); _status.Text = "Could not build this view: " + ex.Message; }
        }
        void AddHeader(ref int y, int width, string title, string subtitle)
        {
            _title = new Label { Text = title, Font = new Font("Segoe UI", 19f, FontStyle.Bold), ForeColor = Ink, Location = new Point(18, y), Size = new Size(width, 38), AutoEllipsis = true };
            _subtitle = new Label { Text = subtitle, Font = new Font("Segoe UI", 9f), ForeColor = Muted, Location = new Point(20, y + 40), Size = new Size(width, 40) };
            _canvas.Controls.Add(_title); _canvas.Controls.Add(_subtitle); y += 90;
        }
        void AddCards(ref int y, int width, string[] labels, double[] values, string[] notes, Color[] colors, int[] formats = null)
        {
            int gap = 10, w = (width - gap * 4) / 5;
            for (int i = 0; i < 5; i++)
            {
                Panel p = new Panel { Location = new Point(18 + i * (w + gap), y), Size = new Size(w, 106), BackColor = Color.White };
                Panel stripe = new Panel { Dock = DockStyle.Top, Height = 4, BackColor = colors[i] };
                Label lab = new Label { Text = labels[i], Location = new Point(12, 14), Size = new Size(w - 24, 18), Font = new Font("Segoe UI", 7.8f, FontStyle.Bold), ForeColor = Muted };
                int format = formats == null ? 0 : formats[i];
                Label val = new Label { Text = format == 1 ? values[i].ToString("N1") + "%" : format == 2 ? values[i].ToString("N0") : Compact(values[i]), Location = new Point(12, 36), Size = new Size(w - 20, 34), Font = new Font("Segoe UI", 20f, FontStyle.Bold), ForeColor = Ink };
                Label note = new Label { Text = notes[i], Location = new Point(12, 76), Size = new Size(w - 20, 27), Font = new Font("Segoe UI", 7.5f), ForeColor = Muted };
                if (format != 2) _tip.SetToolTip(val, "Rs. " + Helpers.FmtAmt(values[i])); p.Controls.AddRange(new Control[] { note, val, lab, stripe }); _canvas.Controls.Add(p);
            }
            y += 120;
        }
        static string Compact(double v)
        { double a = Math.Abs(v); string sign = v < 0 ? "-" : ""; if (a >= 10000000) return sign + "Rs. " + (a / 10000000).ToString("N2") + " cr"; if (a >= 100000) return sign + "Rs. " + (a / 100000).ToString("N2") + " L"; return sign + "Rs. " + a.ToString("N0", new CultureInfo("en-IN")); }
        void AddInsights(ref int y, int width, List<LedgerInsight> insights)
        {
            if (insights.Count == 0) return;
            List<LedgerInsight> ordered = insights.OrderByDescending(i => i.Severity == "warning" ? 2 : i.Severity == "attention" ? 1 : 0).Take(6).ToList();
            int h = 36 + ordered.Count * 43;
            Panel p = new Panel { Location = new Point(18, y), Size = new Size(width, h), BackColor = Color.White };
            Label title = new Label { Text = "WHAT DESERVES A CLOSER LOOK", Location = new Point(14, 10), AutoSize = true, Font = new Font("Segoe UI", 9f, FontStyle.Bold), ForeColor = Ink }; p.Controls.Add(title);
            for (int i = 0; i < ordered.Count; i++)
            {
                LedgerInsight ins = ordered[i]; Color c = ins.Severity == "warning" ? Red : ins.Severity == "attention" ? Amber : Teal;
                Panel dot = new Panel { Location = new Point(16, 43 + i * 43), Size = new Size(7, 27), BackColor = c };
                Label text = new Label { Text = ins.Title + "  —  " + ins.Detail, Location = new Point(34, 39 + i * 43), Size = new Size(width - 50, 39), ForeColor = Ink, Font = new Font("Segoe UI", 8.5f) };
                _tip.SetToolTip(text, ins.Detail); p.Controls.Add(dot); p.Controls.Add(text);
            }
            _canvas.Controls.Add(p); y += h + 14;
        }
        void AddSection(ref int y, int width, string title, string subtitle, int height, Action<VisualChart> setup) { AddPanelAt(18, y, width, height, title, subtitle, setup); y += height + 14; }
        void AddPanelAt(int x, int y, int width, int height, string title, string subtitle, Action<VisualChart> setup)
        {
            Panel p = new Panel { Location = new Point(x, y), Size = new Size(width, height), BackColor = Color.White };
            Label t = new Label { Text = title, Location = new Point(14, 10), Size = new Size(width - 28, 23), Font = new Font("Segoe UI", 11f, FontStyle.Bold), ForeColor = Ink };
            Label s = new Label { Text = subtitle, Location = new Point(15, 35), Size = new Size(width - 30, 31), Font = new Font("Segoe UI", 8f), ForeColor = Muted };
            VisualChart c = new VisualChart { Location = new Point(10, 69), Size = new Size(width - 20, height - 78), BackColor = Color.White }; setup(c);
            c.BubbleClicked += delegate(string code) { SelectLedger(code); };
            c.VoucherClicked += delegate(string key) { ShowVoucherEvidence(key); if (OpenVoucherRequested != null) OpenVoucherRequested(key); };
            p.Controls.Add(t); p.Controls.Add(s); p.Controls.Add(c); _canvas.Controls.Add(p);
        }
        void AddLedgerButtons(ref int y, int width, List<LedgerMetrics> metrics)
        {
            Label title = new Label { Text = "OPEN A LEDGER STORY", Location = new Point(18, y), Size = new Size(width, 25), Font = new Font("Segoe UI", 11f, FontStyle.Bold), ForeColor = Ink }; _canvas.Controls.Add(title); y += 32;
            int w = (width - 30) / 3, i = 0;
            foreach (LedgerMetrics m in metrics)
            { Button b = MakeButton(m.Account.Name + "  |  " + Compact(Math.Abs(m.Closing)), w); b.Location = new Point(18 + (i % 3) * (w + 15), y + (i / 3) * 38); b.AutoEllipsis = true; string code = m.Account.Code; b.Click += delegate { SelectLedger(code); }; _tip.SetToolTip(b, m.Account.Kind + " | " + m.Account.Code); _canvas.Controls.Add(b); i++; }
            y += ((i + 2) / 3) * 38 + 20;
        }

        public void LoadSnapshotForTest(LedgerSnapshot snapshot)
        {
            _snapshot = snapshot; _loading = false; _overview = snapshot.Overview(snapshot.FyFrom, snapshot.FyTo);
            _from.Value = snapshot.FyFrom; _to.Value = snapshot.FyTo; FillLedgers(); ShowOverview();
        }
        public void SaveCanvasPng(string path)
        {
            int right = 0, bottom = 0; foreach (Control c in _canvas.Controls) { right = Math.Max(right, c.Right); bottom = Math.Max(bottom, c.Bottom); }
            using (Bitmap bmp = new Bitmap(Math.Max(1, right + 20), Math.Max(1, bottom + 20)))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.Clear(Paper);
                    foreach (Control c in _canvas.Controls)
                    {
                        if (c.Width <= 0 || c.Height <= 0) continue;
                        using (Bitmap tile = new Bitmap(c.Width, c.Height)) { c.DrawToBitmap(tile, new Rectangle(0, 0, c.Width, c.Height)); g.DrawImageUnscaled(tile, c.Location); }
                    }
                }
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
        }
        void ShowVoucherEvidence(string key)
        {
            if (_snapshot == null) return;
            List<LedgerLine> lines = _snapshot.Lines.Where(l => l.Key == key).ToList();
            if (lines.Count == 0) { MessageBox.Show(this, "The source voucher is not present in EFF. It may be a carried-forward reference.", "Source evidence"); return; }
            LedgerLine first = lines[0];
            using (Form f = new Form { Text = "Read-only source | " + first.Book + " #" + first.Voucher + " | " + Helpers.FmtDate(first.Date), Width = 1000, Height = 620, StartPosition = FormStartPosition.CenterParent, Font = new Font("Segoe UI", 9f), BackColor = Paper })
            {
                Label head = new Label { Dock = DockStyle.Top, Height = 66, Padding = new Padding(16, 10, 16, 4), Text = first.Book + "  |  " + Helpers.FmtDate(first.Date) + "  |  ID " + first.Id + "\n" + first.Narration, ForeColor = Ink, Font = new Font("Segoe UI", 11f, FontStyle.Bold) };
                TabControl tabs = new TabControl { Dock = DockStyle.Fill };
                TabPage ledgerTab = new TabPage("Ledger lines"); DataGridView grid = EvidenceGrid();
                grid.Columns.Add("Account", "Account"); grid.Columns.Add("Name", "Name"); grid.Columns.Add("Side", "Dr/Cr"); grid.Columns.Add("Amount", "Amount"); grid.Columns.Add("Narration", "Narration");
                grid.Columns[0].Width = 105; grid.Columns[1].Width = 230; grid.Columns[2].Width = 55; grid.Columns[3].Width = 110; grid.Columns[4].Width = 330;
                foreach (LedgerLine l in lines) grid.Rows.Add(l.Account, _snapshot.AccountName(l.Account), l.Signed > 0 ? "Dr" : "Cr", Helpers.FmtAmt(l.Amount), l.Narration);
                ledgerTab.Controls.Add(grid); tabs.TabPages.Add(ledgerTab);
                List<LedgerItemSale> items = _snapshot.ItemSales.Where(i => i.Key == key).ToList();
                if (items.Count > 0) { TabPage itemTab = new TabPage("Items"); DataGridView ig = EvidenceGrid(); ig.Columns.Add("Item", "Item"); ig.Columns.Add("Qty", "Quantity"); ig.Columns.Add("Unit", "Unit"); ig.Columns.Add("Rate", "Rate"); ig.Columns.Add("Value", "Net value"); ig.Columns[0].Width = 360; foreach (LedgerItemSale i in items) ig.Rows.Add(i.Name, i.Quantity.ToString("N3"), i.Unit, Helpers.FmtAmt(i.Rate), Helpers.FmtAmt(i.Value)); itemTab.Controls.Add(ig); tabs.TabPages.Add(itemTab); }
                List<LedgerPaymentLink> links = _snapshot.Links.Where(l => l.BillId == first.Id && l.BillBT == first.BT).ToList();
                if (links.Count > 0) { TabPage linkTab = new TabPage("Bill allocations"); DataGridView lg = EvidenceGrid(); lg.Columns.Add("Date", "Payment date"); lg.Columns.Add("Voucher", "Payment voucher"); lg.Columns.Add("Amount", "Allocated"); lg.Columns.Add("Discount", "Discount"); lg.Columns.Add("Days", "Days after bill"); foreach (LedgerPaymentLink l in links) lg.Rows.Add(Helpers.FmtDate(l.Date), l.BT + " " + l.Voucher, Helpers.FmtAmt(l.Amount), Helpers.FmtAmt(l.Discount), l.Days); linkTab.Controls.Add(lg); tabs.TabPages.Add(linkTab); }
                Label note = new Label { Dock = DockStyle.Bottom, Height = 30, Padding = new Padding(14, 6, 10, 0), Text = "Source evidence only. No posting or editing is available in this window.", ForeColor = Teal };
                f.Controls.Add(tabs); f.Controls.Add(note); f.Controls.Add(head); tabs.BringToFront(); f.ShowDialog(this);
            }
        }
        static DataGridView EvidenceGrid()
        { return new DataGridView { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false, BackgroundColor = Color.White, BorderStyle = BorderStyle.None, AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells, SelectionMode = DataGridViewSelectionMode.FullRowSelect }; }

        void Export()
        {
            if (_snapshot == null) { MessageBox.Show(this, "Connect to a database first."); return; }
            using (SaveFileDialog d = new SaveFileDialog { Title = "Export ledger visual and evidence", Filter = "PNG image (*.png)|*.png|CSV evidence (*.csv)|*.csv|HTML report (*.html)|*.html", FileName = (_current == null ? "Company" : _current.Account.Code) + "-ledger-story.png" })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    string ext = Path.GetExtension(d.FileName).ToLowerInvariant();
                    if (ext == ".png")
                    {
                        SaveCanvasPng(d.FileName);
                    }
                    else if (_current != null)
                    {
                        StringBuilder sb = new StringBuilder(); bool html = ext == ".html";
                        if (html) sb.AppendLine("<!doctype html><meta charset=\"utf-8\"><title>Ledger evidence</title><style>body{font:14px Segoe UI,sans-serif;color:#18283e;margin:32px}table{border-collapse:collapse;width:100%}td,th{padding:8px;border-bottom:1px solid #ddd;text-align:left}th{background:#eef2f7}</style><h1>" + Html(_current.Account.Name) + "</h1><p>Read-only evidence. " + Helpers.FmtDate(_current.From) + " to " + Helpers.FmtDate(_current.To) + "</p><table><tr><th>Date</th><th>Book</th><th>Voucher</th><th>Side</th><th>Amount</th><th>Narration</th></tr>");
                        else sb.AppendLine("Date,Book,Voucher,Side,Amount,Narration,Account");
                        foreach (LedgerLine l in _current.Lines)
                        {
                            if (html) sb.AppendLine("<tr><td>" + Helpers.FmtDate(l.Date) + "</td><td>" + Html(l.Book) + "</td><td>" + Html(l.Voucher) + "</td><td>" + (l.Signed > 0 ? "Dr" : "Cr") + "</td><td>" + Helpers.FmtAmt(l.Amount) + "</td><td>" + Html(l.Narration) + "</td></tr>");
                            else sb.AppendLine(Csv(Helpers.FmtDate(l.Date)) + "," + Csv(l.Book) + "," + Csv(l.Voucher) + "," + (l.Signed > 0 ? "D" : "C") + "," + l.Amount.ToString("F2", CultureInfo.InvariantCulture) + "," + Csv(l.Narration) + "," + Csv(_current.Account.Code));
                        }
                        if (html) sb.AppendLine("</table>"); File.WriteAllText(d.FileName, sb.ToString(), new UTF8Encoding(true));
                    }
                    else { MessageBox.Show(this, "Open a ledger before exporting CSV/HTML evidence. Company overview can be exported as PNG."); return; }
                    _status.Text = "Exported: " + d.FileName;
                }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "Export failed"); }
            }
        }
        static string Csv(string s) { return "\"" + (s ?? "").Replace("\"", "\"\"") + "\""; }
        static string Html(string s) { return System.Net.WebUtility.HtmlEncode(s ?? ""); }

        sealed class Hit { public RectangleF Bounds; public string Text, Code, Voucher; }
        sealed class VisualChart : Control
        {
            enum Mode { Empty, Monthly, Bridge, Items, Bills, Flow, Heatmap, Overview, Rank, Radar, Evidence }
            Mode _mode;
            readonly List<Hit> _hits = new List<Hit>();
            LedgerMetrics _m; LedgerOverview _o; LedgerSnapshot _s;
            List<NameValue> _rank = new List<NameValue>(), _items = new List<NameValue>();
            List<LedgerBill> _bills = new List<LedgerBill>(); List<LedgerLine> _evidence = new List<LedgerLine>(); List<LedgerMetrics> _radar = new List<LedgerMetrics>();
            Color _color = Blue; DateTime _dataTo; readonly ToolTip _tooltip = new ToolTip(); string _tipText = "";
            public event Action<string> BubbleClicked; public event Action<string> VoucherClicked;
            public VisualChart() { DoubleBuffered = true; SetStyle(ControlStyles.ResizeRedraw, true); Cursor = Cursors.Hand; }
            public void SetupMonthly(LedgerMetrics m) { _m = m; _mode = Mode.Monthly; Invalidate(); }
            public void SetupBridge(LedgerMetrics m) { _m = m; _mode = Mode.Bridge; Invalidate(); }
            public void SetupItems(List<NameValue> items) { _items = items; _mode = Mode.Items; Invalidate(); }
            public void SetupBills(List<LedgerBill> bills, DateTime dataTo) { _bills = bills; _dataTo = dataTo; _mode = Mode.Bills; Invalidate(); }
            public void SetupFlow(LedgerMetrics m) { _m = m; _mode = Mode.Flow; Invalidate(); }
            public void SetupHeatmap(LedgerMetrics m) { _m = m; _mode = Mode.Heatmap; Invalidate(); }
            public void SetupOverview(LedgerOverview o) { _o = o; _mode = Mode.Overview; Invalidate(); }
            public void SetupRank(List<NameValue> values, Color color, bool unused) { _rank = values; _color = color; _mode = Mode.Rank; Invalidate(); }
            public void SetupRadar(List<LedgerMetrics> values, DateTime dataTo) { _radar = values; _dataTo = dataTo; _mode = Mode.Radar; Invalidate(); }
            public void SetupEvidence(List<LedgerLine> lines, LedgerSnapshot s) { _evidence = lines; _s = s; _mode = Mode.Evidence; Invalidate(); }
            void DrawText(Graphics g, string text, RectangleF r, float size, Color color, bool bold = false, StringAlignment align = StringAlignment.Near)
            { using (Font f = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular)) using (Brush b = new SolidBrush(color)) using (StringFormat sf = new StringFormat { Alignment = align, LineAlignment = StringAlignment.Near, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap }) g.DrawString(text, f, b, r, sf); }
            void Line(Graphics g, Color c, float x1, float y1, float x2, float y2, float width = 1) { using (Pen p = new Pen(c, width)) g.DrawLine(p, x1, y1, x2, y2); }
            void Fill(Graphics g, Color c, RectangleF r) { using (Brush b = new SolidBrush(c)) g.FillRectangle(b, r); }
            void HitRect(RectangleF r, string tip, string code = null, string voucher = null) { _hits.Add(new Hit { Bounds = r, Text = tip, Code = code, Voucher = voucher }); }
            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e); Graphics g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(Color.White); _hits.Clear();
                if (_mode == Mode.Monthly) DrawMonthly(g); else if (_mode == Mode.Bridge) DrawBridge(g); else if (_mode == Mode.Items) DrawItems(g); else if (_mode == Mode.Bills) DrawBills(g); else if (_mode == Mode.Flow) DrawFlow(g); else if (_mode == Mode.Heatmap) DrawHeatmap(g); else if (_mode == Mode.Overview) DrawOverview(g); else if (_mode == Mode.Rank) DrawRank(g); else if (_mode == Mode.Radar) DrawRadar(g); else if (_mode == Mode.Evidence) DrawEvidence(g);
            }
            void DrawMonthly(Graphics g)
            {
                if (_m == null || _m.Months.Count == 0) return;
                RectangleF plot = new RectangleF(58, 22, Width - 80, Height - 58); double max = Math.Max(1, _m.Months.Max(x => Math.Max(Math.Max(x.Billing, x.Purchases), Math.Max(x.Cash, Math.Max(x.MoneyIn, x.MoneyOut)))));
                double minBal = Math.Min(0, _m.Months.Min(x => x.Balance)), maxBal = Math.Max(1, _m.Months.Max(x => x.Balance));
                for (int i = 0; i <= 3; i++) { float y = plot.Bottom - plot.Height * i / 3; Line(g, Paper, plot.Left, y, plot.Right, y); DrawText(g, Compact(max * i / 3), new RectangleF(0, y - 8, 54, 18), 7, Muted, false, StringAlignment.Far); }
                float step = plot.Width / _m.Months.Count; List<PointF> balance = new List<PointF>();
                for (int i = 0; i < _m.Months.Count; i++)
                {
                    LedgerMonth mo = _m.Months[i]; float x = plot.Left + i * step; double a = mo.Purchases > 0 ? mo.Purchases : mo.Billing > 0 ? mo.Billing : mo.MoneyIn; double b = mo.Cash > 0 ? mo.Cash : mo.MoneyOut;
                    float bw = Math.Min(18, step / 3); RectangleF ra = new RectangleF(x + step / 2 - bw - 2, plot.Bottom - (float)(Math.Abs(a) / max * plot.Height), bw, (float)(Math.Abs(a) / max * plot.Height)); RectangleF rb = new RectangleF(x + step / 2 + 2, plot.Bottom - (float)(Math.Abs(b) / max * plot.Height), bw, (float)(Math.Abs(b) / max * plot.Height));
                    Fill(g, Blue, ra); Fill(g, Teal, rb); DrawText(g, mo.Month.ToString("MMM"), new RectangleF(x, plot.Bottom + 7, step, 18), 7.5f, Muted, false, StringAlignment.Center);
                    balance.Add(new PointF(x + step / 2, plot.Bottom - (float)((mo.Balance - minBal) / Math.Max(1, maxBal - minBal) * plot.Height * .85)));
                    HitRect(new RectangleF(x, plot.Top, step, plot.Height), mo.Month.ToString("MMM yyyy") + "\nBilling: " + Helpers.FmtAmt(mo.Billing) + "\nPurchases: " + Helpers.FmtAmt(mo.Purchases) + "\nCash: " + Helpers.FmtAmt(mo.Cash) + "\nBalance: " + Helpers.FmtAmt(mo.Balance) + "\nYear-end transfer: " + Helpers.FmtAmt(mo.Closing));
                }
                if (balance.Count > 1) using (Pen p = new Pen(Violet, 2)) g.DrawLines(p, balance.ToArray());
                foreach (PointF p in balance) Fill(g, Violet, new RectangleF(p.X - 3, p.Y - 3, 6, 6));
                DrawText(g, "Blue: billing / in    Teal: cash / out    Purple line: signed balance (own scale)", new RectangleF(58, 0, Width - 70, 18), 7.5f, Muted);
            }
            void DrawBridge(Graphics g)
            {
                bool party = _m.Account.Kind.Contains("Customer") || _m.Account.Kind.Contains("Supplier");
                List<NameValue> values = new List<NameValue> { new NameValue("Opening", _m.Opening, "Signed balance"), new NameValue("Sales", _m.Sales, "Posted sales"), new NameValue("Purchases", -_m.Purchases, "Posted purchases"), new NameValue("Cash in", party ? -_m.Payments : _m.CashIn, "Cash movement"), new NameValue("Cash out", party ? _m.Receipts * -1 : -_m.CashOut, "Cash movement"), new NameValue("Journals", _m.JournalNet, "Non-closing JV"), new NameValue("Other + closing", _m.OtherNet + _m.ClosingTransfers, "Other books / year-end"), new NameValue("Closing", _m.Closing, "Reconciled movement") };
                if (party) { bool customer = _m.Account.Kind.Contains("Customer"); values[3] = new NameValue("Payments / refunds", customer ? -_m.Payments : _m.Payments, ""); values[4] = new NameValue("Receipts", customer ? _m.Receipts : -_m.Receipts, ""); }
                values = values.Where((v, i) => i == 0 || i == values.Count - 1 || Math.Abs(v.Value) > .005).ToList();
                double running = 0, low = Math.Min(0, _m.Opening), high = Math.Max(0, _m.Opening); List<double[]> ranges = new List<double[]>();
                for (int i = 0; i < values.Count; i++) { double start = i == 0 || i == values.Count - 1 ? 0 : running; double end = i == 0 ? values[i].Value : i == values.Count - 1 ? _m.Closing : running + values[i].Value; ranges.Add(new double[] { start, end }); running = end; low = Math.Min(low, Math.Min(start, end)); high = Math.Max(high, Math.Max(start, end)); }
                RectangleF p = new RectangleF(45, 12, Width - 60, Height - 48); double span = Math.Max(1, high - low); float zero = p.Bottom - (float)((0 - low) / span * p.Height); Line(g, Color.FromArgb(180, 193, 210), p.Left, zero, p.Right, zero);
                float step = p.Width / values.Count;
                for (int i = 0; i < values.Count; i++) { double a = ranges[i][0], b = ranges[i][1]; float y1 = p.Bottom - (float)((a - low) / span * p.Height), y2 = p.Bottom - (float)((b - low) / span * p.Height); RectangleF r = new RectangleF(i * step + p.Left + step * .18f, Math.Min(y1, y2), step * .64f, Math.Max(2, Math.Abs(y1 - y2))); Fill(g, i == 0 || i == values.Count - 1 ? Violet : b >= a ? Blue : Teal, r); DrawText(g, values[i].Name, new RectangleF(i * step + p.Left, p.Bottom + 7, step, 28), 7.3f, Muted, false, StringAlignment.Center); DrawText(g, Compact(values[i].Value), new RectangleF(i * step + p.Left, Math.Min(y1, y2) - 17, step, 16), 7.2f, Ink, true, StringAlignment.Center); HitRect(r, values[i].Name + ": Rs. " + Helpers.FmtAmt(values[i].Value)); }
            }
            void DrawItems(Graphics g)
            {
                if (_items.Count == 0) { DrawText(g, "No item lines in this period.\nBank, tax and journal ledgers can still have a full money story.", new RectangleF(20, 25, Width - 40, 70), 10, Muted); return; }
                RectangleF r = new RectangleF(4, 4, Width - 8, Height - 8); double total = _items.Sum(x => Math.Abs(x.Value)); Color[] colors = { Blue, Teal, Violet, Amber };
                for (int i = 0; i < _items.Count; i++)
                {
                    double fraction = Math.Abs(_items[i].Value) / Math.Max(1, total); bool horizontal = r.Width >= r.Height;
                    RectangleF block = horizontal ? new RectangleF(r.X, r.Y, (float)(r.Width * fraction), r.Height) : new RectangleF(r.X, r.Y, r.Width, (float)(r.Height * fraction));
                    RectangleF inner = new RectangleF(block.X + 2, block.Y + 2, Math.Max(1, block.Width - 4), Math.Max(1, block.Height - 4)); Fill(g, colors[i % colors.Length], inner);
                    if (inner.Width > 65 && inner.Height > 30) { DrawText(g, _items[i].Name, new RectangleF(inner.X + 7, inner.Y + 7, inner.Width - 14, 24), 8.5f, Color.White, true); DrawText(g, Compact(_items[i].Value), new RectangleF(inner.X + 7, inner.Y + 31, inner.Width - 14, 20), 8, Color.White); }
                    HitRect(inner, _items[i].Name + "\nRs. " + Helpers.FmtAmt(_items[i].Value) + "\n" + _items[i].Detail);
                    if (horizontal) { r.X += block.Width; r.Width -= block.Width; } else { r.Y += block.Height; r.Height -= block.Height; }
                }
            }
            void DrawBills(Graphics g)
            {
                if (_bills.Count == 0) { DrawText(g, "No current-year bill register rows.\nThis does not mean the balance is settled: see opening balance, cash and journals.", new RectangleF(20, 25, Width - 40, 80), 10, Muted); return; }
                float rowH = Math.Min(25, (Height - 8) / _bills.Count), labelW = Math.Min(155, Width * .36f), barW = Width - labelW - 95;
                for (int i = 0; i < _bills.Count; i++)
                {
                    LedgerBill b = _bills[i]; float y = i * rowH + 3; DrawText(g, b.Date.ToString("dd MMM") + "  " + b.BT + " " + b.Voucher, new RectangleF(2, y, labelW - 5, rowH), 7.5f, Ink);
                    double total = Math.Max(Math.Max(1, b.Amount), b.Adjusted); RectangleF settled = new RectangleF(labelW, y + 3, (float)(Math.Min(b.Amount, Math.Max(0, b.Adjusted)) / total * barW), rowH - 8), pending = new RectangleF(settled.Right, y + 3, (float)(b.Pending / total * barW), rowH - 8);
                    Fill(g, Paper, new RectangleF(labelW, y + 3, barW, rowH - 8)); Fill(g, Teal, settled); Fill(g, b.OverAdjusted ? Red : Amber, pending); DrawText(g, Compact(b.Pending), new RectangleF(labelW + barW + 6, y, 86, rowH), 7.5f, Muted);
                    HitRect(new RectangleF(0, y, Width, rowH), b.BT + " " + b.Voucher + " | " + Helpers.FmtDate(b.Date) + "\nBill: " + Helpers.FmtAmt(b.Amount) + "\nAdjusted (including discounts): " + Helpers.FmtAmt(b.Adjusted) + "\nPending: " + Helpers.FmtAmt(b.Pending) + "\nAge at latest data: " + b.AgeDays + " days", null, b.Key);
                }
            }
            void DrawFlow(Graphics g)
            {
                List<NameValue> src = _m.Sources.Take(5).ToList(), dst = _m.Destinations.Take(5).ToList(); float midX = Width * .5f, nodeW = Math.Min(190, Width * .24f), sideW = midX - nodeW / 2 - 28;
                double leftTotal = Math.Max(1, src.Sum(x => x.Value)), rightTotal = Math.Max(1, dst.Sum(x => x.Value)); float yL = 8, yR = 8, h = Height - 20;
                for (int i = 0; i < src.Count; i++) { float bh = (float)(src[i].Value / leftTotal * h); RectangleF node = new RectangleF(4, yL + 2, sideW, Math.Max(10, bh - 4)); Fill(g, Color.FromArgb(232, 240, 253), node); DrawText(g, src[i].Name, new RectangleF(node.X + 6, node.Y + 3, node.Width - 12, Math.Max(12, node.Height - 5)), 8, Ink); using (Pen p = new Pen(Color.FromArgb(100, Blue), Math.Max(2, bh * .35f))) g.DrawBezier(p, node.Right, node.Y + node.Height / 2, midX - 45, node.Y + node.Height / 2, midX - 60, Height / 2, midX - nodeW / 2, Height / 2); HitRect(node, "Debit-side relationship\n" + src[i].Name + "\nRs. " + Helpers.FmtAmt(src[i].Value)); yL += bh; }
                for (int i = 0; i < dst.Count; i++) { float bh = (float)(dst[i].Value / rightTotal * h); RectangleF node = new RectangleF(Width - sideW - 4, yR + 2, sideW, Math.Max(10, bh - 4)); Fill(g, Color.FromArgb(229, 245, 241), node); DrawText(g, dst[i].Name, new RectangleF(node.X + 6, node.Y + 3, node.Width - 12, Math.Max(12, node.Height - 5)), 8, Ink); using (Pen p = new Pen(Color.FromArgb(100, Teal), Math.Max(2, bh * .35f))) g.DrawBezier(p, midX + nodeW / 2, Height / 2, midX + 60, Height / 2, node.Left - 30, node.Y + node.Height / 2, node.Left, node.Y + node.Height / 2); HitRect(node, "Credit-side relationship\n" + dst[i].Name + "\nRs. " + Helpers.FmtAmt(dst[i].Value)); yR += bh; }
                RectangleF center = new RectangleF(midX - nodeW / 2, Height / 2 - 43, nodeW, 86); Fill(g, Ink, center); DrawText(g, _m.Account.Name, new RectangleF(center.X + 10, center.Y + 12, center.Width - 20, 35), 10, Color.White, true); DrawText(g, mBalance(), new RectangleF(center.X + 10, center.Y + 53, center.Width - 20, 22), 9, Color.White);
            }
            string mBalance() { return Compact(Math.Abs(_m.Closing)) + " " + (_m.Closing < 0 ? "Cr" : "Dr"); }
            void DrawHeatmap(Graphics g)
            {
                string[] cats = _m.Categories.Select(x => x.Name).Distinct().Take(6).ToArray(); if (cats.Length == 0 || _m.Months.Count == 0) { DrawText(g, "No activity to map in this period.", new RectangleF(20, 20, Width - 40, 40), 10, Muted); return; }
                float left = 135, cw = (Width - left - 10) / _m.Months.Count, rh = Math.Min(27, (Height - 25) / cats.Length); double max = 1;
                Dictionary<string, double> values = new Dictionary<string, double>();
                foreach (LedgerLine l in _m.Lines) { string k = l.Date.ToString("yyyyMM") + "|" + l.Category; values[k] = (values.ContainsKey(k) ? values[k] : 0) + l.Amount; max = Math.Max(max, values[k]); }
                for (int r = 0; r < cats.Length; r++) { DrawText(g, cats[r], new RectangleF(0, r * rh + 22, left - 8, rh), 8, Muted); for (int c = 0; c < _m.Months.Count; c++) { string k = _m.Months[c].Month.ToString("yyyyMM") + "|" + cats[r]; double v = values.ContainsKey(k) ? values[k] : 0; int alpha = v == 0 ? 18 : (int)(55 + 190 * Math.Sqrt(v / max)); RectangleF cell = new RectangleF(left + c * cw + 2, r * rh + 22, cw - 4, rh - 4); Fill(g, Color.FromArgb(alpha, Teal), cell); HitRect(cell, cats[r] + " | " + _m.Months[c].Month.ToString("MMM yyyy") + "\nRs. " + Helpers.FmtAmt(v)); } }
                for (int c = 0; c < _m.Months.Count; c++) DrawText(g, _m.Months[c].Month.ToString("MMM"), new RectangleF(left + c * cw, 0, cw, 20), 7, Muted, false, StringAlignment.Center);
            }
            void DrawOverview(Graphics g)
            {
                RectangleF p = new RectangleF(58, 24, Width - 80, Height - 55); double max = Math.Max(1, _o.Months.Max(x => Math.Max(Math.Max(x.Billing, x.Purchases), x.Cash))); float step = p.Width / Math.Max(1, _o.Months.Count);
                for (int i = 0; i < _o.Months.Count; i++) { LedgerMonth m = _o.Months[i]; double[] vals = { m.Billing, m.Purchases, m.Cash }; Color[] colors = { Blue, Amber, Teal }; float bw = Math.Min(20, step / 4); for (int j = 0; j < 3; j++) { float h = (float)(vals[j] / max * p.Height); RectangleF r = new RectangleF(p.Left + i * step + step / 2 + (j - 1) * (bw + 2) - bw / 2, p.Bottom - h, bw, h); Fill(g, colors[j], r); } DrawText(g, m.Month.ToString("MMM"), new RectangleF(p.Left + i * step, p.Bottom + 6, step, 18), 7.5f, Muted, false, StringAlignment.Center); HitRect(new RectangleF(p.Left + i * step, p.Top, step, p.Height), m.Month.ToString("MMM yyyy") + "\nSales: " + Helpers.FmtAmt(m.Billing) + "\nPurchases: " + Helpers.FmtAmt(m.Purchases) + "\nCustomer collections: " + Helpers.FmtAmt(m.Cash)); }
                DrawText(g, "Blue: sales     Amber: purchases     Teal: customer collections", new RectangleF(58, 0, Width - 70, 20), 8, Muted);
            }
            void DrawRank(Graphics g)
            {
                if (_rank.Count == 0) { DrawText(g, "No matching balances in this period.", new RectangleF(15, 25, Width - 30, 40), 10, Muted); return; }
                double max = Math.Max(1, _rank.Max(x => x.Value)); float rh = (Height - 8) / _rank.Count, label = Math.Min(210, Width * .48f), bar = Width - label - 95;
                for (int i = 0; i < _rank.Count; i++) { float y = i * rh; NameValue v = _rank[i]; DrawText(g, v.Name, new RectangleF(2, y + 3, label - 8, rh - 3), 8, Ink); RectangleF r = new RectangleF(label, y + 5, (float)(v.Value / max * bar), Math.Max(4, rh - 10)); Fill(g, _color, r); DrawText(g, Compact(v.Value), new RectangleF(label + bar + 7, y + 3, 88, rh), 8, Muted); HitRect(new RectangleF(0, y, Width, rh), v.Name + "\nRs. " + Helpers.FmtAmt(v.Value) + "\nClick to open ledger", v.Detail); }
            }
            void DrawRadar(Graphics g)
            {
                RectangleF p = new RectangleF(58, 12, Width - 85, Height - 45); double maxBal = Math.Max(1, _radar.Count == 0 ? 1 : _radar.Max(x => Math.Abs(x.Closing))), maxTurn = Math.Max(1, _radar.Count == 0 ? 1 : _radar.Max(x => x.Debit + x.Credit)); int maxDays = Math.Max(90, _radar.Count == 0 ? 90 : _radar.Max(x => x.LastActivity == DateTime.MinValue ? 365 : (_dataTo - x.LastActivity).Days));
                Line(g, Muted, p.Left, p.Bottom, p.Right, p.Bottom); Line(g, Muted, p.Left, p.Top, p.Left, p.Bottom);
                foreach (LedgerMetrics m in _radar.OrderByDescending(x => x.Debit + x.Credit).Take(160)) { int days = m.LastActivity == DateTime.MinValue ? maxDays : (_dataTo - m.LastActivity).Days; float x = p.Left + (float)(Math.Sqrt(Math.Abs(m.Closing) / maxBal) * p.Width), y = p.Bottom - (float)(days / (double)maxDays * p.Height), size = (float)(7 + 19 * Math.Sqrt((m.Debit + m.Credit) / maxTurn)); Color c = m.Account.Kind.Contains("Customer") ? Blue : m.Account.Kind.Contains("Supplier") ? Amber : Teal; RectangleF r = new RectangleF(x - size / 2, y - size / 2, size, size); using (Brush b = new SolidBrush(Color.FromArgb(155, c))) g.FillEllipse(b, r); HitRect(r, m.Account.Name + "\n" + m.Account.Kind + "\nBalance: " + Helpers.FmtAmt(m.Closing) + "\nDays since movement: " + days + "\nClick to investigate", m.Account.Code); }
                DrawText(g, "0", new RectangleF(p.Left - 15, p.Bottom + 4, 20, 18), 8, Muted); DrawText(g, Compact(maxBal), new RectangleF(p.Right - 100, p.Bottom + 4, 100, 18), 8, Muted, false, StringAlignment.Far); DrawText(g, maxDays + " days idle", new RectangleF(0, p.Top, 55, 30), 7.5f, Muted);
            }
            void DrawEvidence(Graphics g)
            {
                float rh = 24; DrawText(g, "DATE                BOOK / VOUCHER                         SIDE             AMOUNT", new RectangleF(4, 0, Width - 8, 20), 7.5f, Muted, true);
                for (int i = 0; i < _evidence.Count && (i + 1) * rh < Height; i++) { LedgerLine l = _evidence[i]; float y = (i + 1) * rh; if (i % 2 == 0) Fill(g, Paper, new RectangleF(0, y, Width, rh)); DrawText(g, Helpers.FmtDate(l.Date) + "     " + l.Book + " #" + l.Voucher, new RectangleF(4, y + 3, Width * .55f, rh - 3), 8, Ink); DrawText(g, l.Signed > 0 ? "Dr" : "Cr", new RectangleF(Width * .57f, y + 3, 35, rh), 8, Muted); DrawText(g, Helpers.FmtAmt(l.Amount), new RectangleF(Width * .65f, y + 3, Width * .34f - 5, rh), 8, Ink, false, StringAlignment.Far); HitRect(new RectangleF(0, y, Width, rh), l.Book + " " + l.Voucher + "\n" + l.Narration + "\nContra: " + _s.AccountName(l.Contra) + "\nClick for voucher evidence", null, l.Key); }
            }
            protected override void OnMouseMove(MouseEventArgs e)
            { base.OnMouseMove(e); Hit h = _hits.LastOrDefault(x => x.Bounds.Contains(e.Location)); string t = h == null ? "" : h.Text; if (t != _tipText) { _tipText = t; _tooltip.SetToolTip(this, t); } Cursor = h != null && (h.Code != null || h.Voucher != null) ? Cursors.Hand : Cursors.Default; }
            protected override void OnMouseClick(MouseEventArgs e)
            { base.OnMouseClick(e); Hit h = _hits.LastOrDefault(x => x.Bounds.Contains(e.Location)); if (h == null) return; if (h.Code != null && BubbleClicked != null) BubbleClicked(h.Code); else if (h.Voucher != null) { if (VoucherClicked != null) VoucherClicked(h.Voucher); else MessageBox.Show(this, h.Text, "Source evidence", MessageBoxButtons.OK, MessageBoxIcon.Information); } else if (!string.IsNullOrEmpty(h.Text)) _tooltip.SetToolTip(this, h.Text); }
        }
    }
}
