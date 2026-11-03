using System;
using System.Collections.Generic;
using System.Data;
using System.Data.OleDb;

namespace AccuVisionRepost.Core
{
    /// <summary>
    /// Minimal, careful wrapper around Microsoft.Jet.OLEDB.4.0 (32-bit only - the
    /// whole application is compiled x86 for this reason).
    /// All queries are parameterised with positional "?" markers exactly like the
    /// proven VBScript injectors in the AccNew analysis kit.
    /// </summary>
    public sealed class JetDb
    {
        public string DbPath { get; private set; }
        public string Password { get; private set; }

        private Dictionary<string, List<string>> _columns;      // table(upper) -> column names (original case)
        private Dictionary<string, string> _tableNames;         // upper -> original case
        private Dictionary<string, int> _colMaxLen;              // TABLE|COL -> max text length (0 = n/a)

        public JetDb(string dbPath, string Password)
        {
            DbPath = dbPath;
            this.Password = Password ?? "";
            _columns = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            _tableNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _colMaxLen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }

        public string BuildConnectionString(bool exclusive)
        {
            // OLE DB Services=-2 disables session pooling: pooled Jet sessions
            // would keep the .mdb open in shared mode and the exclusive
            // posting connection (and file copies) could then be refused.
            string s = "Provider=Microsoft.Jet.OLEDB.4.0;Data Source=" + DbPath + ";Persist Security Info=False;OLE DB Services=-2;";
            if (Password.Length > 0) s += "Jet OLEDB:Database Password=" + Password + ";";
            if (exclusive) s += "Mode=Share Exclusive;";
            return s;
        }

        public OleDbConnection Open(bool exclusive)
        {
            OleDbConnection cn = new OleDbConnection(BuildConnectionString(exclusive));
            cn.Open();
            return cn;
        }

        // ---------------------------------------------------------------- schema

        public void LoadSchema(OleDbConnection cn)
        {
            _columns.Clear(); _tableNames.Clear(); _colMaxLen.Clear();
            DataTable tables = cn.GetSchema("Tables");
            List<string> names = new List<string>();
            foreach (DataRow r in tables.Rows)
            {
                string type = Convert.ToString(r["TABLE_TYPE"]);
                string name = Convert.ToString(r["TABLE_NAME"]);
                if (type == "TABLE" && !name.StartsWith("MSys", StringComparison.OrdinalIgnoreCase))
                    names.Add(name);
            }
            foreach (string name in names) _tableNames[name.ToUpperInvariant()] = name;

            DataTable cols = cn.GetSchema("Columns");
            // GetSchema("Columns") is not guaranteed ordered; sort by ORDINAL_POSITION.
            DataView dv = new DataView(cols);
            dv.Sort = "TABLE_NAME ASC, ORDINAL_POSITION ASC";
            foreach (DataRowView rv in dv)
            {
                DataRow r = rv.Row;
                string t = Convert.ToString(r["TABLE_NAME"]);
                string c = Convert.ToString(r["COLUMN_NAME"]);
                if (!_tableNames.ContainsKey(t.ToUpperInvariant())) continue;
                List<string> list;
                if (!_columns.TryGetValue(t, out list)) { list = new List<string>(); _columns[t] = list; }
                list.Add(c);
                object lenObj = r["CHARACTER_MAXIMUM_LENGTH"];
                int len = (lenObj == DBNull.Value || lenObj == null) ? 0 : Convert.ToInt32(lenObj);
                _colMaxLen[(t + "|" + c).ToUpperInvariant()] = len;
            }
        }

