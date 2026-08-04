cmake_minimum_required(VERSION 3.16)
project(mdplayer_opna LANGUAGES C)
if(NOT CMAKE_BUILD_TYPE)
    set(CMAKE_BUILD_TYPE Release CACHE STRING "Build type" FORCE)
endif()

set(CMAKE_C_STANDARD 99)
set(CMAKE_C_STANDARD_REQUIRED ON)

# Sanitizer test builds (mirrors the MDPlayer.SpcNative convention). Default
# OFF. The vendored YM2608-LLE core performs intentional signed-int arithmetic
# that UBSan flags (left shifts of large constants) by design. To keep the
# adapter and native tests fully instrumented while treating the byte-identical
# upstream snapshot as an external dependency (Task C §12), the upstream core
# target `mdplayer_opna_core` is built with UBSan disabled and the adapter +
# tests are built with UBSan enabled (both under AddressSanitizer when
# requested). MDPLAYER_OPNA_ADAPTER_UBSAN selects adapter+test UBSan.
option(MDPLAYER_OPNA_SANITIZERS "Build with AddressSanitizer" OFF)
option(MDPLAYER_OPNA_ADAPTER_UBSAN "Also add UndefinedBehaviorSanitizer to adapter + tests (upstream core stays UBSan-free)" OFF)
if(MDPLAYER_OPNA_ADAPTER_UBSAN AND NOT MSVC)
    set(CMAKE_C_FLAGS "${CMAKE_C_FLAGS} -fsanitize=undefined")
    set(CMAKE_SHARED_LINKER_FLAGS "${CMAKE_SHARED_LINKER_FLAGS} -fsanitize=undefined")
    set(CMAKE_EXE_LINKER_FLAGS "${CMAKE_EXE_LINKER_FLAGS} -fsanitize=undefined")
endif()
# Adapter + test UBSan is applied globally below; the upstream core target is
# built with -fno-sanitize=undefined (Task C §12) so the identical vendored
# snapshot is never instrumented.
if(MDPLAYER_OPNA_SANITIZERS AND NOT MSVC)
    set(CMAKE_C_FLAGS "${CMAKE_C_FLAGS} -fsanitize=address -fno-omit-frame-pointer -g")
    set(CMAKE_SHARED_LINKER_FLAGS "${CMAKE_SHARED_LINKER_FLAGS} -fsanitize=address")
    set(CMAKE_EXE_LINKER_FLAGS "${CMAKE_EXE_LINKER_FLAGS} -fsanitize=address")
endif()

# Vendored Furnace YM2608-LLE core (GPL-2.0-or-later; see UPSTREAM.md). We
# compile fmopna_2608.c directly from the vendored tree — no build-time
# patching is needed for this backend. It defines FMOPNA_YM2608 and includes
# fmopna_impl.c, exactly as Furnace builds it.
set(OPNANATIVE_UPSTREAM_DIR "${CMAKE_CURRENT_SOURCE_DIR}/upstream/furnace-ym2608-lle")

# ---------------------------------------------------------------------------
# Static internal target compiling ONLY the pinned YM2608-LLE core.
#
# This is the Prompt-2 vendoring gate: it proves the vendored core compiles
# in isolation and can be initialized/clocked. No MDPlayer integration code,
# bus transactions, audio decoding, or resampling is included here — that
# lives in the `mdplayer_opna` shared library target declared below.
#
# C standard: C99 (the vendored core is written to the C99 dialect that
# Furnace builds it with; see UPSTREAM.md). Warnings are enabled; the core is
# compiled with `-pedantic`-free -Wall -Wextra. Include paths are configured
# in CMake (never by editing the vendored files).
add_library(mdplayer_opna_core STATIC
    "${OPNANATIVE_UPSTREAM_DIR}/fmopna_2608.c")
target_compile_features(mdplayer_opna_core PRIVATE c_std_99)
target_include_directories(mdplayer_opna_core PUBLIC
    "${OPNANATIVE_UPSTREAM_DIR}")
