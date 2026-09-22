cmake_minimum_required(VERSION 3.20)

project(KeySecBox.DLL LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

if(NOT CMAKE_BUILD_TYPE AND NOT CMAKE_CONFIGURATION_TYPES)
  set(CMAKE_BUILD_TYPE Debug CACHE STRING "Build type" FORCE)
endif()

# 本文件位于 <root>/KeySecBox.Core/，故输出目录需向上一级：
#   <root>/bin/<Config>/<Platform>/
# 与 KeySecBox.UI 的 exe 子目录同级（UI 输出到 bin/<Config>/<Platform>/exe/）。
set(ROOT_DIR "${CMAKE_CURRENT_SOURCE_DIR}/..")
if(CMAKE_CONFIGURATION_TYPES)
  set(OUTPUT_DIR "${ROOT_DIR}/bin/$<CONFIG>/${CMAKE_GENERATOR_PLATFORM}")
else()
  set(OUTPUT_DIR "${ROOT_DIR}/bin/${CMAKE_BUILD_TYPE}/${CMAKE_GENERATOR_PLATFORM}")
endif()

add_library(KeySecBox.DLL SHARED
  vault.cpp
  diag.cpp
  format.cpp
  persist.cpp
  crypto.cpp
  crypto.h
  internal.h
  json.cpp
  json.hpp
  keysecbox.h
)

target_include_directories(KeySecBox.DLL PUBLIC
  ${CMAKE_CURRENT_SOURCE_DIR}
)

target_compile_definitions(KeySecBox.DLL PRIVATE KSBOX_BUILD_DLL)

if(MSVC)
  target_compile_options(KeySecBox.DLL PRIVATE /utf-8)
endif()

set_target_properties(KeySecBox.DLL PROPERTIES
  OUTPUT_NAME "KeySecBox.DLL"
  PREFIX ""
  RUNTIME_OUTPUT_DIRECTORY "${OUTPUT_DIR}"
  LIBRARY_OUTPUT_DIRECTORY "${OUTPUT_DIR}"
  ARCHIVE_OUTPUT_DIRECTORY "${OUTPUT_DIR}"
)
