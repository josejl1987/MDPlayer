using Fmp.Core.Visualization;

namespace Fmp.Core.Visualization.Rendering;

/// <summary>
/// Bounded, allocation-free presentation state for one lane. The first three
/// notes are retained for highlighting; the status badge displays the first
/// two and a cached bounded suffix when more are active.
/// </summary>
internal readonly record struct CurrentLaneState(
    PreparedNote Note1,
    string Label1,
    PreparedNote Note2,
    string Label2,
    PreparedNote Note3,
    string Label3,
    int TotalLabelCount)
{
    internal bool HasActiveNote => Note1 is not null || Note2 is not null || Note3 is not null;
    internal bool HasLabel => !string.IsNullOrEmpty(Label1);
    internal int AdditionalNotes => Math.Max(0, TotalLabelCount - 2);
    internal string AdditionalLabel => PolyphonyLabelCache.SuffixForAdditionalNotes(AdditionalNotes);
}

/// <summary>
/// Resolves current lane state from prepared timeline data. Sequential access
/// advances per-lane cursors amortized over note transitions; backwards/random
/// access uses the prepared start ordering and a binary-search boundary.
/// </summary>
internal sealed class CurrentLaneStateResolver
{
    private readonly PreparedPanel[] _panels;
    private readonly int[] _noteCursors;
    private readonly int[][] _operatorCursors;
    private readonly int[] _sampleCursors;
    private readonly int[] _rhythmCursors;
    private readonly long[] _lastSamples;

    internal CurrentLaneStateResolver(PreparedPanel[] panels)
    {
        _panels = panels ?? throw new ArgumentNullException(nameof(panels));
        _noteCursors = new int[panels.Length];
        _operatorCursors = new int[panels.Length][];
        _sampleCursors = new int[panels.Length];
        _rhythmCursors = new int[panels.Length];
        _lastSamples = new long[panels.Length];
        Array.Fill(_lastSamples, long.MinValue);
        for (int index = 0; index < panels.Length; index++)
            _operatorCursors[index] = new int[panels[index].OperatorNotes.Length];
    }

    internal CurrentLaneState Resolve(int panelIndex, long currentSample, double samplesPerFrame)
    {
        PreparedPanel panel = _panels[panelIndex];
        bool sequential = currentSample >= _lastSamples[panelIndex];
        if (!sequential)
        {
            _noteCursors[panelIndex] = 0;
            _sampleCursors[panelIndex] = 0;
            _rhythmCursors[panelIndex] = 0;
            Array.Clear(_operatorCursors[panelIndex]);
        }
        _lastSamples[panelIndex] = currentSample;

        return panel.Schema switch
        {
            PanelPresentationSchema.PitchedLane
                or PanelPresentationSchema.WaveTableLane
                or PanelPresentationSchema.FmOperatorGroup
                or PanelPresentationSchema.GenericLane
                => ResolveNotes(panelIndex, panel, currentSample, samplesPerFrame, sequential),
            PanelPresentationSchema.SampleLane
                => ResolveSample(panelIndex, panel, currentSample, sequential),
            PanelPresentationSchema.PercussionRows
                => ResolveRhythm(panelIndex, panel, currentSample, samplesPerFrame, sequential),
            _ => default,
        };
    }

    internal static CurrentLaneState ResolveNotesForTest(
        PreparedNote[] notes,
        long sample,
        double samplesPerFrame = 1)
    {
        var builder = new StateBuilder(samplesPerFrame, sample);
        CollectRandom(notes ?? Array.Empty<PreparedNote>(), sample, samplesPerFrame, ref builder);
        return builder.Build();
    }

