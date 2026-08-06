#!/usr/bin/env python3
"""Quick-win production-core generator (perf/README.md "Quick wins").

Derives one or more FMOPNA_Clock variants from the SAME vendored body
(upstream/furnace-ym2608-lle/fmopna_impl.c); the vendored file is never
edited. Each quick win is an independent, independently switchable textual
transform:

  --fixed-prescaler (QW1)  collapse both `switch (prescaler_sel)` blocks to
                           their select=2 (fixed 144-clock) arm. Bit-exact
                           while the session keeps prescaler select 2 (the
                           session ABI enforces that contract).
  --split-reset     (QW2)  emit two functions from the same body with the
                           `chip->ic` seed pinned to a constant:
                             mdp_fmopna_clock_running (ic=0, hot path)
                             mdp_fmopna_clock_reset    (ic=1, cold path)
                           `chip->ic` is derived from input.ic at line 90 and
                           written nowhere else, so pinning is by construction
                           equivalent to the reference while the IC input
                           stays at the corresponding level.
  --copy-helpers    (QW4)  replace the most code-expensive repeated inline
                           pipeline memcpys with calls to shared static
                           noinline helpers (defined at the end of the body),
                           shrinking the hot instruction fetch stream.
  --strip-debug     (QW5)  delete state that only feeds diagnostics:
                           pg_dbg / pg_dbgsync, eg_debug / eg_debug_inc /
                           eg_dbg / eg_dbg_sync, o_gpio_a / o_gpio_b and the
                           test-register read_bus construction. Never touches
                           timer / busy / key / ADPCM / IRQ state.
  --hot-cold        (QW6)  place the reset function in a cold section
                           (__attribute__((cold, noinline))) and the running
                           function in a hot one (GCC/Clang only; benchmark
                           flag). --hot-running-only / --cold-reset-only
                           apply either attribute alone.
  --count-events    (QW3)  instrument the hot clock function with per-signal
                           activity counters (write0..3 / write*_en / read0,
                           read2, read3 / ssg writes / reset, per half-edge),
                           exported through mdp_qw3_ev_reset/get for the
                           measurement harness (tests/signal_freq.c).
  --write-split     (QW3)  requires --split-reset. On the ic=0 running
                           function, replace the write-gated register
                           decode/update chain with the steady-state pipeline
                           shifts and call a cold noinline helper
                           (mdp_fmopna_apply_write_event) only while a
                           delayed FM write strobe is active. Bit-exact by
                           construction: every extracted condition implies
                           write0_en || write1_en, and the helper re-runs the
                           verbatim chain at the same chip state.
  --read-split      (QW3)  extract the status-read output construction
                           (chip->read_bus building) into a cold noinline
                           helper called only while a read strobe is active.
                           The block only touches read_bus, so extraction
                           cannot affect synthesis state.

Emitted into <out_dir>:
  mdp_fmopna_clock_quickwin.{c,body.c}          (always; the QW1/4/5 carrier)
  mdp_fmopna_clock_running.{c,body.c}           (with --split-reset)
  mdp_fmopna_clock_reset.{c,body.c}             (with --split-reset)

Usage:
  gen_quickwin_core.py <fmopna_impl.c> <out_dir> [--fixed-prescaler]
      [--split-reset] [--copy-helpers] [--strip-debug] [--hot-cold]
"""
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import gen_fixed_prescaler as gfp  # reuse the select=2 switch collapse

SIGNATURE = "void FMOPNA_Clock(fmopna_t* chip, int clk)"
IC_SEED = "    chip->ic = !chip->input.ic;"


def _check_unique(src: str, needle: str, what: str) -> None:
    n = src.count(needle)
    assert n == 1, f"{what}: expected exactly one occurrence, found {n}"


# ---------------------------------------------------------------------------
# QW1: fixed prescaler (select=2)
# ---------------------------------------------------------------------------
def _fixed_prescaler(src: str) -> str:
    out = src
    switches = list(gfp.SWITCH_RE.finditer(out))
    assert len(switches) == 2, f"expected 2 prescaler switches, found {len(switches)}"
    for sw in sorted(switches, key=lambda m: -m.start()):
        out = out[: sw.start()] + gfp._case0_body(sw.group(0), 2) + out[sw.end() :]
    return out


