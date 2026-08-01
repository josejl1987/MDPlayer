cmake_minimum_required(VERSION 3.16)
project(mdplayer_spc LANGUAGES CXX)
if(NOT CMAKE_BUILD_TYPE)
    set(CMAKE_BUILD_TYPE Release CACHE STRING "Build type" FORCE)
endif()

set(CMAKE_CXX_STANDARD 11)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# PR 10 sanitizer test builds (spec §33): -fsanitize=address,undefined on the
# shared library and every native test executable. Default OFF. Recommended CI
# variants: ASan (this option), UBSan-only (edit the flag), and a plain Release
# build. GCC/Clang only — MSVC does not understand -fsanitize here.
option(MDPLAYER_SPC_SANITIZERS "Build mdplayer_spc and its test executables with AddressSanitizer + UndefinedBehaviorSanitizer" OFF)
if(MDPLAYER_SPC_SANITIZERS AND NOT MSVC)
    set(MDPLAYER_SPC_SANITIZE_FLAGS "-fsanitize=address,undefined -fno-omit-frame-pointer -g")
    set(CMAKE_CXX_FLAGS "${CMAKE_CXX_FLAGS} ${MDPLAYER_SPC_SANITIZE_FLAGS}")
    # Shared-library and executable link steps must also carry the sanitizer
    # runtimes so ASan/UBSan can report at runtime.
    set(CMAKE_SHARED_LINKER_FLAGS "${CMAKE_SHARED_LINKER_FLAGS} -fsanitize=address,undefined")
    set(CMAKE_EXE_LINKER_FLAGS "${CMAKE_EXE_LINKER_FLAGS} -fsanitize=address,undefined")
endif()

# Applies the sanitizer flags to one target (library or test executable).
function(mdplayer_spc_enable_sanitizers target)
    if(MDPLAYER_SPC_SANITIZERS AND NOT MSVC)
        target_compile_options(${target} PRIVATE ${MDPLAYER_SPC_SANITIZE_FLAGS})
        target_link_options(${target} PRIVATE -fsanitize=address,undefined)
    endif()
endfunction()

set(SPCNATIVE_UPSTREAM_DIR "${CMAKE_CURRENT_SOURCE_DIR}/upstream")
set(SPCNATIVE_UPSTREAM_PATCHED_DIR "${CMAKE_CURRENT_BINARY_DIR}/upstream_patched")
set(SPCNATIVE_VOICE_PATCH "${CMAKE_CURRENT_SOURCE_DIR}/patches/0001-voice-capture.patch")
set(SPCNATIVE_TAPS_PATCH "${CMAKE_CURRENT_SOURCE_DIR}/patches/0002-voice-echo-capture.patch")
set(SPCNATIVE_EFFECTIVE_PITCH_PATCH "${CMAKE_CURRENT_SOURCE_DIR}/patches/0003-effective-pitch.patch")
set(SPCNATIVE_EVENT_CAPTURE_HEADER_PATCH "${CMAKE_CURRENT_SOURCE_DIR}/patches/0004-event-capture.patch")
set(SPCNATIVE_EVENT_CAPTURE_RUN_PATCH "${CMAKE_CURRENT_SOURCE_DIR}/patches/0005-event-capture-run.patch")

# blargg headers need an explicit endianness macro (upstream CMake does this
# via test_big_endian; same logic here so vendored files stay byte-identical).
include(TestBigEndian)
test_big_endian(MDPLAYER_SPC_WORDS_BIGENDIAN)
if(MDPLAYER_SPC_WORDS_BIGENDIAN)
    set(MDPLAYER_SPC_ENDIAN_DEFINE BLARGG_BIG_ENDIAN=1)
else()
    set(MDPLAYER_SPC_ENDIAN_DEFINE BLARGG_LITTLE_ENDIAN=1)
endif()

find_program(MDPLAYER_SPC_PATCH_EXECUTABLE patch)
if(NOT MDPLAYER_SPC_PATCH_EXECUTABLE)
    message(FATAL_ERROR "The 'patch' utility is required to apply the build-time "
        "instrumentation patches (0001-voice-capture, 0002-voice-echo-capture, "
        "0003-effective-pitch, 0004-event-capture, 0005-event-capture-run)")
endif()

