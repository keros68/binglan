#include <windows.h>
#include <ocidl.h>
#include <xamlom.h>
#undef GetCurrentTime
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.UI.Xaml.h>
#include <winrt/Windows.UI.Xaml.Media.h>
#include <memory>
#include <mutex>
#include <string>
#include <unordered_map>

using namespace winrt;
using namespace Windows::UI::Xaml;

namespace
{
    constexpr GUID TapClass = { 0x561329cb, 0x595c, 0x4cd3, { 0xa8, 0x4f, 0x22, 0xa7, 0xe2, 0x91, 0x44, 0x81 } };
    constexpr LONG Protocol = 0x424c0001;
    constexpr ULONGLONG LeaseMilliseconds = 5000;
    constexpr ULONGLONG RestoreWaitTimeoutMilliseconds = 3000;
    constexpr ULONGLONG PostUnadviseWaitTimeoutMilliseconds = 500;

    ULONGLONG UnbiasedNowMilliseconds() noexcept
    {
        ULONGLONG unbiasedTime = 0;
        if (QueryUnbiasedInterruptTime(&unbiasedTime))
        {
            return unbiasedTime / 10000ULL;
        }
        return GetTickCount64();
    }

    // Fixed ABI shared with TaskbarXamlSession. No paths or executable commands cross it.
    struct alignas(8) State
    {
        LONG protocol;
        DWORD owner;
        volatile LONG requested;
        volatile LONG activeTargets;
        volatile LONG stopped;
        volatile LONG error;
        volatile LONG64 heartbeat;
    };
    static_assert(sizeof(State) == 32);

    struct Session
    {
        HANDLE mapping = nullptr;
        HANDLE owner = nullptr;
        State* state = nullptr;

        ~Session()
        {
            if (owner) CloseHandle(owner);
            if (state) UnmapViewOfFile(state);
            if (mapping) CloseHandle(mapping);
        }

        bool IsRequested() const noexcept
        {
            if (!state || !owner || WaitForSingleObject(owner, 0) != WAIT_TIMEOUT) return false;
            const auto beat = static_cast<ULONGLONG>(InterlockedCompareExchange64(&state->heartbeat, 0, 0));
            const auto now = UnbiasedNowMilliseconds();
            const auto delta = now >= beat ? now - beat : beat - now;
            return InterlockedCompareExchange(&state->requested, 0, 0) == 1
                && delta < LeaseMilliseconds;
        }

        void Fail(HRESULT result) noexcept
        {
            if (state)
            {
                InterlockedCompareExchange(&state->error, result, S_OK);
                InterlockedExchange(&state->requested, 0);
            }
        }
    };

    struct Target : std::enable_shared_from_this<Target>
    {
        std::shared_ptr<Session> session;
        FrameworkElement element{ nullptr };
        Windows::Foundation::IInspectable original{ nullptr };
        DispatcherTimer timer{ nullptr };
        event_token tick{};
        bool wasUnset = false;
        bool applied = false;
        bool finished = false;
        int restoreAttempts = 0;
        volatile LONG removed = 0;

        void Restore() noexcept
        {
            if (finished) return;
            try
            {
                // Another writer's nonzero opacity wins. Our only written value is zero.
                if (applied && element.Opacity() == 0)
                {
                    if (wasUnset) element.ClearValue(UIElement::OpacityProperty());
                    else element.SetValue(UIElement::OpacityProperty(), original);
                }
                Finish();
            }
            catch (...)
            {
                session->Fail(to_hresult());
                // A disconnected/invalid XAML object must not cause an endless UI retry.
                if (++restoreAttempts >= 3)
                {
                    InterlockedExchange(&session->state->stopped, 2);
                    Finish();
                }
            }
        }

        void Finish() noexcept
        {
            finished = true;
            try { timer.Stop(); timer.Tick(tick); }
            catch (...)
            {
                session->Fail(to_hresult());
                InterlockedExchange(&session->state->stopped, 2);
            }
            if (applied)
            {
                applied = false;
                InterlockedDecrement(&session->state->activeTargets);
            }
        }

