namespace Fmp.Core.Visualization;

/// <summary>
/// Consumes normalized <see cref="DacOperation"/>s and reconstructs semantic
/// DAC playback sessions. Each completed session produces one
/// <see cref="DacPlaybackEvent"/> and one <see cref="DacSampleCandidate"/>.
///
/// This is deliberately renderer-agnostic: hashing, deduplication, asset-id
/// assignment and MIDI note mapping happen in later phases. Timing uses the
/// project's established timeline time unit (audio sample positions).
///
/// Live state machine (§27 of the DAC specification):
///   Idle   --playback start / first byte--&gt; Active
///   Active --contiguous byte / rate change--&gt; Active
///   Active --natural completion / stop / retrigger / discontinuity /
///            source change / reset / EOF--&gt; (closed event) or Idle
/// </summary>
internal sealed class DacPlaybackTracker
{
    private readonly Dictionary<int, DacSourceStore> _sources = [];
    private readonly List<DacPlaybackEvent> _events = [];
    private readonly List<DacSampleCandidate> _candidates = [];
    private readonly List<DacDiagnostic> _diagnostics = [];
    private ActivePlayback _active;
    private long _nextInstanceId;

    public IReadOnlyList<DacPlaybackEvent> PlaybackEvents => _events;
    public IReadOnlyList<DacSampleCandidate> Candidates => _candidates;
    public IReadOnlyList<DacDiagnostic> Diagnostics => _diagnostics;

    /// <summary>
    /// Binds catalog asset ids onto playback events. Candidates and events are
    /// produced in lockstep (one per closed session), so candidate ordinal
    /// <c>i</c> corresponds to event <c>i</c>. Events without a resolved asset
    /// (empty payload or catalog mismatch) keep a null asset id.
    /// </summary>
    public void ApplyAssetIds(DacCatalogResult catalog)
    {
        int count = Math.Min(_candidates.Count, _events.Count);
        for (int i = 0; i < count; i++)
        {
            int assetId = catalog.ResolveAssetId(i);
            _events[i] = _events[i] with
            {
                AssetId = assetId >= 0 ? assetId : null,
                SampleId = assetId >= 0 ? $"dac:{assetId}" : null,
            };
        }
    }

    /// <summary>Declares a sample source (e.g. a VGM data block).</summary>
    public void DefineSource(int sourceId, ReadOnlyMemory<byte> data, DacSampleFormat format)
    {
        if (sourceId < 0)
            throw new ArgumentOutOfRangeException(nameof(sourceId));
        _sources[sourceId] = new DacSourceStore(sourceId, data, format);
    }

    /// <summary>
    /// Advances the logical cursor of an existing source without starting
    /// playback. Emits an explicit seek so boundary detection treats later
    /// byte consumption as a new trigger only when it is non-contiguous.
    /// </summary>
    public void SeekSource(long timestamp, int sourceId, long position)
    {
        Add(new DacOperation.DacSourcePositionChanged(timestamp, sourceId, position));
    }

    public void Add(DacOperation operation)
    {
        switch (operation)
        {
            case DacOperation.DacSourceDefined d:
                DefineSource(d.SourceId, d.Data, d.Format);
                break;
            case DacOperation.DacSourcePositionChanged p:
                HandlePositionChanged(p);
                break;
            case DacOperation.DacPlaybackStarted s when _active is null:
                StartPlayback(s);
                break;
            case DacOperation.DacPlaybackStarted s:
                // Retrigger: close old at new start, then open new.
                EndPlayback(s.Timestamp, DacStopReason.Retriggered);
                StartPlayback(s);
                break;
            case DacOperation.DacByteConsumed c:
                ConsumeByte(c);
                break;
            case DacOperation.DacRateChanged r:
                ChangeRate(r.Timestamp, r.RateHz);
                break;
            case DacOperation.DacPlaybackStopped st:
                EndPlayback(st.Timestamp, st.Reason);
                break;
            case DacOperation.DacDecoderReset reset:
                EndPlayback(reset.Timestamp, DacStopReason.DecoderReset);
                break;
            default:
                break;
        }
    }

    /// <summary>Closes any active playback at end-of-stream.</summary>
    public void Complete(long endSample)
    {
        EndPlayback(endSample, DacStopReason.EndOfStream);
    }