    private CurrentLaneState ResolveNotes(
        int panelIndex,
        PreparedPanel panel,
        long sample,
        double samplesPerFrame,
        bool sequential)
    {
        var builder = new StateBuilder(samplesPerFrame, sample);
        if (sequential)
        {
            CollectSequential(
                panel.MainNotes,
                ref _noteCursors[panelIndex],
                sample,
                samplesPerFrame,
                ref builder);
        }
        else
        {
            CollectRandom(panel.MainNotes, sample, samplesPerFrame, ref builder);
        }

        // FM3 operator data is a fallback identity source when the parent
        // channel has no active main note. The operator ribbons remain
        // independently rendered and highlighted by the normal note path.
        if (!builder.HasLabel && panel.Schema == PanelPresentationSchema.FmOperatorGroup)
        {
            int count = Math.Min(panel.OperatorNotes.Length, _operatorCursors[panelIndex].Length);
            for (int operatorIndex = 0; operatorIndex < count; operatorIndex++)
            {
                if (sequential)
                {
                    CollectSequential(
                        panel.OperatorNotes[operatorIndex],
                        ref _operatorCursors[panelIndex][operatorIndex],
                        sample,
                        samplesPerFrame,
                        ref builder);
                }
                else
                {
                    CollectRandom(panel.OperatorNotes[operatorIndex], sample, samplesPerFrame, ref builder);
                }
            }
        }

        return builder.Build();
    }

    private CurrentLaneState ResolveSample(
        int panelIndex,
        PreparedPanel panel,
        long sample,
        bool sequential)
    {
        SamplePlaybackEvent[] events = panel.SamplePlayback;
        if (events.Length == 0)
            return default;

        int first = sequential
            ? AdvanceSampleCursor(events, ref _sampleCursors[panelIndex], sample)
            : UpperBoundSampleStarts(events, sample);
        var builder = new StateBuilder(1, sample);
        for (int index = sequential ? first : 0;
             index < events.Length && events[index].StartSample <= sample;
             index++)
        {
            SamplePlaybackEvent value = events[index];
            if (sample < value.EndSample)
                builder.AddLabel(SampleLabel(panel, value));
        }
        return builder.Build();
    }

    private CurrentLaneState ResolveRhythm(
        int panelIndex,
        PreparedPanel panel,
        long sample,
        double samplesPerFrame,
        bool sequential)
    {
        PreparedRhythmEvent[] events = panel.Rhythm;
        PanelRowDefinition[] rows = panel.Rows;
        if (events.Length == 0 || rows.Length == 0)
            return default;

        long tolerance = Math.Max(1, (long)Math.Ceiling(samplesPerFrame * 2));
        long minimumSample = sample - tolerance;
        long maximumSample = sample + tolerance;
        int first = sequential
            ? AdvanceRhythmCursor(events, ref _rhythmCursors[panelIndex], minimumSample)
            : LowerBoundRhythm(events, minimumSample);
        var builder = new StateBuilder(samplesPerFrame, sample);
        for (int index = first; index < events.Length; index++)
        {
            PreparedRhythmEvent value = events[index];
            if (value.SamplePosition > maximumSample)
                break;
            if (value.SamplePosition < minimumSample)
                continue;

            int row = value.RowIndex;
            if ((uint)row >= (uint)rows.Length)
                row = rows.Length - 1;
            string label = PresentationMetadata.OptionalLabel(rows[row].Label)
                ?? PresentationMetadata.OptionalLabel(value.Voice);
            builder.AddLabel(label);
        }
        return builder.Build();
    }

    private static string SampleLabel(PreparedPanel panel, SamplePlaybackEvent value)
    {
        string semantic = panel.SamplesById.TryGetValue(value.SampleId, out SampleDefinition sample)
            ? PresentationMetadata.OptionalLabel(sample.DisplayName)
            : null;
        if (semantic is not null)
            return semantic;

        string identity = PresentationMetadata.OptionalLabel(value.SampleId);
        if (identity is not null)
            return identity;

        return value.MidiPitch is double pitch
            ? PitchLabelCache.Format(pitch)
            : null;
    }

