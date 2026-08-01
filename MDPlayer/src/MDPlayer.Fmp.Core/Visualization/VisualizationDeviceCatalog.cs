namespace Fmp.Core.Visualization;

internal static class VisualizationDeviceCatalog
{
    public static DeviceDescriptor Ym2203(int instance = 0, long clockHz = 3_579_545) => new(
        new DeviceId(ChipType.Ym2203, instance),
        $"YM2203 #{instance}",
        clockHz,
        DeviceCapabilities.Notes
            | DeviceCapabilities.ContinuousPitch
            | DeviceCapabilities.Instruments
            | DeviceCapabilities.PercussionVoices,
        ScopeSupport.Master);

    public static DeviceDescriptor Ym2610(int instance = 0, long clockHz = 8_000_000) => new(
        new DeviceId(ChipType.Ym2610, instance),
        $"YM2610 #{instance}",
        clockHz,
        DeviceCapabilities.Notes
            | DeviceCapabilities.ContinuousPitch
            | DeviceCapabilities.Instruments
            | DeviceCapabilities.PercussionVoices
            | DeviceCapabilities.SampleIdentity,
        ScopeSupport.Master);

    public static IReadOnlyList<VoiceDescriptor> Ym2610Voices(int instance = 0)
    {
        DeviceId device = new(ChipType.Ym2610, instance);
        var voices = new List<VoiceDescriptor>(9);
        for (int index = 0; index < 4; index++)
        {
            voices.Add(new VoiceDescriptor(
                new VoiceId(device, VoiceKind.Fm, index),
                $"FM{index + 1}",
                VoicePresentationKind.Fm,
                index,
                false,
                false,
                true));
        }
        for (int index = 0; index < 3; index++)
        {
            voices.Add(new VoiceDescriptor(
                new VoiceId(device, VoiceKind.Ssg, index),
                $"SSG{index + 1}",
                VoicePresentationKind.Psg,
                4 + index,
                false,
                false,
                true));
        }
        voices.Add(new VoiceDescriptor(
            new VoiceId(device, VoiceKind.Adpcm, 0, Name: "adpcm-a"),
            "ADPCM-A",
            VoicePresentationKind.Pcm,
            7,
            false,
            false,
            false));
        voices.Add(new VoiceDescriptor(
            new VoiceId(device, VoiceKind.Adpcm, 1, Name: "adpcm-b"),
            "ADPCM-B",
            VoicePresentationKind.Pcm,
            8,
            false,
            false,
            false));
        return voices;
    }

    public static DeviceDescriptor Ym2413(int instance = 0, long clockHz = 3_579_545) => new(
        new DeviceId(ChipType.Ym2413, instance),
        $"YM2413 #{instance}",
        clockHz,
        DeviceCapabilities.Notes
            | DeviceCapabilities.ContinuousPitch
            | DeviceCapabilities.Instruments
            | DeviceCapabilities.PercussionVoices,
        ScopeSupport.Master);

    public static IReadOnlyList<VoiceDescriptor> Ym2413Voices(int instance = 0)
    {
        DeviceId device = new(ChipType.Ym2413, instance);
        var voices = new List<VoiceDescriptor>(14);
        for (int index = 0; index < 9; index++)
        {
            voices.Add(new VoiceDescriptor(
                new VoiceId(device, VoiceKind.Fm, index),
                $"FM{index + 1}",
                VoicePresentationKind.Fm,
                index,
                false,
                false,
                true));
        }
        string[] names = ["BD", "SD", "Tom", "Cymbal", "Hi-hat"];
        for (int index = 0; index < names.Length; index++)
        {
            voices.Add(new VoiceDescriptor(
                new VoiceId(device, VoiceKind.Rhythm, index, Name: names[index].ToLowerInvariant()),
                names[index],
                VoicePresentationKind.Percussion,
                9 + index,
                true,
                true,
                false));
        }
        return voices;
    }

    public static DeviceDescriptor Ym3526(int instance = 0, long clockHz = 3_579_545) => OplDevice(
        ChipType.Ym3526, instance, clockHz, "YM3526", pan: false);