# ---------------------------------------------------------------------------
# QW2: split-reset ic pinning
# ---------------------------------------------------------------------------
def _pin_ic(src: str, value: int) -> str:
    """Pin every `chip->ic` read to a constant.

    chip->ic is derived from input.ic at the seed line and written nowhere
    else in the body, so replacing the seed and every read is by construction
    equivalent to holding the IC input at the pinned level. GCC does not
    propagate a `chip->ic = k;` store through this 57 KiB body (alias
    analysis), so the reads must be textual constants for the dead reset
    branches to disappear.
    """
    _check_unique(src, IC_SEED, "ic seed line")
    out = src.replace(IC_SEED, "")  # chip->ic is written nowhere else
    # `\b` keeps chip->ic_latch*/ic_check*/ic_latch_fm untouched.
    return re.compile(r"chip->ic\b").sub(str(value), out)


# ---------------------------------------------------------------------------
# QW4: shared noinline copy helpers
# ---------------------------------------------------------------------------
_COPY_PATTERNS = [
    (re.compile(
        r"memcpy\((&chip->pg_phase\[0\]\[1\], &chip->pg_phase\[1\]\[0\]), "
        r"22 \* sizeof\(int\)\);"),
     "mdp_copy_i32_22"),
    (re.compile(
        r"memcpy\((&chip->pg_phase\[1\]\[0\], &chip->pg_phase\[0\]\[0\]), "
        r"23 \* sizeof\(int\)\);"),
     "mdp_copy_i32_23"),
    (re.compile(
        r"memcpy\((&chip->eg_state\[0\]\[1\], &chip->eg_state\[1\]\[0\]), "
        r"22 \* sizeof\(unsigned char\)\);"),
     "mdp_copy_u8_22"),
    (re.compile(
        r"memcpy\((&chip->eg_state\[1\]\[0\], &chip->eg_state\[0\]\[0\]), "
        r"23 \* sizeof\(unsigned char\)\);"),
     "mdp_copy_u8_23"),
    (re.compile(
        r"memcpy\((&chip->eg_level\[0\]\[1\], &chip->eg_level\[1\]\[0\]), "
        r"21 \* sizeof\(unsigned short\)\);"),
     "mdp_copy_u16_21"),
    (re.compile(
        r"memcpy\((&chip->eg_level\[1\]\[0\], &chip->eg_level\[0\]\[0\]), "
        r"22 \* sizeof\(unsigned short\)\);"),
     "mdp_copy_u16_22"),
    (re.compile(
        r"memcpy\((&chip->op_\w+\[\d\]\[\d\]\[\d\], &chip->op_\w+\[\d\]\[\d\]\[\d\]), "
        r"11 \* 2 \* sizeof\(unsigned char\)\);"),
     "mdp_copy_u8_22"),
    (re.compile(
        r"memcpy\((&chip->op_\w+\[\d\]\[\d\]\[\d\], &chip->op_\w+\[\d\]\[\d\]\[\d\]), "
        r"12 \* 2 \* sizeof\(unsigned char\)\);"),
     "mdp_copy_u8_24"),
]

_COPY_HELPER_PROTOS = (
    "/* QW4 (generated): shared noinline pipeline-copy prototypes. */\n"
    "static void mdp_copy_u8_22(unsigned char *dst, const unsigned char *src);\n"
    "static void mdp_copy_u8_23(unsigned char *dst, const unsigned char *src);\n"
    "static void mdp_copy_u8_24(unsigned char *dst, const unsigned char *src);\n"
    "static void mdp_copy_u16_21(unsigned short *dst, const unsigned short *src);\n"
    "static void mdp_copy_u16_22(unsigned short *dst, const unsigned short *src);\n"
    "static void mdp_copy_i32_22(int *dst, const int *src);\n"
    "static void mdp_copy_i32_23(int *dst, const int *src);\n"
)

