/*
 * Determinism test: the same FM/SSG operation stream rendered in three
 * SEPARATE operating-system processes must produce byte-identical native
 * PCM (same SHA-256). Reuses the real FM trace fixture plus a deterministic
 * SSG fold so the stream exercises both the FM serial path and the SSG
 * analogue path.
 *
 * Invoked two ways:
 *   determinism_test --child   : renders the fixed stream and prints the SHA-256
 *   determinism_test           : spawns 3 child processes, compares the hashes
 *
 * Gate: "Run the same FM/SSG/rhythm/ADPCM operation stream in three separate
 *        processes. Require identical native PCM SHA-256."
 * (rhythm/ADPCM-B playback remain gated on the bus-scheduler conformance work
 *  tracked alongside the RSS state progression; FM + SSG are the native PCM
 *  contributors available today and are exercised here.)
 *
 * Adapted from Furnace Tracker's YM2608-LLE integration.
 * Original copyright: Copyright (C) 2021-2026 tildearrow and contributors
 * Source: src/engine/platform/ym2608.cpp
 * Furnace commit: 3bdfc824fb7d2e813852f6fcfa482d8ea999588a
 *
 * Adaptations copyright (C) 2026 MDPlayer contributors.
 *
 * SPDX-License-Identifier: GPL-2.0-or-later
 */
#include "../src/mdplayer_opna_internal.h"
#include "furnace_reference_fixture.c"

#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* ---- compact SHA-256 (public-domain style, single-authored here) ----------- */

#define ROR(x,n) (((x)>>(n))|((x)<<(32-(n))))

typedef struct { uint32_t s[8]; uint64_t bitlen; } ShaC;

static const uint32_t SHA_K[64] = {
 0x428a2f98,0x71374491,0xb5c0fbcf,0xe9b5dba5,0x3956c25b,0x59f111f1,0x923f82a4,0xab1c5ed5,
 0xd807aa98,0x12835b01,0x243185be,0x550c7dc3,0x72be5d74,0x80deb1fe,0x9bdc06a7,0xc19bf174,
 0xe49b69c1,0xefbe4786,0x0fc19dc6,0x240ca1cc,0x2de92c6f,0x4a7484aa,0x5cb0a9dc,0x76f988da,
 0x983e5152,0xa831c66d,0xb00327c8,0xbf597fc7,0xc6e00bf3,0xd5a79147,0x06ca6351,0x14292967,
 0x27b70a85,0x2e1b2138,0x4d2c6dfc,0x53380d13,0x650a7354,0x766a0abb,0x81c2c92e,0x92722c85,
 0xa2bfe8a1,0xa81a664b,0xc24b8b70,0xc76c51a3,0xd192e819,0xd6990624,0xf40e3585,0x106aa070,
 0x19a4c116,0x1e376c08,0x2748774c,0x34b0bcb5,0x391c0cb3,0x4ed8aa4a,0x5b9cca4f,0x682e6ff3,
 0x748f82ee,0x78a5636f,0x84c87814,0x8cc70208,0x90befffa,0xa4506ceb,0xbef9a3f7,0xc67178f2
};

static void sha_transform(ShaC *c, const uint8_t *p)
{
    uint32_t w[64], a,b,cc,d,e,f,g,h,t1,t2;
    for (int i = 0; i < 16; i++)
        w[i] = (uint32_t)((p[i<<2]<<24)|(p[(i<<2)+1]<<16)|(p[(i<<2)+2]<<8)|p[(i<<2)+3]);
    for (int i = 16; i < 64; i++) {
        uint32_t s0 = ROR(w[i-15],7)^ROR(w[i-15],18)^(w[i-15]>>3);
        uint32_t s1 = ROR(w[i-2],17)^ROR(w[i-2],19)^(w[i-2]>>10);
        w[i] = w[i-16]+s0+w[i-7]+s1;
    }
    a=c->s[0]; b=c->s[1]; cc=c->s[2]; d=c->s[3];
    e=c->s[4]; f=c->s[5]; g=c->s[6]; h=c->s[7];
    for (int i = 0; i < 64; i++) {
        t1 = h + (ROR(e,6)^ROR(e,11)^ROR(e,25)) + ((e&f)^((~e)&g)) + SHA_K[i] + w[i];
        t2 = (ROR(a,2)^ROR(a,13)^ROR(a,22)) + ((a&b)^(a&cc)^(b&cc));
        h=g; g=f; f=e; e=d+t1; d=cc; cc=b; b=a; a=t1+t2;
    }
    c->s[0]+=a; c->s[1]+=b; c->s[2]+=cc; c->s[3]+=d;
    c->s[4]+=e; c->s[5]+=f; c->s[6]+=g; c->s[7]+=h;
    c->bitlen += 512;
}

