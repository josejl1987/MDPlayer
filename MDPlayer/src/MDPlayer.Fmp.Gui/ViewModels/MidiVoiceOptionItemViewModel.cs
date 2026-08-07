using Fmp.Application.Export;

namespace Fmp.Gui.ViewModels;

/// <summary>
/// One row in the GUI's per-voice MIDI export panel. Binds to include, GM
/// program, MIDI channel, velocity, and transpose for a specific timeline
/// <c>ChannelId</c>. A "send to project" / reset keeps the surface honest.
/// </summary>
public sealed class MidiVoiceOptionItemViewModel : ObservableObject
{
    private bool _include = true;
    private int _program = -1;
    private int _channel = -1;
    private int _velocity = 90;
    private int _transposeSemitones;

    public MidiVoiceOptionItemViewModel(MidiVoiceDescriptor descriptor)
    {
        _descriptor = descriptor;
        Program = descriptor.IsPercussion ? -1 : 0;
    }

    private MidiVoiceDescriptor _descriptor;
    public MidiVoiceDescriptor Descriptor => _descriptor;

    /// <summary>Updates the descriptor without losing the user's per-voice edits.</summary>
    public void ReplaceDescriptor(MidiVoiceDescriptor descriptor)
    {
        _descriptor = descriptor;
        OnPropertyChanged(nameof(ChannelId));
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(IsPercussion));
    }

    public string ChannelId => _descriptor.ChannelId;
    public string Label => _descriptor.Label;
    public bool IsPercussion => _descriptor.IsPercussion;

    /// <summary>Include this voice in the export.</summary>
    public bool Include
    {
        get => _include;
        set => SetProperty(ref _include, value);
    }

    /// <summary>GM program (0–127); -1 means "leave default".</summary>
    public int Program
    {
        get => _program;
        set => SetProperty(ref _program, value);
    }

    /// <summary>MIDI channel (0–15); -1 means "auto".</summary>
    public int Channel
    {
        get => _channel;
        set => SetProperty(ref _channel, value);
    }

    /// <summary>Note velocity (1–127).</summary>
    public int Velocity
    {
        get => _velocity;
        set => SetProperty(ref _velocity, Math.Clamp(value, 1, 127));
    }

    /// <summary>Semitones to transpose this voice.</summary>
    public int TransposeSemitones
    {
        get => _transposeSemitones;
        set => SetProperty(ref _transposeSemitones, value);
    }

    /// <summary>Expose as a request DTO when this voice is actually part of an export.</summary>
    public MidiVoiceOption ToOption(bool isDefaultProgram, bool isDefaultChannel)
    {
        var option = new MidiVoiceOption(ChannelId)
        {
            Include = Include,
            TransposeSemitones = TransposeSemitones,
        };
        if (!isDefaultProgram)
            option.Program = Math.Clamp(Program, 0, 127);
        if (!isDefaultChannel)
            option.Channel = Math.Clamp(Channel, 0, 15);
        if (Velocity != 90)
            option.Velocity = Velocity;
        return option;
    }
}