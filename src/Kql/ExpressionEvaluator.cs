using System.Globalization;
using System.Text.Json;
using Kusto.Language.Syntax;

namespace Azure.Cosmos.LightEmulator.Kql;

/// <summary>
/// Evaluates Kusto AST expressions against a row dictionary.
/// </summary>
public static class ExpressionEvaluator
{
    public static object? Evaluate(Expression expr, Dictionary<string, object?> row)
    {
        switch (expr)
        {
            case LiteralExpression lit:
                return lit.LiteralValue;

            case CompoundStringLiteralExpression compoundStr:
                return compoundStr.LiteralValue;

            case NameReference nameRef:
                return row.GetValueOrDefault(nameRef.SimpleName);

            case ParenthesizedExpression paren:
                return Evaluate(paren.Expression, row);

            case BinaryExpression binExpr:
                return EvaluateBinary(binExpr, row);

            case PrefixUnaryExpression unary:
                return EvaluateUnary(unary, row);

            case FunctionCallExpression funcExpr:
                return EvaluateFunction(funcExpr, row);

            case SimpleNamedExpression namedExpr:
                return Evaluate(namedExpr.Expression, row);

            case InExpression inExpr:
                return EvaluateInExpression(inExpr, row);

            default:
                throw new NotSupportedException($"Expression type '{expr.GetType().Name}' is not supported.");
        }
    }

    private static object? EvaluateBinary(BinaryExpression binExpr, Dictionary<string, object?> row)
    {
        var left = Evaluate(binExpr.Left, row);
        var right = Evaluate(binExpr.Right, row);

        return binExpr.Kind switch
        {
            // Equality
            SyntaxKind.EqualExpression => ObjectEquals(left, right),
            SyntaxKind.NotEqualExpression => !ObjectEquals(left, right),

            // Comparison
            SyntaxKind.LessThanExpression => CompareValues(left, right) < 0,
            SyntaxKind.LessThanOrEqualExpression => CompareValues(left, right) <= 0,
            SyntaxKind.GreaterThanExpression => CompareValues(left, right) > 0,
            SyntaxKind.GreaterThanOrEqualExpression => CompareValues(left, right) >= 0,

            // Arithmetic
            SyntaxKind.AddExpression => Add(left, right),
            SyntaxKind.SubtractExpression => Subtract(left, right),
            SyntaxKind.MultiplyExpression => Multiply(left, right),
            SyntaxKind.DivideExpression => Divide(left, right),
            SyntaxKind.ModuloExpression => Modulo(left, right),

            // Logical
            SyntaxKind.AndExpression => ConvertToBool(left) == true && ConvertToBool(right) == true,
            SyntaxKind.OrExpression => ConvertToBool(left) == true || ConvertToBool(right) == true,

            // String operators (case-insensitive)
            SyntaxKind.HasExpression => StringContains(left, right, ignoreCase: true),
            SyntaxKind.NotHasExpression => !StringContains(left, right, ignoreCase: true),
            SyntaxKind.ContainsExpression => StringContains(left, right, ignoreCase: true),
            SyntaxKind.NotContainsExpression => !StringContains(left, right, ignoreCase: true),
            SyntaxKind.StartsWithExpression => StringStartsWith(left, right, ignoreCase: true),
            SyntaxKind.NotStartsWithExpression => !StringStartsWith(left, right, ignoreCase: true),
            SyntaxKind.EndsWithExpression => StringEndsWith(left, right, ignoreCase: true),
            SyntaxKind.NotEndsWithExpression => !StringEndsWith(left, right, ignoreCase: true),

            // String operators (case-sensitive)
            SyntaxKind.HasCsExpression => StringContains(left, right, ignoreCase: false),
            SyntaxKind.NotHasCsExpression => !StringContains(left, right, ignoreCase: false),
            SyntaxKind.ContainsCsExpression => StringContains(left, right, ignoreCase: false),
            SyntaxKind.NotContainsCsExpression => !StringContains(left, right, ignoreCase: false),
            SyntaxKind.StartsWithCsExpression => StringStartsWith(left, right, ignoreCase: false),
            SyntaxKind.NotStartsWithCsExpression => !StringStartsWith(left, right, ignoreCase: false),
            SyntaxKind.EndsWithCsExpression => StringEndsWith(left, right, ignoreCase: false),
            SyntaxKind.NotEndsWithCsExpression => !StringEndsWith(left, right, ignoreCase: false),

            _ => throw new NotSupportedException($"Binary operator '{binExpr.Kind}' is not supported.")
        };
    }

