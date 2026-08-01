using Fmp.Application.Contracts;
using Fmp.Gui.ViewModels;
using Xunit;

namespace Fmp.Gui.Tests;

public class TimelineViewModelTests
{
    private static TimelineViewModel CreateWithPoints()
    {
        var viewModel = new TimelineViewModel();
        viewModel.SynchronizePlan(new VisualizationPlanResult
        {
            ResolvedLayout = "unified",
            RequestedLayout = "auto",
            InputPath = "/tmp/test.vgz",
            EstimatedDurationSeconds = 100,
            RepresentativePoints =
            [
                new RepresentativePoint { Kind = "intro-end", TimeSeconds = 0.75, Label = "Intro" },
                new RepresentativePoint { Kind = "first-event", TimeSeconds = 2.0, Label = "First" },
                new RepresentativePoint { Kind = "middle", TimeSeconds = 50.0, Label = "Middle" },
                new RepresentativePoint { Kind = "near-outro", TimeSeconds = 97.0, Label = "Outro" },
            ],
        });
        return viewModel;
    }

    [Fact]
    public void SynchronizePlan_FillsPointsAndDuration()
    {
        TimelineViewModel viewModel = CreateWithPoints();
        Assert.Equal(4, viewModel.RepresentativePoints.Count);
        Assert.Equal(100, viewModel.DurationSeconds);
    }

    [Fact]
    public void NextPoint_MovesForward()
    {
        TimelineViewModel viewModel = CreateWithPoints();
        viewModel.SetPosition(0, notify: false);

        double? seen = null;
        viewModel.SeekRequested += time => seen = time;

        viewModel.NextPointCommand.Execute(null);
        Assert.NotNull(seen);
        Assert.Equal(0.75, seen.Value, precision: 3);

        viewModel.NextPointCommand.Execute(null);
        Assert.Equal(2.0, seen.Value, precision: 3);
    }

    [Fact]
    public void PreviousPoint_MovesBackward_AndClampsAtStart()
    {
        TimelineViewModel viewModel = CreateWithPoints();
        viewModel.SetPosition(60, notify: false);

        double? seen = null;
        viewModel.SeekRequested += time => seen = time;

        viewModel.PreviousPointCommand.Execute(null);
        Assert.Equal(50.0, seen!.Value, precision: 3);

        // Seek past the start: no point below the first.
        viewModel.SetPosition(0.1, notify: false);
        viewModel.PreviousPointCommand.Execute(null);
        Assert.Equal(0.1, viewModel.PositionSeconds, precision: 3);
    }

    [Fact]
    public void PositionSeconds_RaisesPropertyChanged()
    {
        TimelineViewModel viewModel = CreateWithPoints();
        bool raised = false;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(TimelineViewModel.PositionSeconds))
                raised = true;
        };

        viewModel.SetPosition(5, notify: false);
        Assert.True(raised);
        Assert.Equal(5, viewModel.PositionSeconds);
    }

    [Fact]
    public void ScrubPosition_IsClampedToDuration()
    {
        TimelineViewModel viewModel = CreateWithPoints();
        viewModel.ScrubPosition = 500;
        Assert.Equal(100, viewModel.ScrubPosition, precision: 3);
    }
}