_COPY_HELPERS = r"""
/* --------------------------------------------------------------------- */
/* QW4 (generated): shared noinline pipeline copies.                     */
/* static + noinline: GCC computes exact memory effects within the TU    */
/* (no spurious reloads across the calls) while keeping the copy code    */
/* out of the inline instruction stream.                                 */
/* --------------------------------------------------------------------- */
#if defined(__GNUC__) || defined(__clang__)
#define MDP_QW_NOINLINE __attribute__((noinline))
#else
#define MDP_QW_NOINLINE
#endif

static MDP_QW_NOINLINE void mdp_copy_u8_22(unsigned char *dst,
                                           const unsigned char *src)
{
    memcpy(dst, src, 22);
}

static MDP_QW_NOINLINE void mdp_copy_u8_23(unsigned char *dst,
                                           const unsigned char *src)
{
    memcpy(dst, src, 23);
}

static MDP_QW_NOINLINE void mdp_copy_u8_24(unsigned char *dst,
                                           const unsigned char *src)
{
    memcpy(dst, src, 24);
}

static MDP_QW_NOINLINE void mdp_copy_u16_21(unsigned short *dst,
                                            const unsigned short *src)
{
    memcpy(dst, src, 21 * sizeof(unsigned short));
}

static MDP_QW_NOINLINE void mdp_copy_u16_22(unsigned short *dst,
                                            const unsigned short *src)
{
    memcpy(dst, src, 22 * sizeof(unsigned short));
}

static MDP_QW_NOINLINE void mdp_copy_i32_22(int *dst, const int *src)
{
    memcpy(dst, src, 22 * sizeof(int));
}

static MDP_QW_NOINLINE void mdp_copy_i32_23(int *dst, const int *src)
{
    memcpy(dst, src, 23 * sizeof(int));
}
"""


def _copy_helpers(src: str) -> str:
    out = src
    for pat, helper in _COPY_PATTERNS:
        hits = len(pat.findall(out))
        assert hits > 0, f"copy pattern for {helper}: no sites found"
        out = pat.sub(lambda m: f"{helper}({m.group(1)});", out)
    _check_unique(out, SIGNATURE, "function signature (for QW4 prototypes)")
    out = out.replace(SIGNATURE, _COPY_HELPER_PROTOS + SIGNATURE, 1)
    return out + _COPY_HELPERS


# ---------------------------------------------------------------------------
# QW5: strip isolated debug-only state (see perf/README.md Quick win 5).
# Each block is asserted to occur exactly once in the vendored body.
# ---------------------------------------------------------------------------
_DEBUG_BLOCKS = [
    ("pg_dbg shift (clk1)", (
        "            chip->pg_dbg[0] = chip->pg_dbg[1] >> 1;\n"
        "            if (chip->pg_dbgsync)\n"
        "                chip->pg_dbg[0] |= chip->pg_phase[1][22] & 1023;\n"
        "#ifdef FMOPNA_YM2610\n"
        "            chip->pg_dbgsync_l[1] = chip->pg_dbgsync_l[0];\n"
        "#endif\n"
    )),
    ("pg_dbgsync + pg_dbg[1] (clk2)", (
        "#ifdef FMOPNA_YM2610\n"
        "            chip->pg_dbgsync_l[0] = chip->fsm_sel2[1];\n"
        "            chip->pg_dbgsync = chip->pg_dbgsync_l[1];\n"
        "#else\n"
        "            chip->pg_dbgsync = chip->fsm_sel2[1];\n"
        "#endif\n"
        "\n"
        "            chip->pg_dbg[1] = chip->pg_dbg[0];\n"
    )),
    ("eg_debug shift + eg_debug_inc", (
        "            chip->eg_debug[0] = chip->eg_debug[1] << 1;\n"
        "\n"
        "            if (chip->eg_dbg_sync)\n"
        "            {\n"
        "                chip->eg_debug[0] |= chip->eg_out;\n"
        "            }\n"
        "\n"
        "            chip->eg_debug_inc = chip->eg_incsh0[1] || chip->eg_incsh1[1] || chip->eg_incsh2[1] || chip->eg_incsh3[1];\n"
    )),
    ("eg_dbg_sync + eg_debug[1]", (
        "            chip->eg_dbg_sync = chip->fsm_sel2[1];\n"
        "\n"
        "            chip->eg_debug[1] = chip->eg_debug[0];\n"
    )),
    ("o_gpio_a/b (SSG env write)", (
        "#ifdef FMOPNA_YM2608\n"
        "            chip->o_gpio_a = chip->data_bus1 & 255;\n"
        "            chip->o_gpio_b = chip->data_bus1 & 255;\n"
        "#endif\n"
    )),
    ("o_gpio_a/b (SSG cases 0xe/0xf)", (
        "#ifdef FMOPNA_YM2608\n"
        "                case 0xe:\n"
        "                    chip->o_gpio_a = chip->data_bus1 & 255;\n"
        "                    break;\n"
        "                case 0xf:\n"
        "                    chip->o_gpio_b = chip->data_bus1 & 255;\n"
        "                    break;\n"
        "#endif\n"
    )),
    ("eg_dbg assignment", (
        "            chip->eg_dbg = chip->reg_test_21[0] ? (chip->eg_debug[0] & 0x200) != 0 :\n"
        "                chip->eg_debug_inc;\n"
    )),
    ("test-register read_bus construction", (
        "#ifndef FMOPNA_YM2612\n"
        "        if ((chip->reg_test_12[1] & 0x20) != 0 && chip->read0)\n"
        "        {\n"
        "#ifdef  FMOPNA_YM2608\n"
        "            if ((chip->reg_test_12[1] & 0x40) == 0)\n"
        "            {\n"
        "                // FIXME\n"
        "                chip->read_bus = rss_rom[(chip->rss_ix >> 2) & 0x1fff];\n"
        "            }\n"
        "            else\n"
        "#endif\n"
        "            if ((chip->reg_test_12[1] & 0x80) == 0)\n"
        "            {\n"
        "                chip->read_bus = (chip->rss_dbg_data >> 8) & 255;\n"
        "            }\n"
        "            else\n"
        "            {\n"
        "                chip->read_bus = chip->rss_dbg_data & 255;\n"
        "            }\n"
        "        }\n"
        "#endif\n"
        "        if ((chip->reg_test_21[1] & 0x40) != 0 && chip->read0)\n"
        "        {\n"
        "            int testdata = 0;\n"
        "#ifndef FMOPNA_YM2612\n"
        "            testdata = chip->op_output[3] & 0x3fff;\n"
        "#endif\n"
        "#ifdef FMOPNA_YM2612\n"
        "            if (chip->reg_test_2c[1] & 16)\n"
        "                testdata |= chip->ch_dbg[1] & 0x1ff;\n"
        "            else\n"
        "                testdata |= chip->op_output[3] & 0x3fff;\n"
        "#endif\n"
        "\n"
        "            testdata |= (chip->pg_dbg[1] & 1) << 15;\n"
        "            testdata |= chip->eg_dbg << 14;\n"
        "\n"
        "            if ((chip->reg_test_21[1] & 0x80) == 0)\n"
        "            {\n"
        "                chip->read_bus = (testdata >> 8) & 255;\n"
        "            }\n"
        "            else\n"
        "            {\n"
        "                chip->read_bus = testdata & 255;\n"
        "            }\n"
        "        }\n"
    )),
]


