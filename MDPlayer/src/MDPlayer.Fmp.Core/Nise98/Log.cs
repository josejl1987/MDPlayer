using musicDriverInterface;

namespace Fmp.Core.Nise98;

/// <summary>
/// Backward-compatible log wrapper for the Nise98 emulator.
/// Routes musicDriverInterface-style Log.WriteLine calls to the injected IFmpLogger.
/// </summary>
public static class Log
{
    private static IFmpLogger _logger = new NullLogger();

    public static void SetLogger(IFmpLogger logger)
    {
        _logger = logger ?? new NullLogger();
    }

    public static void Write(LogLevel level, string message)
    {
        _logger.Write(level, message);
    }

    public static void WriteLine(LogLevel level, string message)
    {
        _logger.Write(level, message);
    }

    public static void Write(LogLevel level, string format, params object[] args)
    {
        _logger.Write(level, string.Format(format, args));
    }

    public static void WriteLine(LogLevel level, string format, params object[] args)
    {
        _logger.Write(level, string.Format(format, args));
    }

    public static void ForcedWrite(Exception ex)
    {
        _logger.ForcedWrite(ex);
    }

    public static void ForcedWrite(string message)
    {
        _logger.ForcedWrite(message);
    }

    private class NullLogger : IFmpLogger
    {
        public void Write(LogLevel level, string message) { }
        public void Write(string message) { }
        public void ForcedWrite(string message) { }
        public void ForcedWrite(Exception ex) { }
    }
}
