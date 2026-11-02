param(
    [Parameter(Mandatory=$true)][string]$DependencyDirectory,
    [string]$IsccPath = 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
    [string]$DotnetExecutable = 'dotnet'
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $repo
try {
    [xml]$xml = Get-Content src/Zipora/Zipora.csproj -Raw
    $version = $xml.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
    $release = Join-Path $repo 'artifacts/release'
    if (Test-Path -LiteralPath $release) { throw 'Release directory exists; use a clean checkout.' }
    New-Item -ItemType Directory -Path $release | Out-Null
    $standard = Join-Path $repo 'artifacts/standard'
    $portable = Join-Path $repo "artifacts/Zipora-v$version-win-x64-portable"
    $engine = (Resolve-Path 'src/Zipora/7zip/7z.exe').Path
    $native = 'src/Zipora.ShellExtension/build/Release/Zipora.ShellExtension.dll'
    if (-not (Test-Path -LiteralPath $native)) { throw 'Build the native shell extension first.' }

    function Publish-App([bool]$PortableMode, [string]$Output) {
        $properties = if ($PortableMode) { @('-p:Portable=true','-p:PublishProfile=Portable','-p:PublishSingleFile=false') }
                      else { @('-p:Portable=false','-p:PublishSingleFile=true','-p:IncludeNativeLibrariesForSelfExtract=true') }
        & $DotnetExecutable publish src/Zipora/Zipora.csproj -c Release -r win-x64 --self-contained true @properties '-p:DebugType=embedded' -o $Output
        if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
        $assemblyPath = (Resolve-Path 'src/Zipora/bin/Release/net8.0-windows/win-x64/Zipora.dll').Path
        # Isolated PowerShell invocation avoids assembly identity caching between builds.
        $check = @'
param($AssemblyPath, $Expected)
$assembly = [Reflection.Assembly]::LoadFile($AssemblyPath)
$actual = $assembly.GetType('Zipora.Services.AppStorage').GetField('PortableBuild',[Reflection.BindingFlags]'NonPublic,Static').GetRawConstantValue()
if ($actual.ToString() -ne $Expected) { throw 'Incorrect storage mode' }
'@
        $checkPath = Join-Path $repo 'artifacts/check-mode.ps1'
        $check | Set-Content -LiteralPath $checkPath -Encoding utf8
        & pwsh -NoProfile -File $checkPath $assemblyPath ($PortableMode.ToString())
        if ($LASTEXITCODE -ne 0) { throw 'Storage mode check failed.' }
        foreach ($name in @('Zipora.exe','Zipora.ico','7zip/7z.exe','7zip/7z.dll','7zip/7z.sfx','7zip/License.txt')) {
            if (-not (Test-Path -LiteralPath (Join-Path $Output $name))) { throw "Missing component: $name" }
        }
    }

    function Add-Licenses([string]$Root) {
        Copy-Item LICENSE,THIRD_PARTY_NOTICES.md -Destination $Root
        Copy-Item -LiteralPath (Join-Path $DependencyDirectory 'Licenses') -Destination $Root -Recurse
        $management = Join-Path $env:USERPROFILE '.nuget/packages/system.management/8.0.0'
        foreach ($name in @('LICENSE.TXT','THIRD-PARTY-NOTICES.TXT')) {
            Copy-Item -LiteralPath (Join-Path $management $name) -Destination (Join-Path $Root "Licenses/System.Management-$name")
        }
    }

    Publish-App $false $standard
    if (-not (Test-Path "$standard/Zipora.ShellExtension.dll")) { throw 'Native extension missing from standard publish.' }
    if (Test-Path "$standard/Data") { throw 'Standard publish contains a user profile.' }
    Add-Licenses $standard
    @'
# Zipora Windows x64
安装版：运行同版本 Setup-x64.exe，升级时会识别旧安装目录，保留 AppData 中的设置和密码库。
ZIP 常规版：完整解压后运行 Zipora.exe，设置同样保存在 %APPDATA%/Zipora。
包含 .NET 运行库及官方 7-Zip 引擎，不需要单独安装 .NET。
修改或删除已注册右键扩展的目录前，请先在设置中关闭右键菜单。
源码与更新：https://github.com/Alnitak44/Zipora
'@ | Set-Content "$standard/README-Usage.md" -Encoding utf8

    if (-not (Test-Path -LiteralPath $IsccPath)) { throw 'Inno Setup compiler not found.' }
    Copy-Item -LiteralPath (Join-Path (Split-Path -Parent $IsccPath) 'License.txt') -Destination "$standard/Licenses/InnoSetup-LICENSE.txt"
    & $IsccPath "/DMyAppVersion=$version" "/DSourceDir=$standard" "/DOutputDir=$release" installer/Zipora.iss
    if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }

    Publish-App $true (Join-Path $portable 'App')
    Add-Licenses $portable
    New-Item -ItemType Directory -Path "$portable/Data/Dictionaries" -Force | Out-Null
    '{"portable":true}' | Set-Content "$portable/portable.json" -Encoding ascii
    @'
@echo off
start "" "%~dp0App\Zipora.exe" %*
'@ | Set-Content "$portable/Start-Zipora.cmd" -Encoding ascii
    @'
# Zipora Portable
完整解压后运行 Start-Zipora.cmd 或 App/Zipora.exe，不需要安装 .NET。
设置、背景、字典与密码库保存在 Data；升级请保留整个 Data。
常用密码库使用主密码保护，遗忘后无法恢复；不要用普通版的 DPAPI 密码库文件直接覆盖。
可将自己的 TXT 字典放入 Data/Dictionaries，或在字典破解窗口点击浏览。
退出程序并完成清理后再安全弹出 U 盘；便携版不注册系统右键菜单。
KeyFetch 查询遵守本地与服务端每日额度，便携版默认关闭自动贡献。
源码与更新：https://github.com/Alnitak44/Zipora
'@ | Set-Content "$portable/README-Usage.md" -Encoding utf8
    if (@(Get-ChildItem "$portable/Data" -Recurse -File).Count) { throw 'Portable package contains personal data.' }

    foreach ($pair in @(@($standard,"Zipora-v$version-win-x64.zip"),@($portable,"Zipora-v$version-win-x64-portable.zip"))) {
        Push-Location -LiteralPath $pair[0]
        try {
            & $engine a -tzip -mx5 (Join-Path $release $pair[1]) '.\*'
            if ($LASTEXITCODE -ne 0) { throw 'ZIP creation failed.' }
        } finally { Pop-Location }
        & $engine t (Join-Path $release $pair[1])
        if ($LASTEXITCODE -ne 0) { throw 'ZIP integrity test failed.' }
    }
    Get-ChildItem $release -File | Sort-Object Name | ForEach-Object {
        ((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $_.Name)
    } | Set-Content "$release/SHA256SUMS.txt" -Encoding ascii
    Write-Output "Release built: $release"
} finally { Pop-Location }