    public static DeviceDescriptor Ym3812(int instance = 0, long clockHz = 3_579_545) => OplDevice(
        ChipType.Ym3812, instance, clockHz, "YM3812", pan: false);

    public static DeviceDescriptor Y8950(int instance = 0, long clockHz = 3_579_545) => OplDevice(
        ChipType.Y8950, instance, clockHz, "Y8950", pan: false);

    public static DeviceDescriptor Ymf262(int instance = 0, long clockHz = 14_318_180) => OplDevice(
        ChipType.Ymf262, instance, clockHz, "YMF262", pan: true);

    public static DeviceDescriptor Ymf278b(int instance = 0, long clockHz = 33_868_800) => OplDevice(
        ChipType.Ymf278b, instance, clockHz, "YMF278B", pan: true) with
    {
        Capabilities = DeviceCapabilities.Notes
            | DeviceCapabilities.ContinuousPitch
            | DeviceCapabilities.Instruments
            | DeviceCapabilities.PercussionVoices
            | DeviceCapabilities.SampleIdentity
            | DeviceCapabilities.Pan,
    };

    public static IReadOnlyList<VoiceDescriptor> OplVoices(ChipType type, int instance = 0)
    {
        int channelCount = type is ChipType.Ymf262 or ChipType.Ymf278b ? 18 : 9;
        DeviceId device = new(type, instance);
        var voices = new List<VoiceDescriptor>(channelCount + 5);
        for (int index = 0; index < channelCount; index++)
        {
            voices.Add(new VoiceDescriptor(
                new VoiceId(device, VoiceKind.Fm, index),
                $"FM{index + 1}",
                VoicePresentationKind.Fm,
                index,
                false,
                false,
                true));
        }

        string[] names = ["BD", "SD", "Tom", "Cymbal", "Hi-hat"];
        for (int index = 0; index < names.Length; index++)
        {
            voices.Add(new VoiceDescriptor(
                new VoiceId(device, VoiceKind.Rhythm, index, Name: names[index].ToLowerInvariant()),
                names[index],
                VoicePresentationKind.Percussion,
                channelCount + index,
                true,
                true,
                false));
        }
        return voices;
    }

    private static DeviceDescriptor OplDevice(
        ChipType type,
        int instance,
        long clockHz,
        string name,
        bool pan) => new(
        new DeviceId(type, instance),
        $"{name} #{instance}",
        clockHz,
        DeviceCapabilities.Notes
            | DeviceCapabilities.ContinuousPitch
            | DeviceCapabilities.Instruments
            | DeviceCapabilities.PercussionVoices
            | (pan ? DeviceCapabilities.Pan : DeviceCapabilities.None),
        ScopeSupport.Master);

    public static IReadOnlyList<VoiceDescriptor> Ym2203Voices(int instance = 0)
    {
        DeviceId device = new(ChipType.Ym2203, instance);
        var voices = new List<VoiceDescriptor>(7);
        for (int index = 0; index < 3; index++)
        {
            voices.Add(new VoiceDescriptor(
                new VoiceId(device, VoiceKind.Fm, index),
                $"FM{index + 1}",
                VoicePresentationKind.Fm,
                index,
                false,
                false,
                true));
        }
        for (int index = 0; index < 3; index++)
        {
            voices.Add(new VoiceDescriptor(
                new VoiceId(device, VoiceKind.Ssg, index),
                $"SSG{index + 1}",
                VoicePresentationKind.Psg,
                3 + index,
                false,
                false,
                true));
        }
        voices.Add(new VoiceDescriptor(
            new VoiceId(device, VoiceKind.Noise, 0),
            "Noise",
            VoicePresentationKind.Noise,
            6,
            false,
            true,
            false));
        return voices;
    }

