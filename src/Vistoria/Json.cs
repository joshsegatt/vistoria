using System;
using System.Collections;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Vistoria
{
    // shortcut: write-only JSON over public fields, enough for evidencias.json; swap for System.Text.Json
    // if the tool ever needs to read JSON back (it would add DLLs next to the exe).
    public static class Json
    {
        public static string Write(object o)
        {
            var sb = new StringBuilder();
            Value(sb, o, 0);
            return sb.ToString();
        }

        static void Value(StringBuilder sb, object o, int depth)
        {
            switch (o)
            {
                case null: sb.Append("null"); break;
                case string s: Str(sb, s); break;
                case bool b: sb.Append(b ? "true" : "false"); break;
                case DateTime d: Str(sb, d.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture)); break;
                case Enum e: Str(sb, e.ToString()); break;
                case float f: sb.Append(f.ToString("R", CultureInfo.InvariantCulture)); break;
                case double f: sb.Append(f.ToString("R", CultureInfo.InvariantCulture)); break;
                case IFormattable n when o.GetType().IsPrimitive || o is decimal: sb.Append(n.ToString(null, CultureInfo.InvariantCulture)); break;
                case IDictionary dict:
                    sb.Append('{');
                    bool first = true;
                    foreach (DictionaryEntry kv in dict)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        Nl(sb, depth + 1); Str(sb, Convert.ToString(kv.Key, CultureInfo.InvariantCulture)); sb.Append(": "); Value(sb, kv.Value, depth + 1);
                    }
                    if (!first) Nl(sb, depth);
                    sb.Append('}');
                    break;
                case IEnumerable list:
                    sb.Append('[');
                    bool firstItem = true;
                    foreach (var item in list)
                    {
                        if (!firstItem) sb.Append(',');
                        firstItem = false;
                        Nl(sb, depth + 1); Value(sb, item, depth + 1);
                    }
                    if (!firstItem) Nl(sb, depth);
                    sb.Append(']');
                    break;
                default:
                    var fields = o.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance);
                    sb.Append('{');
                    for (int i = 0; i < fields.Length; i++)
                    {
                        if (i > 0) sb.Append(',');
                        Nl(sb, depth + 1); Str(sb, fields[i].Name); sb.Append(": "); Value(sb, fields[i].GetValue(o), depth + 1);
                    }
                    if (fields.Length > 0) Nl(sb, depth);
                    sb.Append('}');
                    break;
            }
        }

        static void Nl(StringBuilder sb, int depth) => sb.Append('\n').Append(' ', depth * 2);

        static void Str(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (var ch in s)
            {
                switch (ch)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (ch < 0x20) sb.Append("\\u").Append(((int)ch).ToString("x4"));
                        else sb.Append(ch);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
