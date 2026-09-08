using System.Globalization;
using System.Numerics;

namespace CcDirector.Gateway.Mentor;

/// <summary>
/// The Python numeric rules the metrics document rests on, reproduced exactly so the port answers the
/// same digits.
///
/// WHY THIS EXISTS. Python's <c>round(x, n)</c> and its <c>"%.nf"</c> formatting both round the EXACT binary
/// value of the double half-to-even at the n-th decimal: <c>round(0.00015, 4)</c> is <c>0.0001</c> because the
/// double nearest 0.00015 lies below the midpoint, and <c>round(0.00025, 4)</c> is <c>0.0003</c> because that
/// one lies above. <c>Math.Round(x, n, MidpointRounding.ToEven)</c> scales by a power of ten first and rounds
/// the scaled double, which agrees on most values and not on all of them; a share that differs in its last
/// digit is a parity defect. So every rounding in the port goes through <see cref="Round"/>, which decomposes
/// the double into its exact integer mantissa and binary exponent, rounds the exact rational with big
/// integers, and parses the decimal string back the way Python does.
///
/// <see cref="TotalSeconds"/> is <c>timedelta.total_seconds()</c>: the whole microseconds divided by a
/// million in one correctly rounded division. The reference parses every stamp to microseconds, so a
/// duration is a whole number of microseconds on both sides.
/// </summary>
internal static class PyNumbers
{
    /// <summary>Python's <c>round(value, digits)</c> for a float and a positive number of digits.</summary>
    public static double Round(double value, int digits)
    {
        if (digits < 1) throw new ArgumentOutOfRangeException(nameof(digits), "The port rounds to one or more decimals.");
        if (double.IsNaN(value) || double.IsInfinity(value))
            throw new MentorDataException("A NaN or infinite value reached round(); the reference never computes one.");
        if (value == 0) return value;
        return double.Parse(FixedDecimal(value, digits), NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    /// <summary>Python's <c>"%.{digits}f" % value</c>.</summary>
    public static string FormatFixed(double value, int digits)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            throw new MentorDataException("A NaN or infinite value reached a fixed format; the reference never computes one.");
        return FixedDecimal(value, digits);
    }

    /// <summary>The exact binary value of <paramref name="value"/>, rounded half-to-even to <paramref name="digits"/>
    /// decimals, as a plain decimal string with that many digits after the point.</summary>
    private static string FixedDecimal(double value, int digits)
    {
        var bits = BitConverter.DoubleToInt64Bits(value);
        var negative = bits < 0;
        var exponent = (int)((bits >> 52) & 0x7FF);
        var mantissa = bits & 0xFFFFFFFFFFFFFL;
        if (exponent == 0) exponent = 1;
        else mantissa |= 1L << 52;
        exponent -= 1075;   // value = mantissa * 2^exponent, exactly
        var scaled = new BigInteger(mantissa) * BigInteger.Pow(10, digits);
        BigInteger quotient;
        if (exponent >= 0)
        {
            quotient = scaled << exponent;
        }
        else
        {
            var denominator = BigInteger.One << -exponent;
            quotient = BigInteger.DivRem(scaled, denominator, out var remainder);
            var twice = remainder * 2;
            var compared = twice.CompareTo(denominator);
            if (compared > 0 || (compared == 0 && !quotient.IsEven)) quotient += BigInteger.One;
        }
        var text = quotient.ToString(CultureInfo.InvariantCulture).PadLeft(digits + 1, '0');
        var result = text.Substring(0, text.Length - digits) + "." + text.Substring(text.Length - digits);
        return negative ? "-" + result : result;
    }

    /// <summary>Python's <c>timedelta.total_seconds()</c> on a duration that is a whole number of microseconds.</summary>
    public static double TotalSeconds(TimeSpan span)
    {
        if (span.Ticks % 10 != 0)
            throw new MentorDataException("A duration with sub-microsecond ticks reached total_seconds(); the reference's stamps are microseconds. "
                + "Truncate the timestamps to six fractional digits at the reader, as MentorReaders.ToMicroseconds does.");
        var microseconds = span.Ticks / 10;
        return microseconds / 1000000.0;
    }

    /// <summary>Python's <c>str()</c> of a number the document holds: an int's digits, a float's repr, None for null.</summary>
    public static string Str(object? number) => number switch
    {
        null => "None",
        long l => l.ToString(CultureInfo.InvariantCulture),
        int i => i.ToString(CultureInfo.InvariantCulture),
        double d => PythonFloat.Repr(d),
        _ => throw new MentorDataException("str() of a " + number.GetType().Name + " is not a number the port renders."),
    };
}