    public static DeviceDescriptor Ym2608(int instance = 0) => new(
        new DeviceId(ChipType.Ym2608, instance),
        $"YM2608 #{instance}",
        7_987_200,
        DeviceCapabilities.Notes
            | DeviceCapabilities.ContinuousPitch
            | DeviceCapabilities.Instruments
            | DeviceCapabilities.PercussionVoices
            | DeviceCapabilities.VoiceMasking
            | DeviceCapabilities.ParallelSynthesis
            | DeviceCapabilities.Pan,
        ScopeSupport.Channel);

    public static DeviceDescriptor Ppz8(int instance = 0) => new(
        new DeviceId(ChipType.Ppz8, instance),
        $"PPZ8 #{instance}",
        0,
        DeviceCapabilities.SampleIdentity
            | DeviceCapabilities.VoiceMasking
            | DeviceCapabilities.ParallelSynthesis,
        ScopeSupport.Channel);

    public static IReadOnlyList<VoiceDescriptor> Ym2608Voices(int instance = 0)
    {
        DeviceId device = new(ChipType.Ym2608, instance);
        var voices = new List<VoiceDescriptor>(12);
        for (int index = 0; index < 6; index++)
        {
            voices.Add(new VoiceDescriptor(
                new VoiceId(device, VoiceKind.Fm, index),
                $"FM{index + 1}",
                index == 2 ? VoicePresentationKind.Fm3 : VoicePresentationKind.Fm,
                index,
                false,
                false,
                true));
        }

        for (int index = 0; index < 3; index++)
        {
            voices.Add(new VoiceDescriptor(
                new VoiceId(device, VoiceKind.Ssg, index),
                $"SSG{index + 1}",
                VoicePresentationKind.Psg,
                6 + index,
                false,
                false,
                true));
        }

        voices.Add(new VoiceDescriptor(
            new VoiceId(device, VoiceKind.Rhythm, 0, Name: "rhythm"),
            "Rhythm",
            VoicePresentationKind.Percussion,
            9,
            true,
            true,
            false));
        voices.Add(new VoiceDescriptor(
            new VoiceId(device, VoiceKind.Adpcm, 0, Name: "adpcm-b"),
            "ADPCM-B",
            VoicePresentationKind.Pcm,
            10,
            false,
            false,
            false));

        DeviceId ppz = new(ChipType.Ppz8, instance);
        voices.Add(new VoiceDescriptor(
            new VoiceId(ppz, VoiceKind.Pcm, 0, Name: "ppz8"),
            "PPZ8",
            VoicePresentationKind.Pcm,
            11,
            false,
            false,
            false));
        return voices;
    }

    public static DeviceDescriptor Ym2612(int instance = 0, long clockHz = 7_670_454) => new(
        new DeviceId(ChipType.Ym2612, instance),
        $"YM2612 #{instance}",
        clockHz,
        DeviceCapabilities.Notes
            | DeviceCapabilities.ContinuousPitch
            | DeviceCapabilities.Instruments
            | DeviceCapabilities.Pan,
        ScopeSupport.Master);

    public static IReadOnlyList<VoiceDescriptor> Ym2612Voices(int instance = 0)
    {
        DeviceId device = new(ChipType.Ym2612, instance);
        var voices = new List<VoiceDescriptor>(10);
        for (int index = 0; index < 6; index++)
        {
            voices.Add(new VoiceDescriptor(
                new VoiceId(device, VoiceKind.Fm, index),
                $"FM{index + 1}",
                VoicePresentationKind.Fm,
                index,
                false,
                false,
                true));
        }

        for (int index = 0; index < 4; index++)
        {
            voices.Add(new VoiceDescriptor(
                new VoiceId(device, VoiceKind.Fm3Operator, index),
                $"FM3 OP{index + 1}",
                VoicePresentationKind.Fm3,
                6 + index,
                false,
                false,
                true));
        }
        return voices;
    }

    public static DeviceDescriptor Ym2151(int instance = 0, long clockHz = 3_579_545) => new(
        new DeviceId(ChipType.Ym2151, instance),
        $"YM2151 #{instance}",
        clockHz,
        DeviceCapabilities.Notes
            | DeviceCapabilities.ContinuousPitch
            | DeviceCapabilities.Instruments
            | DeviceCapabilities.Pan,
        ScopeSupport.Master);

