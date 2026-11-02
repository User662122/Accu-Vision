using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace AccuVisionRepost.Core
{
    public static class SimpleJson
    {
        public static string Serialize(object value, bool indent)
        {
            StringBuilder sb = new StringBuilder();
            WriteValue(sb, value, indent, 0);
            return sb.ToString();
        }

        private static void WriteIndent(StringBuilder sb, bool indent, int depth)
        {
            if (!indent) return;
            for (int i = 0; i < depth * 2; i++) sb.Append(' ');
        }

        private static void WriteValue(StringBuilder sb, object val, bool indent, int depth)
        {
            if (val == null || val == DBNull.Value)
            {
                sb.Append("null");
                return;
            }
            if (val is bool)
            {
                sb.Append(((bool)val) ? "true" : "false");
                return;
            }
            if (val is int || val is long || val is short || val is byte)
            {
                sb.Append(Convert.ToInt64(val).ToString(CultureInfo.InvariantCulture));
                return;
            }
            if (val is double || val is float || val is decimal)
            {
                double d = Convert.ToDouble(val);
                if (double.IsNaN(d) || double.IsInfinity(d)) sb.Append("null");
                else sb.Append(d.ToString("0.####", CultureInfo.InvariantCulture));
                return;
            }
            if (val is DateTime)
            {
                WriteString(sb, Helpers.FmtDateIso((DateTime)val));
                return;
            }
            if (val is string)
            {
                WriteString(sb, (string)val);
                return;
            }
            Dictionary<string, object> dict = val as Dictionary<string, object>;
            if (dict != null)
            {
                if (dict.Count == 0) { sb.Append("{}"); return; }
                sb.Append('{');
                if (indent) sb.Append("\r\n");
                int i = 0;
                foreach (KeyValuePair<string, object> kv in dict)
                {
                    if (i > 0) { sb.Append(','); if (indent) sb.Append("\r\n"); }
                    WriteIndent(sb, indent, depth + 1);
                    WriteString(sb, kv.Key);
                    sb.Append(indent ? ": " : ":");
                    WriteValue(sb, kv.Value, indent, depth + 1);
                    i++;
                }
                if (indent) { sb.Append("\r\n"); WriteIndent(sb, indent, depth); }
                sb.Append('}');
                return;
            }
            System.Collections.IEnumerable list = val as System.Collections.IEnumerable;
            if (list != null)
            {
                List<object> items = new List<object>();
                foreach (object item in list) items.Add(item);
                if (items.Count == 0) { sb.Append("[]"); return; }
                sb.Append('[');
                if (indent) sb.Append("\r\n");
                for (int i = 0; i < items.Count; i++)
                {
                    if (i > 0) { sb.Append(','); if (indent) sb.Append("\r\n"); }
                    WriteIndent(sb, indent, depth + 1);
                    WriteValue(sb, items[i], indent, depth + 1);
                }
                if (indent) { sb.Append("\r\n"); WriteIndent(sb, indent, depth); }
                sb.Append(']');
                return;
            }
            WriteString(sb, Convert.ToString(val));
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            if (s != null)
            {
                for (int i = 0; i < s.Length; i++)
                {
                    char c = s[i];
                    switch (c)
                    {
                        case '"': sb.Append("\\\""); break;
                        case '\\': sb.Append("\\\\"); break;
                        case '\b': sb.Append("\\b"); break;
                        case '\f': sb.Append("\\f"); break;
                        case '\n': sb.Append("\\n"); break;
                        case '\r': sb.Append("\\r"); break;
                        case '\t': sb.Append("\\t"); break;
                        default:
                            if (c < 32) sb.Append("\\u" + ((int)c).ToString("x4"));
                            else sb.Append(c);
                            break;
                    }
                }
            }
            sb.Append('"');
        }

        public static object Parse(string json)
        {
            if (string.IsNullOrEmpty(json)) throw new FormatException("JSON input is empty.");
            int idx = 0;
            object v = ParseValue(json, ref idx);
            SkipWs(json, ref idx);
            return v;
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length)
            {
                char c = s[i];
                if (char.IsWhiteSpace(c) || c == '\uFEFF') { i++; continue; }
                if (c == '/' && i + 1 < s.Length && s[i + 1] == '/')
                {
                    i += 2;
                    while (i < s.Length && s[i] != '\n') i++;
                    continue;
                }
                if (c == '/' && i + 1 < s.Length && s[i + 1] == '*')
                {
                    i += 2;
                    while (i + 1 < s.Length && !(s[i] == '*' && s[i + 1] == '/')) i++;
                    if (i + 1 < s.Length) i += 2;
                    continue;
                }
                break;
            }
        }

        private static object ParseValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) throw new FormatException("Unexpected end of JSON input.");
            char c = s[i];
            if (c == '{') return ParseObject(s, ref i);
            if (c == '[') return ParseArray(s, ref i);
            if (c == '"') return ParseString(s, ref i);
            if (c == 't' && i + 3 < s.Length && s.Substring(i, 4) == "true") { i += 4; return true; }
            if (c == 'f' && i + 4 < s.Length && s.Substring(i, 5) == "false") { i += 5; return false; }
            if (c == 'n' && i + 3 < s.Length && s.Substring(i, 4) == "null") { i += 4; return null; }
            if (c == '-' || char.IsDigit(c)) return ParseNumber(s, ref i);
            throw new FormatException("Unexpected character '" + c + "' in JSON at position " + i + ".");
        }

        private static Dictionary<string, object> ParseObject(string s, ref int i)
        {
            Dictionary<string, object> d = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            i++; // '{'
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return d; }
            while (i < s.Length)
            {
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == '}') { i++; return d; }
                if (s[i] != '"') throw new FormatException("Expected string property name in JSON object at position " + i + ".");
                string key = ParseString(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new FormatException("Expected ':' after property '" + key + "' in JSON at position " + i + ".");
                i++;
                object val = ParseValue(s, ref i);
                d[key] = val;
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == '}') { i++; return d; }
                throw new FormatException("Expected ',' or '}' in JSON object at position " + i + ".");
            }
            throw new FormatException("Unterminated JSON object.");
        }

        private static List<object> ParseArray(string s, ref int i)
        {
            List<object> list = new List<object>();
            i++; // '['
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return list; }
            while (i < s.Length)
            {
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ']') { i++; return list; }
                list.Add(ParseValue(s, ref i));
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == ']') { i++; return list; }
                throw new FormatException("Expected ',' or ']' in JSON array at position " + i + ".");
            }
            throw new FormatException("Unterminated JSON array.");
        }

        private static string ParseString(string s, ref int i)
        {
            StringBuilder sb = new StringBuilder();
            i++; // '"'
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c == '\\')
                {
                    if (i >= s.Length) throw new FormatException("Unterminated escape sequence in JSON string.");
                    char esc = s[i++];
                    switch (esc)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (i + 4 > s.Length) throw new FormatException("Invalid \\uXXXX escape in JSON string.");
                            int code = int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                            sb.Append((char)code);
                            i += 4;
                            break;
                        default: sb.Append(esc); break;
                    }
                }
                else sb.Append(c);
            }
            throw new FormatException("Unterminated JSON string.");
        }

        private static object ParseNumber(string s, ref int i)
        {
            int start = i;
            if (s[i] == '-') i++;
            while (i < s.Length && char.IsDigit(s[i])) i++;
            bool isFloat = false;
            if (i < s.Length && s[i] == '.')
            {
                isFloat = true;
                i++;
                while (i < s.Length && char.IsDigit(s[i])) i++;
            }
            if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
            {
                isFloat = true;
                i++;
                if (i < s.Length && (s[i] == '+' || s[i] == '-')) i++;
                while (i < s.Length && char.IsDigit(s[i])) i++;
            }
            string num = s.Substring(start, i - start);
            if (!isFloat)
            {
                long lv;
                if (long.TryParse(num, NumberStyles.Integer, CultureInfo.InvariantCulture, out lv))
                {
                    if (lv >= int.MinValue && lv <= int.MaxValue) return (int)lv;
                    return lv;
                }
            }
            double dv;
            if (double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out dv)) return dv;
            throw new FormatException("Invalid number '" + num + "' in JSON.");
        }
    }
}
