namespace Fmp.Gui.Services;

/// <summary>No-op notification stub (in-app toasts not yet implemented).</summary>
public sealed class NotificationService
{
    public void Notify(string message)
    {
        // Stub: future in-app toast / OS notification.
    }

    public void NotifyError(string message)
    {
        // Stub.
    }
}
