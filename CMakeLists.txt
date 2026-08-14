cmake_minimum_required(VERSION 3.20)

project(KeySecBox.DLL LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

if(NOT CMAKE_BUILD_TYPE AND NOT CMAKE_CONFIGURATION_TYPES)
  set(CMAKE_BUILD_TYPE Debug CACHE STRING "Build type" FORCE)
endif()

# 与 WinUI 3 (KeySecBox.UI) 输出到同一目录: <root>/bin/<Config>/
if(CMAKE_CONFIGURATION_TYPES)
  set(OUTPUT_DIR "${CMAKE_SOURCE_DIR}/bin/$<CONFIG>")
else()
  set(OUTPUT_DIR "${CMAKE_SOURCE_DIR}/bin/${CMAKE_BUILD_TYPE}")
endif()

add_library(KeySecBox.DLL SHARED
  src/vault.cpp
  src/crypto.cpp
  src/crypto.h
  src/json.cpp
  src/json.hpp
  src/keysecbox.h
)

target_include_directories(KeySecBox.DLL PUBLIC
  ${CMAKE_CURRENT_SOURCE_DIR}/src
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
