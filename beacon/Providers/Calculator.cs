using System.Globalization;

namespace Beacon.Providers;

/// <summary>
/// Recursive-descent arithmetic evaluator.
///
/// Written by hand rather than leaning on DataTable.Compute because that path does integer
/// division ("5/2" = 2), has no exponent operator, and drags in System.Data for what is
/// ultimately eighty lines of parsing.
/// </summary>
public static class Calculator
{
    public static bool TryEvaluate(string input, out double result)
    {
        result = 0;
        if (string.IsNullOrWhiteSpace(input)) return false;

        // Require at least one operator so bare numbers don't produce a pointless "42 = 42" row.
        if (!input.Any(c => "+-*/%^(".Contains(c))) return false;

        try
        {
            var parser = new Parser(input);
            var value = parser.ParseExpression();
            parser.SkipWhitespace();

            if (!parser.AtEnd) return false;
            if (double.IsNaN(value) || double.IsInfinity(value)) return false;

            result = value;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string Format(double value)
    {
        if (Math.Abs(value - Math.Round(value)) < 1e-10 && Math.Abs(value) < 1e15)
            return ((long)Math.Round(value)).ToString("N0", CultureInfo.InvariantCulture);

        return Math.Round(value, 8).ToString("N4", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.');
    }

    private sealed class Parser(string text)
    {
        private int _pos;

        public bool AtEnd => _pos >= text.Length;

        public void SkipWhitespace()
        {
            while (_pos < text.Length && char.IsWhiteSpace(text[_pos])) _pos++;
        }

        private bool Take(char c)
        {
            SkipWhitespace();
            if (_pos < text.Length && text[_pos] == c)
            {
                _pos++;
                return true;
            }
            return false;
        }

        private char Peek()
        {
            SkipWhitespace();
            return _pos < text.Length ? text[_pos] : '\0';
        }

        // expression := term (('+' | '-') term)*
        public double ParseExpression()
        {
            var value = ParseTerm();

            while (true)
            {
                if (Take('+')) value += ParseTerm();
                else if (Take('-')) value -= ParseTerm();
                else return value;
            }
        }

        // term := factor (('*' | '/' | '%') factor)*
        private double ParseTerm()
        {
            var value = ParseFactor();

            while (true)
            {
                if (Take('*'))
                {
                    value *= ParseFactor();
                }
                else if (Take('/'))
                {
                    var d = ParseFactor();
                    if (d == 0) throw new DivideByZeroException();
                    value /= d;
                }
                else if (Take('%'))
                {
                    var d = ParseFactor();
                    if (d == 0) throw new DivideByZeroException();
                    value %= d;
                }
                else
                {
                    return value;
                }
            }
        }

        // factor := unary ('^' factor)?   -- right-associative, so 2^3^2 is 2^(3^2)
        private double ParseFactor()
        {
            var value = ParseUnary();
            if (Take('^')) value = Math.Pow(value, ParseFactor());
            return value;
        }

        private double ParseUnary()
        {
            if (Take('-')) return -ParseUnary();
            if (Take('+')) return ParseUnary();
            return ParsePrimary();
        }

        private double ParsePrimary()
        {
            SkipWhitespace();

            if (Take('('))
            {
                var inner = ParseExpression();
                if (!Take(')')) throw new FormatException("unbalanced parenthesis");
                return inner;
            }

            var start = _pos;
            while (_pos < text.Length && (char.IsDigit(text[_pos]) || text[_pos] == '.' || text[_pos] == ','))
            {
                // Treat commas as thousands separators, not decimal points.
                _pos++;
            }

            if (_pos == start) throw new FormatException("expected a number");

            var token = text[start.._pos].Replace(",", "");
            return double.Parse(token, CultureInfo.InvariantCulture);
        }
    }
}
