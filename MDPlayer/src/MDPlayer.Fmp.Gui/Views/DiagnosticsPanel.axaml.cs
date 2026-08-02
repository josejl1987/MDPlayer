using System;
using Avalonia.Controls;

namespace Fmp.Gui.Views;

/// <summary>
/// Read-only diagnostics rail. Bound to the inherited <c>MainWindowViewModel</c>
/// and shared by the wide right-hand rail and the compact preview-header flyout
/// so the two presentations never drift.
/// </summary>
public partial class DiagnosticsPanel : UserControl
{
    public DiagnosticsPanel()
    {
        InitializeComponent();
    }
}
