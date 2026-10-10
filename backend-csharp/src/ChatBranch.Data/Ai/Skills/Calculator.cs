using System.Globalization;
using System.Text.RegularExpressions;

namespace ChatBranch.Data.Ai.Skills;

/// <summary>
/// The <c>calculate</c> built-in: exact decimal arithmetic for the AI, so it
/// stops doing sums in its head. A small recursive-descent parser — never
/// <c>eval</c>, no variables, no code: numbers, <c>+ - * / ^</c> (also × ÷),
/// parentheses, postfix <c>%</c> and <c>round(x, n)</c>, <c>floor</c>,
/// <c>ceil</c>, <c>abs</c>, <c>min</c>, <c>max</c>. Money-friendly input: "RM",
/// "$" and thousands separators are ignored ("RM 1,250.50 * 3").
/// <para>Percent follows the calculator convention: <c>200 + 10%</c> = 220 and
/// <c>200 - 10%</c> = 180 (a percent OF the left side); anywhere else
/// <c>10%</c> = 0.1 (<c>200 * 10%</c> = 20).</para>
/// Results are <see cref="decimal"/> (28 significant digits); rounding is half
/// away from zero.
/// </summary>
public static class Calculator
{
    public const int MaxLength = 500;
    private const int MaxDepth = 50;

    private static readonly string[] Functions = { "round", "floor", "ceil", "abs", "min", "max" };

    public static decimal Evaluate(string expression)
    {
        var src = Normalize(expression);
        if (src.Length == 0) throw new ArgumentException("the expression is empty");
        var p = new Parser(src);
        var (value, _) = p.Expr(0);
        p.SkipSpaces();
        if (!p.End) throw new ArgumentException($"unexpected '{p.Rest}' in the expression");
        return value;
    }

    /// <summary>"1,250.50" → "1250.50"; currency words and symbols dropped; × ÷ → * /.</summary>
    public static string Normalize(string? raw)
    {
        var s = (raw ?? "").Trim();
        if (s.Length > MaxLength) throw new ArgumentException($"the expression is longer than {MaxLength} characters");
        s = s.Replace('×', '*').Replace('÷', '/').Replace('−', '-').Replace("**", "^");
        s = Regex.Replace(s, @"\b(RM|MYR|USD|SGD|EUR|GBP|IDR|THB|PHP|CNY|RMB)\b", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"[$€£¥]", "");
        s = Regex.Replace(s, @"(?<=\d),(?=\d{3}(?!\d))", "");     // thousands separators
        s = Regex.Replace(s, @"(?<=[\d)])\s*[xX]\s*(?=[\d(])", "*"); // 3 x 4
        return s.Trim();
    }

    /// <summary>A result as text: rounded to <paramref name="decimals"/> when given,
    /// else at most 10 places, trailing zeros dropped.</summary>
    public static string Format(decimal v, int? decimals)
    {
        if (decimals is int d) return Math.Round(v, d, MidpointRounding.AwayFromZero).ToString("F" + d, CultureInfo.InvariantCulture);
        var r = Math.Round(v, 10, MidpointRounding.AwayFromZero);
        return r.ToString("0.##########", CultureInfo.InvariantCulture);
    }

    private sealed class Parser
    {
        private readonly string _s;
        private int _i;
        public Parser(string s) => _s = s;
        public bool End => _i >= _s.Length;
        public string Rest => _s[_i..];

        public void SkipSpaces() { while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++; }

        private bool Eat(char c)
        {
            SkipSpaces();
            if (_i < _s.Length && _s[_i] == c) { _i++; return true; }
            return false;
        }

        private char Peek()
        {
            SkipSpaces();
            return _i < _s.Length ? _s[_i] : '\0';
        }

        /// <summary>expr := term (('+'|'-') term)* — a bare percent on the right is OF the left side.</summary>
        public (decimal, bool) Expr(int depth)
        {
            if (depth > MaxDepth) throw new ArgumentException("the expression is nested too deeply");
            var (left, leftPct) = Term(depth);
            while (true)
            {
                var c = Peek();
                if (c is not ('+' or '-')) return (left, leftPct);
                _i++;
                var (right, pct) = Term(depth);
                var delta = pct ? left * right : right;
                left = c == '+' ? left + delta : left - delta;
                leftPct = false;
            }
        }

