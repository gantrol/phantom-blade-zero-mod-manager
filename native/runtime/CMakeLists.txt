cmake_minimum_required(VERSION 3.24)
project(PhantomBladeRuntime LANGUAGES C CXX ASM_MASM)
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
set(MINHOOK "${CMAKE_CURRENT_SOURCE_DIR}/../../mods/features/phantom-blade-training/native/vendor/minhook")
add_library(version SHARED Loader.cpp Proxy.cpp Proxy.asm version.def
    "${MINHOOK}/src/buffer.c" "${MINHOOK}/src/hook.c"
    "${MINHOOK}/src/trampoline.c" "${MINHOOK}/src/hde/hde64.c")
target_include_directories(version PRIVATE "${MINHOOK}/include")
target_link_libraries(version PRIVATE bcrypt)
target_compile_definitions(version PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX UNICODE _UNICODE)
target_compile_options(version PRIVATE $<$<COMPILE_LANGUAGE:CXX>:/W4> $<$<COMPILE_LANGUAGE:CXX>:/utf-8>)
set_target_properties(version PROPERTIES OUTPUT_NAME version)