    public static IReadOnlyList<VoiceDescriptor> Ym2151Voices(int instance = 0)
    {
        DeviceId device = new(ChipType.Ym2151, instance);
        var voices = new List<VoiceDescriptor>(8);
        for (int index = 0; index < 8; index++)
        {
            voices.Add(new VoiceDescriptor(
                new VoiceId(device, VoiceKind.Fm, index),
                $"FM{index + 1}",
                VoicePresentationKind.Fm,
                index,
                false,
                false,
                true));
        }
        return voices;
    }

    public static DeviceDescriptor Sn76489(int instance = 0, long clockHz = 3_579_545) => new(
        new DeviceId(ChipType.Sn76489, instance),
        $"SN76489 #{instance}",
        clockHz,
        DeviceCapabilities.Notes
            | DeviceCapabilities.ContinuousPitch,
        ScopeSupport.Master);

    public static IReadOnlyList<VoiceDescriptor> Sn76489Voices(int instance = 0)
    {
        DeviceId device = new(ChipType.Sn76489, instance);
        var voices = new List<VoiceDescriptor>(4);
        for (int index = 0; index < 3; index++)
        {
            voices.Add(new VoiceDescriptor(
                new VoiceId(device, VoiceKind.Psg, index),
                $"Tone {index + 1}",
                VoicePresentationKind.Psg,
                index,
                false,
                false,
                true));
        }
        voices.Add(new VoiceDescriptor(
            new VoiceId(device, VoiceKind.Noise, 0),
            "Noise",
            VoicePresentationKind.Noise,
            3,
            false,
            true,
            false));
        return voices;
    }

    public static DeviceDescriptor Ay8910(int instance = 0, long clockHz = 1_789_773) => new(
        new DeviceId(ChipType.Ay8910, instance),
        $"AY8910 #{instance}",
        clockHz,
        DeviceCapabilities.Notes
            | DeviceCapabilities.ContinuousPitch
            | DeviceCapabilities.PercussionVoices,
        ScopeSupport.Master);

    public static IReadOnlyList<VoiceDescriptor> Ay8910Voices(int instance = 0)
    {
        DeviceId device = new(ChipType.Ay8910, instance);
        var voices = new List<VoiceDescriptor>(4);
        for (int index = 0; index < 3; index++)
        {
            voices.Add(new VoiceDescriptor(
                new VoiceId(device, VoiceKind.Psg, index),
                $"Tone {index + 1}",
                VoicePresentationKind.Psg,
                index,
                false,
                false,
                true));
        }
        voices.Add(new VoiceDescriptor(
            new VoiceId(device, VoiceKind.Noise, 0),
            "Noise",
            VoicePresentationKind.Noise,
            3,
            false,
            true,
            false));
        return voices;
    }

    public static DeviceDescriptor Dmg(int instance = 0, long clockHz = 4_194_304) => new(
        new DeviceId(ChipType.Dmg, instance),
        $"DMG #{instance}",
        clockHz,
        DeviceCapabilities.Notes
            | DeviceCapabilities.ContinuousPitch
            | DeviceCapabilities.PercussionVoices,
        ScopeSupport.Master);

    public static IReadOnlyList<VoiceDescriptor> DmgVoices(int instance = 0)
    {
        DeviceId device = new(ChipType.Dmg, instance);
        return
        [
            new VoiceDescriptor(new VoiceId(device, VoiceKind.Pulse, 0), "Pulse 1", VoicePresentationKind.Psg, 0, false, false, true),
            new VoiceDescriptor(new VoiceId(device, VoiceKind.Pulse, 1), "Pulse 2", VoicePresentationKind.Psg, 1, false, false, true),
            new VoiceDescriptor(new VoiceId(device, VoiceKind.Wavetable, 0), "Wave", VoicePresentationKind.Pitched, 2, false, false, true),
            new VoiceDescriptor(new VoiceId(device, VoiceKind.Noise, 0), "Noise", VoicePresentationKind.Noise, 3, false, true, false),
        ];
    }

