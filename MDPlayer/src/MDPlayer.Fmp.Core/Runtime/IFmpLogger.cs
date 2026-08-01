using musicDriverInterface;

namespace Fmp.Core;

public interface IFmpLogger
{
    void Write(LogLevel level, string message);
    void Write(string message);
    void ForcedWrite(string message);
    void ForcedWrite(Exception ex);
}
