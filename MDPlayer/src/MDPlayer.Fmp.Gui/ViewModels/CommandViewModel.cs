using Fmp.Application.Contracts;
using Fmp.Application.Export;

namespace Fmp.Gui.ViewModels;

/// <summary>
/// The canonical command preview: display text, validity summary and the
/// Compact/FullyResolved toggle.
/// </summary>
public sealed class CommandViewModel : ObservableObject
{
    private readonly Action<bool> _resolvedChanged;
    private bool _isExpanded;
    private bool _isResolved;
    private string _displayText = "Run mdplayer-render visualize …";
    private bool _isValid = true;
    private string _issuesSummary = "";

    public CommandViewModel(Action<bool> resolvedChanged)
    {
        _resolvedChanged = resolvedChanged;
        ShowResolvedCommand = new RelayCommand(ToggleResolved);
    }

    public RelayCommand ShowResolvedCommand { get; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public bool IsResolved
    {
        get => _isResolved;
        private set
        {
            if (SetProperty(ref _isResolved, value))
                OnPropertyChanged(nameof(ShowResolvedText));
        }
    }

    public string ShowResolvedText => IsResolved ? "Show compact" : "Show resolved";

    public string DisplayText
    {
        get => _displayText;
        private set => SetProperty(ref _displayText, value);
    }

    public bool IsValid
    {
        get => _isValid;
        private set => SetProperty(ref _isValid, value);
    }

    public string IssuesSummary
    {
        get => _issuesSummary;
        private set => SetProperty(ref _issuesSummary, value);
    }

    public void SetDisplay(CanonicalCommand? command)
    {
        DisplayText = command?.DisplayText ?? "(no request)";
    }

    public void SetValidity(IReadOnlyList<ValidationIssue> issues)
    {
        IReadOnlyList<ValidationIssue> errors = issues
            .Where(i => i.Severity == ValidationSeverity.Error)
            .ToList();
        IReadOnlyList<ValidationIssue> warnings = issues
            .Where(i => i.Severity == ValidationSeverity.Warning)
            .ToList();

        IsValid = errors.Count == 0;
        if (errors.Count == 0 && warnings.Count == 0)
            IssuesSummary = "Command is valid.";
        else if (errors.Count == 0)
            IssuesSummary = $"{warnings.Count} warning(s): " + string.Join(" | ", warnings.Select(w => w.Message));
        else
            IssuesSummary = $"{errors.Count} error(s): " + string.Join(" | ", errors.Select(e => e.Message));
    }

    private void ToggleResolved()
    {
        IsResolved = !IsResolved;
        _resolvedChanged(IsResolved);
    }
}
