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
# ---------------------------------------------------------------------------
add_library(mdplayer_opna_core STATIC
    "${OPNANATIVE_UPSTREAM_DIR}/fmopna_2608.c")
target_compile_features(mdplayer_opna_core PRIVATE c_std_99)
target_include_directories(mdplayer_opna_core PUBLIC
    "${OPNANATIVE_UPSTREAM_DIR}")
if(NOT MSVC)
    target_compile_options(mdplayer_opna_core PRIVATE
        -Wall -Wextra)
    # Hidden visibility: the vendored core is linked into the shared library
    # but must never appear in its exported surface (Workstream P).
    target_compile_options(mdplayer_opna_core PRIVATE -fvisibility=hidden)
    if(MDPLAYER_OPNA_ADAPTER_UBSAN)
        target_compile_options(mdplayer_opna_core PRIVATE
            -fno-sanitize=undefined)
    endif()
endif()
set_target_properties(mdplayer_opna_core PROPERTIES
    OUTPUT_NAME "mdplayer_opna_core"
    POSITION_INDEPENDENT_CODE ON)

# SpeexDSP standalone resampler (fixed-point, prefixed, private). Vendored
# under third_party/speexdsp (see its UPSTREAM.md); compiled as its own static
# target and linked privately so no SpeexDSP symbol is exported from
# libmdplayer_opna. RANDOM_PREFIX=mdp_opna_speex renames all its exported symbols.
add_library(mdplayer_opna_speex_resampler STATIC
    third_party/speexdsp/resample.c)
set_target_properties(mdplayer_opna_speex_resampler PROPERTIES
    POSITION_INDEPENDENT_CODE ON)
target_include_directories(mdplayer_opna_speex_resampler PRIVATE
    third_party/speexdsp)
target_compile_definitions(mdplayer_opna_speex_resampler PRIVATE
    FIXED_POINT
    OUTSIDE_SPEEX
    RANDOM_PREFIX=mdp_opna_speex)
if(NOT MSVC)
    # Hide SpeexDSP symbols; it is statically linked and never exported
    # (Workstream P / no system SpeexDSP dependency).
    target_compile_options(mdplayer_opna_speex_resampler PRIVATE -fvisibility=hidden)
endif()
if(UNIX)
    target_link_libraries(mdplayer_opna_speex_resampler PRIVATE m)
endif()

# The shipped adapter sources. The shared library compiles these directly so
# every public mdp_opna_* symbol is present in the loadable artifact.
set(MDPLAYER_OPNA_ADAPTER_SOURCES
    src/mdplayer_opna.c
    src/mdplayer_opna_core.c
    src/mdplayer_opna_bus.c
    src/mdplayer_opna_serial.c
    src/mdplayer_opna_adpcm.c
    src/mdplayer_opna_mix.c
    src/mdplayer_opna_fifo.c
    src/mdplayer_opna_session.c
    src/mdplayer_opna_resampler.c)

# ---------------------------------------------------------------------------
# Shared runtime payload: the adapter sources + vendored core + SpeexDSP,
# exported with a restricted symbol surface.
# ---------------------------------------------------------------------------
add_library(mdplayer_opna SHARED ${MDPLAYER_OPNA_ADAPTER_SOURCES})
target_include_directories(mdplayer_opna PUBLIC
    "${CMAKE_CURRENT_SOURCE_DIR}/src"
    "${CMAKE_CURRENT_SOURCE_DIR}/include")
target_include_directories(mdplayer_opna PRIVATE
    "${OPNANATIVE_UPSTREAM_DIR}"
    "${CMAKE_CURRENT_SOURCE_DIR}/third_party/speexdsp")
target_compile_definitions(mdplayer_opna PRIVATE
    MDP_OPNA_BUILDING
    OUTSIDE_SPEEX
    RANDOM_PREFIX=mdp_opna_speex)
if(NOT MSVC)
    target_compile_options(mdplayer_opna PRIVATE -Wall -Wextra)
    # Hide everything unless explicitly marked MDP_OPNA_API (Workstream P).
    target_compile_options(mdplayer_opna PRIVATE -fvisibility=hidden)
endif()
target_link_libraries(mdplayer_opna PRIVATE
    mdplayer_opna_core mdplayer_opna_speex_resampler)
set_target_properties(mdplayer_opna PROPERTIES
    OUTPUT_NAME "mdplayer_opna"
    POSITION_INDEPENDENT_CODE ON)

