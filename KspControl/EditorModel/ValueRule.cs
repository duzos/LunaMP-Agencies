using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
namespace KspControl.EditorModel
{
    /// <summary>
    /// Default tokenised value comparison: numbers compare within 1e-6 + 1e-6 * max(|a|, |b|), every other token exactly.
    /// Tokens: signed numbers, runs of non-numeric characters, and single other characters.
    /// </summary>
    public static class ValueRule
    {
        private static readonly Regex TokenRegex = new Regex(@"[-+]?(\d+\.?\d*|\.\d+)([eE][-+]?\d+)?|[^\d\s.+-]+|\S", RegexOptions.CultureInvariant);
        private static readonly Regex NumberRegex = new Regex(@"^[-+]?(\d+\.?\d*|\.\d+)([eE][-+]?\d+)?$", RegexOptions.CultureInvariant);
        public static List<string> Tokens(string value)
        {
            var list = new List<string>();
            if (value == null) return list;
            foreach (Match m in TokenRegex.Matches(value)) list.Add(m.Value);
            return list;
        }
        public static bool TryNumber(string token, out double value)
        {
            value = 0;
            return NumberRegex.IsMatch(token) && double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
        public static bool Equal(string a, string b)
        {
            if (string.Equals(a, b, StringComparison.Ordinal)) return true;
            return Compare(a, b, -1);
        }
        /// <summary>Same tokenisation, numbers compared by absolute difference at most <paramref name="absoluteTolerance"/>.</summary>
        public static bool EqualAbsolute(string a, string b, double absoluteTolerance) { return Compare(a, b, absoluteTolerance); }
        private static bool Compare(string a, string b, double absTol)
        {
            var ta = Tokens(a); var tb = Tokens(b);
            if (ta.Count != tb.Count) return false;
            for (int i = 0; i < ta.Count; i++)
            {
                double x, y;
                if (TryNumber(ta[i], out x) && TryNumber(tb[i], out y))
                {
                    // Integer-valued tokens (no '.' or exponent) are counters, flags and ids: they compare exactly.
                    var integers = ta[i].IndexOfAny(new[] { '.', 'e', 'E' }) < 0 && tb[i].IndexOfAny(new[] { '.', 'e', 'E' }) < 0;
                    var tol = integers ? 0 : (absTol >= 0 ? absTol : 1e-6 + 1e-6 * Math.Max(Math.Abs(x), Math.Abs(y)));
                    if (!(Math.Abs(x - y) <= tol)) return false;
                }
                else if (!string.Equals(ta[i], tb[i], StringComparison.Ordinal)) return false;
            }
            return true;
        }
    }
}