def _strip_debug(src: str) -> str:
    out = src
    for what, block in _DEBUG_BLOCKS:
        _check_unique(out, block, f"strip-debug block ({what})")
        out = out.replace(block, "")
    return out


# ---------------------------------------------------------------------------
# QW3 measurement (--count-events): per-signal activity counters.
#
# Counter order (kept in sync with tests/signal_freq.c):
#   0 calls | 1 reset (input.ic) | 2 write0 | 3 write0_en | 4 write1 |
#   5 write1_en | 6 write2 | 7 write2_en | 8 write3 | 9 write3_en |
#   10 read0 | 11 read2 | 12 read3 | 13 ssg_write0 | 14 ssg_write1 |
#   15 ssg_read1
#
# Anchors are the vendored assignment lines (unique in the body) and are
# matched BEFORE ic pinning (they contain `chip->ic`); the injected
# increments reference only chip fields / locals that the pinning transform
# leaves untouched, so --count-events composes with --split-reset.
# ---------------------------------------------------------------------------
_EV_INJECTIONS = [
    ("    chip->input.clk = clk;\n",
     "    mdp_qw3_ev[0] += 1;\n"
     "    mdp_qw3_ev[1] += (chip->input.ic == 0);\n"),
    ("        int writeaddr = chip->ic || (!chip->input.wr && !chip->input.cs && !chip->input.a0);\n",
     "        mdp_qw3_ev[2] += writeaddr != 0;\n"),
    ("        int writedata = !chip->ic && !chip->input.wr && !chip->input.cs && chip->input.a0;\n",
     "        mdp_qw3_ev[4] += writedata != 0;\n"),
    ("        chip->read0 = !chip->ic && !chip->input.rd && !chip->input.cs && !chip->input.a1 && !chip->input.a0;\n",
     "        mdp_qw3_ev[10] += chip->read0 != 0;\n"),
    ("        chip->read2 = !chip->ic && !chip->input.rd && !chip->input.cs && chip->input.a1 && !chip->input.a0;\n",
     "        mdp_qw3_ev[11] += chip->read2 != 0;\n"),
    ("        chip->read3 = !chip->ic && !chip->input.rd && !chip->input.cs && chip->input.a1 && chip->input.a0;\n",
     "        mdp_qw3_ev[12] += chip->read3 != 0;\n"),
    ("        chip->ssg_write0 = writeaddr && !chip->input.a1;\n",
     "        mdp_qw3_ev[13] += chip->ssg_write0 != 0;\n"),
    ("        chip->ssg_write1 = (writedata && !chip->input.a1) || chip->ic;\n",
     "        mdp_qw3_ev[14] += chip->ssg_write1 != 0;\n"),
    ("        chip->ssg_read1 = read1;\n",
     "        mdp_qw3_ev[15] += chip->ssg_read1 != 0;\n"),
    ("        chip->write0_en = chip->write0_l[0] && !chip->write0_l[2];\n",
     "        mdp_qw3_ev[3] += chip->write0_en != 0;\n"),
    ("        chip->write1_en = chip->write1_l[0] && !chip->write1_l[2];\n",
     "        mdp_qw3_ev[5] += chip->write1_en != 0;\n"),
    ("        chip->write2_en = chip->write2_l[0] && !chip->write2_l[2];\n",
     "        mdp_qw3_ev[7] += chip->write2_en != 0;\n"),
    ("        chip->write3_en = chip->write3_l[0] && !chip->write3_l[2];\n",
     "        mdp_qw3_ev[9] += chip->write3_en != 0;\n"),
]

