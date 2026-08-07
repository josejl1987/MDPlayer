namespace Fmp.Core.Visualization;

/// <summary>
/// Maps DAC sample asset ids to stable display bank/note destinations
/// (spec §17). The note is an identity label, never a pitch estimate; bank
/// overflow avoids silently reusing a note on the same destination.
/// </summary>
internal sealed record DacNoteAssignment(int Bank, int Note)
{
    public override string ToString() => $"Bank {Bank} Note {Note}";
}

internal sealed class DacNoteMapper
{
    private const int DefaultNoteBase = 0;
    private const int DefaultNotesPerBank = 128;

    private readonly int _noteBase;
    private readonly int _notesPerBank;

    public DacNoteMapper(int noteBase = DefaultNoteBase, int notesPerBank = DefaultNotesPerBank)
    {
        if (noteBase is < 0 or > 127)
            throw new ArgumentOutOfRangeException(nameof(noteBase), "noteBase must be in [0, 127].");
        if (notesPerBank is < 1 or > 128)
            throw new ArgumentOutOfRangeException(nameof(notesPerBank), "notesPerBank must be in [1, 128].");
        if (notesPerBank > 128 - noteBase)
            throw new ArgumentOutOfRangeException(nameof(notesPerBank), "notesPerBank must not exceed (128 - noteBase).");

        _noteBase = noteBase;
        _notesPerBank = notesPerBank;
    }

    public int NoteBase => _noteBase;
    public int NotesPerBank => _notesPerBank;

    public DacNoteAssignment Map(int assetId)
    {
        if (assetId < 0)
            throw new ArgumentOutOfRangeException(nameof(assetId), "assetId must be non-negative.");

        int bank = assetId / _notesPerBank;
        int note = _noteBase + (assetId % _notesPerBank);
        return new DacNoteAssignment(bank, note);
    }
}