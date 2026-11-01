#include <windows.h>
#include <shobjidl_core.h>
#include <shellapi.h>

#include <algorithm>
#include <atomic>
#include <cstring>
#include <cwctype>
#include <new>
#include <string>
#include <vector>

// Declared by the linker for the current DLL image.
extern "C" IMAGE_DOS_HEADER __ImageBase;

namespace
{

    // Keep this CLSID stable: it is the identity persisted in the registry.
    constexpr CLSID CLSID_ZiporaExplorerCommand =
        { 0xb53761d2, 0x18a8, 0x4c23, { 0xb6, 0xa6, 0x94, 0x8c, 0x2f, 0xbe, 0x8f, 0x4a } };

    enum class CommandKind { Root, Open, ExtractHere, ExtractDesktop, ExtractPick, Create };

    std::wstring AllocString(const wchar_t* value, LPWSTR* output)
    {
        if (output == nullptr) return {};
        const size_t bytes = (wcslen(value) + 1) * sizeof(wchar_t);
        auto* copy = static_cast<wchar_t*>(CoTaskMemAlloc(bytes));
        if (copy == nullptr) return {};
        memcpy(copy, value, bytes);
        *output = copy;
        return copy;
    }

    HRESULT CopyString(const wchar_t* value, LPWSTR* output)
    {
        if (output == nullptr) return E_POINTER;
        *output = nullptr;
        return AllocString(value, output).empty() ? E_OUTOFMEMORY : S_OK;
    }

    std::wstring ModuleDirectory()
    {
        wchar_t path[MAX_PATH]{};
        const DWORD length = GetModuleFileNameW(reinterpret_cast<HMODULE>(&__ImageBase), path, ARRAYSIZE(path));
        if (length == 0 || length >= ARRAYSIZE(path)) return {};
        std::wstring result(path, length);
        const auto slash = result.find_last_of(L"\\/");
        return slash == std::wstring::npos ? std::wstring{} : result.substr(0, slash);
    }

    std::wstring ZiporaExePath()
    {
        const auto directory = ModuleDirectory();
        return directory.empty() ? L"Zipora.exe" : directory + L"\\Zipora.exe";
    }

    std::wstring ZiporaIconLocation()
    {
        const auto directory = ModuleDirectory();
        if (!directory.empty())
        {
            const auto icon = directory + L"\\Zipora.ico";
            if (GetFileAttributesW(icon.c_str()) != INVALID_FILE_ATTRIBUTES)
                return icon + L",0";
        }

        // 发布包缺少独立 .ico 时仍可回退到 exe 的内嵌图标。
        return ZiporaExePath() + L",0";
    }

    std::wstring Quote(const std::wstring& value)
    {
        // Win32 command-line escaping for paths: quote backslashes before a
        // quote/end quote so CommandLineToArgvW reconstructs the exact value.
        std::wstring quoted = L"\"";
        size_t slashes = 0;
        for (wchar_t ch : value)
        {
            if (ch == L'\\') { ++slashes; continue; }
            if (ch == L'\"') quoted.append(slashes * 2 + 1, L'\\');
            else quoted.append(slashes, L'\\');
            slashes = 0;
            quoted += ch;
        }
        quoted.append(slashes * 2, L'\\');
        quoted += L'\"';
        return quoted;
    }

    std::vector<std::wstring> GetPaths(IShellItemArray* items)
    {
        std::vector<std::wstring> paths;
        if (items == nullptr) return paths;
        DWORD count = 0;
        if (FAILED(items->GetCount(&count))) return paths;
        for (DWORD index = 0; index < count; ++index)
        {
            IShellItem* item = nullptr;
            if (FAILED(items->GetItemAt(index, &item)) || item == nullptr) continue;
            PWSTR path = nullptr;
            if (SUCCEEDED(item->GetDisplayName(SIGDN_FILESYSPATH, &path)) && path != nullptr)
            {
                paths.emplace_back(path);
                CoTaskMemFree(path);
            }
            item->Release();
        }
        return paths;
    }

    bool IsArchive(const std::wstring& path)
    {
        const auto dot = path.find_last_of(L'.');
        if (dot == std::wstring::npos) return false;
        std::wstring extension = path.substr(dot);
        std::transform(extension.begin(), extension.end(), extension.begin(), towlower);
        static const wchar_t* extensions[] = { L".7z", L".zip", L".rar", L".tar", L".gz", L".tgz", L".bz2", L".xz", L".zst", L".lz", L".lzma", L".iso", L".cab", L".wim", L".zipx", L".jar", L".apk" };
        return std::any_of(std::begin(extensions), std::end(extensions), [&](const wchar_t* known) { return extension == known; });
    }