_EV_TAIL = r"""
/* --------------------------------------------------------------------- */
/* QW3 measurement (--count-events): per-signal activity counters.       */
/* Updated on every half-edge of this clock function; read back through   */
/* the accessors below by the measurement harness (tests/signal_freq.c).  */
/* --------------------------------------------------------------------- */
void mdp_qw3_ev_reset(void)
{
    memset(mdp_qw3_ev, 0, sizeof(mdp_qw3_ev));
}

void mdp_qw3_ev_get(uint64_t *out, int n)
{
    memcpy(out, mdp_qw3_ev, (size_t)n * sizeof(uint64_t));
}
"""

_EV_PREAMBLE = (
    "/* QW3 measurement (--count-events): per-signal activity counters. */\n"
    "#include <stdint.h>\n"
    "#include <string.h>\n"
    "static uint64_t mdp_qw3_ev[16];\n"
)


def _count_events(src: str) -> str:
    out = src
    for anchor, injection in _EV_INJECTIONS:
        _check_unique(out, anchor, "count-events anchor")
        out = out.replace(anchor, anchor + injection)
    return _EV_PREAMBLE + out + _EV_TAIL


# ---------------------------------------------------------------------------
# QW3 extraction (--write-split / --read-split).
#
# Both transforms run on the ic-pinned running function (--write-split
# additionally requires --split-reset): the hot body keeps the steady-state
# pipeline shifts, and the write/read-gated work moves to cold noinline
# helpers called only while the corresponding strobe is active.
# ---------------------------------------------------------------------------
_QW3_MACROS = (
    "/* QW3 (generated): event-split support macros. */\n"
    "#ifndef MDP_QW_UNLIKELY\n"
    "#if defined(__GNUC__) || defined(__clang__)\n"
    "#define MDP_QW_UNLIKELY(x) __builtin_expect(!!(x), 0)\n"
    "#else\n"
    "#define MDP_QW_UNLIKELY(x) (x)\n"
    "#endif\n"
    "#endif\n"
    "#ifndef MDP_QW_NOINLINE\n"
    "#if defined(__GNUC__) || defined(__clang__)\n"
    "#define MDP_QW_NOINLINE __attribute__((noinline))\n"
    "#else\n"
    "#define MDP_QW_NOINLINE\n"
    "#endif\n"
    "#endif\n"
    "static MDP_QW_NOINLINE void mdp_fmopna_apply_write_event(fmopna_t *chip);\n"
    "static MDP_QW_NOINLINE void mdp_fmopna_apply_read_event(fmopna_t *chip);\n"
)


def _ensure_qw3_macros(src: str) -> str:
    if "MDP_QW_UNLIKELY(x) __builtin_expect" in src:
        return src
    _check_unique(src, SIGNATURE, "signature for QW3 macro placement")
    return src.replace(SIGNATURE, _QW3_MACROS + SIGNATURE)


_IS_FM_LINE = "            int is_fm = (chip->data_bus1 & 0xf0) != 0;\n"
_WRITE_SPAN_START = ("            chip->write_fm_address[0] = chip->write0_en ? is_fm : "
                     "chip->write_fm_address[1];\n")
_WRITE_SPAN_END = ("            chip->reg_timer_b_reset[0] = chip->addr_27[1] && "
                   "(chip->data_bus1 & 0x100) == 0 && chip->write1_en && "
                   "((chip->data_bus1 >> 5) & 1) != 0;\n")
