// MarkForge Markdown preview handler for the Windows Explorer preview pane.
//
// This DLL is deliberately thin. Explorer loads preview handlers into its prevhost.exe
// surrogate, and running .NET inside that shared process is fragile (one CLR per process,
// clashes with other managed handlers). So all this does is implement the COM interfaces
// Explorer talks to and, on DoPreview, launch MarkForge.PreviewHost.exe (a .NET app that
// reuses MarkForge.Core's renderer) with the file path, the preview pane's window handle
// and the rectangle to fill. The host re-parents its own window into that pane; this side
// keeps it sized (SetRect) and shuts it down (Unload).

#include <windows.h>
#include <shlobj.h>
#include <shlwapi.h>
#include <shobjidl.h>
#include <strsafe.h>
#include <new>
#include <string>

#pragma comment(lib, "shlwapi.lib")

// {05E77F8A-F696-4E53-87D1-B4DA1DCA49D6}
static const CLSID CLSID_MarkForgePreviewHandler =
    { 0x05e77f8a, 0xf696, 0x4e53, { 0x87, 0xd1, 0xb4, 0xda, 0x1d, 0xca, 0x49, 0xd6 } };

static const wchar_t kClsidString[] = L"{05E77F8A-F696-4E53-87D1-B4DA1DCA49D6}";
static const wchar_t kHandlerName[] = L"MarkForge Markdown Preview Handler";
static const wchar_t kPreviewHandlerShellEx[] = L"{8895b1c6-b41f-4c1c-a562-0d564250836f}";
// prevhost.exe surrogate for 64-bit handlers.
static const wchar_t kPrevhostAppId[] = L"{6d2b5079-2f0b-48dd-ab7f-97cec514d30b}";
static const wchar_t kHostExeName[] = L"MarkForge.PreviewHost.exe";
static const wchar_t* const kExtensions[] = { L".md", L".markdown" };

static HMODULE g_module = nullptr;
static LONG g_objects = 0;
static LONG g_locks = 0;

// ---------------------------------------------------------------------------------------

