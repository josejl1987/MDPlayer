namespace Fmp.Core.Visualization;

/// <summary>
/// How precisely an S-DSP source's pitch is known to the overlay (§21.5).
/// </summary>
public enum PitchAccuracy
{
    /// <summary>The pitch is exact (pitch register is authoritative for the whole note).</summary>
    Exact,
    /// <summary>The pitch is estimated from the observed playback/sample rate.</summary>
    Estimated,
    /// <summary>The pitch is only known relative to the sample's natural rate (S-DSP pitch register, §13.3).</summary>
    Relative,
    /// <summary>No pitch information (noise/unpitched source); rendered as an activity/envelope lane.</summary>
    Unpitched,
}

/// <summary>
/// Reference panel-grid geometry for the SNES S-DSP overlay (§21.2/§21.3).
/// </summary>
internal sealed record SnesDspLayoutGeometry(
    int W,
    int H,
    int TopBar,
    int BottomBar,
    int GridHeight,
    int PanelW,
    int PanelH,
    int HeaderH,
    int ScopeH,
    int DividerH,
    int PitchLaneH);

/// <summary>
/// Structured panel-header metadata for one S-DSP voice (§21.4). The label is
/// the full token line for plain-text drawing; <see cref="Pan"/> and
/// <see cref="PhaseMarker"/> carry the numeric values the renderer needs for
/// richer pan/phase visuals.
/// </summary>
internal sealed record SnesDspPanelHeader(
    string Label,
    IReadOnlyList<string> Badges,
    double Pan,
    bool PhaseMarker);

/// <summary>
/// Echo-bus state shown in the small echo strip the bottom status bar
/// reserves for SPC sessions (§20.2/§27). Pure data: the actual strip is
/// rendered by the existing OverlaySceneBuilder bottom bar.
/// </summary>
internal sealed record SnesDspEchoStripState(
    bool Enabled,
    int Feedback,
    int Delay,
    bool FirActive);

/// <summary>
/// Pitch-lane presentation for an S-DSP panel (§21.5).
/// </summary>
internal sealed record SnesDspPitchLane(
    PitchAccuracy Accuracy,
    string Header,
    bool ChromaticGrid,
    IReadOnlyList<string> AxisLabels);

/// <summary>
/// The SNES S-DSP presentation rules the overlay renderer consumes (§17,
/// §21, §27): the fixed 4-column x 2-row voice topology, the 1080p/720p
/// reference geometry, the panel-header token format (badges, pan, phase
/// marker) and the pitch-lane rules. Pure managed metadata: no rendering,
/// no file I/O, no audio.
/// </summary>
internal sealed class SnesDspPresentation
{
    /// <summary>The S-DSP always exposes exactly eight voices (§11.2/§21.1).</summary>
    public const int VoiceCount = 8;

    /// <summary>Fixed voice grid: four columns (§21.1).</summary>
    public const int Columns = 4;

    /// <summary>Fixed voice grid: two rows (§21.1).</summary>
    public const int Rows = 2;

    // S-DSP echo registers (§20.2/§27): EFB $0C (echo feedback, signed 7-bit),
    // EON $4D (per-voice echo enable), FLG $6C (RES = echo-buffer reset,
    // bit 4), EDL $7D (echo delay in frames).
    public const int EchoFeedbackRegister = 0x0C;
    public const int EchoOnRegister = 0x4D;
    public const int FlagsRegister = 0x6C;
    public const int EchoDelayRegister = 0x7D;

    /// <summary>FLG.RES (bit 4): while set, the echo buffer is being reset (§27).</summary>
    public const byte FlagEchoResetMask = 0x10;

    /// <summary>Reference geometry at 1080p: canvas 1920x1080, top bar 64px,
    /// bottom status/echo bar 72px, grid 944px, panel 480x472, header 28px,
    /// scope 180px, divider 3px, pitch lane 245px (§21.2).</summary>
    public static readonly SnesDspLayoutGeometry Geometry1080p = new(
        W: 1920, H: 1080, TopBar: 64, BottomBar: 72, GridHeight: 944,
        PanelW: 480, PanelH: 472, HeaderH: 28, ScopeH: 180, DividerH: 3, PitchLaneH: 245);

    /// <summary>Reference geometry at 720p: canvas 1440x720, top bar 44px,
    /// bottom bar 48px, grid 628px, panel 360x314, header 18px, scope 116px,
    /// divider 2px, pitch lane 164px (§21.3).</summary>
    public static readonly SnesDspLayoutGeometry Geometry720p = new(
        W: 1440, H: 720, TopBar: 44, BottomBar: 48, GridHeight: 628,
        PanelW: 360, PanelH: 314, HeaderH: 18, ScopeH: 116, DividerH: 2, PitchLaneH: 164);