_WRITE_SPAN_FRAGS = [
    ("addr_10 ternary",
     "            chip->addr_10[0] = chip->write0_en ? ADDRESS_MATCH(0x10) : chip->addr_10[1];\n"),
    ("addr_21 ternary",
     "            chip->addr_21[0] = chip->write0_en ? ADDRESS_MATCH(0x21) : chip->addr_21[1];\n"),
    ("write10r",
     "                int write10r = write10 && (chip->data_bus2 & 0x80) != 0;\n"),
    ("reg_mask shift",
     "                    chip->reg_mask[0] = chip->reg_mask[1];\n"),
    ("timer_a OR form",
     "                    chip->reg_timer_a[0] |= chip->reg_timer_a[1] & 0x3fc;\n"),
    ("kon_operator shift",
     "                    chip->reg_kon_operator[0] = chip->reg_kon_operator[1];\n"),
    ("timer_a_reset",
     "            chip->reg_timer_a_reset[0] = chip->addr_27[1] && "
     "(chip->data_bus1 & 0x100) == 0 && chip->write1_en && "
     "((chip->data_bus1 >> 4) & 1) != 0;\n"),
]

_QW3_WRITE_HOT = r"""            /* QW3 (generated --write-split): steady-state register pipeline
             * shifts; the write-gated decode/update chain runs in
             * mdp_fmopna_apply_write_event (cold, write strobes only). */
            chip->write_fm_address[0] = chip->write_fm_address[1];
            chip->fm_address[0] = chip->fm_address[1];
            chip->fm_data[0] = chip->fm_data[1];
            chip->write_fm_data[0] = chip->write_fm_data[1];
#ifdef FMOPNA_YM2608
            chip->addr_10[0] = chip->addr_10[1];
            chip->addr_10h[0] = chip->addr_10h[1];
            chip->addr_12[0] = chip->addr_12[1];
            chip->addr_29[0] = chip->addr_29[1];
            chip->addr_ff[0] = chip->addr_ff[1];
            int write10 = chip->addr_10h[1] && (chip->data_bus1 & 0x100) != 0 && chip->write1_en;
            int irq_rst = write10 && (chip->data_bus2 & 0x80) == 0;
#endif
#ifdef FMOPNA_YM2610
            chip->addr_00[0] = chip->addr_00[1];
            chip->addr_02[0] = chip->addr_02[1];
            chip->addr_1c[0] = chip->addr_1c[1];
#endif
#ifdef FMOPNA_YM2612
            chip->addr_2a[0] = chip->addr_2a[1];
            chip->addr_2b[0] = chip->addr_2b[1];
            chip->addr_2c[0] = chip->addr_2c[1];
#endif
            chip->addr_21[0] = chip->addr_21[1];
            chip->addr_22[0] = chip->addr_22[1];
            chip->addr_24[0] = chip->addr_24[1];
            chip->addr_25[0] = chip->addr_25[1];
            chip->addr_26[0] = chip->addr_26[1];
            chip->addr_27[0] = chip->addr_27[1];
            chip->addr_28[0] = chip->addr_28[1];
#ifdef FMOPNA_YM2608
            chip->reg_mask[0] = chip->reg_mask[1];
            chip->reg_test_12[0] = chip->reg_test_12[1];
#endif
#ifdef FMOPNA_YM2610
            chip->reg_flags[0] = chip->reg_flags[1];
            chip->reg_test_12[0] = chip->reg_test_12[1];
#endif
            chip->reg_test_21[0] = chip->reg_test_21[1];
            chip->reg_lfo[0] = chip->reg_lfo[1];
            chip->reg_timer_a[0] = chip->reg_timer_a[1] & 0x3ff;
            chip->reg_timer_b[0] = chip->reg_timer_b[1];
            chip->reg_ch3[0] = chip->reg_ch3[1];
            chip->reg_timer_a_load[0] = chip->reg_timer_a_load[1];
            chip->reg_timer_b_load[0] = chip->reg_timer_b_load[1];
            chip->reg_timer_a_enable[0] = chip->reg_timer_a_enable[1];
            chip->reg_timer_b_enable[0] = chip->reg_timer_b_enable[1];
            chip->reg_kon_operator[0] = chip->reg_kon_operator[1];
            chip->reg_kon_channel[0] = chip->reg_kon_channel[1];
#ifdef FMOPNA_YM2608
            chip->reg_sch[0] = chip->reg_sch[1];
            chip->reg_irq[0] = chip->reg_irq[1];
#endif
#ifdef FMOPNA_YM2612
            chip->reg_dac_en[0] = chip->reg_dac_en[1];
            chip->reg_dac_data[0] = chip->reg_dac_data[1];
            chip->reg_test_2c[0] = chip->reg_test_2c[1];
#endif
            chip->reg_timer_a_reset[0] = 0;
            chip->reg_timer_b_reset[0] = 0;
            if (MDP_QW_UNLIKELY(chip->write0_en || chip->write1_en))
                mdp_fmopna_apply_write_event(chip);
"""


