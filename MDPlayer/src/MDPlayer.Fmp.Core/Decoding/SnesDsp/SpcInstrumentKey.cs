namespace Fmp.Core.Decoding.SnesDsp;

/// <summary>Identity of a resolved SPC instrument: sample content (hash) plus the DSP
/// envelope registers and noise mode that shape playback.</summary>
internal readonly record struct SpcInstrumentKey(string SampleHash, byte Adsr1, byte Adsr2, byte Gain, bool Noise);