    private static object? EvaluateUnary(PrefixUnaryExpression unary, Dictionary<string, object?> row)
    {
        var operand = Evaluate(unary.Expression, row);
        return unary.Kind switch
        {
            SyntaxKind.UnaryMinusExpression => Negate(operand),
            SyntaxKind.UnaryPlusExpression => operand,
            _ => throw new NotSupportedException($"Unary operator '{unary.Kind}' is not supported.")
        };
    }

    private static object? EvaluateFunction(FunctionCallExpression funcExpr, Dictionary<string, object?> row)
    {
        var funcName = funcExpr.Name.SimpleName.ToLowerInvariant();
        var args = funcExpr.ArgumentList.Expressions
            .Select(e => e.Element)
            .ToList();

        switch (funcName)
        {
            case "now":
                return DateTimeOffset.UtcNow;

            case "ago":
            {
                var duration = Evaluate(args[0], row);
                var ts = ConvertToTimeSpan(duration);
                return DateTimeOffset.UtcNow - ts;
            }

            case "strlen":
                return (long)(ConvertToString(Evaluate(args[0], row))?.Length ?? 0);

            case "toupper":
                return ConvertToString(Evaluate(args[0], row))?.ToUpperInvariant();

            case "tolower":
                return ConvertToString(Evaluate(args[0], row))?.ToLowerInvariant();

            case "trim":
            {
                if (args.Count >= 2)
                {
                    var str = ConvertToString(Evaluate(args[1], row)) ?? "";
                    return str.Trim();
                }
                return ConvertToString(Evaluate(args[0], row))?.Trim();
            }

            case "substring":
            {
                var str = ConvertToString(Evaluate(args[0], row)) ?? "";
                var start = (int)ConvertToLong(Evaluate(args[1], row));
                var len = args.Count > 2 ? (int)ConvertToLong(Evaluate(args[2], row)) : str.Length - start;
                start = Math.Max(0, Math.Min(start, str.Length));
                len = Math.Max(0, Math.Min(len, str.Length - start));
                return str.Substring(start, len);
            }

            case "strcat":
                return string.Concat(args.Select(a => ConvertToString(Evaluate(a, row)) ?? ""));

            case "tostring":
                return ConvertToString(Evaluate(args[0], row));

            case "toint":
            case "tolong":
                return ConvertToLong(Evaluate(args[0], row));

            case "todouble":
            case "toreal":
                return ConvertToDouble(Evaluate(args[0], row));

            case "todatetime":
                return ConvertToDateTime(Evaluate(args[0], row));

            case "isnull":
            case "isempty":
            {
                var val = Evaluate(args[0], row);
                if (val is null) return true;
                if (val is string s) return string.IsNullOrEmpty(s);
                return false;
            }

            case "isnotnull":
            case "isnotempty":
            {
                var val = Evaluate(args[0], row);
                if (val is null) return false;
                if (val is string s) return !string.IsNullOrEmpty(s);
                return true;
            }

            case "iff":
            case "iif":
            {
                var cond = ConvertToBool(Evaluate(args[0], row));
                return cond == true ? Evaluate(args[1], row) : Evaluate(args[2], row);
            }

            case "coalesce":
            {
                foreach (var arg in args)
                {
                    var val = Evaluate(arg, row);
                    if (val is not null) return val;
                }
                return null;
            }

            case "not":
            {
                var val = ConvertToBool(Evaluate(args[0], row));
                return !(val ?? false);
            }

            case "bin":
            case "floor":
            {
                var value = Evaluate(args[0], row);
                var roundTo = Evaluate(args[1], row);

                if (value is DateTimeOffset dto && roundTo is TimeSpan ts)
                {
                    var ticks = dto.UtcTicks - (dto.UtcTicks % ts.Ticks);
                    return new DateTimeOffset(ticks, TimeSpan.Zero);
                }

                var numVal = ConvertToDouble(value);
                var numRound = ConvertToDouble(roundTo);
                if (numRound == 0) return numVal;
                return Math.Floor(numVal / numRound) * numRound;
            }

            case "round":
            {
                var value = ConvertToDouble(Evaluate(args[0], row));
                var precision = args.Count > 1 ? (int)ConvertToLong(Evaluate(args[1], row)) : 0;
                return Math.Round(value, precision);
            }

            case "format_datetime":
            {
                var dt = ConvertToDateTime(Evaluate(args[0], row));
                var format = ConvertToString(Evaluate(args[1], row)) ?? "yyyy-MM-dd HH:mm:ss";
                return dt?.ToString(format, CultureInfo.InvariantCulture);
            }

            case "datetime_diff":
            {
                var part = ConvertToString(Evaluate(args[0], row))?.ToLowerInvariant() ?? "second";
                var dt1 = ConvertToDateTime(Evaluate(args[1], row));
                var dt2 = ConvertToDateTime(Evaluate(args[2], row));
                if (dt1 is null || dt2 is null) return null;
                var diff = dt1.Value - dt2.Value;
                return part switch
                {
                    "second" => (long)diff.TotalSeconds,
                    "minute" => (long)diff.TotalMinutes,
                    "hour" => (long)diff.TotalHours,
                    "day" => (long)diff.TotalDays,
                    _ => (long)diff.TotalSeconds,
                };
            }

            default:
                throw new NotSupportedException($"Function '{funcName}' is not supported.");
        }
    }

