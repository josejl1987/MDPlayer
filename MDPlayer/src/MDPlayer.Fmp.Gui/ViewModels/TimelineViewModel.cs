using System.Collections.ObjectModel;
using Avalonia.Threading;
using Fmp.Application.Contracts;

namespace Fmp.Gui.ViewModels;

/// <summary>
/// Transport/timeline state: current position, duration, representative scrub
/// points and the scrub slider. Scrubbing is throttled to at most 10 requests
/// per second (layout previews) and settles to a final accurate still after the
/// user stops dragging for 500 ms.
/// </summary>
public sealed class TimelineViewModel : ObservableObject
{
    private readonly DispatcherTimer _settleTimer;
    private double _positionSeconds;
    private double _durationSeconds;
    private double _scrubPosition;
    private double _pendingScrubTime;
    private long _lastScrubRaisedMs;
    private bool _suppressScrub;
    private bool _isPlaying;

    public TimelineViewModel()
    {
        _settleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _settleTimer.Tick += OnSettleTick;

        PreviousPointCommand = new RelayCommand(GoPreviousPoint, () => HasTimeline && RepresentativePoints.Count > 0);
        NextPointCommand = new RelayCommand(GoNextPoint, () => HasTimeline && RepresentativePoints.Count > 0);
        SeekRelativeCommand = new RelayCommand(SeekRelative, _ => HasTimeline);
        SeekStartCommand = new RelayCommand(() => Seek(0), () => HasTimeline);
        SeekEndCommand = new RelayCommand(() => Seek(_durationSeconds), () => HasTimeline);
        TogglePlayCommand = new RelayCommand(() => PlayPauseRequested?.Invoke(), () => HasTimeline);
    }

    /// <summary>(timeSeconds, isFinal) — throttled scrub events.</summary>
    public event Action<double, bool>? ScrubRequested;

    public event Action? PlayPauseRequested;

    /// <summary>Absolute seek (from keyboard, representative points, transport).</summary>
    public event Action<double>? SeekRequested;

    public RelayCommand PreviousPointCommand { get; }
    public RelayCommand NextPointCommand { get; }
    public RelayCommand SeekRelativeCommand { get; }
    public RelayCommand SeekStartCommand { get; }
    public RelayCommand SeekEndCommand { get; }
    public RelayCommand TogglePlayCommand { get; }

    public ObservableCollection<RepresentativePoint> RepresentativePoints { get; } = new();

    public double PositionSeconds
    {
        get => _positionSeconds;
        private set
        {
            if (SetProperty(ref _positionSeconds, value))
                OnPropertyChanged(nameof(TimeText));
        }
    }

    public double DurationSeconds
    {
        get => _durationSeconds;
        set
        {
            if (SetProperty(ref _durationSeconds, value))
            {
                OnPropertyChanged(nameof(TimeText));
                OnPropertyChanged(nameof(HasTimeline));
                RaiseTimelineCommandStates();
            }
        }
    }

    public bool HasTimeline => DurationSeconds > 0;

    /// <summary>Two-way bound to the scrub slider.</summary>
    public double ScrubPosition
    {
        get => _scrubPosition;
        set
        {
            double clamped = Math.Clamp(value, 0, Math.Max(0, _durationSeconds));
            if (Math.Abs(_scrubPosition - clamped) < 0.0005 && !_settleTimer.IsEnabled)
                return;
            _scrubPosition = clamped;
            OnPropertyChanged();
            if (_suppressScrub)
                return;

            PositionSeconds = clamped;
            _pendingScrubTime = clamped;

            long now = Environment.TickCount64;
            if (now - _lastScrubRaisedMs >= 100)
            {
                _lastScrubRaisedMs = now;
                ScrubRequested?.Invoke(clamped, false);
            }

            _settleTimer.Stop();
            _settleTimer.Start();
        }
    }

    public string TimeText => $"{Format(_positionSeconds)} / {Format(_durationSeconds)}";

    public bool IsPlaying
    {
        get => _isPlaying;
        private set
        {
            if (SetProperty(ref _isPlaying, value))
            {
                OnPropertyChanged(nameof(PlayPauseGlyph));
                RaiseTimelineCommandStates();
            }
        }
    }

    public string PlayPauseGlyph => IsPlaying ? "⏸" : "▶";

    /// <summary>Sets the position without raising scrub events (seek/motion).</summary>
    public void SetPosition(double timeSeconds, bool notify)
    {
        PositionSeconds = Math.Clamp(timeSeconds, 0, Math.Max(0, _durationSeconds));
        _suppressScrub = true;
        try
        {
            ScrubPosition = PositionSeconds;
        }
        finally
        {
            _suppressScrub = false;
        }
        if (notify)
            SeekRequested?.Invoke(PositionSeconds);
    }

    /// <summary>Motion-playback clock update (no events).</summary>
    public void SetPositionFromMotion(double timeSeconds)
    {
        PositionSeconds = Math.Clamp(timeSeconds, 0, Math.Max(0, _durationSeconds));
        _suppressScrub = true;
        try
        {
            ScrubPosition = PositionSeconds;
        }
        finally
        {
            _suppressScrub = false;
        }
    }

    public void SetPlaying(bool playing) => IsPlaying = playing;

    public void Dispose()
    {
        _settleTimer.Stop();
        SetPlaying(false);
    }

    public void SynchronizePlan(VisualizationPlanResult? plan)
    {
        RepresentativePoints.Clear();
        if (plan is not null && plan.EstimatedDurationSeconds is double duration)
        {
            foreach (RepresentativePoint point in plan.RepresentativePoints)
                RepresentativePoints.Add(point);
            DurationSeconds = duration;
        }
        else
        {
            DurationSeconds = 0;
            SetPosition(0, notify: false);
        }
        PreviousPointCommand.RaiseCanExecuteChanged();
        NextPointCommand.RaiseCanExecuteChanged();
        RaiseTimelineCommandStates();
    }

    private void OnSettleTick(object? sender, EventArgs e)
    {
        _settleTimer.Stop();
        ScrubRequested?.Invoke(_pendingScrubTime, true);
    }

    private void SeekRelative(object? parameter)
    {
        int delta = parameter switch
        {
            int i => i,
            string s when int.TryParse(s, out int n) => n,
            _ => 0,
        };
        Seek(_positionSeconds + delta);
    }

    private void Seek(double timeSeconds) => SetPosition(timeSeconds, notify: true);

    private void RaiseTimelineCommandStates()
    {
        PreviousPointCommand.RaiseCanExecuteChanged();
        NextPointCommand.RaiseCanExecuteChanged();
        SeekRelativeCommand.RaiseCanExecuteChanged();
        SeekStartCommand.RaiseCanExecuteChanged();
        SeekEndCommand.RaiseCanExecuteChanged();
        TogglePlayCommand.RaiseCanExecuteChanged();
    }

    private void GoPreviousPoint()
    {
        RepresentativePoint? point = RepresentativePoints
            .Where(p => p.TimeSeconds < _positionSeconds - 0.01)
            .OrderByDescending(p => p.TimeSeconds)
            .FirstOrDefault();
        if (point is not null)
            Seek(point.TimeSeconds);
    }

    private void GoNextPoint()
    {
        RepresentativePoint? point = RepresentativePoints
            .Where(p => p.TimeSeconds > _positionSeconds + 0.01)
            .OrderBy(p => p.TimeSeconds)
            .FirstOrDefault();
        if (point is not null)
            Seek(point.TimeSeconds);
    }

    private static string Format(double seconds)
    {
        int total = (int)Math.Max(0, seconds);
        return $"{total / 60:00}:{total % 60:00}";
    }
}