# ---------------------------------------------------------------------------
# Workstream P — symbol visibility. Restrict shared-library exports to the
# documented mdp_opna_* API only (the managed wrapper's DllImport set). Hide
# the vendored Furnace core, FIFO helpers, resampler wrapper and the
# statically-prefixed SpeexDSP. Linux uses a GNU version script; Windows/MinGW
# links an explicit export table.
# ---------------------------------------------------------------------------
if(UNIX AND NOT APPLE)
    target_link_options(mdplayer_opna PRIVATE
        "-Wl,--version-script=${CMAKE_CURRENT_SOURCE_DIR}/libmdplayer_opna.map")
elseif(WIN32)
    # Restrict the Windows DLL export table to the documented mdp_opna_* API
    # using the explicit export list. Passing the .def as a linker input makes
    # MinGW export only the listed symbols (no auto-export of every global), so
    # SpeexDSP / FIFO / resampler wrapper / vendored-core internals stay private.
    set_target_properties(mdplayer_opna PROPERTIES WINDOWS_EXPORT_ALL_SYMBOLS OFF)
    target_link_options(mdplayer_opna PRIVATE
        "${CMAKE_CURRENT_SOURCE_DIR}/mdplayer_opna.def")
endif()

# Convenience dependency aggregate for native unit tests that probe internals
# (vendored core, FIFO helpers, resampler wrapper, session internals). The
# adapter is recompiled as its own static archive so test executables can
# resolve internals that the shipped shared library now hides (Workstream P).
add_library(mdplayer_opna_testlib STATIC ${MDPLAYER_OPNA_ADAPTER_SOURCES})
set_target_properties(mdplayer_opna_testlib PROPERTIES POSITION_INDEPENDENT_CODE ON)
target_include_directories(mdplayer_opna_testlib PUBLIC
    "${CMAKE_CURRENT_SOURCE_DIR}/src"
    "${CMAKE_CURRENT_SOURCE_DIR}/include")
target_include_directories(mdplayer_opna_testlib PRIVATE
    "${OPNANATIVE_UPSTREAM_DIR}"
    "${CMAKE_CURRENT_SOURCE_DIR}/third_party/speexdsp")
target_compile_definitions(mdplayer_opna_testlib PRIVATE
    OUTSIDE_SPEEX
    RANDOM_PREFIX=mdp_opna_speex)
# The testlib is a static aggregate: consumers of the dev interface link it
# statically, so the header must not emit dllimport on Windows.
target_compile_definitions(mdplayer_opna_testlib PUBLIC MDP_OPNA_STATIC)
if(NOT MSVC)
    target_compile_options(mdplayer_opna_testlib PRIVATE -Wall -Wextra)
endif()
if(UNIX)
    add_library(mdplayer_opna_dev INTERFACE)
    target_link_libraries(mdplayer_opna_dev INTERFACE
        "-Wl,--start-group"
        mdplayer_opna_testlib mdplayer_opna_core mdplayer_opna_speex_resampler
        "-Wl,--end-group")
else()
    add_library(mdplayer_opna_dev INTERFACE)
    target_link_libraries(mdplayer_opna_dev INTERFACE
        mdplayer_opna_testlib mdplayer_opna_core mdplayer_opna_speex_resampler)
endif()

# Packaging: copy the built library to MDPlayer runtimes/<rid>/native so the
# managed wrapper can probe it there.
if(WIN32)
    set(MDPLAYER_OPNA_RUNTIME_RID "win-x64")
    set(MDPLAYER_OPNA_RUNTIME_LIBRARY "mdplayer_opna.dll")
else()
    set(MDPLAYER_OPNA_RUNTIME_RID "linux-x64")
    set(MDPLAYER_OPNA_RUNTIME_LIBRARY "libmdplayer_opna.so")
endif()
set(MDPLAYER_OPNA_RUNTIMES_DIR
    "${CMAKE_CURRENT_SOURCE_DIR}/../../runtimes/${MDPLAYER_OPNA_RUNTIME_RID}/native")
