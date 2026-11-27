using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;

namespace Vistoria.Collectors
{
    static class Wmi
    {
        public const string Cim = @"root\cimv2";
        public const string Storage = @"root\Microsoft\Windows\Storage";
        public const string Root = @"root\wmi";

        public static List<ManagementBaseObject> Query(string scope, string wql)
        {
            using (var s = new ManagementObjectSearcher(scope, wql))
                return s.Get().Cast<ManagementBaseObject>().ToList();
        }

        public static string Str(ManagementBaseObject o, string p) => Clean(o[p]?.ToString());

        public static T? Num<T>(ManagementBaseObject o, string p) where T : struct
        {
            var v = o[p];
            if (v == null) return null;
            try { return (T)Convert.ChangeType(v, typeof(T)); }
            catch (Exception e) when (e is InvalidCastException || e is OverflowException || e is FormatException) { return null; }
        }

        public static DateTime? Date(ManagementBaseObject o, string p)
        {
            var s = o[p] as string;
            if (string.IsNullOrEmpty(s)) return null;
            try { return ManagementDateTimeConverter.ToDateTime(s).ToUniversalTime(); }
            catch (ArgumentOutOfRangeException) { return null; }
        }

        // OEMs fill serials with blanks or placeholders; treat those as missing.
        public static string Clean(string s)
        {
            s = s?.Trim();
            if (string.IsNullOrEmpty(s)) return null;
            var lower = s.ToLowerInvariant();
            return lower == "to be filled by o.e.m." || lower == "default string" || lower == "system serial number"
                || lower == "none" ? null : s;
        }
    }
}
