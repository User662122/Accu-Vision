using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using AccuVisionRepost.Core;

namespace AccuVisionRepost.Ui
{
    /// <summary>Searchable picker for ledger accounts and items (by name or code).</summary>
    public sealed class LookupDialog : Form
    {
        private readonly TextBox _txt;
        private readonly ListBox _list;
        private readonly DbProbe _probe;
        private readonly bool _items;
        private readonly bool _leafOnly;
        private List<object> _all = new List<object>();

        public AccountRow PickedAccount { get; private set; }
        public ItemRow PickedItem { get; private set; }

        private LookupDialog(DbProbe probe, bool items, bool leafOnly, string initial)
        {
            _probe = probe;
            _items = items;
            _leafOnly = leafOnly;
            Text = items ? "Select Item" : "Select Account";
            Width = 640; Height = 480;
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9f);

            _txt = new TextBox();
            _txt.Dock = DockStyle.Top;
            _txt.Text = initial ?? "";
            _txt.TextChanged += delegate { Refill(); };
            _txt.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Down && _list.Items.Count > 0) { _list.Focus(); _list.SelectedIndex = 0; }
                if (e.KeyCode == Keys.Enter) Accept();
                if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
            };

            _list = new ListBox();
            _list.Dock = DockStyle.Fill;
            _list.DoubleClick += delegate { Accept(); };
            _list.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter) Accept();
                if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
            };

            Label hint = new Label();
            hint.Text = items ? "Type item name or code, double-click to select." : "Type account name or code, double-click to select. (G) = group account - cannot be used in a voucher.";
            hint.Dock = DockStyle.Bottom;
            hint.AutoSize = false;
            hint.Height = 22;

            Controls.Add(_list);
            Controls.Add(hint);
            Controls.Add(_txt);

            if (items)
            {
                List<ItemRow> sorted = new List<ItemRow>(_probe.Items);
                sorted.Sort(delegate(ItemRow a, ItemRow b) { return string.Compare(a.Name.Trim(), b.Name.Trim(), StringComparison.OrdinalIgnoreCase); });
                foreach (ItemRow it in sorted) _all.Add(it);
            }
            else
            {
                List<AccountRow> sorted = new List<AccountRow>(_probe.Accounts.Values);
                sorted.Sort(delegate(AccountRow a, AccountRow b) { return string.Compare(a.Name.Trim(), b.Name.Trim(), StringComparison.OrdinalIgnoreCase); });
                foreach (AccountRow a in sorted)
                {
                    if (leafOnly && a.IsGroup) continue;
                    _all.Add(a);
                }
            }
            Refill();
            Shown += delegate { _txt.Focus(); _txt.SelectAll(); };
        }

        private void Refill()
        {
            string q = Helpers.NormName(_txt.Text);
            _list.BeginUpdate();
            _list.Items.Clear();
            int shown = 0;
            foreach (object o in _all)
            {
                string disp;
                string code;
                string name;
                bool grp = false;
                if (_items)
                {
                    ItemRow it = (ItemRow)o;
                    disp = it.Display; code = it.ItemCode.ToString(); name = it.Name.Trim();
                }
                else
                {
                    AccountRow a = (AccountRow)o;
                    disp = a.Display; code = a.Code; name = a.Name.Trim(); grp = a.IsGroup;
                }
                if (q.Length == 0 || Helpers.NormName(name).Contains(q) || code.IndexOf(_txt.Text.Trim(), StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    if (grp) disp += "   (G)";
                    _list.Items.Add(new Entry(disp, o));
                    shown++;
                    if (shown >= 600) break;
                }
            }
            _list.EndUpdate();
            if (_list.Items.Count == 1) _list.SelectedIndex = 0;
        }

        private sealed class Entry
        {
            public readonly string Text;
            public readonly object Obj;
            public Entry(string text, object obj) { Text = text; Obj = obj; }
            public override string ToString() { return Text; }
        }

        private void Accept()
        {
            Entry e = _list.SelectedItem as Entry;
            if (e == null && _list.Items.Count == 1) e = (Entry)_list.Items[0];
            if (e == null) return;
            if (_items) PickedItem = e.Obj as ItemRow;
            else
            {
                AccountRow a = e.Obj as AccountRow;
                if (a != null && a.IsGroup)
                {
                    MessageBox.Show(this, "That is a group account and cannot be used in a voucher (AccNew would reject it). Pick a ledger under it.",
                        "Group account", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                PickedAccount = a;
            }
            DialogResult = DialogResult.OK;
            Close();
        }

        public static AccountRow PickAccount(IWin32Window owner, DbProbe probe, string initial)
        {
            MainForm mf = owner as MainForm;
            if (mf != null && mf.HeadlessMode)
            {
                List<string> errs = new List<string>();
                AccountRow resolved = HeadlessEngine.ResolveAccount(probe, initial, true, "Account", errs);
                if (resolved != null) return resolved;
                foreach (string e in errs) mf.RecordHeadlessMessage("Select Account", e, MessageBoxIcon.Warning);
                return null;
            }
            using (LookupDialog d = new LookupDialog(probe, false, false, initial))
                return d.ShowDialog(owner) == DialogResult.OK ? d.PickedAccount : null;
        }

        public static ItemRow PickItem(IWin32Window owner, DbProbe probe, string initial)
        {
            MainForm mf = owner as MainForm;
            if (mf != null && mf.HeadlessMode)
            {
                List<string> errs = new List<string>();
                ItemRow resolved = HeadlessEngine.ResolveItem(probe, initial, "Item", errs);
                if (resolved != null) return resolved;
                foreach (string e in errs) mf.RecordHeadlessMessage("Select Item", e, MessageBoxIcon.Warning);
                return null;
            }
            using (LookupDialog d = new LookupDialog(probe, true, false, initial))
                return d.ShowDialog(owner) == DialogResult.OK ? d.PickedItem : null;
        }
    }
}