static void sha_final(ShaC *c, const uint8_t *data, size_t len, uint8_t out[32])
{
    /* includes one-block padding handling for arbitrary length */
    size_t i = 0, block_off = 0;
    uint8_t block[64];
    uint64_t bitlen = c->bitlen + (uint64_t)len * 8;
    size_t buflen = 0;
    /* consume full input blocks first */
    while (len - i >= 64) { sha_transform(c, data + i); i += 64; }
    /* build final padded block(s) */
    size_t rem = len - i;
    memset(block, 0, 64);
    memcpy(block, data + i, rem);
    block[rem] = 0x80;
    size_t total = i + rem;
    size_t padrem = (total % 64);
    size_t final_len = (padrem < 56) ? 64 : 128;
    uint8_t fb[128];
    memset(fb, 0, 128);
    memcpy(fb, block, padrem);
    fb[padrem] = 0x80;
    for (int z = 0; z < 8; z++) fb[final_len-1-z] = (uint8_t)(bitlen >> (8*z));
    if (final_len == 64) { sha_transform(c, fb); }
    else { uint8_t b1[64],b2[64]; memcpy(b1,fb,64); memcpy(b2,fb+64,64); sha_transform(c,b1); sha_transform(c,b2); }
    (void)block_off; (void)buflen;
    for (int z = 0; z < 32; z++) out[z] = (uint8_t)(c->s[z>>2] >> (8*(3-(z&3))));
}

/* Render the fixed stream and emit a 32-byte SHA-256 of interleaved PCM. */
static void render_and_digest(uint8_t digest[32])
{
    enum { kFrames = 40000 };
    int16_t *l = (int16_t *)malloc(sizeof(int16_t)*kFrames);
    int16_t *r = (int16_t *)malloc(sizeof(int16_t)*kFrames);
    if (!l || !r) { fprintf(stderr,"OOM\n"); exit(1); }

    OpnaLle ctx;
    memset(&ctx, 0, sizeof(ctx));
    opna_lle_reset(&ctx, true);

    for (int i = 0; i < kFurnaceRefWriteCount; i++) {
        int addr = kFurnaceRefWrites[i].bank ? (0x100 | kFurnaceRefWrites[i].reg)
                                             : kFurnaceRefWrites[i].reg;
        if (kFurnaceRefWrites[i].bank == 0 && kFurnaceRefWrites[i].reg == 0xb4)
            opna_lle_write(&ctx, addr, 0xc0);
        else
            opna_lle_write(&ctx, addr, kFurnaceRefWrites[i].value);
    }
    opna_lle_write(&ctx, 0x07, 0x38);
    opna_lle_write(&ctx, 0x09, 0);
    opna_lle_write(&ctx, 0x0a, 8);

    opna_lle_render(&ctx, l, r, kFrames);

    /* interleaved PCM byte stream = kFrames * 2 samples * 2 bytes */
    int16_t *pcm = (int16_t *)malloc(sizeof(int16_t) * (size_t)2 * kFrames);
    if (!pcm) { free(l); free(r); exit(1); }
    for (int i = 0; i < kFrames; i++) { pcm[2*i]=l[i]; pcm[2*i+1]=r[i]; }
    ShaC c;
    memset(&c, 0, sizeof(c));
    c.s[0]=0x6a09e667u; c.s[1]=0xbb67ae85u; c.s[2]=0x3c6ef372u; c.s[3]=0xa54ff53au;
    c.s[4]=0x510e527fu; c.s[5]=0x9b05688cu; c.s[6]=0x1f83d9abu; c.s[7]=0x5be0cd19u;
    sha_final(&c, (const uint8_t*)pcm, (size_t)2*kFrames*2, digest);

    free(pcm); free(l); free(r);
}

static void print_hex(const uint8_t d[32], char out[65])
{
    static const char hx[] = "0123456789abcdef";
    for (int i = 0; i < 32; i++) { out[2*i]=hx[d[i]>>4]; out[2*i+1]=hx[d[i]&15]; }
    out[64]=0;
}

int main(int argc, char **argv)
{
    int child = (argc > 1 && strcmp(argv[1], "--child") == 0);
    if (child) {
        uint8_t d[32]; char hex[65];
        render_and_digest(d);
        print_hex(d, hex);
        printf("%s\n", hex);
        return 0;
    }

    /* parent: run three separate processes, compare digest strings */
    char digests[3][65];
    for (int p = 0; p < 3; p++) {
        char cmd[512];
        snprintf(cmd, sizeof(cmd), "\"%s\" --child", argv[0]);
        FILE *fp = popen(cmd, "r");
        if (!fp) { fprintf(stderr, "determinism_test: cannot spawn child\n"); return 1; }
        if (!fgets(digests[p], sizeof(digests[p]), fp)) {
            fclose(fp); fprintf(stderr, "determinism_test: child produced no digest\n"); return 1;
        }
        /* strip trailing newline */
        size_t ll = strlen(digests[p]);
        while (ll && (digests[p][ll-1]=='\n'||digests[p][ll-1]=='\r')) digests[p][--ll]=0;
        int rc = pclose(fp);
        if (rc != 0) { fprintf(stderr,"determinism_test: child process %d failed\n", p); return 1; }
    }

    if (strcmp(digests[0], digests[1]) != 0 ||
        strcmp(digests[1], digests[2]) != 0) {
        fprintf(stderr, "determinism_test: FAIL: hashes differ across processes:\n  %s\n  %s\n  %s\n",
                digests[0], digests[1], digests[2]);
        return 1;
    }
    fprintf(stdout, "determinism_test: OK — 3 processes, identical sha256=%s\n", digests[0]);
    return 0;
}