if(NOT MSVC)
    target_compile_options(mdplayer_opna_core PRIVATE
        -Wall -Wextra)
    if(MDPLAYER_OPNA_ADAPTER_UBSAN)
        # Upstream core stays UBSan-free (Task C §12): the pinned core performs
        # intentional signed-int arithmetic that UBSan would flag.
        target_compile_options(mdplayer_opna_core PRIVATE
            -fno-sanitize=undefined)
    endif()
endif()
set_target_properties(mdplayer_opna_core PROPERTIES
    OUTPUT_NAME "mdplayer_opna_core"
    POSITION_INDEPENDENT_CODE ON)

add_library(mdplayer_opna SHARED
    src/mdplayer_opna.c
    src/mdplayer_opna_core.c
    src/mdplayer_opna_bus.c
    src/mdplayer_opna_serial.c
    src/mdplayer_opna_adpcm.c
    src/mdplayer_opna_mix.c
    src/mdplayer_opna_fifo.c
    src/mdplayer_opna_session.c
    src/mdplayer_opna_resampler.c)
target_include_directories(mdplayer_opna PUBLIC
    "${CMAKE_CURRENT_SOURCE_DIR}/src"
    "${CMAKE_CURRENT_SOURCE_DIR}/include")
target_include_directories(mdplayer_opna PRIVATE
    "${OPNANATIVE_UPSTREAM_DIR}"
    "${CMAKE_CURRENT_SOURCE_DIR}/third_party/speexdsp")
# The wrapper includes speex_resampler.h with the same OUTSIDE_SPEEX /
# RANDOM_PREFIX shim so its types and renamed declarations match the compiled
# SpeexDSP static target.
target_compile_definitions(mdplayer_opna PRIVATE
    OUTSIDE_SPEEX
    RANDOM_PREFIX=mdp_opna_speex)
# The identical vendored core is linked as its own static target
# (`mdplayer_opna_core`) so the adapter/tests can run UBSan-instrumented while
# the upstream snapshot stays UBSan-free (Task C §12).
target_link_libraries(mdplayer_opna PRIVATE mdplayer_opna_core)
# SpeexDSP standalone resampler (fixed-point, prefixed, private). Vendored
# under third_party/speexdsp (see its UPSTREAM.md); compiled as its own static
# target and linked privately so no SpeexDSP symbol is exported from
# libmdplayer_opna. RANDOM_PREFIX=mdp_opna_speex renames all its exported symbols.
add_library(mdplayer_opna_speex_resampler STATIC
    third_party/speexdsp/resample.c)
# Linked into the shared libmdplayer_opna.so: must be position-independent
# (required by -fsanitize=address builds and hardened linkers).
set_target_properties(mdplayer_opna_speex_resampler PROPERTIES
    POSITION_INDEPENDENT_CODE ON)
target_include_directories(mdplayer_opna_speex_resampler PRIVATE
    third_party/speexdsp)
target_compile_definitions(mdplayer_opna_speex_resampler PRIVATE
    FIXED_POINT
    OUTSIDE_SPEEX
    RANDOM_PREFIX=mdp_opna_speex)
target_link_libraries(mdplayer_opna PRIVATE mdplayer_opna_speex_resampler)
if(UNIX)
    # SpeexDSP filter-table creation requires libm.
    target_link_libraries(mdplayer_opna_speex_resampler PRIVATE m)
endif()
set_target_properties(mdplayer_opna PROPERTIES
    OUTPUT_NAME "mdplayer_opna"
    POSITION_INDEPENDENT_CODE ON)

# Packaging: copy the built library to MDPlayer runtimes/<rid>/native so a
# future managed wrapper can probe it there. Linux now; Windows work deferred.
if(WIN32)
    set(MDPLAYER_OPNA_RUNTIME_RID "win-x64")
    set(MDPLAYER_OPNA_RUNTIME_LIBRARY "mdplayer_opna.dll")
else()
    set(MDPLAYER_OPNA_RUNTIME_RID "linux-x64")
    set(MDPLAYER_OPNA_RUNTIME_LIBRARY "libmdplayer_opna.so")
endif()
set(MDPLAYER_OPNA_RUNTIMES_DIR
    "${CMAKE_CURRENT_SOURCE_DIR}/../../runtimes/${MDPLAYER_OPNA_RUNTIME_RID}/native")