    public static DeviceDescriptor NesApu(int instance = 0, long clockHz = 1_789_773) => new(
        new DeviceId(ChipType.NesApu, instance),
        $"NES APU #{instance}",
        clockHz,
        DeviceCapabilities.Notes
            | DeviceCapabilities.ContinuousPitch
            | DeviceCapabilities.PercussionVoices
            | DeviceCapabilities.SampleIdentity,
        ScopeSupport.Master);

    public static IReadOnlyList<VoiceDescriptor> NesApuVoices(int instance = 0)
    {
        DeviceId device = new(ChipType.NesApu, instance);
        return
        [
            new VoiceDescriptor(new VoiceId(device, VoiceKind.Pulse, 0), "Pulse 1", VoicePresentationKind.Psg, 0, false, false, true),
            new VoiceDescriptor(new VoiceId(device, VoiceKind.Pulse, 1), "Pulse 2", VoicePresentationKind.Psg, 1, false, false, true),
            new VoiceDescriptor(new VoiceId(device, VoiceKind.Triangle, 0), "Triangle", VoicePresentationKind.Pitched, 2, false, false, true),
            new VoiceDescriptor(new VoiceId(device, VoiceKind.Noise, 0), "Noise", VoicePresentationKind.Noise, 3, false, true, false),
            new VoiceDescriptor(new VoiceId(device, VoiceKind.Dpcm, 0), "DPCM", VoicePresentationKind.Pcm, 4, false, true, false),
        ];
    }

    public static DeviceDescriptor Huc6280(int instance = 0, long clockHz = 3_579_545) => new(
        new DeviceId(ChipType.Huc6280, instance),
        $"HuC6280 #{instance}",
        clockHz,
        DeviceCapabilities.Notes
            | DeviceCapabilities.ContinuousPitch
            | DeviceCapabilities.Pan,
        ScopeSupport.Master);

    public static IReadOnlyList<VoiceDescriptor> Huc6280Voices(int instance = 0)
    {
        DeviceId device = new(ChipType.Huc6280, instance);
        var voices = new List<VoiceDescriptor>(6);
        for (int index = 0; index < 6; index++)
            voices.Add(new VoiceDescriptor(
                new VoiceId(device, VoiceKind.Wavetable, index),
                $"Wave {index + 1}",
                VoicePresentationKind.Pitched,
                index,
                false,
                false,
                true));
        return voices;
    }

    public static DeviceDescriptor K051649(int instance = 0, long clockHz = 3_579_545) => new(
        new DeviceId(ChipType.K051649, instance),
        $"K051649 #{instance}",
        clockHz,
        DeviceCapabilities.Notes
            | DeviceCapabilities.ContinuousPitch
            | DeviceCapabilities.VoiceMasking,
        ScopeSupport.Master);

    public static IReadOnlyList<VoiceDescriptor> K051649Voices(int instance = 0)
    {
        DeviceId device = new(ChipType.K051649, instance);
        var voices = new List<VoiceDescriptor>(5);
        for (int index = 0; index < 5; index++)
            voices.Add(new VoiceDescriptor(
                new VoiceId(device, VoiceKind.Wavetable, index),
                $"Wave {index + 1}",
                VoicePresentationKind.Pitched,
                index,
                false,
                false,
                true));
        return voices;
    }

    public static DeviceDescriptor Ymz280b(int instance = 0, long clockHz = 16_934_400) => new(
        new DeviceId(ChipType.Ymz280b, instance),
        $"YMZ280B #{instance}",
        clockHz,
        DeviceCapabilities.Notes
            | DeviceCapabilities.ContinuousPitch
            | DeviceCapabilities.SampleIdentity
            | DeviceCapabilities.Pan,
        ScopeSupport.Master);

    public static DeviceDescriptor SegaPcm(int instance = 0, long clockHz = 4_000_000) => PcmDevice(
        ChipType.SegaPcm, instance, clockHz, "SEGAPCM", 16, true);

