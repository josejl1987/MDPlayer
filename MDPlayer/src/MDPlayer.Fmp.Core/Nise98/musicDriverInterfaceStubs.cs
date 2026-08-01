// Stub for musicDriverInterface types used by the extracted Nise98 emulator.
// These are minimal replacements for the musicDriverInterface.dll types
// that the original MDPlayer references.

namespace musicDriverInterface
{
    public enum LogLevel
    {
        TRACE = 0,
        DEBUG = 1,
        INFO = 8,
        INFORMATION = 2,
        Information = 2,
        WARNING = 3,
        Warning = 3,
        ERROR = 4,
        Error = 4,
        FATAL = 5,
        Fatal = 5
    }

    public static class Log
    {
        public static void WriteLine(LogLevel level, string message)
        {
            Fmp.Core.Nise98.Log.Write(level, message);
        }

        public static void WriteLine(LogLevel level, string format, params object[] args)
        {
            Fmp.Core.Nise98.Log.Write(level, string.Format(format, args));
        }

        public static void ForcedWrite(string msg)
        {
            Fmp.Core.Nise98.Log.ForcedWrite(msg);
        }

        public static void ForcedWrite(Exception ex)
        {
            Fmp.Core.Nise98.Log.ForcedWrite(ex);
        }
    }

    // Minimal ChipDatum for compilation only; actual ChipDatum from the FMP driver
    // is handled through the callbacks. This type is referenced by Nise98.cs.
    public struct ChipDatum
    {
        public int port;
        public int address;
        public int data;

        public ChipDatum(int port, int address, int data)
        {
            this.port = port;
            this.address = address;
            this.data = data;
        }
    }
}