add_custom_command(TARGET mdplayer_opna POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E make_directory "${MDPLAYER_OPNA_RUNTIMES_DIR}"
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
        "$<TARGET_FILE:mdplayer_opna>" "${MDPLAYER_OPNA_RUNTIMES_DIR}/${MDPLAYER_OPNA_RUNTIME_LIBRARY}"
    COMMENT "Copying ${MDPLAYER_OPNA_RUNTIME_LIBRARY} to runtimes/${MDPLAYER_OPNA_RUNTIME_RID}/native/"
    VERBATIM)

# Stage-1 acceptance probe: the first audio gate. Vendored core + copied reset
# + copied bus scheduler + copied serial decoder + bounded real FM trace prefix
# -> nonzero stereo PCM.
enable_testing()
add_executable(mdplayer_opna_probe tests/furnace_reference_probe.c)
target_include_directories(mdplayer_opna_probe PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}/src"
    "${CMAKE_CURRENT_SOURCE_DIR}/tests"
    "${OPNANATIVE_UPSTREAM_DIR}")
target_link_libraries(mdplayer_opna_probe PRIVATE mdplayer_opna)
add_test(NAME mdplayer_opna_probe COMMAND mdplayer_opna_probe)

# ---------------------------------------------------------------------------
# Prompt-2 vendoring gate: compile + init test for the pinned core.
# Proves the vendored core compiles in isolation (via mdplayer_opna_core)
# and can be initialized and clocked. Produces no audio.
# ---------------------------------------------------------------------------
add_executable(upstream_compile_test tests/upstream_compile_test.c)
target_link_libraries(upstream_compile_test PRIVATE mdplayer_opna_core)
add_test(NAME upstream_compile_test COMMAND upstream_compile_test)

# ---------------------------------------------------------------------------
# Reset + clock test (Prompt 3): verifies the Furnace reset contract
# (576/576/576 phases), the initial pin state, and that reset clears every
# transient driver state while advancing master_clock monotonically.
# ---------------------------------------------------------------------------
add_executable(mdplayer_opna_reset_test tests/reset_test.c)
target_include_directories(mdplayer_opna_reset_test PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}/src"
    "${OPNANATIVE_UPSTREAM_DIR}")
target_link_libraries(mdplayer_opna_reset_test PRIVATE mdplayer_opna)
add_test(NAME mdplayer_opna_reset_test COMMAND mdplayer_opna_reset_test)

# ---------------------------------------------------------------------------
# Bus scheduler test (Prompt 3): verifies the pin-level write state machine
# (address/data phases, A1/A0 selects, CS/RD/WR, prescaler write opportunity,
# busy wait, SSG delay, explicit idle, FIFO order).
# ---------------------------------------------------------------------------
add_executable(mdplayer_opna_bus_test tests/bus_test.c)
target_include_directories(mdplayer_opna_bus_test PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}/src"
    "${OPNANATIVE_UPSTREAM_DIR}")
target_link_libraries(mdplayer_opna_bus_test PRIVATE mdplayer_opna)
add_test(NAME mdplayer_opna_bus_test COMMAND mdplayer_opna_bus_test)

# ---------------------------------------------------------------------------
# Serial PCM decoder + real-trace fixture test (Prompt 3): replays the
# checked-in furnace_reference_fixture.c (real YM2608 OPNA trace) and verifies
# the serial decoder emits only complete stereo frames, that both channels
# produce frames, and that the real trace yields nonzero output.
# ---------------------------------------------------------------------------
add_executable(mdplayer_opna_fm_audio_test tests/fm_audio_test.c)
target_include_directories(mdplayer_opna_fm_audio_test PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}/src"
    "${CMAKE_CURRENT_SOURCE_DIR}/tests"
    "${OPNANATIVE_UPSTREAM_DIR}")
target_link_libraries(mdplayer_opna_fm_audio_test PRIVATE mdplayer_opna)
add_test(NAME mdplayer_opna_fm_audio_test COMMAND mdplayer_opna_fm_audio_test)

# ---------------------------------------------------------------------------
# Prompt-4A stereo pan/routing gate: left/right ordering, mono-SSG fold pan.
# ---------------------------------------------------------------------------
add_executable(mdplayer_opna_pan_test tests/pan_test.c)
target_include_directories(mdplayer_opna_pan_test PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}/src"
    "${OPNANATIVE_UPSTREAM_DIR}")