    public static DeviceDescriptor Rf5c68(int instance = 0, long clockHz = 12_500_000) => PcmDevice(
        ChipType.Rf5c68, instance, clockHz, "RF5C68", 8, true);

    public static DeviceDescriptor Rf5c164(int instance = 0, long clockHz = 12_500_000) => PcmDevice(
        ChipType.Rf5c164, instance, clockHz, "RF5C164", 8, true);

    public static DeviceDescriptor C140(int instance = 0, long clockHz = 21_390) => PcmDevice(
        ChipType.C140, instance, clockHz, "C140", 24, true);

    public static DeviceDescriptor C352(int instance = 0, long clockHz = 24_192_000) => PcmDevice(
        ChipType.C352, instance, clockHz, "C352", 32, true);

    public static DeviceDescriptor K054539(int instance = 0, long clockHz = 18_000_000) => PcmDevice(
        ChipType.K054539, instance, clockHz, "K054539", 8, true);

    public static DeviceDescriptor Ga20(int instance = 0, long clockHz = 8_000_000) => PcmDevice(
        ChipType.Ga20, instance, clockHz, "GA20", 4, true);

    private static DeviceDescriptor PcmDevice(
        ChipType type,
        int instance,
        long clockHz,
        string name,
        int channelCount,
        bool masking) => new(
        new DeviceId(type, instance),
        $"{name} #{instance}",
        clockHz,
        DeviceCapabilities.Notes
            | DeviceCapabilities.ContinuousPitch
            | DeviceCapabilities.SampleIdentity
            | (masking ? DeviceCapabilities.VoiceMasking : DeviceCapabilities.None),
        ScopeSupport.Master);

    public static IReadOnlyList<VoiceDescriptor> PcmVoices(
        ChipType type,
        int instance,
        int channelCount)
    {
        DeviceId device = new(type, instance);
        var voices = new List<VoiceDescriptor>(channelCount);
        for (int index = 0; index < channelCount; index++)
        {
            voices.Add(new VoiceDescriptor(
                new VoiceId(device, VoiceKind.Pcm, index),
                $"PCM {index + 1}",
                VoicePresentationKind.Pcm,
                index,
                false,
                false,
                true));
        }
        return voices;
    }

    public static IReadOnlyList<VoiceDescriptor> Ymz280bVoices(int instance = 0)
    {
        DeviceId device = new(ChipType.Ymz280b, instance);
        var voices = new List<VoiceDescriptor>(8);
        for (int index = 0; index < 8; index++)
        {
            voices.Add(new VoiceDescriptor(
                new VoiceId(device, VoiceKind.Pcm, index),
                $"PCM {index + 1}",
                VoicePresentationKind.Pcm,
                index,
                false,
                false,
                true));
        }
        return voices;
    }

    public static DeviceDescriptor Okim6258(int instance = 0, long clockHz = 4_000_000) => new(
        new DeviceId(ChipType.Okim6258, instance),
        $"OKIM6258 #{instance}",
        clockHz,
        DeviceCapabilities.PercussionVoices | DeviceCapabilities.SampleIdentity,
        ScopeSupport.Master);

    public static IReadOnlyList<VoiceDescriptor> Okim6258Voices(int instance = 0) =>
    [
        new VoiceDescriptor(
            new VoiceId(new DeviceId(ChipType.Okim6258, instance), VoiceKind.Pcm, 0),
            "ADPCM",
            VoicePresentationKind.Pcm,
            0,
            true,
            false,
            false),
    ];

    public static DeviceDescriptor Okim6295(int instance = 0, long clockHz = 4_000_000) => new(
        new DeviceId(ChipType.Okim6295, instance),
        $"OKIM6295 #{instance}",
        clockHz,
        DeviceCapabilities.PercussionVoices
            | DeviceCapabilities.VoiceMasking
            | DeviceCapabilities.SampleIdentity,
        ScopeSupport.Master);