# PR 3 + PR 6 + PR 8 + PR 11 instrumentation: the vendored upstream/ tree must stay
# byte-identical on disk (see UPSTREAM.md). We copy it into the build tree and
# apply the read-only voice-capture patch (0001), the voice/echo tap-capture
# patch (0002), the effective-pitch tap patch (0003), and the in-block event
# capture patches (0004/0005) THERE, then compile against that copy. The
# instrumentation writes only non-emulation state or caller-owned buffers.
add_custom_command(
    OUTPUT "${SPCNATIVE_UPSTREAM_PATCHED_DIR}/Spc_Dsp.h"
    COMMAND ${CMAKE_COMMAND} -E rm -rf "${SPCNATIVE_UPSTREAM_PATCHED_DIR}"
    COMMAND ${CMAKE_COMMAND} -E copy_directory
        "${SPCNATIVE_UPSTREAM_DIR}" "${SPCNATIVE_UPSTREAM_PATCHED_DIR}"
    COMMAND ${MDPLAYER_SPC_PATCH_EXECUTABLE} -p1 -d
        "${SPCNATIVE_UPSTREAM_PATCHED_DIR}" -i "${SPCNATIVE_VOICE_PATCH}"
    COMMAND ${MDPLAYER_SPC_PATCH_EXECUTABLE} -p1 -d
        "${SPCNATIVE_UPSTREAM_PATCHED_DIR}" -i "${SPCNATIVE_TAPS_PATCH}"
    COMMAND ${MDPLAYER_SPC_PATCH_EXECUTABLE} -p1 -d
        "${SPCNATIVE_UPSTREAM_PATCHED_DIR}" -i "${SPCNATIVE_EFFECTIVE_PITCH_PATCH}"
    COMMAND ${MDPLAYER_SPC_PATCH_EXECUTABLE} -p1 -d
        "${SPCNATIVE_UPSTREAM_PATCHED_DIR}" --fuzz=0
        -i "${SPCNATIVE_EVENT_CAPTURE_HEADER_PATCH}"
    COMMAND ${MDPLAYER_SPC_PATCH_EXECUTABLE} -p1 -d
        "${SPCNATIVE_UPSTREAM_PATCHED_DIR}" --fuzz=0
        -i "${SPCNATIVE_EVENT_CAPTURE_RUN_PATCH}"
    DEPENDS
        "${SPCNATIVE_VOICE_PATCH}"
        "${SPCNATIVE_TAPS_PATCH}"
        "${SPCNATIVE_EFFECTIVE_PITCH_PATCH}"
        "${SPCNATIVE_EVENT_CAPTURE_HEADER_PATCH}"
        "${SPCNATIVE_EVENT_CAPTURE_RUN_PATCH}"
    COMMENT "Applying 0001/0002/0003/0004/0005 instrumentation patches to the build-tree upstream copy"
    VERBATIM)
add_custom_target(upstream_patched ALL
    DEPENDS "${SPCNATIVE_UPSTREAM_PATCHED_DIR}/Spc_Dsp.h")

# Vendored upstream Game_Music_Emu SPC core (LGPL-2.1; see UPSTREAM.md for the
# pinned commit). All *.cpp under upstream/ are compiled from the PATCHED
# build-tree copy. If the pinned tree bundles a TU into another via #include,
# remove the bundled file(s) here.
file(GLOB SPCNATIVE_UPSTREAM_SOURCES CONFIGURE_DEPENDS
    "${SPCNATIVE_UPSTREAM_DIR}/*.cpp")
set(SPCNATIVE_UPSTREAM_PATCHED_SOURCES "")
foreach(src IN LISTS SPCNATIVE_UPSTREAM_SOURCES)
    file(RELATIVE_PATH rel "${SPCNATIVE_UPSTREAM_DIR}" "${src}")
    list(APPEND SPCNATIVE_UPSTREAM_PATCHED_SOURCES
        "${SPCNATIVE_UPSTREAM_PATCHED_DIR}/${rel}")
endforeach()
# The patched copies are produced by the build-time patch step above, so they
# do not exist at configure time; declare them generated.
set_source_files_properties(${SPCNATIVE_UPSTREAM_PATCHED_SOURCES}
    PROPERTIES GENERATED TRUE)

add_library(mdplayer_spc SHARED
    src/mdplayer_spc.cpp
    src/spc_capture.cpp
    src/spc_taps.cpp
    src/spc_pitch.cpp
    ${SPCNATIVE_UPSTREAM_PATCHED_SOURCES})
add_dependencies(mdplayer_spc upstream_patched)
mdplayer_spc_enable_sanitizers(mdplayer_spc)

target_include_directories(mdplayer_spc PUBLIC
    "${CMAKE_CURRENT_SOURCE_DIR}/include")
target_include_directories(mdplayer_spc PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}"
    "${SPCNATIVE_UPSTREAM_PATCHED_DIR}")
target_compile_definitions(mdplayer_spc PRIVATE ${MDPLAYER_SPC_ENDIAN_DEFINE})

set_target_properties(mdplayer_spc PROPERTIES
    OUTPUT_NAME "mdplayer_spc"
    POSITION_INDEPENDENT_CODE ON)

# Packaging (PR 10): copy the built library to MDPlayer runtimes/<rid>/native so
# the managed wrapper can find it via AppContext.BaseDirectory probing. Windows
# builds produce mdplayer_spc.dll (win-x64), everything else libmdplayer_spc.so
# (linux-x64). The .NET runtime identifier matches the current OS only.
if(WIN32)
    set(MDPLAYER_SPC_RUNTIME_RID "win-x64")
    set(MDPLAYER_SPC_RUNTIME_LIBRARY "mdplayer_spc.dll")