target_link_libraries(mdplayer_opna_pan_test PRIVATE mdplayer_opna)
add_test(NAME mdplayer_opna_pan_test COMMAND mdplayer_opna_pan_test)

# ---------------------------------------------------------------------------
# Prompt-4A Furnace-compatible SSG mix gate.
# ---------------------------------------------------------------------------
add_executable(mdplayer_opna_ssg_test tests/ssg_test.c)
target_include_directories(mdplayer_opna_ssg_test PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}/src"
    "${OPNANATIVE_UPSTREAM_DIR}")
target_link_libraries(mdplayer_opna_ssg_test PRIVATE mdplayer_opna)
add_test(NAME mdplayer_opna_ssg_test COMMAND mdplayer_opna_ssg_test)

# ---------------------------------------------------------------------------
# Prompt-4A ADPCM-B external-memory protocol gate.
# ---------------------------------------------------------------------------
add_executable(mdplayer_opna_adpcm_test tests/adpcm_test.c)
target_include_directories(mdplayer_opna_adpcm_test PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}/src"
    "${OPNANATIVE_UPSTREAM_DIR}")
target_link_libraries(mdplayer_opna_adpcm_test PRIVATE mdplayer_opna)
add_test(NAME mdplayer_opna_adpcm_test COMMAND mdplayer_opna_adpcm_test)

# ---------------------------------------------------------------------------
# Prompt-4A timer + IRQ observations gate.
# ---------------------------------------------------------------------------
add_executable(mdplayer_opna_timer_irq_test tests/timer_irq_test.c)
target_include_directories(mdplayer_opna_timer_irq_test PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}/src"
    "${OPNANATIVE_UPSTREAM_DIR}")
target_link_libraries(mdplayer_opna_timer_irq_test PRIVATE mdplayer_opna)
add_test(NAME mdplayer_opna_timer_irq_test COMMAND mdplayer_opna_timer_irq_test)

# ---------------------------------------------------------------------------
# Prompt-4A determinism gate: identical PCM SHA-256 across three processes.
# ---------------------------------------------------------------------------
add_executable(mdplayer_opna_determinism_test tests/determinism_test.c)
target_include_directories(mdplayer_opna_determinism_test PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}/src"
    "${CMAKE_CURRENT_SOURCE_DIR}/tests"
    "${OPNANATIVE_UPSTREAM_DIR}")
target_link_libraries(mdplayer_opna_determinism_test PRIVATE mdplayer_opna)
add_test(NAME mdplayer_opna_determinism_test COMMAND mdplayer_opna_determinism_test)

# ---------------------------------------------------------------------------
# Prompt-5 Workstream B: timed native-frame FIFO.
# ---------------------------------------------------------------------------
add_executable(mdplayer_opna_fifo_test tests/fifo_test.c src/mdplayer_opna_fifo.c)
target_include_directories(mdplayer_opna_fifo_test PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}/src")
add_test(NAME mdplayer_opna_fifo_test COMMAND mdplayer_opna_fifo_test)

# ---------------------------------------------------------------------------
# Prompt-5 Workstream A: versioned session ABI.
# ---------------------------------------------------------------------------
add_executable(mdplayer_opna_abi_test tests/abi_test.c)
target_include_directories(mdplayer_opna_abi_test PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}/src"
    "${CMAKE_CURRENT_SOURCE_DIR}/include"
    "${OPNANATIVE_UPSTREAM_DIR}")
target_link_libraries(mdplayer_opna_abi_test PRIVATE mdplayer_opna)
add_test(NAME mdplayer_opna_abi_test COMMAND mdplayer_opna_abi_test)

add_executable(mdplayer_opna_session_reset_test tests/session_reset_test.c)
target_include_directories(mdplayer_opna_session_reset_test PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}/src"
    "${CMAKE_CURRENT_SOURCE_DIR}/include"
    "${OPNANATIVE_UPSTREAM_DIR}")
target_link_libraries(mdplayer_opna_session_reset_test PRIVATE mdplayer_opna)
add_test(NAME mdplayer_opna_session_reset_test COMMAND mdplayer_opna_session_reset_test)

