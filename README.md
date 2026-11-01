# Zipora

基于 .NET 8、WPF 和官方 7-Zip 引擎的 Windows 离线压缩包管理工具。

## 功能

- 浏览、创建及解压常见格式的压缩包，支持分卷和自解压格式。
- 将包内文件或目录拖放到资源管理器，保留目录结构。
- 文件选择、多选、完整性检查、磁盘空间预检与取消操作。
- 本地密码库、密码快速填入、本地字典破解。
- 浅色/深色主题、云母/毛玻璃背景、自定义图片。
- 便携模式：设置、字典、背景图与临时文件放在 Data 中，便携密码库使用主密码加密。

本仓库是 **纯离线社区版**，不包含在线密码查询、密码贡献、远程更新检查、设备指纹或网络 API 客户端。不需要账号、激活码、服务器或访问密钥。软件启动和解压流程不发起网络请求；鸣谢中的外部链接仅在用户主动点击时打开。

## 编译

需要 Windows 10/11 x64、.NET 8 SDK。普通 C# 构建不依赖云端服务：

```powershell
dotnet restore src/Zipora/Zipora.csproj
dotnet build src/Zipora/Zipora.csproj -c Release
```

7-Zip 引擎二进制 **不提交到源码仓库**。运行前，从 [7-Zip 官方网站](https://www.7-zip.org/) 获取 x64 版本，准备以下目录；可从本机 7-Zip 安装目录复制对应文件：

```text
src/Zipora/7zip/
  7z.exe
  7z.dll
  7z.sfx       # 创建自解压包时需要
  7zCon.sfx   # 可选模块
  License.txt
```

本地验证使用 7-Zip 26.03，其他版本需自行回归。没有引擎仍可编译，启动时会提示缺失引擎。

密码字典请自行在 `src/Zipora/dictionaries/` 新建 TXT，每行一个候选密码；不要提交自己的真实密码或私人字典。字典破解仅用于你有权访问的文件。

### 普通版发布

```powershell
dotnet publish src/Zipora/Zipora.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=embedded -o artifacts/windows-x64
```

### 便携版发布

```powershell
dotnet publish src/Zipora/Zipora.csproj -c Release -r win-x64 --self-contained true -p:PublishProfile=Portable -p:Portable=true -p:PublishSingleFile=false -o artifacts/portable/App
```

此专用构建自动把 App 的父目录作为便携根目录，数据放在根目录 Data。完整复制 portable 文件夹，运行 `App/Zipora.exe`，无需预装 .NET。升级时保留 Data；关闭并完成清理后再安全弹出 U 盘。忘记便携密码库主密码无法恢复其内容。应用不保证 Windows 或外部查看器零痕迹。

### 可选：资源管理器右键扩展

需要 Visual Studio C++ x64 工具链、Windows SDK 和 CMake：

```powershell
cmake -S src/Zipora.ShellExtension -B src/Zipora.ShellExtension/build -A x64
cmake --build src/Zipora.ShellExtension/build --config Release
```

然后重新发布 C# 项目，将扩展 DLL 放到主程序同目录，普通版才可注册右键菜单。未编译扩展时不影响核心压缩解压；便携版不注册系统菜单。

## 测试

在仓库根目录运行，每次为提取和文件名测试选择尚不存在的输出目录：

```powershell
dotnet run --project tests/ExtractionRegression -- "$PWD/artifacts/extraction-test" "$PWD/src/Zipora/7zip/7z.exe"
dotnet run --project tests/ArchiveOutputPathRegression -- "$PWD/artifacts/output-path-test"
dotnet run --project tests/VisualStateRegression
```

测试涉及真实 ZIP/加密 7z、Shell 虚拟文件复制、目录结构、取消清理、路径边界、文件名和实际渲染。它们不能替代跨应用鼠标拖放、多显示器 DPI、真实 U 盘跨机和断电测试。

## 许可证与第三方组件

Zipora 自有代码采用 [MIT](LICENSE)。第三方组件不因仓库许可证而改为 MIT，详见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。发布二进制时请携带各组件要求的版权和许可证信息。
