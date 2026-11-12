cmake_minimum_required(VERSION 3.22.1)
project(thumbfree_whisper C CXX)

# One CMake file, two builds: "generic" runs on every 64-bit ARM phone; "v82" uses ARMv8.2 dot-product and fp16
# instructions (most phones from ~2019), about twice as fast for the quantized models. The app loads the best one the
# phone supports at runtime (app.thumbfree.whisper.CpuFeatures).
set(TF_VARIANT "generic" CACHE STRING "generic or v82")
set(WHISPER_ROOT "${CMAKE_CURRENT_SOURCE_DIR}/../../../../../third_party/whisper.cpp")
if (NOT EXISTS "${WHISPER_ROOT}/CMakeLists.txt")
    message(FATAL_ERROR "whisper.cpp is missing at ${WHISPER_ROOT}: run `git submodule update --init`")
endif()

# Recognition has to be fast in debug builds too.
foreach (lang C CXX)
    set(CMAKE_${lang}_FLAGS_DEBUG "-O3 -DNDEBUG")
    set(CMAKE_${lang}_FLAGS_RELEASE "-O3 -DNDEBUG")
endforeach()

set(BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)
set(GGML_NATIVE OFF CACHE BOOL "" FORCE)
# No OpenMP: its worker threads spin-wait between calls, which costs battery on a phone.
set(GGML_OPENMP OFF CACHE BOOL "" FORCE)
set(WHISPER_BUILD_TESTS OFF CACHE BOOL "" FORCE)
set(WHISPER_BUILD_EXAMPLES OFF CACHE BOOL "" FORCE)
set(WHISPER_BUILD_SERVER OFF CACHE BOOL "" FORCE)
set(WHISPER_ALL_WARNINGS OFF CACHE BOOL "" FORCE)

if (ANDROID_ABI STREQUAL "arm64-v8a")
    if (TF_VARIANT STREQUAL "v82")
        set(GGML_CPU_ARM_ARCH "armv8.2-a+dotprod+fp16" CACHE STRING "" FORCE)
    else()
        set(GGML_CPU_ARM_ARCH "armv8-a" CACHE STRING "" FORCE)
    endif()
endif()

if (TF_VARIANT STREQUAL "v82")
    set(TF_LIB thumbfree_whisper_v82)
else()
    set(TF_LIB thumbfree_whisper)
endif()

add_subdirectory("${WHISPER_ROOT}" whisper.cpp EXCLUDE_FROM_ALL)

add_library(${TF_LIB} SHARED whisper_jni.cpp)
target_link_libraries(${TF_LIB} PRIVATE whisper log)
target_compile_options(${TF_LIB} PRIVATE -fvisibility=hidden -ffunction-sections -fdata-sections)
# 16 KB pages (Android 15+ devices; required by Google Play), and keep only what the JNI layer uses.
target_link_options(${TF_LIB} PRIVATE "-Wl,-z,max-page-size=16384" "-Wl,--gc-sections" "-Wl,--exclude-libs,ALL")