    private static object? EvaluateInExpression(InExpression inExpr, Dictionary<string, object?> row)
    {
        var left = Evaluate(inExpr.Left, row);
        var values = inExpr.Right.Expressions
            .Select(e => Evaluate(e.Element, row))
            .ToList();

        bool isIn = values.Any(v => ObjectEquals(left, v));
        return inExpr.Kind == SyntaxKind.InExpression ? isIn : !isIn;
    }

    public static bool ObjectEquals(object? a, object? b)
    {
        if (a is null && b is null) return true;
        if (a is null || b is null) return false;

        // Try numeric comparison
        if (IsNumeric(a) && IsNumeric(b))
            return ConvertToDouble(a) == ConvertToDouble(b);

        // Try string comparison (case-insensitive for KQL)
        if (a is string sa && b is string sb)
            return string.Equals(sa, sb, StringComparison.OrdinalIgnoreCase);

        return a.Equals(b);
    }

    public static int CompareValues(object? a, object? b)
    {
        if (a is null && b is null) return 0;
        if (a is null) return -1;
        if (b is null) return 1;

        // DateTime comparison
        if (a is DateTimeOffset dtoA)
        {
            var dtoB = ConvertToDateTime(b);
            if (dtoB is not null) return dtoA.CompareTo(dtoB.Value);
        }

        // Numeric comparison
        if (IsNumeric(a) || IsNumeric(b))
            return ConvertToDouble(a).CompareTo(ConvertToDouble(b));

        // String comparison
        return string.Compare(
            ConvertToString(a),
            ConvertToString(b),
            StringComparison.OrdinalIgnoreCase);
    }

