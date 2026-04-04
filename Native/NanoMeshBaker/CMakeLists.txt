cmake_minimum_required(VERSION 3.20)

project(NanoMeshBaker LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

set(NANOMESH_THIRD_PARTY_ROOT "${CMAKE_CURRENT_LIST_DIR}/../third_party/meshoptimizer")

file(GLOB NANOMESH_MESHOPTIMIZER_SOURCES
    "${NANOMESH_THIRD_PARTY_ROOT}/src/*.cpp"
)

add_library(NanoMeshBakerLib STATIC
    ${NANOMESH_MESHOPTIMIZER_SOURCES}
    src/NanoMeshBakeBackend.cpp
)

target_include_directories(NanoMeshBakerLib
    PUBLIC
        "${CMAKE_CURRENT_LIST_DIR}/src"
        "${NANOMESH_THIRD_PARTY_ROOT}/src"
)

if(MSVC)
    target_compile_definitions(NanoMeshBakerLib PRIVATE _CRT_SECURE_NO_WARNINGS WIN32_LEAN_AND_MEAN NOMINMAX)
endif()

add_executable(NanoMeshBakerCli
    src/main.cpp
)

target_link_libraries(NanoMeshBakerCli PRIVATE NanoMeshBakerLib)

if(MSVC)
    target_compile_definitions(NanoMeshBakerCli PRIVATE _CRT_SECURE_NO_WARNINGS WIN32_LEAN_AND_MEAN NOMINMAX)
endif()