    bool Supports(IShellItemArray* items, CommandKind kind)
    {
        const auto paths = GetPaths(items);
        if (kind == CommandKind::Root) return !paths.empty();
        if (kind == CommandKind::Create) return !paths.empty();
        return paths.size() == 1 && IsArchive(paths.front());
    }

    const wchar_t* Title(CommandKind kind)
    {
        switch (kind)
        {
        case CommandKind::Root: return L"Zipora";
        case CommandKind::Open: return L"用 Zipora 打开";
        case CommandKind::ExtractHere: return L"解压到当前目录";
        case CommandKind::ExtractDesktop: return L"解压到桌面";
        case CommandKind::ExtractPick: return L"解压到…";
        case CommandKind::Create: return L"新建压缩包";
        }
        return L"Zipora";
    }

    GUID CanonicalName(CommandKind kind)
    {
        GUID guid = CLSID_ZiporaExplorerCommand;
        guid.Data1 += static_cast<unsigned long>(kind) + 1;
        return guid;
    }

    HRESULT Launch(CommandKind kind, IShellItemArray* items)
    {
        const auto paths = GetPaths(items);
        if (!Supports(items, kind)) return E_INVALIDARG;

        std::wstring arguments;
        switch (kind)
        {
        case CommandKind::Open: arguments = Quote(paths.front()); break;
        case CommandKind::ExtractHere: arguments = L"--extract-here " + Quote(paths.front()); break;
        case CommandKind::ExtractDesktop: arguments = L"--extract-desktop " + Quote(paths.front()); break;
        case CommandKind::ExtractPick: arguments = L"--extract-pick " + Quote(paths.front()); break;
        case CommandKind::Create:
            arguments = L"--create";
            for (const auto& path : paths) arguments += L" " + Quote(path);
            break;
        default: return E_NOTIMPL;
        }

        const auto exe = ZiporaExePath();
        if (GetFileAttributesW(exe.c_str()) == INVALID_FILE_ATTRIBUTES) return HRESULT_FROM_WIN32(ERROR_FILE_NOT_FOUND);
        SHELLEXECUTEINFOW info{ sizeof(info) };
        info.fMask = SEE_MASK_ASYNCOK;
        info.lpFile = exe.c_str();
        info.lpParameters = arguments.c_str();
        info.nShow = SW_SHOWNORMAL;
        return ShellExecuteExW(&info) ? S_OK : HRESULT_FROM_WIN32(GetLastError());
    }

    class ExplorerCommand;

    class CommandEnumerator final : public IEnumExplorerCommand
    {
    public:
        explicit CommandEnumerator(ULONG index = 0) : index_(index) {}

        IFACEMETHODIMP QueryInterface(REFIID iid, void** object) override
        {
            if (object == nullptr) return E_POINTER;
            *object = nullptr;
            if (iid == IID_IUnknown || iid == __uuidof(IEnumExplorerCommand))
            {
                *object = static_cast<IEnumExplorerCommand*>(this);
                AddRef();
                return S_OK;
            }
            return E_NOINTERFACE;
        }
        IFACEMETHODIMP_(ULONG) AddRef() override { return ++references_; }
        IFACEMETHODIMP_(ULONG) Release() override { const auto count = --references_; if (count == 0) delete this; return count; }
        IFACEMETHODIMP Next(ULONG count, IExplorerCommand** commands, ULONG* fetched) override;
        IFACEMETHODIMP Skip(ULONG count) override { index_ = std::min(index_ + count, 5ul); return index_ == 5 ? S_FALSE : S_OK; }
        IFACEMETHODIMP Reset() override { index_ = 0; return S_OK; }
        IFACEMETHODIMP Clone(IEnumExplorerCommand** clone) override
        {
            if (clone == nullptr) return E_POINTER;
            *clone = new (std::nothrow) CommandEnumerator(index_);
            return *clone == nullptr ? E_OUTOFMEMORY : S_OK;
        }

    private:
        std::atomic<ULONG> references_{ 1 };
        ULONG index_;
    };

    class ExplorerCommand final : public IExplorerCommand
    {
    public:
        explicit ExplorerCommand(CommandKind kind) : kind_(kind) {}

