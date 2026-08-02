namespace Fmp.Core.Visualization;

/// <summary>
/// MIDI channel pitch state shared by the semantic timeline and the fallback
/// audio renderer. It implements the 14-bit pitch wheel plus the standard
/// pitch-affecting registered parameters:
/// RPN 0 Pitch Bend Sensitivity, RPN 1 Fine Tuning, and RPN 2 Coarse Tuning.
/// </summary>
internal sealed class MidiChannelPitchState
{
    private const int DataCenter = 0x2000;
    private const int DataMaximum = 0x3FFF;

    private ParameterSelection _selection;
    private int _rpnMsb = 0x7F;
    private int _rpnLsb = 0x7F;
    private int _pitchBend = DataCenter;
    private int _bendRangeMsb = 2;
    private int _bendRangeLsb;
    private int _fineTuning = DataCenter;
    private int _coarseTuning = 64;

    public int PitchBendValue => _pitchBend;

    public double BendRangeSemitones =>
        _bendRangeMsb + _bendRangeLsb / 100.0;

    public double BendSemitones =>
        (_pitchBend - DataCenter) / (double)DataCenter * BendRangeSemitones;

    public double FineTuningSemitones =>
        (_fineTuning - DataCenter) / (double)DataCenter;

    public double CoarseTuningSemitones => _coarseTuning - 64;

    public double PitchOffsetSemitones =>
        BendSemitones + FineTuningSemitones + CoarseTuningSemitones;

    public bool ApplyPitchBend(int lsb, int msb)
    {
        double before = PitchOffsetSemitones;
        _pitchBend = (Math.Clamp(msb, 0, 127) << 7)
            | Math.Clamp(lsb, 0, 127);
        return Changed(before);
    }

    public bool ApplyControlChange(int controller, int value)
    {
        controller = Math.Clamp(controller, 0, 127);
        value = Math.Clamp(value, 0, 127);
        double before = PitchOffsetSemitones;

        switch (controller)
        {
            case 98: // NRPN LSB
            case 99: // NRPN MSB
                _selection = ParameterSelection.Nrpn;
                break;

            case 100: // RPN LSB
                _selection = ParameterSelection.Rpn;
                _rpnLsb = value;
                break;

            case 101: // RPN MSB
                _selection = ParameterSelection.Rpn;
                _rpnMsb = value;
                break;

            case 6: // Data Entry MSB
                ApplyDataEntryMsb(value);
                break;

            case 38: // Data Entry LSB
                ApplyDataEntryLsb(value);
                break;

            case 96: // Data Increment
                AdjustSelectedParameter(+1);
                break;

            case 97: // Data Decrement
                AdjustSelectedParameter(-1);
                break;

            case 121: // Reset All Controllers
                _pitchBend = DataCenter;
                _selection = ParameterSelection.None;
                _rpnMsb = 0x7F;
                _rpnLsb = 0x7F;
                break;
        }

        return Changed(before);
    }

    private void ApplyDataEntryMsb(int value)
    {
        if (_selection != ParameterSelection.Rpn)
            return;

        switch ((_rpnMsb, _rpnLsb))
        {
            case (0, 0): // Pitch Bend Sensitivity
                _bendRangeMsb = value;
                break;
            case (0, 1): // Channel Fine Tuning
                _fineTuning = (value << 7) | (_fineTuning & 0x7F);
                break;
            case (0, 2): // Channel Coarse Tuning
                _coarseTuning = value;
                break;
        }
    }

    private void ApplyDataEntryLsb(int value)
    {
        if (_selection != ParameterSelection.Rpn)
            return;

        switch ((_rpnMsb, _rpnLsb))
        {
            case (0, 0): // Pitch Bend Sensitivity, cents
                _bendRangeLsb = value;
                break;
            case (0, 1): // Channel Fine Tuning
                _fineTuning = (_fineTuning & 0x3F80) | value;
                break;
        }
    }

    private void AdjustSelectedParameter(int delta)
    {
        if (_selection != ParameterSelection.Rpn)
            return;

        switch ((_rpnMsb, _rpnLsb))
        {
            case (0, 0):
            {
                int combined = (_bendRangeMsb << 7) | _bendRangeLsb;
                combined = Math.Clamp(combined + delta, 0, DataMaximum);
                _bendRangeMsb = combined >> 7;
                _bendRangeLsb = combined & 0x7F;
                break;
            }
            case (0, 1):
                _fineTuning = Math.Clamp(_fineTuning + delta, 0, DataMaximum);
                break;
            case (0, 2):
                _coarseTuning = Math.Clamp(_coarseTuning + delta, 0, 127);
                break;
        }
    }

    private bool Changed(double before) =>
        Math.Abs(PitchOffsetSemitones - before) > 1e-12;

    private enum ParameterSelection
    {
        None,
        Rpn,
        Nrpn,
    }
}
