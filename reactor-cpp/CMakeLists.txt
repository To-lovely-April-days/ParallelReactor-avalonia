cmake_minimum_required(VERSION 3.16)   # 板子 Ubuntu 20.04 自带 3.16
project(reactor-cpp VERSION 0.1.0 LANGUAGES C CXX)

# 编译方式：
#  - Windows + Visual Studio 2022：打开本文件夹（CMakeSettings.json），用「连接管理器」连到板子，
#    VS 把源码同步到板子上用板子的 GCC 编译（远程 Linux 生成）。
#  - 板子上直接编：cmake -S . -B build && cmake --build build -j4
#  - 开发机：tools/build（后端 + 测试）、tools/preview（界面截图），不需要 CMake。

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
if(NOT CMAKE_BUILD_TYPE)
  set(CMAKE_BUILD_TYPE Release)
endif()

# ---------- 选项 ----------
set(REACTOR_HMI_BACKEND "SDL" CACHE STRING "触屏界面的显示后端：SDL / X11")
set_property(CACHE REACTOR_HMI_BACKEND PROPERTY STRINGS SDL X11)
option(REACTOR_FULLSCREEN "SDL 窗口全屏（板子上打开）" ON)
option(REACTOR_USE_FREETYPE "用 FreeType 加载字体（板子上打开；关掉则用 LVGL 自带的 tiny_ttf）" ON)
option(REACTOR_BUILD_HMI "编译 LVGL 触屏界面" ON)
option(REACTOR_BUILD_TESTS "编译单测" ON)

find_package(Threads REQUIRED)