    /// <summary>
    /// Builds the panel header for one voice (§21.4). Token format:
    /// 'V1  SRC 23 · A1B2C3  [PM] [N] [E]  L◀●▶R'. Only the badges that are
    /// actually active are emitted, in the fixed order PM, N, E. The pan
    /// value follows §17: pan=(rightEnergy-leftEnergy)/max(1,leftEnergy+rightEnergy)
    /// with energy=|volume|. The label keeps the literal 'L◀●▶R' pan scale
    /// and appends the phase marker 'Ø' when VOLL/VOLR have opposite signs
    /// (surround).
    /// </summary>
    public static SnesDspPanelHeader BuildHeader(
        int voiceIndex,
        int sourceNumber,
        string shortHash,
        bool noise,
        bool pitchMod,
        bool echoSend,
        sbyte volLeft,
        sbyte volRight)
    {
        if (voiceIndex < 0 || voiceIndex >= VoiceCount)
            throw new ArgumentOutOfRangeException(nameof(voiceIndex));
        if (sourceNumber is < 0 or > 0xFF)
            throw new ArgumentOutOfRangeException(nameof(sourceNumber));
        ArgumentNullException.ThrowIfNull(shortHash);

        var badges = new List<string>(3);
        if (pitchMod)
            badges.Add("[PM]");
        if (noise)
            badges.Add("[N]");
        if (echoSend)
            badges.Add("[E]");
        string badgeText = string.Join(" ", badges);

        int leftEnergy = Math.Abs((int)volLeft);
        int rightEnergy = Math.Abs((int)volRight);
        double pan = (rightEnergy - leftEnergy) / (double)Math.Max(1, leftEnergy + rightEnergy);
        bool phaseMarker = volLeft != 0 && volRight != 0 && (volLeft < 0) != (volRight < 0);
        string panSegment = phaseMarker ? "L◀●▶RØ" : "L◀●▶R";

        string label = $"V{voiceIndex + 1}  SRC {sourceNumber:D2} · {shortHash}  {badgeText}  {panSegment}";

        return new SnesDspPanelHeader(label, badges, pan, phaseMarker);
    }

    /// <summary>
    /// Pitch-lane rules (§21.5): Estimated and Exact sources use a chromatic
    /// grid labelled by C octaves; Relative pitch uses semitone-offset lanes
    /// anchored at 0 with -12/0/+12 labels and a 'REL' header; Unpitched
    /// sources collapse to an activity/envelope lane.
    /// </summary>
    public static SnesDspPitchLane PitchLaneFor(PitchAccuracy accuracy) => accuracy switch
    {
        PitchAccuracy.Exact or PitchAccuracy.Estimated =>
            new SnesDspPitchLane(accuracy, "", ChromaticGrid: true, ChromaticLabels),
        PitchAccuracy.Relative =>
            new SnesDspPitchLane(accuracy, "REL", ChromaticGrid: false, RelativeLabels),
        _ =>
            new SnesDspPitchLane(PitchAccuracy.Unpitched, "", ChromaticGrid: false, Array.Empty<string>()),
    };

    /// <summary>
    /// Derives the echo-strip state from the S-DSP registers (§20.2/§27):
    /// EFB $0C (signed 7-bit feedback in bits 0-6), EDL $7D (echo delay in
    /// frames), EON $4D (per-voice echo enable), FLG $6C bit 4 (echo-buffer
    /// reset). The FIR chain is active as soon as echo delay is configured
    /// and the buffer is not being reset; the strip is <see cref="SnesDspEchoStripState.Enabled"/>
    /// only when at least one voice is routed to the echo bus.
    /// </summary>
    public static SnesDspEchoStripState DeriveEchoStrip(byte efb, byte edl, byte eon, byte flg)
    {
        bool echoReset = (flg & FlagEchoResetMask) != 0;
        bool firActive = !echoReset && edl > 0;
        bool enabled = firActive && eon != 0;

        int raw = efb & 0x7F;
        int feedback = raw >= 0x40 ? raw - 0x80 : raw;

        return new SnesDspEchoStripState(enabled, feedback, edl, firActive);
    }

    private static readonly string[] ChromaticLabels =
        ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

    private static readonly string[] RelativeLabels =
        ["-12", "0", "+12"];
}