        void Start()
        {
            original = element.ReadLocalValue(UIElement::OpacityProperty());
            wasUnset = original == DependencyProperty::UnsetValue();
            timer = DispatcherTimer();
            timer.Interval(std::chrono::milliseconds(500));
            auto self = shared_from_this();
            tick = timer.Tick([self](auto const&, auto const&) noexcept {
                if (!self->session->IsRequested() || InterlockedCompareExchange(&self->removed, 0, 0))
                {
                    self->Restore();
                    return;
                }
                try
                {
                    // A theme transition may rewrite this value. No brushes, resources,
                    // styles or input properties are replaced.
                    if (self->element.Opacity() != 0) self->element.Opacity(0);
                }
                catch (...) { self->session->Fail(to_hresult()); self->Restore(); }
            });
            // Establish rollback before the first write.
            try
            {
                timer.Start();
                applied = true;
                InterlockedIncrement(&session->state->activeTargets);
                element.Opacity(0);
            }
            catch (...) { session->Fail(to_hresult()); Restore(); }
        }
    };

    struct Tap : implements<Tap, IObjectWithSite, IVisualTreeServiceCallback2>
    {
        com_ptr<IXamlDiagnostics> diagnostics;
        com_ptr<IVisualTreeService> tree;
        std::shared_ptr<Session> session;
        std::mutex siteMutex;
        std::mutex targetsMutex;
        std::unordered_map<InstanceHandle, std::weak_ptr<Target>> targets;