        IFACEMETHODIMP QueryInterface(REFIID iid, void** object) override
        {
            if (object == nullptr) return E_POINTER;
            *object = nullptr;
            if (iid == IID_IUnknown || iid == __uuidof(IExplorerCommand))
            {
                *object = static_cast<IExplorerCommand*>(this);
                AddRef();
                return S_OK;
            }
            return E_NOINTERFACE;
        }
        IFACEMETHODIMP_(ULONG) AddRef() override { return ++references_; }
        IFACEMETHODIMP_(ULONG) Release() override { const auto count = --references_; if (count == 0) delete this; return count; }
        IFACEMETHODIMP GetTitle(IShellItemArray*, LPWSTR* title) override { return CopyString(Title(kind_), title); }
        IFACEMETHODIMP GetIcon(IShellItemArray*, LPWSTR* icon) override
        {
            const auto location = ZiporaIconLocation();
            return CopyString(location.c_str(), icon);
        }
        IFACEMETHODIMP GetToolTip(IShellItemArray*, LPWSTR* tooltip) override { return CopyString(L"Zipora 压缩与解压", tooltip); }
        IFACEMETHODIMP GetCanonicalName(GUID* guid) override { if (guid == nullptr) return E_POINTER; *guid = CanonicalName(kind_); return S_OK; }
        IFACEMETHODIMP GetState(IShellItemArray* items, BOOL, EXPCMDSTATE* state) override
        {
            if (state == nullptr) return E_POINTER;
            *state = Supports(items, kind_) ? ECS_ENABLED : ECS_HIDDEN;
            return S_OK;
        }
        IFACEMETHODIMP Invoke(IShellItemArray* items, IBindCtx*) override { return Launch(kind_, items); }
        IFACEMETHODIMP GetFlags(EXPCMDFLAGS* flags) override
        {
            if (flags == nullptr) return E_POINTER;
            *flags = kind_ == CommandKind::Root ? ECF_HASSUBCOMMANDS : ECF_DEFAULT;
            return S_OK;
        }
        IFACEMETHODIMP EnumSubCommands(IEnumExplorerCommand** commands) override
        {
            if (commands == nullptr) return E_POINTER;
            *commands = nullptr;
            if (kind_ != CommandKind::Root) return E_NOTIMPL;
            *commands = new (std::nothrow) CommandEnumerator();
            return *commands == nullptr ? E_OUTOFMEMORY : S_OK;
        }

    private:
        std::atomic<ULONG> references_{ 1 };
        CommandKind kind_;
    };

    IFACEMETHODIMP CommandEnumerator::Next(ULONG count, IExplorerCommand** commands, ULONG* fetched)
    {
        if (commands == nullptr) return E_POINTER;
        if (count != 1 && fetched == nullptr) return E_POINTER;
        ULONG returned = 0;
        while (returned < count && index_ < 5)
        {
            commands[returned] = new (std::nothrow) ExplorerCommand(static_cast<CommandKind>(index_ + 1));
            if (commands[returned] == nullptr) break;
            ++returned;
            ++index_;
        }
        if (fetched != nullptr) *fetched = returned;
        return returned == count ? S_OK : S_FALSE;
    }

    class ClassFactory final : public IClassFactory
    {
    public:
        IFACEMETHODIMP QueryInterface(REFIID iid, void** object) override
        {
            if (object == nullptr) return E_POINTER;
            *object = nullptr;
            if (iid == IID_IUnknown || iid == IID_IClassFactory)
            {
                *object = static_cast<IClassFactory*>(this);
                AddRef();
                return S_OK;
            }
            return E_NOINTERFACE;
        }
        IFACEMETHODIMP_(ULONG) AddRef() override { return ++references_; }
        IFACEMETHODIMP_(ULONG) Release() override { return --references_; }
        IFACEMETHODIMP CreateInstance(IUnknown* outer, REFIID iid, void** object) override
        {
            if (outer != nullptr) return CLASS_E_NOAGGREGATION;
            auto* command = new (std::nothrow) ExplorerCommand(CommandKind::Root);
            if (command == nullptr) return E_OUTOFMEMORY;
            const auto result = command->QueryInterface(iid, object);
            command->Release();
            return result;
        }
        IFACEMETHODIMP LockServer(BOOL) override { return S_OK; }
    private:
        std::atomic<ULONG> references_{ 1 };
    };
}

// DLL 导出通过 ZiporaShellExtension.def 声明（新版 Windows SDK 的 combaseapi.h
// 已内联声明这些入口，__declspec(dllexport) 会与之冲突）。
extern "C" HRESULT WINAPI DllGetClassObject(REFCLSID clsid, REFIID iid, void** object)
{
    if (object == nullptr) return E_POINTER;
    *object = nullptr;
    if (clsid != CLSID_ZiporaExplorerCommand) return CLASS_E_CLASSNOTAVAILABLE;
    static ClassFactory factory;
    return factory.QueryInterface(iid, object);
}

extern "C" HRESULT WINAPI DllCanUnloadNow()
{
    // Explorer owns this process; retaining the tiny command module avoids
    // unload races while its menu is still displayed.
    return S_FALSE;
}