        /// <summary>term := unary (('*'|'/') unary)*; the bool = "this term is just a percent".</summary>
        private (decimal, bool) Term(int depth)
        {
            var (left, pct) = Unary(depth);
            while (true)
            {
                var c = Peek();
                if (c is not ('*' or '/')) return (left, pct);
                _i++;
                var (right, _) = Unary(depth);
                if (c == '*') left = Checked(() => left * right);
                else
                {
                    if (right == 0) throw new ArgumentException("division by zero");
                    left = Checked(() => left / right);
                }
                pct = false;
            }
        }

        /// <summary>power := postfix ('^' unary)? — right-associative (-2^2 = -4), whole exponents only.</summary>
        private (decimal, bool) Power(int depth)
        {
            if (depth > MaxDepth) throw new ArgumentException("the expression is nested too deeply");
            var (b, pct) = Postfix(depth);
            if (Peek() != '^') return (b, pct);
            _i++;
            var (e, _) = Unary(depth + 1);
            if (e != Math.Truncate(e) || Math.Abs(e) > 100) throw new ArgumentException("exponents must be whole numbers up to 100");
            var n = (int)Math.Abs(e);
            decimal r = 1;
            for (var k = 0; k < n; k++) r = Checked(() => r * b);
            if (e < 0)
            {
                if (r == 0) throw new ArgumentException("division by zero");
                r = 1 / r;
            }
            return (r, false);
        }

        private (decimal, bool) Unary(int depth)
        {
            if (Eat('-')) { var (v, p) = Unary(depth + 1); return (-v, p); }
            if (Eat('+')) return Unary(depth + 1);
            return Power(depth);
        }

        private (decimal, bool) Postfix(int depth)
        {
            var v = Primary(depth);
            var pct = false;
            while (Eat('%')) { v /= 100m; pct = true; }
            return (v, pct);
        }

        private decimal Primary(int depth)
        {
            SkipSpaces();
            if (Eat('('))
            {
                var (v, _) = Expr(depth + 1);
                if (!Eat(')')) throw new ArgumentException("a ')' is missing");
                return v;
            }
            if (_i < _s.Length && char.IsAsciiLetter(_s[_i]))
            {
                var start = _i;
                while (_i < _s.Length && char.IsAsciiLetter(_s[_i])) _i++;
                var name = _s[start.._i].ToLowerInvariant();
                if (!Functions.Contains(name)) throw new ArgumentException($"unknown name '{name}' (numbers and round, floor, ceil, abs, min, max only)");
                if (!Eat('(')) throw new ArgumentException($"{name} needs ( )");
                var args = new List<decimal>();
                if (!Eat(')'))
                {
                    do { args.Add(Expr(depth + 1).Item1); } while (Eat(','));
                    if (!Eat(')')) throw new ArgumentException("a ')' is missing");
                }
                return Call(name, args);
            }
            var numStart = _i;
            while (_i < _s.Length && (char.IsAsciiDigit(_s[_i]) || _s[_i] == '.')) _i++;
            if (numStart == _i) throw new ArgumentException(End ? "the expression ends too early" : $"unexpected '{_s[_i]}'");
            var text = _s[numStart.._i];
            if (!decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n))
                throw new ArgumentException($"'{text}' is not a number");
            return n;
        }

        private static decimal Call(string name, List<decimal> a)
        {
            void Need(int min, int max)
            {
                if (a.Count < min || a.Count > max) throw new ArgumentException($"{name} takes {(min == max ? min.ToString() : $"{min}–{max}")} value(s)");
            }
            switch (name)
            {
                case "round":
                    Need(1, 2);
                    var places = a.Count == 2 ? a[1] : 0;
                    if (places != Math.Truncate(places) || places < 0 || places > 10)
                        throw new ArgumentException("round(x, n): n must be a whole number from 0 to 10");
                    return Math.Round(a[0], (int)places, MidpointRounding.AwayFromZero);
                case "floor": Need(1, 1); return Math.Floor(a[0]);
                case "ceil": Need(1, 1); return Math.Ceiling(a[0]);
                case "abs": Need(1, 1); return Math.Abs(a[0]);
                case "min": Need(1, 20); return a.Min();
                default: Need(1, 20); return a.Max();
            }
        }

        private static decimal Checked(Func<decimal> f)
        {
            try { return f(); }
            catch (OverflowException) { throw new ArgumentException("the number is too large"); }
        }
    }
}
