using System.Globalization;

namespace CcDirector.Core.Machine;

/// <summary>
/// One reading of the machine's network throughput, summed over the physical interfaces that are up,
/// excluding loopback and virtual ones. Virtual switches and filter layers carry the same bytes again -
/// on Windows they counted the same traffic three to four times - so summing them would overstate the
/// machine's real traffic. Both figures are rates, so the first read after start-up is unreadable with
/// <see cref="ResourceUnreadableReasons.FirstSample"/>.
/// </summary>
/// <param name="CouldRead">False when the network could not be read; the rates are then zero and mean nothing.</param>
/// <param name="UnreadableReason">Why the read failed, in plain English. Null when it succeeded.</param>
/// <param name="ReceiveBytesPerSecond">Bytes received per second over the last interval.</param>
/// <param name="SendBytesPerSecond">Bytes sent per second over the last interval.</param>
public readonly record struct NetworkReading(
    bool CouldRead,
    string? UnreadableReason,
    double ReceiveBytesPerSecond,
    double SendBytesPerSecond)
{
    /// <summary>A reading that succeeded.</summary>
    public static NetworkReading Read(double receiveBytesPerSecond, double sendBytesPerSecond) =>
        new(CouldRead: true, UnreadableReason: null, receiveBytesPerSecond, sendBytesPerSecond);

    /// <summary>A reading that failed, carrying why. The rates are zero and mean nothing.</summary>
    public static NetworkReading Unreadable(string reason) =>
        new(CouldRead: false, ResourceReadingGuard.Reason(reason), 0, 0);

    /// <summary>Short, log-safe description. Prints no figure when the reading failed.</summary>
    public override string ToString() =>
        CouldRead
            ? string.Create(CultureInfo.InvariantCulture,
                $"network {ReceiveBytesPerSecond / (1024.0 * 1024):F1} MB/s in, {SendBytesPerSecond / (1024.0 * 1024):F1} MB/s out")
            : $"network unreadable ({UnreadableReason})";
}