class PreviewHandler final :
    public IInitializeWithFile,
    public IPreviewHandler,
    public IOleWindow,
    public IObjectWithSite,
    public IPreviewHandlerVisuals
{
public:
    PreviewHandler() { InterlockedIncrement(&g_objects); }

    // IUnknown
    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        *ppv = nullptr;
        if (riid == IID_IUnknown || riid == IID_IPreviewHandler) *ppv = static_cast<IPreviewHandler*>(this);
        else if (riid == IID_IInitializeWithFile) *ppv = static_cast<IInitializeWithFile*>(this);
        else if (riid == IID_IOleWindow) *ppv = static_cast<IOleWindow*>(this);
        else if (riid == IID_IObjectWithSite) *ppv = static_cast<IObjectWithSite*>(this);
        else if (riid == IID_IPreviewHandlerVisuals) *ppv = static_cast<IPreviewHandlerVisuals*>(this);
        else return E_NOINTERFACE;
        AddRef();
        return S_OK;
    }
    IFACEMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&m_refs); }
    IFACEMETHODIMP_(ULONG) Release() override
    {
        ULONG refs = InterlockedDecrement(&m_refs);
        if (refs == 0) delete this;
        return refs;
    }

    // IInitializeWithFile
    IFACEMETHODIMP Initialize(LPCWSTR path, DWORD) override
    {
        if (!path) return E_INVALIDARG;
        m_filePath = path;
        return S_OK;
    }

    // IPreviewHandler
    IFACEMETHODIMP SetWindow(HWND parent, const RECT* rect) override
    {
        if (!parent || !rect) return E_INVALIDARG;
        m_parent = parent;
        m_rect = *rect;
        PositionHostWindow();
        return S_OK;
    }

    IFACEMETHODIMP SetRect(const RECT* rect) override
    {
        if (!rect) return E_INVALIDARG;
        m_rect = *rect;
        PositionHostWindow();
        return S_OK;
    }

    IFACEMETHODIMP DoPreview() override
    {
        if (m_filePath.empty() || !m_parent) return E_UNEXPECTED;
        StopHost();

        wchar_t exePath[MAX_PATH];
        if (!GetModuleFileNameW(g_module, exePath, ARRAYSIZE(exePath))) return HRESULT_FROM_WIN32(GetLastError());
        PathRemoveFileSpecW(exePath);
        if (!PathAppendW(exePath, kHostExeName)) return E_FAIL;

        // "host.exe" "<file>" <hwnd> <left> <top> <right> <bottom>
        wchar_t args[128];
        StringCchPrintfW(args, ARRAYSIZE(args), L" %llu %ld %ld %ld %ld",
            static_cast<unsigned long long>(reinterpret_cast<ULONG_PTR>(m_parent)),
            m_rect.left, m_rect.top, m_rect.right, m_rect.bottom);

        std::wstring commandLine = L"\"";
        commandLine += exePath;
        commandLine += L"\" \"";
        commandLine += m_filePath;
        commandLine += L"\"";
        commandLine += args;

        STARTUPINFOW si = { sizeof(si) };
        PROCESS_INFORMATION pi = {};
        if (!CreateProcessW(exePath, commandLine.data(), nullptr, nullptr, FALSE, 0, nullptr, nullptr, &si, &pi))
            return HRESULT_FROM_WIN32(GetLastError());

        CloseHandle(pi.hThread);
        m_hostProcess = pi.hProcess;
        m_hostPid = pi.dwProcessId;
        return S_OK;
    }

    IFACEMETHODIMP Unload() override
    {
        StopHost();
        m_filePath.clear();
        return S_OK;
    }

    IFACEMETHODIMP SetFocus() override
    {
        if (HWND host = FindHostWindow()) ::SetFocus(host);
        return S_OK;
    }

    IFACEMETHODIMP QueryFocus(HWND* focused) override
    {
        if (!focused) return E_INVALIDARG;
        *focused = ::GetFocus();
        return *focused ? S_OK : HRESULT_FROM_WIN32(GetLastError());
    }

    IFACEMETHODIMP TranslateAccelerator(MSG* msg) override
    {
        // Give Explorer a chance at its own accelerators (Tab out of the pane, etc.).
        IPreviewHandlerFrame* frame = nullptr;
        HRESULT hr = S_FALSE;
        if (m_site && SUCCEEDED(m_site->QueryInterface(IID_PPV_ARGS(&frame))))
        {
            hr = frame->TranslateAccelerator(msg);
            frame->Release();
        }
        return hr;
    }

    // IOleWindow
    IFACEMETHODIMP GetWindow(HWND* hwnd) override
    {
        if (!hwnd) return E_INVALIDARG;
        *hwnd = m_parent;
        return m_parent ? S_OK : E_FAIL;
    }
    IFACEMETHODIMP ContextSensitiveHelp(BOOL) override { return E_NOTIMPL; }

    // IObjectWithSite
    IFACEMETHODIMP SetSite(IUnknown* site) override
    {
        if (m_site) { m_site->Release(); m_site = nullptr; }
        m_site = site;
        if (m_site) m_site->AddRef();
        return S_OK;
    }
    IFACEMETHODIMP GetSite(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        *ppv = nullptr;
        return m_site ? m_site->QueryInterface(riid, ppv) : E_FAIL;
    }

    // IPreviewHandlerVisuals — accepted but unused: the host follows the Windows light/dark
    // app setting (as Explorer itself does) rather than these colours.
    IFACEMETHODIMP SetBackgroundColor(COLORREF) override { return S_OK; }
    IFACEMETHODIMP SetFont(const LOGFONTW*) override { return S_OK; }
    IFACEMETHODIMP SetTextColor(COLORREF) override { return S_OK; }