else()
    set(MDPLAYER_SPC_RUNTIME_RID "linux-x64")
    set(MDPLAYER_SPC_RUNTIME_LIBRARY "libmdplayer_spc.so")
endif()
set(MDPLAYER_SPC_RUNTIMES_DIR
    "${CMAKE_CURRENT_SOURCE_DIR}/../../runtimes/${MDPLAYER_SPC_RUNTIME_RID}/native")
add_custom_command(TARGET mdplayer_spc POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E make_directory "${MDPLAYER_SPC_RUNTIMES_DIR}"
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
        "$<TARGET_FILE:mdplayer_spc>" "${MDPLAYER_SPC_RUNTIMES_DIR}/${MDPLAYER_SPC_RUNTIME_LIBRARY}"
    COMMENT "Copying ${MDPLAYER_SPC_RUNTIME_LIBRARY} to runtimes/${MDPLAYER_SPC_RUNTIME_RID}/native/"
    VERBATIM)

# PR 3 native parity test (spec §5.3): the master output with the PR 3 voice
# observer active must be bit-identical to a plain GME render of the same SPC.
# PR 10 sanitizer builds instrument this test (and any future tap/pitch test
# executables added by PR 6/8) with ASan+UBSan via mdplayer_spc_enable_sanitizers.
enable_testing()
add_executable(mdplayer_spc_parity_test tests/parity_test.cpp)
target_include_directories(mdplayer_spc_parity_test PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}/include"
    "${CMAKE_CURRENT_SOURCE_DIR}"
    "${SPCNATIVE_UPSTREAM_PATCHED_DIR}")
target_compile_definitions(mdplayer_spc_parity_test PRIVATE ${MDPLAYER_SPC_ENDIAN_DEFINE})
target_link_libraries(mdplayer_spc_parity_test PRIVATE mdplayer_spc)
add_dependencies(mdplayer_spc_parity_test upstream_patched)
mdplayer_spc_enable_sanitizers(mdplayer_spc_parity_test)
add_test(NAME mdplayer_spc_parity COMMAND mdplayer_spc_parity_test)
# PR 6 native tap test (spec §5.3, §8.2, §8.4): renders the synthetic SPC with
# per-voice/echo tap capture armed and asserts the master stays bit-identical
# to a taps-disabled render, voices 0/1 are non-silent, voices 2..7 are silent,
# and the echo return is non-silent (EON and echo volumes are non-zero).
add_executable(mdplayer_spc_tap_test tests/tap_test.cpp)
target_include_directories(mdplayer_spc_tap_test PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}/include"
    "${CMAKE_CURRENT_SOURCE_DIR}"
    "${SPCNATIVE_UPSTREAM_PATCHED_DIR}")
target_compile_definitions(mdplayer_spc_tap_test PRIVATE ${MDPLAYER_SPC_ENDIAN_DEFINE})
target_link_libraries(mdplayer_spc_tap_test PRIVATE mdplayer_spc)
add_dependencies(mdplayer_spc_tap_test upstream_patched)
mdplayer_spc_enable_sanitizers(mdplayer_spc_tap_test)
add_test(NAME mdplayer_spc_tap COMMAND mdplayer_spc_tap_test)

# PR 8 native pitch test (spec §10.2/§13.6/§5.3): a PMON fixture whose voice 1
# is pitch-modulated by voice 0. Asserts the C ABI render (effective-pitch taps
# armed) is byte-identical to a plain GME render, that the observed effective
# pitch differs from the register pitch for the modulated voice, and that
# PITCH_CHANGED events report the effective pitch.
add_executable(mdplayer_spc_pitch_test tests/pitch_test.cpp)
target_include_directories(mdplayer_spc_pitch_test PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}/include"
    "${CMAKE_CURRENT_SOURCE_DIR}"
    "${SPCNATIVE_UPSTREAM_PATCHED_DIR}")
target_compile_definitions(mdplayer_spc_pitch_test PRIVATE ${MDPLAYER_SPC_ENDIAN_DEFINE})
target_link_libraries(mdplayer_spc_pitch_test PRIVATE mdplayer_spc)
add_dependencies(mdplayer_spc_pitch_test upstream_patched)
mdplayer_spc_enable_sanitizers(mdplayer_spc_pitch_test)
add_test(NAME mdplayer_spc_pitch COMMAND mdplayer_spc_pitch_test)

# In-block event semantics: KON/KOFF/retrigger/source/pitch transitions must
# retain their DSP-time positions, and release audio must outlive KOFF.
add_executable(mdplayer_spc_event_capture_test tests/event_capture_test.cpp)
target_include_directories(mdplayer_spc_event_capture_test PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}/include")
target_link_libraries(mdplayer_spc_event_capture_test PRIVATE mdplayer_spc)
add_dependencies(mdplayer_spc_event_capture_test upstream_patched)
mdplayer_spc_enable_sanitizers(mdplayer_spc_event_capture_test)
add_test(NAME mdplayer_spc_event_capture COMMAND mdplayer_spc_event_capture_test)
