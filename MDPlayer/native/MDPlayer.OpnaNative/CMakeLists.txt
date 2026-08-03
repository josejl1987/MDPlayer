cmake_minimum_required(VERSION 3.16)
project(mdplayer_opna LANGUAGES C)
if(NOT CMAKE_BUILD_TYPE)
    set(CMAKE_BUILD_TYPE Release CACHE STRING "Build type" FORCE)
endif()

set(CMAKE_C_STANDARD 99)
set(CMAKE_C_STANDARD_REQUIRED ON)

# Sanitizer test builds (mirrors the MDPlayer.SpcNative convention). Default
# OFF. The vendored YM2608-LLE core performs intentional signed-int arithmetic
# that UBSan flags (left shifts of large constants) by design, so a full
# -fsanitize=undefined build is noisy inside upstream; we therefore offer
# AddressSanitizer only by default for the probe, with <...>_UBSAN optional.
option(MDPLAYER_OPNA_SANITIZERS "Build mdplayer_opna and its probe with AddressSanitizer" OFF)
option(MDPLAYER_OPNA_UBSAN "Also add UndefinedBehaviorSanitizer (noisy inside upstream core)" OFF)
if(MDPLAYER_OPNA_SANITIZERS AND NOT MSVC)
    set(MDPLAYER_OPNA_SANITIZE_FLAGS "-fsanitize=address -fno-omit-frame-pointer -g")
    if(MDPLAYER_OPNA_UBSAN)
        set(MDPLAYER_OPNA_SANITIZE_FLAGS
            "-fsanitize=address,undefined -fno-omit-frame-pointer -g")
    endif()
    set(CMAKE_C_FLAGS "${CMAKE_C_FLAGS} ${MDPLAYER_OPNA_SANITIZE_FLAGS}")
    set(CMAKE_SHARED_LINKER_FLAGS "${CMAKE_SHARED_LINKER_FLAGS} -fsanitize=address")
    set(CMAKE_EXE_LINKER_FLAGS "${CMAKE_EXE_LINKER_FLAGS} -fsanitize=address")
    if(MDPLAYER_OPNA_UBSAN)
        set(CMAKE_SHARED_LINKER_FLAGS "${CMAKE_SHARED_LINKER_FLAGS} -fsanitize=undefined")
        set(CMAKE_EXE_LINKER_FLAGS "${CMAKE_EXE_LINKER_FLAGS} -fsanitize=undefined")
    endif()
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
    "${OPNANATIVE_UPSTREAM_DIR}/fmopna_2608.c")
target_include_directories(mdplayer_opna PUBLIC
    "${CMAKE_CURRENT_SOURCE_DIR}/src")
target_include_directories(mdplayer_opna PRIVATE
    "${OPNANATIVE_UPSTREAM_DIR}")
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