# ---------------------------------------------------------------------------
# Prompt-5 Workstream C: prescaler / native cadence audit + gate.
# The prescaler audit is diagnostic. The cadence gate asserts constant 144
# native cadence; it FAILS when variable cadence is detected (spec §22 hard
# stop: fixed-rate resampler and Workstream D/E are not built).
# NB: this gate is expected to FAIL (variable cadence from honoring 0x2E/0x2F
# prescaler-select writes). It is the spec's hard-stop trigger.
# ---------------------------------------------------------------------------
add_executable(mdplayer_opna_prescaler_audit_test tests/prescaler_audit_test.c)
target_include_directories(mdplayer_opna_prescaler_audit_test PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}/src"
    "${CMAKE_CURRENT_SOURCE_DIR}/include"
    "${OPNANATIVE_UPSTREAM_DIR}")
target_link_libraries(mdplayer_opna_prescaler_audit_test PRIVATE mdplayer_opna)
add_test(NAME mdplayer_opna_prescaler_audit_test COMMAND mdplayer_opna_prescaler_audit_test)
set_tests_properties(mdplayer_opna_prescaler_audit_test PROPERTIES
    RUN_SERIAL ON)

add_executable(mdplayer_opna_native_cadence_test tests/native_cadence_test.c)
target_include_directories(mdplayer_opna_native_cadence_test PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}/src"
    "${CMAKE_CURRENT_SOURCE_DIR}/include"
    "${CMAKE_CURRENT_SOURCE_DIR}/tests"
    "${OPNANATIVE_UPSTREAM_DIR}")
target_link_libraries(mdplayer_opna_native_cadence_test PRIVATE mdplayer_opna)

# The five cadence-policy tests. Every one passes: 0x2E/0x2F pass by detecting
# and reporting unsupported cadence (no audio resampling required). There is no
# deliberately failing test in this suite.
foreach(cadence_case
        default_profile_has_144_clock_cadence
        prescaler_2d_has_144_clock_cadence
        prescaler_2e_reports_unsupported_cadence
        prescaler_2f_reports_unsupported_cadence
        real_fm_trace_has_144_clock_cadence)
    add_test(NAME ${cadence_case}
             COMMAND mdplayer_opna_native_cadence_test -t ${cadence_case})
    set_tests_properties(${cadence_case} PROPERTIES RUN_SERIAL ON)
endforeach()

# ---------------------------------------------------------------------------
# Prompt-5.1 Task D: fixed-cadence resampler (SpeexDSP-backed).
# ---------------------------------------------------------------------------
add_executable(mdplayer_opna_resampler_test tests/resampler_test.c)
target_include_directories(mdplayer_opna_resampler_test PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}/src"
    "${CMAKE_CURRENT_SOURCE_DIR}/include")
target_link_libraries(mdplayer_opna_resampler_test PRIVATE mdplayer_opna)
add_test(NAME mdplayer_opna_resampler_test COMMAND mdplayer_opna_resampler_test)
set_tests_properties(mdplayer_opna_resampler_test PROPERTIES RUN_SERIAL ON)


# ---------------------------------------------------------------------------
# Prompt 8.1: status-read cadence regression.
# A status read must be cadence-neutral: no master-clock advance, no serial
# decoder run, no dropped frame. Verifies raw 144-clock frame cadence with and
# without bank-0/bank-1/dense status polling at every phase of the frame period.
# ---------------------------------------------------------------------------
add_executable(mdplayer_opna_status_cadence_test tests/status_cadence_test.c)
target_include_directories(mdplayer_opna_status_cadence_test PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}/src"
    "${CMAKE_CURRENT_SOURCE_DIR}/include")
target_link_libraries(mdplayer_opna_status_cadence_test PRIVATE mdplayer_opna)

foreach(status_case
        baseline_cadence_144
        bank0_reads_keep_144
        bank1_reads_keep_144
        dense_polling_keep_144)
    add_test(NAME ${status_case}
             COMMAND mdplayer_opna_status_cadence_test -t ${status_case})
    set_tests_properties(${status_case} PROPERTIES RUN_SERIAL ON)
endforeach()
