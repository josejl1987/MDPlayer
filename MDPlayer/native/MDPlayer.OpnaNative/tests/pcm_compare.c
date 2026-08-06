/*
 * Cross-artifact PCM equality gate for the quick-win experiments.
 *
 * Loads TWO mdplayer_opna .so artifacts via dlopen, replays the same real FM
 * trace template through each over a fixed window of master-clock pairs,
 * drains every PCM frame, and compares the streams byte-for-byte. This is the
 * plan's acceptance rule ("identical native stereo frames, identical final
 * PCM") across flag combinations: QW1/QW2/QW4/QW5 must be bit-exact relative
 * to the baseline build.
 *
 * Usage:
 *   pcm_compare <libA.so> <libB.so> [window_pairs=20000000]
 * Exit 0 when the drained PCM streams are identical, 1 otherwise.
 *
 * Not a ctest gate; built only for the perf experiment (perf/README.md).
 * SPDX-License-Identifier: GPL-2.0-or-later
 */
#include <dlfcn.h>
#include <inttypes.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "../tests/furnace_reference_fixture.c"

typedef struct mdp_opna_session mdp_opna_session;
typedef struct mdp_opna_open_options {
    int sample_rate;
} mdp_opna_open_options;
enum {
    MDP_OPNA_OK = 0,
    MDP_OPNA_ERR_CLOCK_REGRESSION = -3,
    MDP_OPNA_ERR_UNSUPPORTED_CADENCE = -6,
};

typedef struct {
    void *h;
    int (*open_fn)(const mdp_opna_open_options *, mdp_opna_session **, char *, size_t);
    int (*write_fn)(mdp_opna_session *, uint32_t, uint8_t, uint8_t, uint8_t);
    int (*advance_fn)(mdp_opna_session *, uint64_t);
    int (*drain_fn)(mdp_opna_session *, int16_t *, int, int *);
    uint64_t (*mclock_fn)(const mdp_opna_session *);
    void (*close_fn)(mdp_opna_session *);
    mdp_opna_session *s;
    int16_t *pcm;
    size_t pcm_len;
    size_t pcm_cap;
} Artifact;

static int load_artifact(const char *libpath, Artifact *a)
{
    memset(a, 0, sizeof(*a));
    a->h = dlopen(libpath, RTLD_NOW | RTLD_LOCAL);
    if (!a->h) { fprintf(stderr, "dlopen %s: %s\n", libpath, dlerror()); return -1; }
    *(void **)(&a->open_fn)    = dlsym(a->h, "mdp_opna_open");
    *(void **)(&a->write_fn)   = dlsym(a->h, "mdp_opna_write_register");
    *(void **)(&a->advance_fn) = dlsym(a->h, "mdp_opna_advance_to");
    *(void **)(&a->drain_fn)   = dlsym(a->h, "mdp_opna_drain_audio");
    *(void **)(&a->mclock_fn)  = dlsym(a->h, "mdp_opna_get_master_clock");
    *(void **)(&a->close_fn)   = dlsym(a->h, "mdp_opna_close");
    if (!a->open_fn || !a->write_fn || !a->advance_fn || !a->drain_fn ||
        !a->mclock_fn || !a->close_fn) {
        fprintf(stderr, "dlsym failed on %s: %s\n", libpath, dlerror());
        return -1;
    }
    return 0;
}

static int run_artifact(Artifact *a, uint64_t window)
{
    char err[64];
    if (a->open_fn(&(mdp_opna_open_options){ 48000 }, &a->s, err, sizeof(err)) != MDP_OPNA_OK) {
        fprintf(stderr, "open failed: %s\n", err);
        return -1;
    }
    for (int i = 0; i < kFurnaceRefWriteCount; i++) {
        int rc = a->write_fn(a->s, (uint32_t)kFurnaceRefWrites[i].master_clock,
                             (uint8_t)kFurnaceRefWrites[i].bank,
                             (uint8_t)kFurnaceRefWrites[i].reg,
                             (uint8_t)kFurnaceRefWrites[i].value);
        if (rc != MDP_OPNA_OK && rc != MDP_OPNA_ERR_CLOCK_REGRESSION &&
            rc != MDP_OPNA_ERR_UNSUPPORTED_CADENCE) {
            fprintf(stderr, "write %d fail rc=%d\n", i, rc);
            return -1;
        }
    }
    enum { CHUNK = 200000 };
    uint64_t start_clk = a->mclock_fn(a->s);
    uint64_t target = start_clk + window;
    while (a->mclock_fn(a->s) < target) {
        uint64_t cur = a->mclock_fn(a->s);
        uint64_t step = (target - cur < CHUNK) ? (target - cur) : CHUNK;
        if (a->advance_fn(a->s, cur + step) != MDP_OPNA_OK) {
            fprintf(stderr, "advance failed rc at clock %" PRIu64 "\n", cur);
            return -1;
        }
        for (;;) {
            int16_t buf[128]; /* requested_frames stereo interleaved */
            int d = -1;
            if (a->drain_fn(a->s, buf, 64, &d) != MDP_OPNA_OK) break;
            if (d <= 0) break;
            if (a->pcm_len + (size_t)d > a->pcm_cap) {
                size_t cap = a->pcm_cap ? a->pcm_cap * 2 : 1u << 20;
                int16_t *np = realloc(a->pcm, cap * sizeof(int16_t));
                if (!np) { fprintf(stderr, "oom\n"); return -1; }
                a->pcm = np;
                a->pcm_cap = cap;
            }
            memcpy(a->pcm + a->pcm_len, buf, (size_t)d * sizeof(int16_t));
            a->pcm_len += (size_t)d;
        }
    }
    return 0;
}

static void close_artifact(Artifact *a)
{
    if (a->close_fn && a->s) a->close_fn(a->s);
    if (a->h) dlclose(a->h);
    free(a->pcm);
}

int main(int argc, char **argv)
{
    if (argc < 3) {
        fprintf(stderr, "usage: pcm_compare <libA.so> <libB.so> [window_pairs=20000000]\n");
        return 2;
    }
    uint64_t window = argc >= 4 ? strtoull(argv[3], NULL, 10) : 20000000ULL;
    if (!window) window = 20000000ULL;

    Artifact a = { 0 }, b = { 0 };
    if (load_artifact(argv[1], &a) || load_artifact(argv[2], &b)) return 2;
    if (run_artifact(&a, window) || run_artifact(&b, window)) {
        close_artifact(&a); close_artifact(&b);
        return 2;
    }
    int same = a.pcm_len == b.pcm_len && memcmp(a.pcm, b.pcm, a.pcm_len * sizeof(int16_t)) == 0;
    printf("A=%s pcm=%zu samples  B=%s pcm=%zu samples  identical=%s\n",
           argv[1], a.pcm_len, argv[2], b.pcm_len, same ? "YES" : "NO");
    if (a.pcm_len != b.pcm_len)
        printf("  length mismatch: A=%zu B=%zu samples\n", a.pcm_len, b.pcm_len);
    else if (!same) {
        size_t first = 0;
        while (first < a.pcm_len && a.pcm[first] == b.pcm[first]) first++;
        printf("  first divergence at sample %zu (A=%d B=%d)\n", first, a.pcm[first], b.pcm[first]);
    }
    close_artifact(&a);
    close_artifact(&b);
    return same ? 0 : 1;
}
