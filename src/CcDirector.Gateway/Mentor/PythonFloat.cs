using System.Globalization;
using System.Text;

namespace CcDirector.Gateway.Mentor;

/// <summary>
/// Python's <c>repr</c> of a float - the form <c>json.dumps</c> writes - reproduced from .NET's shortest
/// round-trip digits. Both runtimes produce the shortest digit string that round-trips to the same double;
/// what differs is the NOTATION, and that is what this class settles: Python writes a whole-number float
/// with <c>.0</c> (<c>12.0</c>), switches to exponent form only when the decimal exponent is below -4 or
/// above 16 (<c>1e-05</c>, <c>1e+16</c>, but <c>0.0001</c> and <c>1000000000000000.0</c>), writes the
/// exponent with a sign and at least two digits, and never writes an uppercase E.
/// </summary>
internal static class PythonFloat
{
    public static string Repr(double value)
    {
        if (double.IsNaN(value)) return "nan";
        if (double.IsPositiveInfinity(value)) return "inf";
        if (double.IsNegativeInfinity(value)) return "-inf";
        if (value == 0) return double.IsNegative(value) ? "-0.0" : "0.0";

        // The shortest round-trip digits, whatever notation .NET chose to write them in.
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        var negative = text.StartsWith('-');
        if (negative) text = text.Substring(1);
        var exponent = 0;
        var e = text.IndexOfAny(new[] { 'E', 'e' });
        if (e >= 0)
        {
            exponent = int.Parse(text.Substring(e + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            text = text.Substring(0, e);
        }
        var point = text.IndexOf('.');
        var digits = point < 0 ? text : text.Remove(point, 1);
        var decpt = (point < 0 ? text.Length : point) + exponent;   // value = 0.<digits> x 10^decpt
        var lead = 0;
        while (lead < digits.Length - 1 && digits[lead] == '0') { lead++; decpt--; }
        digits = digits.Substring(lead).TrimEnd('0');
        if (digits.Length == 0) digits = "0";

        var builder = new StringBuilder();
        if (negative) builder.Append('-');
        if (decpt <= -4 || decpt > 16)
        {
            builder.Append(digits[0]);
            if (digits.Length > 1) builder.Append('.').Append(digits, 1, digits.Length - 1);
            var exp = decpt - 1;
            builder.Append('e').Append(exp < 0 ? '-' : '+').Append(Math.Abs(exp).ToString("00", CultureInfo.InvariantCulture));
        }
        else if (decpt <= 0)
        {
            builder.Append("0.").Append('0', -decpt).Append(digits);
        }
        else if (decpt < digits.Length)
        {
            builder.Append(digits, 0, decpt).Append('.').Append(digits, decpt, digits.Length - decpt);
        }
        else
        {
            builder.Append(digits).Append('0', decpt - digits.Length).Append(".0");
        }
        return builder.ToString();
    }
}