    public static IReadOnlyList<VoiceDescriptor> Okim6295Voices(int instance = 0)
    {
        DeviceId device = new(ChipType.Okim6295, instance);
        var voices = new List<VoiceDescriptor>(4);
        for (int index = 0; index < 4; index++)
        {
            voices.Add(new VoiceDescriptor(
                new VoiceId(device, VoiceKind.Pcm, index),
                $"Voice {index + 1}",
                VoicePresentationKind.Pcm,
                index,
                true,
                false,
                false));
        }
        return voices;
    }

    public static DeviceDescriptor MultiPcm(int instance = 0, long clockHz = 8_000_000) => new(
        new DeviceId(ChipType.MultiPcm, instance),
        $"MultiPCM #{instance}",
        clockHz,
        DeviceCapabilities.Notes
            | DeviceCapabilities.ContinuousPitch
            | DeviceCapabilities.Instruments
            | DeviceCapabilities.VoiceMasking
            | DeviceCapabilities.SampleIdentity
            | DeviceCapabilities.Pan,
        ScopeSupport.Master);

    public static IReadOnlyList<VoiceDescriptor> MultiPcmVoices(int instance = 0)
    {
        DeviceId device = new(ChipType.MultiPcm, instance);
        var voices = new List<VoiceDescriptor>(28);
        for (int index = 0; index < 28; index++)
        {
            voices.Add(new VoiceDescriptor(
                new VoiceId(device, VoiceKind.Pcm, index),
                $"PCM {index + 1}",
                VoicePresentationKind.Pcm,
                index,
                false,
                false,
                true));
        }
        return voices;
    }

    public static DeviceDescriptor Midi(int instance = 0) => new(
        new DeviceId(ChipType.Midi, instance),
        $"MIDI #{instance}",
        0,
        DeviceCapabilities.Notes
            | DeviceCapabilities.ContinuousPitch
            | DeviceCapabilities.Instruments
            | DeviceCapabilities.Expression,
        ScopeSupport.Master);

    public static IReadOnlyList<VoiceDescriptor> MidiVoices(int instance = 0)
    {
        DeviceId device = new(ChipType.Midi, instance);
        var voices = new List<VoiceDescriptor>(16);
        for (int channel = 0; channel < 16; channel++)
        {
            voices.Add(new VoiceDescriptor(
                new VoiceId(device, VoiceKind.MidiChannel, channel),
                channel == 9 ? "Channel 10 (Percussion)" : $"Channel {channel + 1}",
                VoicePresentationKind.Midi,
                channel,
                channel == 9,
                false,
                channel != 9));
        }
        return voices;
    }

    /// <summary>
    /// SNES S-DSP device. The nominal input/output rate is 24,576,000 Hz for
    /// display metadata; the S-DSP emits 32,000 sample frames per second.
    /// Per §11.1 these rates must not be presented as measurements of the
    /// original physical console's oscillator.
    /// </summary>
    public static DeviceDescriptor SnesDsp(int instance = 0) => new(
        new DeviceId(ChipType.SnesDsp, instance),
        $"SNES S-DSP #{instance}",
        24_576_000,
        DeviceCapabilities.Notes
            | DeviceCapabilities.ContinuousPitch
            | DeviceCapabilities.Instruments
            | DeviceCapabilities.NativeVoiceAudio
            | DeviceCapabilities.Pan
            | DeviceCapabilities.Expression
            | DeviceCapabilities.SampleIdentity,
        ScopeSupport.Channel);

    /// <summary>
    /// Exactly eight stable SNES S-DSP voice descriptors (§11.2). A voice can
    /// dynamically become noise-driven, but its stable identity remains the
    /// same.
    /// </summary>
    public static IReadOnlyList<VoiceDescriptor> SnesDspVoices(int instance = 0)
    {
        DeviceId device = new(ChipType.SnesDsp, instance);
        var voices = new List<VoiceDescriptor>(8);
        for (int i = 0; i < 8; i++)
        {
            voices.Add(new VoiceDescriptor(
                new VoiceId(device, VoiceKind.PcmVoice, i),
                $"VOICE {i + 1}",
                VoicePresentationKind.Pcm,
                i,
                false,
                false,
                true));
        }
        return voices;
    }
}