def _write_split(src: str) -> str:
    _check_unique(src, _IS_FM_LINE, "is_fm line")
    _check_unique(src, _WRITE_SPAN_START, "write span start")
    _check_unique(src, _WRITE_SPAN_END, "write span end")
    start = src.index(_WRITE_SPAN_START)
    end = src.index(_WRITE_SPAN_END) + len(_WRITE_SPAN_END)
    span = src[start:end]
    for what, frag in _WRITE_SPAN_FRAGS:
        _check_unique(span, frag, f"write span fragment ({what})")
    is_fm_pos = src.index(_IS_FM_LINE)
    between = src[is_fm_pos + len(_IS_FM_LINE):start]
    assert between == "\n", f"expected one blank line after is_fm, found {between!r}"
    out = src[:is_fm_pos] + _QW3_WRITE_HOT + src[end:]
    helper = (
        "/* --------------------------------------------------------------------- */\n"
        "/* QW3 (generated --write-split): cold write-event application.          */\n"
        "/* Runs only while a delayed FM write strobe is active. The hot body     */\n"
        "/* already performed the steady-state pipeline shifts; this re-applies   */\n"
        "/* the write-gated decode/update chain verbatim at the same chip state   */\n"
        "/* (every extracted condition implies write0_en || write1_en, so the     */\n"
        "/* helper result is bit-identical to the original single body).          */\n"
        "/* --------------------------------------------------------------------- */\n"
        "#define ADDRESS_MATCH(x) ((chip->data_bus2 & x) == 0 && (chip->data_bus1 & (x^511)) == 0)\n"
        "static MDP_QW_NOINLINE void mdp_fmopna_apply_write_event(fmopna_t *chip)\n"
        "{\n"
        "    int is_fm = (chip->data_bus1 & 0xf0) != 0;\n"
        + span
        + "}\n"
        "#undef ADDRESS_MATCH\n"
    )
    return _ensure_qw3_macros(out) + helper


_READ_SPAN_START = "        chip->read_bus = 0; // FIXME\n"
_READ_SPAN_END = "        chip->o_data = chip->read_bus;\n"
_READ_SPAN_FRAGS = [
    ("ssg_read1 construction", "        if (chip->ssg_read1\n"),
    ("read0 status block", "            if (chip->read0)\n"),
    ("read2 status block", "#ifdef FMOPNA_YM2608\n            if (chip->read2)\n"),
    ("test_12 construction", "        if ((chip->reg_test_12[1] & 0x20) != 0 && chip->read0)\n"),
    ("test_21 construction", "        if ((chip->reg_test_21[1] & 0x40) != 0 && chip->read0)\n"),
]

_READ_SPAN_HOT = (
    "        chip->read_bus = 0; // FIXME\n"
    "        if (MDP_QW_UNLIKELY(chip->read0 || chip->read2 || chip->read3 || chip->ssg_read1))\n"
    "            mdp_fmopna_apply_read_event(chip);\n"
)


def _read_split(src: str) -> str:
    _check_unique(src, _READ_SPAN_START, "read_bus zero line")
    _check_unique(src, _READ_SPAN_END, "o_data line")
    s0 = src.index(_READ_SPAN_START) + len(_READ_SPAN_START)
    e0 = src.index(_READ_SPAN_END)
    span = src[s0:e0]
    for what, frag in _READ_SPAN_FRAGS:
        _check_unique(span, frag, f"read span fragment ({what})")
    out = src[:s0 - len(_READ_SPAN_START)] + _READ_SPAN_HOT + src[e0:]
    helper = (
        "/* --------------------------------------------------------------------- */\n"
        "/* QW3 (generated --read-split): cold status-read output construction.    */\n"
        "/* Runs only while a read strobe is active; touches only chip->read_bus,  */\n"
        "/* so extraction cannot affect synthesis state.                           */\n"
        "/* --------------------------------------------------------------------- */\n"
        "static MDP_QW_NOINLINE void mdp_fmopna_apply_read_event(fmopna_t *chip)\n"
        "{\n"
        + span
        + "}\n"
    )
    return _ensure_qw3_macros(out) + helper


