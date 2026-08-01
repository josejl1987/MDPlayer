// Compatibility wrapper: log (lowercase) used by NiseDos and other extracted files.
// In the original MDPlayer this was MDPlayer.log; here it forwards to Log.
using musicDriverInterface;

namespace Fmp.Core.Nise98
{
    public static class log
    {
        public static void Write(string message)
        {
            Log.Write(LogLevel.INFORMATION, message);
        }

        public static void Write(LogLevel level, string message)
        {
            Log.Write(level, message);
        }

        public static void Write(LogLevel level, string format, params object[] args)
        {
            Log.Write(level, string.Format(format, args));
        }

        public static void ForcedWrite(string message)
        {
            Log.ForcedWrite(message);
        }

        public static void ForcedWrite(Exception ex)
        {
            Log.ForcedWrite(ex);
        }
    }
}
