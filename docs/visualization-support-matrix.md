# Visualization support matrix

This is the initial, code-reviewable inventory for generic visualization. A
row is not a promise that the format is already renderable. `Unknown` is kept
explicitly where the repository does not yet prove the required headless path.

| Format family | Driver/path | Active chips | Existing keyboard display | Note source | Mask support | Auxiliary assets | Portable | Planned tier |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| OVI | FMP + Nise98 | YM2608, PPZ8 | Yes | Register callbacks | YM2608 groups, PPZ8 groups | FMP.COM, FMP search paths, sample banks | Yes | Rich/current; verified local renderer fixture |
| OPI/OZI | FMP + Nise98 | YM2608, PPZ8 | Not claimed | Register callbacks | Not claimed | FMP.COM and banks | Unverified | Extension recognized; no distributable renderer fixture |
| MPI/MVI/MZI | FMP + Nise98 | YM2608, PPZ8 | Not claimed | Register callbacks | Not claimed | FMP.COM and banks | Unverified | Extension recognized; no distributable renderer fixture |
| VGM/VGZ | Headless VGM register-log path | YM2151, YM2203, YM2413, YM2608, YM2610, YM3526, YM3812, Y8950, YMF262, YMF278B, YMZ280B, MultiPCM, OKIM6258, OKIM6295, YM2612, SN76489, AY8910, DMG, NES APU, HuC6280, K051649, SEGAPCM, RF5C68, RF5C164, C140, C352, K054539, GA20 (file-dependent) | Yes | Register stream | Master scope current; channel capability planned | Usually none; PCM banks/ROMs remain asset-dependent | Yes | Rich timeline + activity lanes + master waveform |
| ZGM | MDPlayer ZGM path | File-dependent | Per supported chip | Register stream | Per chip | Usually none | Unknown | Progressive |
| S98 v0-v3 | Headless S98 register-log path | YM2151, YM2203, YM2413, YM3526, YM3812, YMF262, YM2608, YM2612, SN76489, AY8910 (current) | Yes | Register stream | Master current; channel planning reported where backend supports it | Usually none; Inflate dump supported | Yes | Rich for portable chip subset |
| XGM | Headless XGM frame/register path | YM2612, SN76489, XGM PCM through YM2612 DAC | Yes | Frame register stream + PCM sample events | Master scope current; channel capability planned | Embedded PCM block | Yes | Rich timeline + master waveform |
| MID | Headless Standard MIDI path | MIDI | Yes | Native MIDI events | Channel semantics; master scope current | Deterministic built-in fallback synth | Yes | Rich timeline + master waveform |
| RCP/RCS | MDPlayer MIDI path (headless bridge not linked) | MIDI, PCM8 where applicable | Yes/partial | Native MIDI events | Channel semantics | CM6/GSD, PCM data | Unknown | Platform boundary |
| MDX | Portable MDX command subset + MXDRV compatibility probe | YM2151 (PCM8/PDX pending) | Yes | Native MDX commands -> register writes | Master scope current | PDX for PCM tracks | Yes for supported command subset | Rich YM2151 + deterministic repeats; advanced MXDRV/PCM progressive |
| MND | MND driver path | YM2151, YM2608, PCM | Yes/partial | Driver writes | Driver/channel audit required | PND | Unknown | Progressive |
| MUC/MUB | MUCOM path | YM2608 | Yes | Driver writes | Driver/channel audit required | Driver assets | Unknown | Progressive |
| PMD M/M2/MZ | PMD path | YM2608 | Yes | Driver writes | Driver/channel audit required | Driver assets | Unknown | Progressive |
| MDR | MDPlayer path | YMF278B | Yes/partial | Driver writes | Unknown | ROM/sample data | Unknown | Progressive |
| NRD/NDP | MDPlayer register/driver paths | File-dependent | Per supported chip | Driver/register writes | Driver-dependent | Driver/runtime assets | Unknown | Platform boundary |
| NSF | NES path | APU and expansions | Yes for listed devices | Register/CPU writes | Per expansion | ROM only | Unknown | Progressive |
| HES | HuC6280 path | HuC6280 | Yes | Register writes | Per channel | ROM only | Unknown | Progressive |
| AY/MGS | AY/MSX paths | AY8910 and related devices | Yes/partial | Register/driver writes | Per chip | Driver/sample assets | Unknown | Progressive |
| ZMS/ZMD | ZMUSIC path | YM2151 and PCM families | Yes/partial | Driver writes | Unknown | PCM/runtime assets | Unknown | Progressive |
| GBS | MDPlayer Game Boy path (headless bridge not linked) | DMG | Listed support must be verified | CPU/register path | Unknown | ROM only | Unknown | Platform boundary |
| SID | MDPlayer SID path (headless bridge not linked) | SID | Listed support must be verified | CPU/register path | Unknown | ROM/runtime data | Unknown | Platform boundary |
| WAV/MP3/OGG/FLAC/AAC | Audio decoder | None | No authoritative note state | None | None | N/A | Master audio only / excluded |

The first generic implementation keeps FMP as the compatibility baseline. The
current non-FMP verticals are deterministic VGM (YM2612, YM2151, YM2203,
YM2413, YM2608, YM2610, YM3526, YM3812, YMF262, SN76489, AY8910, DMG,
NES APU, HuC6280, K051649, YMF278B, YMZ280B, MultiPCM, OKIM6258, OKIM6295,
SEGAPCM, RF5C68, RF5C164, C140, C352, K054539 and GA20), S98 v0-v3 (YM2151, YM2203, YM2413,
YM3526, YM3812, YMF262, YM2608, YM2612, SN76489 and
AY8910), XGM (YM2612, SN76489 and embedded DAC samples), native Standard MIDI, and a
portable MDX/YM2151 command subset. The registry also probes MDX PCM/advanced
commands and other MDPlayer driver families, reporting their PDX/companion
assets and platform-specific MXDRV boundary without admitting unsupported
paths as visualizable. Unsupported formats remain explicit rather than being
admitted by extension alone.
