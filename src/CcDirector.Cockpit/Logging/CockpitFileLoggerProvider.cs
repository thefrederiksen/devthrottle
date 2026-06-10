using Microsoft.Extensions.Logging;

namespace CcDirector.Cockpit.Logging;

/// <summary>
/// An <see cref="ILoggerProvider"/> that routes the Cockpit's ILogger output to the persisted
/// <see cref="CockpitFileLog"/> file sink. Every <c>ILogger&lt;T&gt;.Log*</c> call the Blazor
/// components already make (issue #199 added many INFO action lines) lands in
/// logs/cockpit/cockpit-YYYY-MM-DD-&lt;PID&gt;.log in the Director's text format:
/// <c>LEVEL [Category] message</c>, with the exception appended when present.
///
/// Before this provider the Cockpit's ILogger went to an invisible console only (the supervisor
/// launches it CreateNoWindow with no stdout capture), so nothing persisted - the gap #199 closes.
/// </summary>
public sealed class CockpitFileLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new CockpitFileLogger(categoryName);

    public void Dispose()
    {
        // The shared CockpitFileLog writer is owned by the host lifetime (started in Program and
        // stopped on shutdown), not by this provider, so there is nothing per-provider to release.
    }

    internal sealed class CockpitFileLogger(string categoryName) : ILogger
    {
        // No scope support is needed for a flat file sink; a no-op disposable keeps callers happy.
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;
            if (formatter is null)
                throw new ArgumentNullException(nameof(formatter));

            var message = formatter(state, exception);
            CockpitFileLog.Write(RenderLine(categoryName, logLevel, message, exception));
        }

        /// <summary>
        /// Render one persisted line in the Director text format: <c>LEVEL [ShortCategory] message</c>,
        /// with <c>:: Type: message</c> appended when an exception is present. Extracted (and internal)
        /// so the exact textual contract the proof relies on is unit-tested directly, with no mirror.
        /// </summary>
        internal static string RenderLine(string categoryName, LogLevel logLevel, string message, Exception? exception)
        {
            // Short category (the type name without its namespace) keeps the line readable while
            // still identifying the source component, e.g. "[Cockpit]" / "[TerminalPane]".
            var line = $"{LevelTag(logLevel)} [{ShortCategory(categoryName)}] {message}";
            if (exception is not null)
                line += $" :: {exception.GetType().Name}: {exception.Message}";
            return line;
        }

        private static string ShortCategory(string category)
        {
            if (string.IsNullOrEmpty(category)) return "Cockpit";
            var dot = category.LastIndexOf('.');
            return dot >= 0 && dot < category.Length - 1 ? category[(dot + 1)..] : category;
        }

        private static string LevelTag(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRACE",
            LogLevel.Debug => "DEBUG",
            LogLevel.Information => "INFO",
            LogLevel.Warning => "WARN",
            LogLevel.Error => "ERROR",
            LogLevel.Critical => "CRIT",
            _ => "INFO",
        };
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        private NullScope() { }
        public void Dispose() { }
    }
}