    public static string? ConvertToString(object? value) => value switch
    {
        null => null,
        string s => s,
        DateTimeOffset dto => dto.ToString("o"),
        TimeSpan ts => ts.ToString(),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)
    };

    public static double ConvertToDouble(object? value) => value switch
    {
        null => 0.0,
        double d => d,
        long l => l,
        int i => i,
        decimal dec => (double)dec,
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
        _ => 0.0
    };

    public static long ConvertToLong(object? value) => value switch
    {
        null => 0,
        long l => l,
        int i => i,
        double d => (long)d,
        decimal dec => (long)dec,
        string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) => l,
        _ => 0
    };

    public static bool? ConvertToBool(object? value) => value switch
    {
        null => null,
        bool b => b,
        long l => l != 0,
        int i => i != 0,
        double d => d != 0,
        string s => bool.TryParse(s, out var b) ? b : !string.IsNullOrEmpty(s),
        _ => true
    };

    public static DateTimeOffset? ConvertToDateTime(object? value) => value switch
    {
        null => null,
        DateTimeOffset dto => dto,
        DateTime dt => new DateTimeOffset(dt, TimeSpan.Zero),
        string s when DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto) => dto,
        long ticks when ticks > 1_000_000_000_000L => DateTimeOffset.FromUnixTimeMilliseconds(ticks),
        long ticks => DateTimeOffset.FromUnixTimeSeconds(ticks),
        _ => null
    };

    private static TimeSpan ConvertToTimeSpan(object? value) => value switch
    {
        TimeSpan ts => ts,
        _ => TimeSpan.Zero
    };

    private static bool IsNumeric(object? value) =>
        value is int or long or double or decimal or float;

    private static bool StringContains(object? left, object? right, bool ignoreCase)
    {
        var s = ConvertToString(left);
        var sub = ConvertToString(right);
        if (s is null || sub is null) return false;
        return s.Contains(sub, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static bool StringStartsWith(object? left, object? right, bool ignoreCase)
    {
        var s = ConvertToString(left);
        var prefix = ConvertToString(right);
        if (s is null || prefix is null) return false;
        return s.StartsWith(prefix, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static bool StringEndsWith(object? left, object? right, bool ignoreCase)
    {
        var s = ConvertToString(left);
        var suffix = ConvertToString(right);
        if (s is null || suffix is null) return false;
        return s.EndsWith(suffix, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static object? Negate(object? value) => value switch
    {
        long l => -l,
        int i => -i,
        double d => -d,
        decimal dec => -dec,
        _ => -ConvertToDouble(value)
    };

    private static object? Add(object? left, object? right)
    {
        if (left is string || right is string)
            return (ConvertToString(left) ?? "") + (ConvertToString(right) ?? "");
        if (left is DateTimeOffset dto && right is TimeSpan ts)
            return dto + ts;
        if (left is long l1 && right is long l2)
            return l1 + l2;
        return ConvertToDouble(left) + ConvertToDouble(right);
    }

    private static object? Subtract(object? left, object? right)
    {
        if (left is DateTimeOffset dtoA && right is DateTimeOffset dtoB)
            return dtoA - dtoB;
        if (left is DateTimeOffset dto && right is TimeSpan ts)
            return dto - ts;
        if (left is long l1 && right is long l2)
            return l1 - l2;
        return ConvertToDouble(left) - ConvertToDouble(right);
    }

    private static object? Multiply(object? left, object? right)
    {
        if (left is long l1 && right is long l2) return l1 * l2;
        return ConvertToDouble(left) * ConvertToDouble(right);
    }

    private static object? Divide(object? left, object? right)
    {
        var divisor = ConvertToDouble(right);
        if (divisor == 0) return null;
        return ConvertToDouble(left) / divisor;
    }

    private static object? Modulo(object? left, object? right)
    {
        if (left is long l1 && right is long l2 && l2 != 0) return l1 % l2;
        var divisor = ConvertToDouble(right);
        if (divisor == 0) return null;
        return ConvertToDouble(left) % divisor;
    }
}