# Only a clean (non-sanitized) build may publish the runtime artifact into the
# shared MDPlayer runtimes/ directory; a sanitizer build must never clobber the
# shipping library (that would make managed hosts try to load ASan/UBSan
# runtimes and fail).
if(NOT MDPLAYER_OPNA_SANITIZERS AND NOT MDPLAYER_OPNA_ADAPTER_UBSAN)
    add_custom_command(TARGET mdplayer_opna POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E make_directory "${MDPLAYER_OPNA_RUNTIMES_DIR}"
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
            "$<TARGET_FILE:mdplayer_opna>" "${MDPLAYER_OPNA_RUNTIMES_DIR}/${MDPLAYER_OPNA_RUNTIME_LIBRARY}"
        COMMENT "Copying ${MDPLAYER_OPNA_RUNTIME_LIBRARY} to runtimes/${MDPLAYER_OPNA_RUNTIME_RID}/native/"
        VERBATIM)
endif()

# Stage-1 acceptance probe: the first audio gate.
enable_testing()
add_executable(mdplayer_opna_probe tests/furnace_reference_probe.c)
target_include_directories(mdplayer_opna_probe PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}/src"
    "${CMAKE_CURRENT_SOURCE_DIR}/tests"
    "${OPNANATIVE_UPSTREAM_DIR}")
target_link_libraries(mdplayer_opna_probe PRIVATE mdplayer_opna_dev)
add_test(NAME mdplayer_opna_probe COMMAND mdplayer_opna_probe)

add_executable(upstream_compile_test tests/upstream_compile_test.c)
target_link_libraries(upstream_compile_test PRIVATE mdplayer_opna_core)
add_test(NAME upstream_compile_test COMMAND upstream_compile_test)

add_executable(mdplayer_opna_reset_test tests/reset_test.c)
target_include_directories(mdplayer_opna_reset_test PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}/src"
    "${OPNANATIVE_UPSTREAM_DIR}")
target_link_libraries(mdplayer_opna_reset_test PRIVATE mdplayer_opna_dev)
add_test(NAME mdplayer_opna_reset_test COMMAND mdplayer_opna_reset_test)

add_executable(mdplayer_opna_bus_test tests/bus_test.c)
target_include_directories(mdplayer_opna_bus_test PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}/src"
    "${OPNANATIVE_UPSTREAM_DIR}")
target_link_libraries(mdplayer_opna_bus_test PRIVATE mdplayer_opna_dev)
add_test(NAME mdplayer_opna_bus_test COMMAND mdplayer_opna_bus_test)

add_executable(mdplayer_opna_fm_audio_test tests/fm_audio_test.c)
target_include_directories(mdplayer_opna_fm_audio_test PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}/src"
    "${CMAKE_CURRENT_SOURCE_DIR}/tests"
    "${OPNANATIVE_UPSTREAM_DIR}")
target_link_libraries(mdplayer_opna_fm_audio_test PRIVATE mdplayer_opna_dev)
add_test(NAME mdplayer_opna_fm_audio_test COMMAND mdplayer_opna_fm_audio_test)

add_executable(mdplayer_opna_pan_test tests/pan_test.c)
target_include_directories(mdplayer_opna_pan_test PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}/src"
    "${OPNANATIVE_UPSTREAM_DIR}")
target_link_libraries(mdplayer_opna_pan_test PRIVATE mdplayer_opna_dev)
add_test(NAME mdplayer_opna_pan_test COMMAND mdplayer_opna_pan_test)

add_executable(mdplayer_opna_ssg_test tests/ssg_test.c)
target_include_directories(mdplayer_opna_ssg_test PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}/src"
    "${OPNANATIVE_UPSTREAM_DIR}")
target_link_libraries(mdplayer_opna_ssg_test PRIVATE mdplayer_opna_dev)
add_test(NAME mdplayer_opna_ssg_test COMMAND mdplayer_opna_ssg_test)

add_executable(mdplayer_opna_adpcm_test tests/adpcm_test.c)
target_include_directories(mdplayer_opna_adpcm_test PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}/src"
    "${OPNANATIVE_UPSTREAM_DIR}")
target_link_libraries(mdplayer_opna_adpcm_test PRIVATE mdplayer_opna_dev)
add_test(NAME mdplayer_opna_adpcm_test COMMAND mdplayer_opna_adpcm_test)

add_executable(mdplayer_opna_timer_irq_test tests/timer_irq_test.c)
target_include_directories(mdplayer_opna_timer_irq_test PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}/src"
    "${OPNANATIVE_UPSTREAM_DIR}")