private:
    ~PreviewHandler()
    {
        StopHost();
        if (m_site) m_site->Release();
        InterlockedDecrement(&g_objects);
    }

    struct FindContext { DWORD pid; HWND parent; HWND found; };

    // The host's window is a direct child of the pane, owned by the host's process
    // (WebView2's own windows beneath it belong to msedgewebview2 processes).
    HWND FindHostWindow() const
    {
        if (!m_parent || !m_hostPid) return nullptr;
        FindContext ctx = { m_hostPid, m_parent, nullptr };
        EnumChildWindows(m_parent, [](HWND hwnd, LPARAM lp) -> BOOL
        {
            auto* c = reinterpret_cast<FindContext*>(lp);
            DWORD pid = 0;
            GetWindowThreadProcessId(hwnd, &pid);
            if (pid == c->pid && GetParent(hwnd) == c->parent) { c->found = hwnd; return FALSE; }
            return TRUE;
        }, reinterpret_cast<LPARAM>(&ctx));
        return ctx.found;
    }

    void PositionHostWindow() const
    {
        if (HWND host = FindHostWindow())
        {
            SetWindowPos(host, nullptr, m_rect.left, m_rect.top,
                m_rect.right - m_rect.left, m_rect.bottom - m_rect.top,
                SWP_NOZORDER | SWP_NOACTIVATE);
        }
    }

    void StopHost()
    {
        if (!m_hostProcess) return;
        // Ask nicely first so WebView2 can tear down cleanly; don't let a hung host hold up
        // Explorer moving on to the next file.
        if (HWND host = FindHostWindow())
        {
            PostMessageW(host, WM_CLOSE, 0, 0);
            if (WaitForSingleObject(m_hostProcess, 500) == WAIT_TIMEOUT)
                TerminateProcess(m_hostProcess, 0);
        }
        else
        {
            TerminateProcess(m_hostProcess, 0);
        }
        CloseHandle(m_hostProcess);
        m_hostProcess = nullptr;
        m_hostPid = 0;
    }

    LONG m_refs = 1;
    std::wstring m_filePath;
    HWND m_parent = nullptr;
    RECT m_rect = {};
    IUnknown* m_site = nullptr;
    HANDLE m_hostProcess = nullptr;
    DWORD m_hostPid = 0;
};

// ---------------------------------------------------------------------------------------

class ClassFactory final : public IClassFactory
{
public:
    IFACEMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        if (riid == IID_IUnknown || riid == IID_IClassFactory)
        {
            *ppv = static_cast<IClassFactory*>(this);
            AddRef();
            return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }
    IFACEMETHODIMP_(ULONG) AddRef() override { return 2; }   // static instance
    IFACEMETHODIMP_(ULONG) Release() override { return 1; }

    IFACEMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** ppv) override
    {
        if (outer) return CLASS_E_NOAGGREGATION;
        auto* handler = new (std::nothrow) PreviewHandler();
        if (!handler) return E_OUTOFMEMORY;
        HRESULT hr = handler->QueryInterface(riid, ppv);
        handler->Release();
        return hr;
    }
    IFACEMETHODIMP LockServer(BOOL lock) override
    {
        if (lock) InterlockedIncrement(&g_locks); else InterlockedDecrement(&g_locks);
        return S_OK;
    }
};

static ClassFactory g_factory;

// ---------------------------------------------------------------------------------------
// Exports

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_module = module;
        DisableThreadLibraryCalls(module);
    }
    return TRUE;
}

STDAPI DllGetClassObject(REFCLSID clsid, REFIID riid, void** ppv)
{
    if (clsid != CLSID_MarkForgePreviewHandler) return CLASS_E_CLASSNOTAVAILABLE;
    return g_factory.QueryInterface(riid, ppv);
}

STDAPI DllCanUnloadNow()
{
    return (g_objects == 0 && g_locks == 0) ? S_OK : S_FALSE;
}

// regsvr32 (from an elevated prompt) registers machine-wide — the same keys installer.iss
// writes. It has to be HKLM: COM will not activate a per-user (HKCU) class inside the
// prevhost.exe surrogate (CoCreateInstance fails with REGDB_E_CLASSNOTREG), so a per-user
// registration is silently ignored by Explorer ("沒有預覽可用").