        HRESULT __stdcall SetSite(IUnknown* site) noexcept override
        {
            std::scoped_lock siteLock(siteMutex);
            try
            {
                if (!site)
                {
                    if (session && session->state) InterlockedExchange(&session->state->requested, 0);
                    return S_OK;
                }
                if (session) return E_UNEXPECTED;
                check_hresult(site->QueryInterface(diagnostics.put()));
                tree = diagnostics.as<IVisualTreeService>();
                BSTR data = nullptr;
                check_hresult(diagnostics->GetInitializationData(&data));
                const std::wstring name = data ? data : L"";
                SysFreeString(data);
                if (!name.starts_with(L"Local\\BingLan.Taskbar.Xaml.") || name.size() > 100) return E_INVALIDARG;
                session = std::make_shared<Session>();
                session->mapping = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, name.c_str());
                if (!session->mapping) return HRESULT_FROM_WIN32(GetLastError());
                session->state = static_cast<State*>(MapViewOfFile(session->mapping, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, sizeof(State)));
                if (!session->state) return HRESULT_FROM_WIN32(GetLastError());
                if (session->state->protocol != Protocol) return E_INVALIDARG;
                session->owner = OpenProcess(SYNCHRONIZE, FALSE, session->state->owner);
                if (!session->IsRequested()) return HRESULT_FROM_WIN32(ERROR_CANCELLED);

                // Diagnostics may retain the DLL after detaching. Pin one small image per
                // application version for Explorer's lifetime, avoiding unload races with
                // COM callbacks. There are no timers or workers after a session stops.
                HMODULE pinned = nullptr;
                if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN,
                    reinterpret_cast<LPCWSTR>(&TapClass), &pinned)) return HRESULT_FROM_WIN32(GetLastError());
                auto self = new com_ptr<Tap>(get_strong());
                HANDLE thread = CreateThread(nullptr, 0, [](void* context) -> DWORD {
                    std::unique_ptr<com_ptr<Tap>> holder(static_cast<com_ptr<Tap>*>(context));
                    auto tap = *holder;
                    bool advised = false;
                    const auto apartment = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
                    const auto waitForTargets = [&](ULONGLONG timeoutMs) noexcept {
                        const auto start = UnbiasedNowMilliseconds();
                        while (InterlockedCompareExchange(&tap->session->state->activeTargets, 0, 0) > 0
                            && UnbiasedNowMilliseconds() - start < timeoutMs)
                        {
                            Sleep(100);
                        }
                    };
                    try
                    {
                        check_hresult(apartment);
                        check_hresult(tap->tree->AdviseVisualTreeChange(tap.as<IVisualTreeServiceCallback2>().get()));
                        advised = true;
                        while (tap->session->IsRequested()) Sleep(250);
                        // Each XAML thread restores its own objects; never access them from
                        // this worker. Bound the wait so a destroyed monitor island cannot
                        // leave this Explorer worker spinning forever before Unadvise.
                        waitForTargets(RestoreWaitTimeoutMilliseconds);
                    }
                    catch (...) { tap->session->Fail(to_hresult()); }
                    if (advised)
                    {
                        const auto hr = tap->tree->UnadviseVisualTreeChange(tap.as<IVisualTreeServiceCallback2>().get());
                        if (FAILED(hr)) tap->session->Fail(hr);
                    }
                    waitForTargets(PostUnadviseWaitTimeoutMilliseconds);
                    InterlockedCompareExchange(&tap->session->state->stopped, 1, 0);
                    {
                        std::scoped_lock siteLock(tap->siteMutex);
                        std::scoped_lock targetsLock(tap->targetsMutex);
                        tap->targets.clear();
                        tap->tree = nullptr;
                        tap->diagnostics = nullptr;
                        tap->session.reset();
                    }
                    if (SUCCEEDED(apartment)) CoUninitialize();
                    return 0;
                }, self, 0, nullptr);
                if (!thread)
                {
                    const auto error = HRESULT_FROM_WIN32(GetLastError());
                    delete self;
                    session->Fail(error);
                    return error;
                }
                CloseHandle(thread);
                return S_OK;
            }
            catch (...) { if (session) session->Fail(to_hresult()); return to_hresult(); }
        }

        HRESULT __stdcall GetSite(REFIID iid, void** object) noexcept override
        {
            std::scoped_lock siteLock(siteMutex);
            if (!object) return E_POINTER;
            *object = nullptr;
            return diagnostics ? diagnostics->QueryInterface(iid, object) : E_FAIL;
        }

        HRESULT __stdcall OnVisualTreeChange(ParentChildRelation, VisualElement element, VisualMutationType mutation) noexcept override
        {
            std::scoped_lock siteLock(siteMutex);
            if (!session) return S_OK;
            try
            {
                if (mutation == Remove)
                {
                    std::scoped_lock lock(targetsMutex);
                    const auto found = targets.find(element.Handle);
                    if (found != targets.end())
                    {
                        if (auto target = found->second.lock()) InterlockedExchange(&target->removed, 1);
                        targets.erase(found);
                    }
                    return S_OK;
                }
                if (!session->IsRequested() || !element.Type || !element.Name
                    || std::wstring_view(element.Type) != L"Taskbar.TaskbarBackground"
                    || std::wstring_view(element.Name) != L"BackgroundControl") return S_OK;
                com_ptr<::IInspectable> object;
                check_hresult(diagnostics->GetIInspectableFromHandle(element.Handle, object.put()));
                auto background = object.try_as<FrameworkElement>();
                if (!background) return S_OK;
                auto parent = Media::VisualTreeHelper::GetParent(background);
                bool taskbarFrame = false;
                for (int i = 0; parent && i < 3; ++i)
                {
                    if (get_class_name(parent) == L"Taskbar.TaskbarFrame") { taskbarFrame = true; break; }
                    parent = Media::VisualTreeHelper::GetParent(parent);
                }
                if (!taskbarFrame) return S_OK;
                std::scoped_lock lock(targetsMutex);
                if (auto existing = targets[element.Handle].lock()) return S_OK;
                auto target = std::make_shared<Target>();
                target->session = session;
                target->element = background;
                target->Start();
                targets[element.Handle] = target;
            }
            catch (...) { session->Fail(to_hresult()); }
            return S_OK;
        }

        HRESULT __stdcall OnElementStateChanged(InstanceHandle, VisualElementState, LPCWSTR) noexcept override { return S_OK; }
    };

    struct Factory : implements<Factory, IClassFactory>
    {
        HRESULT __stdcall CreateInstance(IUnknown* outer, REFIID iid, void** object) noexcept override
        {
            if (!object) return E_POINTER;
            *object = nullptr;
            if (outer) return CLASS_E_NOAGGREGATION;
            try { return make_self<Tap>()->QueryInterface(iid, object); }
            catch (...) { return to_hresult(); }
        }
        HRESULT __stdcall LockServer(BOOL) noexcept override { return S_OK; }
    };
}

