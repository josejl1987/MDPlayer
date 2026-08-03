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