static HRESULT SetRegString(HKEY root, const std::wstring& subkey, const wchar_t* name, const std::wstring& value)
{
    LSTATUS s = RegSetKeyValueW(root, subkey.c_str(), name, REG_SZ, value.c_str(),
        static_cast<DWORD>((value.size() + 1) * sizeof(wchar_t)));
    return HRESULT_FROM_WIN32(s);
}

static HRESULT SetRegDword(HKEY root, const std::wstring& subkey, const wchar_t* name, DWORD value)
{
    return HRESULT_FROM_WIN32(RegSetKeyValueW(root, subkey.c_str(), name, REG_DWORD, &value, sizeof(value)));
}

STDAPI DllRegisterServer()
{
    wchar_t dllPath[MAX_PATH];
    if (!GetModuleFileNameW(g_module, dllPath, ARRAYSIZE(dllPath))) return HRESULT_FROM_WIN32(GetLastError());

    const std::wstring classes = L"Software\\Classes\\";
    const std::wstring clsidKey = classes + L"CLSID\\" + kClsidString;
    HRESULT hr = S_OK;
    if (SUCCEEDED(hr)) hr = SetRegString(HKEY_LOCAL_MACHINE, clsidKey, nullptr, kHandlerName);
    if (SUCCEEDED(hr)) hr = SetRegString(HKEY_LOCAL_MACHINE, clsidKey, L"DisplayName", kHandlerName);
    if (SUCCEEDED(hr)) hr = SetRegString(HKEY_LOCAL_MACHINE, clsidKey, L"AppID", kPrevhostAppId);
    // The host needs WebView2, which doesn't run in a low-integrity process.
    if (SUCCEEDED(hr)) hr = SetRegDword(HKEY_LOCAL_MACHINE, clsidKey, L"DisableLowILProcessIsolation", 1);
    if (SUCCEEDED(hr)) hr = SetRegString(HKEY_LOCAL_MACHINE, clsidKey + L"\\InprocServer32", nullptr, dllPath);
    if (SUCCEEDED(hr)) hr = SetRegString(HKEY_LOCAL_MACHINE, clsidKey + L"\\InprocServer32", L"ThreadingModel", L"Apartment");
    for (const wchar_t* ext : kExtensions)
    {
        if (SUCCEEDED(hr)) hr = SetRegString(HKEY_LOCAL_MACHINE, classes + ext + L"\\ShellEx\\" + kPreviewHandlerShellEx, nullptr, kClsidString);
    }
    if (SUCCEEDED(hr))
        hr = SetRegString(HKEY_LOCAL_MACHINE, L"Software\\Microsoft\\Windows\\CurrentVersion\\PreviewHandlers", kClsidString, kHandlerName);
    SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, nullptr, nullptr);
    return hr;
}

STDAPI DllUnregisterServer()
{
    const std::wstring classes = L"Software\\Classes\\";
    for (const wchar_t* ext : kExtensions)
    {
        // Only remove the association if it's still ours (another app may have taken it).
        std::wstring key = classes + ext + L"\\ShellEx\\" + kPreviewHandlerShellEx;
        wchar_t current[64] = {};
        DWORD size = sizeof(current);
        if (RegGetValueW(HKEY_LOCAL_MACHINE, key.c_str(), nullptr, RRF_RT_REG_SZ, nullptr, current, &size) == ERROR_SUCCESS
            && _wcsicmp(current, kClsidString) == 0)
        {
            RegDeleteTreeW(HKEY_LOCAL_MACHINE, key.c_str());
        }
    }
    RegDeleteTreeW(HKEY_LOCAL_MACHINE, (classes + L"CLSID\\" + kClsidString).c_str());
    RegDeleteKeyValueW(HKEY_LOCAL_MACHINE, L"Software\\Microsoft\\Windows\\CurrentVersion\\PreviewHandlers", kClsidString);
    SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, nullptr, nullptr);
    return S_OK;
}