HRESULT __stdcall DllGetClassObject(REFCLSID clsid, REFIID iid, void** object)
{
    if (!object) return E_POINTER;
    *object = nullptr;
    if (clsid != TapClass) return CLASS_E_CLASSNOTAVAILABLE;
    try { return make_self<Factory>()->QueryInterface(iid, object); }
    catch (...) { return to_hresult(); }
}

HRESULT __stdcall DllCanUnloadNow() { return get_module_lock() ? S_FALSE : S_OK; }

extern "C" __declspec(dllexport) HRESULT __stdcall BingLanConnect(LPCWSTR mappingName) noexcept
{
    try
    {
        if (!mappingName) return E_INVALIDARG;
        auto taskbar = FindWindowW(L"Shell_TrayWnd", nullptr);
        DWORD processId = 0;
        GetWindowThreadProcessId(taskbar, &processId);
        if (!processId) return HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
        auto process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, processId);
        if (!process) return HRESULT_FROM_WIN32(GetLastError());
        wchar_t processPath[MAX_PATH]{};
        DWORD length = MAX_PATH;
        const bool queried = QueryFullProcessImageNameW(process, 0, processPath, &length) != FALSE;
        CloseHandle(process);
        wchar_t windows[MAX_PATH]{};
        GetWindowsDirectoryW(windows, MAX_PATH);
        if (!queried || _wcsicmp(processPath, (std::wstring(windows) + L"\\explorer.exe").c_str())) return E_ACCESSDENIED;
        HMODULE self = nullptr;
        if (!GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            reinterpret_cast<LPCWSTR>(&TapClass), &self)) return HRESULT_FROM_WIN32(GetLastError());
        wchar_t dllPath[32768]{};
        if (!GetModuleFileNameW(self, dllPath, ARRAYSIZE(dllPath))) return HRESULT_FROM_WIN32(GetLastError());
        // A packaged launcher can redirect AppData writes. Explorer needs the actual
        // file path, not the launcher's virtual view of the component cache.
        const auto file = CreateFileW(dllPath, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_DELETE,
            nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE) return HRESULT_FROM_WIN32(GetLastError());
        wchar_t physicalPath[32768]{};
        const auto pathLength = GetFinalPathNameByHandleW(file, physicalPath, ARRAYSIZE(physicalPath), FILE_NAME_NORMALIZED);
        const auto pathError = GetLastError();
        CloseHandle(file);
        if (!pathLength) return HRESULT_FROM_WIN32(pathError);
        if (pathLength >= ARRAYSIZE(physicalPath)) return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);
        auto xaml = LoadLibraryExW(L"Windows.UI.Xaml.dll", nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
        if (!xaml) return HRESULT_FROM_WIN32(GetLastError());
        auto initialize = reinterpret_cast<decltype(&InitializeXamlDiagnosticsEx)>(GetProcAddress(xaml, "InitializeXamlDiagnosticsEx"));
        HRESULT result = E_NOINTERFACE;
        if (initialize)
        {
            for (int i = 1; i <= 5; ++i)
            {
                const auto endpoint = L"VisualDiagConnection" + std::to_wstring(i);
                result = initialize(endpoint.c_str(), processId, L"", physicalPath, TapClass, mappingName);
                if (result != HRESULT_FROM_WIN32(ERROR_NOT_FOUND)) break;
            }
        }
        FreeLibrary(xaml);
        return result;
    }
    catch (...) { return to_hresult(); }
}
