namespace CcRecorder.Account;

/// <summary>
/// The recorder's one log line writer. On Android, .NET routes standard output to logcat, so these lines
/// can be read with <c>adb logcat -s DOTNET</c> on a real phone - which is where sign-in has to be proved.
/// Never pass a token, a device key or any other credential to this method (security rule DT-05).
/// </summary>
public static class RecorderLog
{
    /// <summary>Writes one ASCII log line, prefixed so it can be filtered out of logcat.</summary>
    public static void Write(string message) => Console.WriteLine("[CcRecorder] " + message);
}
