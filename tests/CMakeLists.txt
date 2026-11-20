# SPDX-FileCopyrightText: 2026 K_PU
# SPDX-License-Identifier: AGPL-3.0-or-later
#
# 核心 C API 测试。测试程序与 KeySecBox.DLL 输出到同一目录，
# 运行时无需额外配置 PATH（Windows 优先在 exe 所在目录解析依赖）。

add_executable(ksbox_tests core_tests.cpp)

target_include_directories(ksbox_tests PRIVATE ${CMAKE_SOURCE_DIR}/src)
target_link_libraries(ksbox_tests PRIVATE KeySecBox.DLL)

if(MSVC)
  target_compile_options(ksbox_tests PRIVATE /utf-8)
endif()

# OUTPUT_DIR 来自父级 CMakeLists：<root>/bin/<Config>/<Platform>/
set_target_properties(ksbox_tests PROPERTIES
  OUTPUT_NAME "ksbox_tests"
  RUNTIME_OUTPUT_DIRECTORY "${OUTPUT_DIR}"
)

add_test(NAME core COMMAND ksbox_tests)