# ---------- mbedTLS 3.6（第三方原样源码 + 项目自己的裁剪配置 config/reactor_mbedtls_config.h）----------
# PC 端 TCP 入口跑 HTTPS / WSS（自签证书 + 指纹固定）。第三方源码不开警告。
file(GLOB REACTOR_MBEDTLS_SRC CONFIGURE_DEPENDS ${CMAKE_CURRENT_SOURCE_DIR}/third_party/mbedtls/library/*.c)
add_library(reactor_mbedtls STATIC ${REACTOR_MBEDTLS_SRC})
target_include_directories(reactor_mbedtls SYSTEM PUBLIC
  ${CMAKE_CURRENT_SOURCE_DIR}/third_party/mbedtls/include ${CMAKE_CURRENT_SOURCE_DIR}/config)
target_compile_definitions(reactor_mbedtls PUBLIC "MBEDTLS_CONFIG_FILE=\"reactor_mbedtls_config.h\"")
target_compile_options(reactor_mbedtls PRIVATE -w)
set_target_properties(reactor_mbedtls PROPERTIES C_STANDARD 99 POSITION_INDEPENDENT_CODE ON)
target_link_libraries(reactor_mbedtls PUBLIC Threads::Threads)

# ---------- 后台公共库：src/ 下全部（common / core / hw / store / net / ctl / cmd / api）----------
file(GLOB_RECURSE REACTOR_SRC CONFIGURE_DEPENDS ${CMAKE_CURRENT_SOURCE_DIR}/src/*.cpp)
add_library(reactor_core STATIC ${REACTOR_SRC})
target_include_directories(reactor_core PUBLIC src third_party)
target_compile_options(reactor_core PRIVATE -Wall -Wextra -Wno-unused-parameter)
target_link_libraries(reactor_core PUBLIC reactor_mbedtls Threads::Threads)

add_executable(reactord apps/reactord/main.cpp)
target_link_libraries(reactord PRIVATE reactor_core)

add_executable(rtu-probe apps/rtu-probe/main.cpp)
target_link_libraries(rtu-probe PRIVATE reactor_core)

if(REACTOR_BUILD_TESTS)
  enable_testing()
  file(GLOB_RECURSE REACTOR_TEST_SRC CONFIGURE_DEPENDS ${CMAKE_CURRENT_SOURCE_DIR}/tests/*.cpp)
  add_executable(run_tests ${REACTOR_TEST_SRC})
  target_include_directories(run_tests PRIVATE tests)
  target_link_libraries(run_tests PRIVATE reactor_core)
  add_test(NAME all COMMAND run_tests)
endif()

# ---------- LVGL 触屏界面（独立进程，不链接后台代码）----------
if(REACTOR_BUILD_HMI)
  # 不用 LV_CONF_PATH：它会把绝对路径塞进宏里，路径里有中文时 GCC 9 编不过。
  set(LV_CONF_BUILD_DISABLE_EXAMPLES ON CACHE BOOL "" FORCE)
  set(LV_CONF_BUILD_DISABLE_DEMOS ON CACHE BOOL "" FORCE)
  set(LV_CONF_BUILD_DISABLE_THORVG_INTERNAL ON CACHE BOOL "" FORCE)
  add_subdirectory(third_party/lvgl EXCLUDE_FROM_ALL)
  target_include_directories(lvgl SYSTEM PUBLIC ${CMAKE_CURRENT_SOURCE_DIR}/config)

  find_package(PkgConfig REQUIRED)
  set(_defs)
  if(REACTOR_HMI_BACKEND STREQUAL "SDL")
    pkg_check_modules(SDL2 REQUIRED IMPORTED_TARGET sdl2)
    target_link_libraries(lvgl PUBLIC PkgConfig::SDL2)
    list(APPEND _defs REACTOR_USE_SDL=1)
  elseif(REACTOR_HMI_BACKEND STREQUAL "X11")
    pkg_check_modules(X11 REQUIRED IMPORTED_TARGET x11)
    target_link_libraries(lvgl PUBLIC PkgConfig::X11)
    list(APPEND _defs REACTOR_USE_X11=1)
  else()
    message(FATAL_ERROR "REACTOR_HMI_BACKEND 只能是 SDL 或 X11")
  endif()
  if(REACTOR_FULLSCREEN)
    list(APPEND _defs REACTOR_FULLSCREEN=1)
  endif()
  if(REACTOR_USE_FREETYPE)
    pkg_check_modules(FREETYPE REQUIRED IMPORTED_TARGET freetype2)
    target_link_libraries(lvgl PUBLIC PkgConfig::FREETYPE)
    list(APPEND _defs REACTOR_USE_FREETYPE=1)
  else()
    list(APPEND _defs REACTOR_USE_TINY_TTF=1)
  endif()
  target_compile_definitions(lvgl PUBLIC ${_defs})
  target_link_libraries(lvgl PUBLIC Threads::Threads m)

  file(GLOB_RECURSE HMI_SRC CONFIGURE_DEPENDS ${CMAKE_CURRENT_SOURCE_DIR}/apps/reactor-hmi/*.cpp)
  add_executable(reactor-hmi ${HMI_SRC})
  # 图标数组（1 倍 + 1.5 倍，十几 MB 的十六进制）：不优化，板子上编得快、不吃内存
  set_source_files_properties(${CMAKE_CURRENT_SOURCE_DIR}/apps/reactor-hmi/ui/kit/icons_gen.cpp PROPERTIES COMPILE_OPTIONS "-O0")
  target_include_directories(reactor-hmi PRIVATE apps/reactor-hmi third_party)
  target_link_libraries(reactor-hmi PRIVATE lvgl)
  target_compile_options(reactor-hmi PRIVATE -Wall)

  # 字体随程序走：编译后拷到可执行文件旁边的 fonts/
  add_custom_command(TARGET reactor-hmi POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_directory ${CMAKE_CURRENT_SOURCE_DIR}/assets/fonts $<TARGET_FILE_DIR:reactor-hmi>/fonts)
endif()

# ---------- 安装（打包到板子上的目录结构）----------
install(TARGETS reactord rtu-probe RUNTIME DESTINATION bin)
if(REACTOR_BUILD_HMI)
  install(TARGETS reactor-hmi RUNTIME DESTINATION bin)
  install(DIRECTORY assets/fonts/ DESTINATION bin/fonts)
endif()