target_link_libraries(mdplayer_opna_timer_irq_test PRIVATE mdplayer_opna_dev)
add_test(NAME mdplayer_opna_timer_irq_test COMMAND mdplayer_opna_timer_irq_test)

add_executable(mdplayer_opna_determinism_test tests/determinism_test.c)
target_include_directories(mdplayer_opna_determinism_test PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}/src"
    "${CMAKE_CURRENT_SOURCE_DIR}/tests"
    "${OPNANATIVE_UPSTREAM_DIR}")
target_link_libraries(mdplayer_opna_determinism_test PRIVATE mdplayer_opna_dev)
add_test(NAME mdplayer_opna_determinism_test COMMAND mdplayer_opna_determinism_test)

add_executable(mdplayer_opna_fifo_test tests/fifo_test.c src/mdplayer_opna_fifo.c)
target_include_directories(mdplayer_opna_fifo_test PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}/src")
add_test(NAME mdplayer_opna_fifo_test COMMAND mdplayer_opna_fifo_test)

add_executable(mdplayer_opna_abi_test tests/abi_test.c)
target_include_directories(mdplayer_opna_abi_test PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}/src"
    "${CMAKE_CURRENT_SOURCE_DIR}/include"
    "${OPNANATIVE_UPSTREAM_DIR}")
target_link_libraries(mdplayer_opna_abi_test PRIVATE mdplayer_opna_dev)
add_test(NAME mdplayer_opna_abi_test COMMAND mdplayer_opna_abi_test)

add_executable(mdplayer_opna_session_reset_test tests/session_reset_test.c)
target_include_directories(mdplayer_opna_session_reset_test PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}/src"
    "${CMAKE_CURRENT_SOURCE_DIR}/include"
    "${OPNANATIVE_UPSTREAM_DIR}")
target_link_libraries(mdplayer_opna_session_reset_test PRIVATE mdplayer_opna_dev)
add_test(NAME mdplayer_opna_session_reset_test COMMAND mdplayer_opna_session_reset_test)

add_executable(mdplayer_opna_prescaler_audit_test tests/prescaler_audit_test.c)
target_include_directories(mdplayer_opna_prescaler_audit_test PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}/src"
    "${CMAKE_CURRENT_SOURCE_DIR}/include"
    "${OPNANATIVE_UPSTREAM_DIR}")
target_link_libraries(mdplayer_opna_prescaler_audit_test PRIVATE mdplayer_opna_dev)
add_test(NAME mdplayer_opna_prescaler_audit_test COMMAND mdplayer_opna_prescaler_audit_test)
set_tests_properties(mdplayer_opna_prescaler_audit_test PROPERTIES
    RUN_SERIAL ON)

add_executable(mdplayer_opna_native_cadence_test tests/native_cadence_test.c)
target_include_directories(mdplayer_opna_native_cadence_test PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}/src"
    "${CMAKE_CURRENT_SOURCE_DIR}/include"
    "${CMAKE_CURRENT_SOURCE_DIR}/tests"
    "${OPNANATIVE_UPSTREAM_DIR}")
target_link_libraries(mdplayer_opna_native_cadence_test PRIVATE mdplayer_opna_dev)

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

add_executable(mdplayer_opna_resampler_test tests/resampler_test.c)
target_include_directories(mdplayer_opna_resampler_test PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}/src"
    "${CMAKE_CURRENT_SOURCE_DIR}/include")
target_link_libraries(mdplayer_opna_resampler_test PRIVATE mdplayer_opna_dev)
add_test(NAME mdplayer_opna_resampler_test COMMAND mdplayer_opna_resampler_test)
set_tests_properties(mdplayer_opna_resampler_test PROPERTIES RUN_SERIAL ON)

add_executable(mdplayer_opna_status_cadence_test tests/status_cadence_test.c)
target_include_directories(mdplayer_opna_status_cadence_test PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}/src"
    "${CMAKE_CURRENT_SOURCE_DIR}/include")
target_link_libraries(mdplayer_opna_status_cadence_test PRIVATE mdplayer_opna_dev)

foreach(status_case
        baseline_cadence_144
        bank0_reads_keep_144
        bank1_reads_keep_144
        dense_polling_keep_144)
    add_test(NAME ${status_case}
             COMMAND mdplayer_opna_status_cadence_test -t ${status_case})
    set_tests_properties(${status_case} PROPERTIES RUN_SERIAL ON)
endforeach()