    private void HandlePositionChanged(DacOperation.DacSourcePositionChanged op)
    {
        if (op.SourceId < 0)
        {
            Diagnose(op.Timestamp, "invalid-seek", $"DAC source {op.SourceId} seek to negative source id.");
            return;
        }
        if (op.Position < 0)
        {
            Diagnose(op.Timestamp, "invalid-seek", $"DAC source {op.SourceId} seek to negative position {op.Position}.");
            return;
        }

        // An explicit non-contiguous position change while a session is active
        // is a source-discontinuity retrigger (9.2). When idle, the position is
        // carried by the source auto-store and picked up by the next byte.
        long expected = _active?.ExpectedPosition ?? -1;
        if (_active != null && expected >= 0 && op.Position != expected)
            EndPlayback(op.Timestamp, DacStopReason.SourceDiscontinuity);
    }

    private void StartPlayback(DacOperation.DacPlaybackStarted op)
    {
        DacSourceStore source = ResolveSource(op.SourceId);
        if (op.Position < 0)
        {
            Diagnose(op.Timestamp, "invalid-start", $"DAC playback start has negative position {op.Position}.");
            EmitEmpty();
            return;
        }

        bool rateValid = op.RateHz is null || (double.IsFinite(op.RateHz.Value) && op.RateHz > 0);
        if (!rateValid)
        {
            Diagnose(op.Timestamp, "invalid-rate", $"DAC playback rate {op.RateHz} is not a finite positive value.");
        }

        double? rate = rateValid ? op.RateHz : null;
        if (op.DeclaredLength is < 0)
        {
            Diagnose(op.Timestamp, "invalid-length", $"DAC playback declared length {op.DeclaredLength} is negative.");
        }

        _active = ActivePlayback.Create(
            NextInstanceId(),
            source,
            op.Timestamp,
            op.Position,
            op.Position,
            op.DeclaredLength is >= 0 ? op.DeclaredLength : null,
            rate);
    }

    private void ConsumeByte(DacOperation.DacByteConsumed op)
    {
        if (_active is null)
        {
            StartImplicitPlayback(op);
            return;
        }

        if (!IsExpectedSourceAndPosition(_active, op))
        {
            EndPlayback(op.Timestamp, DacStopReason.SourceDiscontinuity);
            StartImplicitPlayback(op);
            return;
        }

        _active.Append(op.Value, op.Position);
        _active.ExpectedPosition = op.Position + 1;

        if (_active.HasConsumedDeclaredLength)
            EndPlayback(op.Timestamp + 1, DacStopReason.NaturalEnd);
    }

    private void StartImplicitPlayback(DacOperation.DacByteConsumed op)
    {
        // A byte arrives with no explicit start. This implies the decoder
        // began DAC playback implicitly; synthesize a start op and retain that
        // provenance so a full-capture placeholder can be rejected downstream.
        _active = ActivePlayback.Create(
            NextInstanceId(),
            ResolveSource(op.SourceId),
            op.Timestamp,
            op.Position,
            op.Position,
            null,
            null,
            wasImplicit: true);
        _active.Append(op.Value, op.Position);
    }

    private void ChangeRate(long timestamp, double rateHz)
    {
        if (_active is null)
        {
            Diagnose(timestamp, "rate-without-playback", "DAC rate change while no playback is active.");
            return;
        }
        if (!double.IsFinite(rateHz) || rateHz <= 0)
        {
            Diagnose(timestamp, "invalid-rate", $"DAC playback rate {rateHz} is not a finite positive value.");
            return;
        }

        _active.SetRate(timestamp, rateHz);
    }

    private void EndPlayback(long timestamp, DacStopReason reason)
    {
        ActivePlayback active = _active;
        if (active is null)
        {
            if (reason == DacStopReason.ExplicitStop)
                Diagnose(timestamp, "stop-without-playback", "DAC stop while no playback is active.");
            return;
        }
        _active = null;

        if (active.PayloadLength == 0)
            return;

        long endSample = Math.Max(active.StartSample, timestamp);
        (ReadOnlyMemory<byte> payload, bool truncated) = active.ExtractPayload();

        DacSourceReference sourceRef = new(
            active.Source.SourceId,
            active.StartPosition,
            active.EndPosition);
        var candidate = new DacSampleCandidate(
            payload,
            active.Source.Format,
            active.StartSample,
            sourceRef,
            active.DeclaredLength,
            truncated);
        _candidates.Add(candidate);

        _events.Add(new DacPlaybackEvent(
            active.InstanceId,
            AssetId: null,
            SampleId: null,
            StartSample: active.StartSample,
            EndSample: endSample,
            reason,
            active.PayloadLength,
            truncated,
            active.InitialRateHz,
            active.RatePoints.ToArray(),
            Gain: null,
            Pan: null,
            WasImplicit: active.WasImplicit));

        if (truncated)
            Diagnose(timestamp, "truncated-input", "DAC playback ended before its declared length was available.");
    }

