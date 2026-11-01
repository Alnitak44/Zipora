cmake_minimum_required(VERSION 3.21)
project(ZiporaShellExtension LANGUAGES CXX)

add_library(Zipora.ShellExtension SHARED ZiporaShellExtension.cpp)
target_compile_features(Zipora.ShellExtension PRIVATE cxx_std_20)
target_compile_definitions(Zipora.ShellExtension PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX)
target_link_libraries(Zipora.ShellExtension PRIVATE ole32 shell32)
set_target_properties(Zipora.ShellExtension PROPERTIES
    OUTPUT_NAME "Zipora.ShellExtension"
    PREFIX ""
    SUFFIX ".dll")
# DLL 导出通过 .def 声明（避免与新版 SDK 内联的 DllGetClassObject 冲突）
target_sources(Zipora.ShellExtension PRIVATE ZiporaShellExtension.def)
