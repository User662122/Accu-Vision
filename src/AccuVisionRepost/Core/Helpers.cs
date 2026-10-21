using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace AccuVisionRepost.Core
{
    public static class Helpers
    {
        public static double Round2(double v) { return Math.Round(v, 2); }
        public static bool EqAmt(double a, double b) { return Math.Abs(a - b) < 0.004; }

        public static string PadVch(long n) { return n.ToString(CultureInfo.InvariantCulture).PadLeft(9, ' '); }
        public static string PadId(long n) { return n.ToString(CultureInfo.InvariantCulture).PadLeft(10, '0'); }

        public static string FmtDate(DateTime d)
        {
            if (d == DateTime.MinValue) return "";
            return d.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture);
        }

        public static string FmtDateIso(DateTime d)
        {
            if (d == DateTime.MinValue) return "";
            return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        public static bool TryParseDate(string s, out DateTime result)
        {
            result = DateTime.MinValue;
            if (string.IsNullOrEmpty(s)) return false;
            string t = s.Trim();
            if (t.Length == 0) return false;
            if (string.Equals(t, "today", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t, "now", StringComparison.OrdinalIgnoreCase))
            {
                result = DateTime.Today;
                return true;
            }
            string[] formats = new string[]
            {
                "yyyy-MM-dd", "dd-MM-yyyy", "dd/MM/yyyy", "yyyy/MM/dd",
                "d-M-yyyy", "d/M/yyyy", "dd-MMM-yyyy", "dd MMM yyyy",
                "yyyy-MM-dd HH:mm:ss", "dd-MM-yyyy HH:mm:ss", "dd/MM/yyyy HH:mm:ss"
            };
            DateTime dt;
            if (DateTime.TryParseExact(t, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
            {
                result = dt.Date;
                return true;
            }
            if (DateTime.TryParse(t, new CultureInfo("en-IN"), DateTimeStyles.None, out dt))
            {
                result = dt.Date;
                return true;
            }
            if (DateTime.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt))
            {
                result = dt.Date;
                return true;
            }
            return false;
        }

        public static string FmtAmt(double v)
        {
            return v.ToString("#,##0.00", new CultureInfo("en-IN"));
        }

        public static string FmtNumRaw(double v)
        {
            return v.ToString("0.00", CultureInfo.InvariantCulture);
        }

        public static double ParseDbl(string s, double fallback)
        {
            double v;
            if (double.TryParse((s ?? "").Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out v)) return v;
            if (double.TryParse((s ?? "").Trim(), NumberStyles.Any, CultureInfo.CurrentCulture, out v)) return v;
            return fallback;
        }

        public static int ParseInt(string s, int fallback)
        {
            int v;
            if (int.TryParse((s ?? "").Trim(), out v)) return v;
            double d = ParseDbl(s, double.NaN);
            return double.IsNaN(d) ? fallback : (int)Math.Round(d);
        }

        public static string NormName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return Regex.Replace(s.Trim(), "\\s+", " ").ToUpperInvariant();
        }

        public static DateTime AddOneMonthClamped(DateTime d)
        {
            int y = d.Year, m = d.Month + 1, day = d.Day;
            if (m > 12) { m = 1; y++; }
            int last = DateTime.DaysInMonth(y, m);
            if (day > last) day = last;
            return new DateTime(y, m, day);
        }

        /// <summary>
        /// Shift month references in a narration from the source month to the
        /// new month - same rule as SaleBillRepost.BumpNarration: only exact
        /// dd/mm/yyyy (source day and month-last-day) and mm/yyyy matches of
        /// the SOURCE date are replaced.
        /// </summary>
        public static string BumpNarration(string text, DateTime src, DateTime dst)
        {
            if (string.IsNullOrEmpty(text) || src.Date == dst.Date) return text;
            DateTime srcLast = new DateTime(src.Year, src.Month, DateTime.DaysInMonth(src.Year, src.Month));
            DateTime dstLast = new DateTime(dst.Year, dst.Month, DateTime.DaysInMonth(dst.Year, dst.Month));
            string s = text;
            s = s.Replace(srcLast.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
                          dstLast.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture));
            s = s.Replace(src.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
                          dst.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture));
            s = s.Replace(src.ToString("MM/yyyy", CultureInfo.InvariantCulture),
                          dst.ToString("MM/yyyy", CultureInfo.InvariantCulture));
            return s;
        }

        /// <summary>SaleBillRepost.BumpInvoiceNo: replace trailing digits with the new voucher number (same width).</summary>
        public static string TaxInvSameAsVch(string srcInv, long newVch)
        {
            string s = (srcInv ?? "").Trim();
            if (s.Length == 0) return newVch.ToString(CultureInfo.InvariantCulture);
            int i = s.Length;
            while (i > 0 && char.IsDigit(s[i - 1])) i--;
            string prefix = s.Substring(0, i);
            string digits = s.Substring(i);
            string num = newVch.ToString(CultureInfo.InvariantCulture);
            if (digits.Length > num.Length) num = num.PadLeft(digits.Length, '0');
            return prefix + num;
        }

        /// <summary>Keep the source offset between TAX_INV_NO and VchNo (manual bill 132 -&gt; tax invoice 134).</summary>
        public static string TaxInvKeepOffset(string srcInv, long srcVch, long newVch)
        {
            string s = (srcInv ?? "").Trim();
            long invNum;
            if (long.TryParse(TrailingDigits(s), out invNum))
            {
                long offset = invNum - srcVch;
                return TaxInvSameAsVch(s, newVch + offset);
            }
            return s;
        }

        public static string TrailingDigits(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            int i = s.Length;
            while (i > 0 && char.IsDigit(s[i - 1])) i--;
            return s.Substring(i);
        }

        public static long VchNoToLong(string vchNo)
        {
            long v;
            return long.TryParse((vchNo ?? "").Trim(), out v) ? v : 0;
        }
    }
}