    private DacSourceStore ResolveSource(int sourceId)
    {
        if (sourceId < 0)
            throw new InvalidOperationException($"DAC source {sourceId} is not valid.");
        if (_sources.TryGetValue(sourceId, out DacSourceStore source))
            return source;

        // Direct-register-write DAC (e.g. YM2612 0x2A) has no preloaded data
        // block: the payload is the self-recorded byte stream. Lazily create a
        // format-only source whose bytes are accumulated by the playback
        // session itself.
        source = new DacSourceStore(sourceId, ReadOnlyMemory<byte>.Empty, DacSampleFormat.DefaultYm2612);
        _sources.Add(sourceId, source);
        return source;
    }

    private void Diagnose(long timestamp, string code, string message)
        => _diagnostics.Add(new DacDiagnostic(timestamp, code, message));

    private void EmitEmpty()
    {
        // Playback start that cannot be realized (invalid source) produces no
        // asset and no event; the next byte/start will re-open a session.
        _active = null;
    }

    private long NextInstanceId() => ++_nextInstanceId;

    private static bool IsExpectedSourceAndPosition(ActivePlayback active, DacOperation.DacByteConsumed op)
        => op.SourceId == active.Source.SourceId
            && op.Position == active.ExpectedPosition;

    /// <summary>
    /// Mutable playback session. Bytes are appended to a growable buffer so
    /// extraction never concatenates immutable arrays per consumed byte.
    /// </summary>
    private sealed class ActivePlayback
    {
        private readonly List<byte> _bytes = [];
        private readonly List<DacRatePoint> _ratePoints = [];
        private ActivePlayback(
            long instanceId,
            DacSourceStore source,
            long startSample,
            long startPosition,
            long? declaredLength,
            double? rateHz,
            bool wasImplicit)
        {
            InstanceId = instanceId;
            Source = source;
            StartSample = startSample;
            StartPosition = startPosition;
            ExpectedPosition = startPosition;
            DeclaredLength = declaredLength;
            InitialRateHz = rateHz;
            CurrentRateHz = rateHz;
            WasImplicit = wasImplicit;
        }

        public static ActivePlayback Create(
            long instanceId,
            DacSourceStore source,
            long startSample,
            long startPosition,
            long cursorPosition,
            long? declaredLength,
            double? rateHz,
            bool wasImplicit = false)
            => new(instanceId, source, startSample, startPosition, declaredLength, rateHz, wasImplicit)
            {
                ExpectedPosition = cursorPosition,
            };

        public long InstanceId { get; }
        public DacSourceStore Source { get; }
        public long StartSample { get; }
        public long StartPosition { get; }
        public long EndPosition => StartPosition + PayloadLength;
        public long ExpectedPosition { get; set; }
        public long? DeclaredLength { get; }
        public double? InitialRateHz { get; }
        public double? CurrentRateHz { get; private set; }
        public bool WasImplicit { get; }
        public int PayloadLength => _bytes.Count;
        public IReadOnlyList<DacRatePoint> RatePoints => _ratePoints;
        public bool HasConsumedDeclaredLength =>
            DeclaredLength is long length && PayloadLength >= length;

        public void Append(byte value, long position)
        {
            _bytes.Add(value);
            ExpectedPosition = position + 1;
        }

        public void SetRate(long timestamp, double rateHz)
        {
            if (CurrentRateHz == rateHz)
                return;
            _ratePoints.Add(new DacRatePoint(timestamp, rateHz));
            CurrentRateHz = rateHz;
        }

        public (ReadOnlyMemory<byte> Payload, bool Truncated) ExtractPayload()
        {
            // The self-recorded bytes are the payload for direct-register-write
            // DAC (no preloaded source buffer). Declared-length sources still
            // rely on these bytes so truncation is measured against actual
            // consumption rather than source capacity.
            bool truncated = DeclaredLength is long length
                && length > 0
                && PayloadLength < length;

            return (new ReadOnlyMemory<byte>(_bytes.ToArray()), truncated);
        }
    }
}