    private static void CollectSequential(
        PreparedNote[] notes,
        ref int cursor,
        long sample,
        double samplesPerFrame,
        ref StateBuilder builder)
    {
        while (cursor < notes.Length && notes[cursor].EndSample <= sample)
            cursor++;
        for (int index = cursor; index < notes.Length; index++)
        {
            PreparedNote note = notes[index];
            if (note.StartSample > sample)
                break;
            if (sample < note.EndSample)
                builder.Add(note);
        }
    }

    private static void CollectRandom(
        PreparedNote[] notes,
        long sample,
        double samplesPerFrame,
        ref StateBuilder builder)
    {
        int upper = UpperBoundNoteStarts(notes, sample);
        for (int index = 0; index < upper; index++)
        {
            PreparedNote note = notes[index];
            if (sample < note.EndSample)
                builder.Add(note);
        }
    }

    private static int AdvanceSampleCursor(
        SamplePlaybackEvent[] events,
        ref int cursor,
        long sample)
    {
        while (cursor < events.Length && events[cursor].EndSample <= sample)
            cursor++;
        return cursor;
    }

    private static int AdvanceRhythmCursor(
        PreparedRhythmEvent[] events,
        ref int cursor,
        long minimumSample)
    {
        while (cursor < events.Length && events[cursor].SamplePosition < minimumSample)
            cursor++;
        return cursor;
    }

    private static int LowerBoundRhythm(PreparedRhythmEvent[] events, long sample)
    {
        int low = 0;
        int high = events.Length;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (events[middle].SamplePosition < sample)
                low = middle + 1;
            else
                high = middle;
        }
        return low;
    }

    private static int UpperBoundNoteStarts(PreparedNote[] notes, long sample)
    {
        int low = 0;
        int high = notes.Length;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (notes[middle].StartSample <= sample)
                low = middle + 1;
            else
                high = middle;
        }
        return low;
    }

    private static int UpperBoundSampleStarts(SamplePlaybackEvent[] events, long sample)
    {
        int low = 0;
        int high = events.Length;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (events[middle].StartSample <= sample)
                low = middle + 1;
            else
                high = middle;
        }
        return low;
    }

    private ref struct StateBuilder
    {
        private readonly double _samplesPerFrame;
        private readonly long _sample;
        private PreparedNote _note1;
        private PreparedNote _note2;
        private PreparedNote _note3;
        private string _label1;
        private string _label2;
        private string _label3;
        private int _totalLabelCount;

        internal StateBuilder(double samplesPerFrame, long sample)
        {
            _samplesPerFrame = samplesPerFrame;
            _sample = sample;
            _note1 = null;
            _note2 = null;
            _note3 = null;
            _label1 = null;
            _label2 = null;
            _label3 = null;
            _totalLabelCount = 0;
        }

        internal bool HasLabel => _totalLabelCount > 0;

        internal void Add(PreparedNote note)
        {
            if (note.InitialMidiNote < 0
                || note.Mode is VisualizationNoteMode.SsgNoise or VisualizationNoteMode.SsgEnvelopeNoise)
                return;

            string label = PitchLabelCache.Format(
                PitchContour.PitchAtSample(note, _sample, _samplesPerFrame));
            if (string.IsNullOrEmpty(label))
                return;

            AddLabel(label, note);
        }

        internal void AddLabel(string label, PreparedNote note = null)
        {
            if (string.IsNullOrEmpty(label)
                || string.Equals(label, _label1, StringComparison.Ordinal)
                || string.Equals(label, _label2, StringComparison.Ordinal)
                || string.Equals(label, _label3, StringComparison.Ordinal))
                return;

            _totalLabelCount++;
            switch (_totalLabelCount)
            {
                case 1:
                    _note1 = note;
                    _label1 = label;
                    break;
                case 2:
                    _note2 = note;
                    _label2 = label;
                    break;
                case 3:
                    _note3 = note;
                    _label3 = label;
                    break;
            }
        }

        internal CurrentLaneState Build()
            => new(_note1, _label1, _note2, _label2, _note3, _label3, _totalLabelCount);
    }
}