        public bool HasSchema { get { return _tableNames.Count > 0; } }
        public bool TableExists(string table) { return table != null && _tableNames.ContainsKey(table.ToUpperInvariant()); }
        public string RealTable(string table)
        {
            string v;
            return _tableNames.TryGetValue((table ?? "").ToUpperInvariant(), out v) ? v : table;
        }
        public List<string> ColumnsOf(string table)
        {
            List<string> list;
            return _columns.TryGetValue((table ?? "").ToUpperInvariant(), out list) ? list : new List<string>();
        }
        public bool HasColumn(string table, string column)
        {
            foreach (string c in ColumnsOf(table))
                if (string.Equals(c, column, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
        public string RealColumn(string table, string column)
        {
            foreach (string c in ColumnsOf(table))
                if (string.Equals(c, column, StringComparison.OrdinalIgnoreCase)) return c;
            return column;
        }
        public int ColumnMaxLen(string table, string column)
        {
            int v;
            return _colMaxLen.TryGetValue(((table ?? "") + "|" + (column ?? "")).ToUpperInvariant(), out v) ? v : 0;
        }
        public List<string> AllTables()
        {
            List<string> l = new List<string>(_tableNames.Values);
            l.Sort(StringComparer.OrdinalIgnoreCase);
            return l;
        }

        // ---------------------------------------------------------------- helpers

        public static OleDbParameter Param(object value)
        {
            if (value == null || value == DBNull.Value)
            {
                OleDbParameter p0 = new OleDbParameter();
                p0.OleDbType = OleDbType.VarWChar;
                p0.Size = 2;
                p0.Value = DBNull.Value;
                return p0;
            }
            OleDbParameter p = new OleDbParameter();
            if (value is string)
            {
                string s = (string)value;
                p.OleDbType = OleDbType.VarWChar;
                p.Size = Math.Max(2, s.Length * 2);
                p.Value = s;
            }
            else if (value is DateTime)
            {
                p.OleDbType = OleDbType.Date;
                p.Value = value;
            }
            else if (value is double || value is float || value is decimal)
            {
                p.OleDbType = OleDbType.Double;
                p.Value = Convert.ToDouble(value);
            }
            else if (value is bool)
            {
                p.OleDbType = OleDbType.Boolean;
                p.Value = value;
            }
            else if (value is int || value is long || value is short || value is byte)
            {
                p.OleDbType = OleDbType.BigInt;
                p.Value = Convert.ToInt64(value);
            }
            else
            {
                p.OleDbType = OleDbType.VarWChar;
                string s = Convert.ToString(value);
                p.Size = Math.Max(2, s.Length * 2);
                p.Value = s;
            }
            return p;
        }

        /// <summary>Convert an edited value to the exact CLR type of a DataColumn.</summary>
        public static object ConvertForColumn(object value, DataColumn col)
        {
            if (value == null || value == DBNull.Value) return DBNull.Value;
            if (col == null) return value;
            Type t = col.DataType;
            try
            {
                if (t == typeof(string))
                {
                    string s = Convert.ToString(value);
                    if (s.Length == 0) return DBNull.Value;
                    return s;
                }
                if (t == typeof(DateTime)) return Convert.ToDateTime(value);
                if (t == typeof(double)) return Convert.ToDouble(value);
                if (t == typeof(float)) return Convert.ToSingle(value);
                if (t == typeof(decimal)) return Convert.ToDecimal(value);
                if (t == typeof(int)) return Convert.ToInt32(value);
                if (t == typeof(long)) return Convert.ToInt64(value);
                if (t == typeof(short)) return Convert.ToInt16(value);
                if (t == typeof(byte)) return Convert.ToByte(value);
                if (t == typeof(bool)) return Convert.ToBoolean(value);
            }
            catch
            {
                return value;
            }
            return value;
        }

        public OleDbCommand Cmd(OleDbConnection cn, OleDbTransaction tx, string sql, params object[] values)
        {
            OleDbCommand cmd = new OleDbCommand(sql, cn);
            if (tx != null) cmd.Transaction = tx;
            if (values != null)
                foreach (object v in values) cmd.Parameters.Add(Param(v));
            return cmd;
        }

        public DataTable Query(OleDbConnection cn, OleDbTransaction tx, string sql, params object[] values)
        {
            using (OleDbCommand cmd = Cmd(cn, tx, sql, values))
            using (OleDbDataAdapter da = new OleDbDataAdapter(cmd))
            {
                DataTable dt = new DataTable();
                da.Fill(dt);
                return dt;
            }
        }

        /// <summary>Query that returns an empty table instead of throwing when the table/column does not exist.</summary>
        public DataTable QuerySafe(OleDbConnection cn, string sql, params object[] values)
        {
            try { return Query(cn, null, sql, values); }
            catch { return new DataTable(); }
        }

        public object Scalar(OleDbConnection cn, OleDbTransaction tx, string sql, params object[] values)
        {
            using (OleDbCommand cmd = Cmd(cn, tx, sql, values))
            {
                object o = cmd.ExecuteScalar();
                return (o == null) ? DBNull.Value : o;
            }
        }

        public int Exec(OleDbConnection cn, OleDbTransaction tx, string sql, params object[] values)
        {
            using (OleDbCommand cmd = Cmd(cn, tx, sql, values))
                return cmd.ExecuteNonQuery();
        }

        // ---------------------------------------------------------------- row access (case-insensitive, typed)

        public static bool RowHas(DataRow r, string col)
        {
            if (r == null) return false;
            foreach (DataColumn c in r.Table.Columns)
                if (string.Equals(c.ColumnName, col, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static string RealCol(DataRow r, string col)
        {
            foreach (DataColumn c in r.Table.Columns)
                if (string.Equals(c.ColumnName, col, StringComparison.OrdinalIgnoreCase)) return c.ColumnName;
            return null;
        }

        public static object GetRaw(DataRow r, string col)
        {
            string real = RealCol(r, col);
            return real == null ? DBNull.Value : r[real];
        }

        public static string GetStr(DataRow r, string col)
        {
            object o = GetRaw(r, col);
            return (o == null || o == DBNull.Value) ? "" : Convert.ToString(o);
        }

        public static double GetDbl(DataRow r, string col)
        {
            object o = GetRaw(r, col);
            if (o == null || o == DBNull.Value) return 0.0;
            try { return Convert.ToDouble(o); } catch { return 0.0; }
        }

        public static long GetLong(DataRow r, string col)
        {
            string c = RealCol(r, col);
            if (c == null) return 0;
            object o = r[c];
            if (o == null || o == DBNull.Value) return 0;
            try { return Convert.ToInt64(o); } catch { return 0; }
        }

        public static int GetInt(DataRow r, string col)
        {
            object o = GetRaw(r, col);
            if (o == null || o == DBNull.Value) return 0;
            try { return Convert.ToInt32(o); } catch { return 0; }
        }

        public static DateTime GetDate(DataRow r, string col, DateTime fallback)
        {
            object o = GetRaw(r, col);
            if (o == null || o == DBNull.Value) return fallback;
            try { return Convert.ToDateTime(o); } catch { return fallback; }
        }

        public static bool IsNull(DataRow r, string col)
        {
            object o = GetRaw(r, col);
            return o == null || o == DBNull.Value;
        }

        public static void SetVal(DataRow r, string col, object value)
        {
            string real = RealCol(r, col);
            if (real == null) return;
            DataColumn dc = r.Table.Columns[real];
            r[real] = ConvertForColumn(value, dc);
        }

        // ---------------------------------------------------------------- insert

        /// <summary>Insert one DataRow copying EVERY column of its table schema. Returns 1.</summary>
        public int InsertRow(OleDbConnection cn, OleDbTransaction tx, string table, DataRow row)
        {
            string realTable = RealTable(table);
            List<string> cols = new List<string>();
            List<object> vals = new List<object>();
            foreach (DataColumn dc in row.Table.Columns)
            {
                object v = row[dc];
                // Jet: several text columns have AllowZeroLength=No, so "" fails
                // at insert while Null succeeds (SaleBillPost fix). Proven
                // exceptions below keep the empty string the app itself wrote.
                if (v is string && ((string)v).Length == 0 && !KeepEmptyString(realTable, dc.ColumnName))
                    v = DBNull.Value;
                cols.Add(dc.ColumnName);
                vals.Add(v);
            }
            string sql = "INSERT INTO [" + realTable + "] (" +
                         string.Join(",", WrapCols(cols)) + ") VALUES (" +
                         string.Join(",", QMarks(cols.Count)) + ")";
            return Exec(cn, tx, sql, vals.ToArray());
        }

        private static bool KeepEmptyString(string table, string col)
        {
            // Columns where a manual AccNew entry verifiably stored "" (see
            // ops_manual_session.json PTL row: Remark1/Remark2/chno/ID_NO1).
            if (string.Equals(table, "PTL", StringComparison.OrdinalIgnoreCase))
            {
                string c = col.ToUpperInvariant();
                return c == "REMARK1" || c == "REMARK2" || c == "CHNO" || c == "ID_NO1";
            }
            return false;
        }

        private static string[] WrapCols(List<string> cols)
        {
            string[] a = new string[cols.Count];
            for (int i = 0; i < cols.Count; i++) a[i] = "[" + cols[i] + "]";
            return a;
        }

        private static string[] QMarks(int n)
        {
            string[] a = new string[n];
            for (int i = 0; i < n; i++) a[i] = "?";
            return a;
        }
    }
}
