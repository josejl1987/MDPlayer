/*
 * MDPlayer YM2608-LLE native backend — public header.
 *
 * This is the PUBLIC surface of the native YM2608-LLE backend packaged at
 * this stage. Per the YM2608-LLE implementation plan, this header currently
 * exposes only forward declarations and version/pin constants. The ABI is NOT
 * defined yet; the internal driver types (OpnaLle and helper structs) are
 * declared in the private header (src/mdplayer_opna_internal.h) only.
 *
 * Adapted from Furnace Tracker's YM2608-LLE integration.
 * Original copyright: Copyright (C) 2021-2026 tildearrow and contributors
 * Source: src/engine/platform/ym2608.cpp / ym2608.h
 * Furnace commit: 3bdfc824fb7d2e813852f6fcfa482d8ea999588a
 *
 * Adaptations copyright (C) 2026 MDPlayer contributors.
 *
 * SPDX-License-Identifier: GPL-2.0-or-later
 */
#ifndef MDPLAYER_OPNA_H
#define MDPLAYER_OPNA_H

#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/* --------------------------------------------------------------------- */
/* Version / pin constants                                               */
/* --------------------------------------------------------------------- */

/* Backend semantic version. Not an ABI; informational only at this stage. */
#define MDPLAYER_OPNA_VERSION_MAJOR 0
#define MDPLAYER_OPNA_VERSION_MINOR 0
#define MDPLAYER_OPNA_VERSION_PATCH 1

/* The exact Furnace Tracker commit pinned for the vendored YM2608-LLE core. */
#define MDPLAYER_OPNA_FURNACE_COMMIT "3bdfc824fb7d2e813852f6fcfa482d8ea999588a"

/* --------------------------------------------------------------------- */
/* Forward declarations (no ABI yet)                                     */
/* --------------------------------------------------------------------- */

/*
 * The native YM2608-LLE driver instance. Its layout is not part of the
 * public ABI at this stage; all construction/destruction and access is
 * performed through opaque API functions added in a later stage.
 */
typedef struct OpnaLle OpnaLle;

#ifdef __cplusplus
}
#endif

#endif /* MDPLAYER_OPNA_H */