# ---------------------------------------------------------------------------
# Emit
# ---------------------------------------------------------------------------
def _rename(src: str, name: str) -> str:
    _check_unique(src, SIGNATURE, "function signature")
    return src.replace(SIGNATURE, f"void {name}(fmopna_t* chip, int clk)")


def _emit(out_dir: str, name: str, body: str, note: str) -> None:
    body_path = os.path.join(out_dir, name + ".body.c")
    open(body_path, "w", encoding="utf-8").write(body)
    wrapper = (
        "/* Auto-generated by tools/gen_quickwin_core.py: {note}\n"
        " * SPDX-License-Identifier: GPL-2.0-or-later\n"
        " */\n"
        "#define FMOPNA_YM2608\n"
        '#include "{name}.body.c"\n'
    ).format(note=note, name=name)
    open(os.path.join(out_dir, name + ".c"), "w", encoding="utf-8").write(wrapper)


def main() -> None:
    args = sys.argv[1:]
    flags = {
        "fixed-prescaler": False,
        "split-reset": False,
        "copy-helpers": False,
        "strip-debug": False,
        "hot-cold": False,
        "hot-running-only": False,
        "cold-reset-only": False,
        "count-events": False,
        "write-split": False,
        "read-split": False,
    }
    rest = []
    for a in args:
        key = a[2:]
        if a.startswith("--") and key in flags:
            flags[key] = True
        else:
            rest.append(a)
    if len(rest) != 2:
        print(__doc__)
        sys.exit(2)
    if flags["write-split"] and not flags["split-reset"]:
        print("error: --write-split requires --split-reset (the ic=0 running "
              "function; the extraction is not valid for ic=1 reset clocks)",
              file=sys.stderr)
        sys.exit(2)
    if flags["strip-debug"] and flags["read-split"]:
        print("error: --read-split and --strip-debug overlap on the "
              "test-register read_bus construction; benchmark them separately",
              file=sys.stderr)
        sys.exit(2)
    src_path, out_dir = rest
    src = open(src_path, encoding="utf-8").read()
    os.makedirs(out_dir, exist_ok=True)

    def transforms(s: str) -> str:
        if flags["fixed-prescaler"]:
            s = _fixed_prescaler(s)
        if flags["copy-helpers"]:
            s = _copy_helpers(s)
        if flags["strip-debug"]:
            s = _strip_debug(s)
        return s

    on = [k for k, v in flags.items() if v]
    tag = ",".join(on) if on else "none"
    base = transforms(src)
    if flags["count-events"] and not flags["split-reset"]:
        base = _count_events(base)
    if flags["read-split"] and not flags["split-reset"]:
        base = _read_split(base)

    # Carrier function (QW1/QW4/QW5 when QW2 is off, and the transform
    # reference for the split-reset pair).
    _emit(out_dir, "mdp_fmopna_clock_quickwin",
          _rename(base, "mdp_fmopna_clock_quickwin"),
          f"quickwin core (flags: {tag})")

    if flags["split-reset"]:
        for name, ic, attr in (
            ("mdp_fmopna_clock_running", 0, "__attribute__((hot)) "),
            ("mdp_fmopna_clock_reset", 1, "__attribute__((cold, noinline)) "),
        ):
            pre = transforms(src)
            if flags["count-events"] and name == "mdp_fmopna_clock_running":
                pre = _count_events(pre)
            body = _pin_ic(pre, ic)
            if name == "mdp_fmopna_clock_running":
                if flags["write-split"]:
                    body = _write_split(body)
                if flags["read-split"]:
                    body = _read_split(body)
            _check_unique(body, SIGNATURE, "function signature")
            use_attr = (flags["hot-cold"] or flags["hot-running-only"]) \
                if name == "mdp_fmopna_clock_running" \
                else (flags["hot-cold"] or flags["cold-reset-only"])
            body = body.replace(
                SIGNATURE, f"{attr if use_attr else ''}void {name}(fmopna_t* chip, int clk)")
            _emit(out_dir, name, body,
                  f"ic-pinned ({ic}) clock, flags: {tag}")

    print(f"wrote {out_dir}/mdp_fmopna_clock_{{quickwin"
          + (",running,reset" if flags["split-reset"] else "")
          + "}.{c,body.c}")
    print(f"transforms: {tag}")


if __name__ == "__main__":
    main()
