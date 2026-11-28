#define WIN32_LEAN_AND_MEAN
#include "TrayContextNative.h"
#include <objbase.h>
#include <oleacc.h>
#include <oleauto.h>
#include <UIAutomation.h>
#include <wrl/client.h>
#include <algorithm>
#include <new>
#include <string>
#include <vector>

using Microsoft::WRL::ComPtr;

namespace
{
    constexpr int MaximumRoots = 64;
    constexpr int MaximumNodes = 4096;
    constexpr int MaximumDepth = 32;

    std::wstring ClassName(HWND window)
    {
        wchar_t text[256]{};
        GetClassNameW(window, text, ARRAYSIZE(text));
        return text;
    }

    struct SurfaceRoots
    {
        DWORD explorerPid;
        HWND windows[MaximumRoots]{};
        int count = 0;
        HRESULT status = S_OK;

        void Add(HWND window)
        {
            if (std::find(windows, windows + count, window) != windows + count) return;
            if (count == MaximumRoots) { status = HRESULT_FROM_WIN32(ERROR_MORE_DATA); return; }
            windows[count++] = window;
        }
    };

    BOOL CALLBACK CollectInputSite(HWND window, LPARAM parameter)
    {
        auto& roots = *reinterpret_cast<SurfaceRoots*>(parameter);
        try
        {
            DWORD processId = 0;
            GetWindowThreadProcessId(window, &processId);
            if (processId == roots.explorerPid && ClassName(window) == L"Windows.UI.Input.InputSite.WindowClass") roots.Add(window);
            return SUCCEEDED(roots.status);
        }
        catch (...) { roots.status = E_OUTOFMEMORY; return FALSE; }
    }

    BOOL CALLBACK CollectSurface(HWND window, LPARAM parameter)
    {
        auto& roots = *reinterpret_cast<SurfaceRoots*>(parameter);
        try
        {
            DWORD processId = 0;
            GetWindowThreadProcessId(window, &processId);
            if (processId != roots.explorerPid) return TRUE;
            const auto name = ClassName(window);
            if (name != L"Shell_TrayWnd" && name != L"Shell_SecondaryTrayWnd" &&
                name != L"TopLevelWindowForOverflowXamlIsland" && name != L"NotifyIconOverflowWindow") return TRUE;
            roots.Add(window);
            EnumChildWindows(window, CollectInputSite, parameter);
            return SUCCEEDED(roots.status);
        }
        catch (...) { roots.status = E_OUTOFMEMORY; return FALSE; }
    }

    struct Bstr
    {
        BSTR value = nullptr;
        ~Bstr() { SysFreeString(value); }
        bool Equals(const wchar_t* expected) const { return value && wcscmp(value, expected) == 0; }
    };

    struct PropertyValue
    {
        VARIANT value{};
        ~PropertyValue() { VariantClear(&value); }

        bool TextEquals(const wchar_t* expected) const
        {
            return value.vt == VT_BSTR && value.bstrVal && wcscmp(value.bstrVal, expected) == 0;
        }
    };

    bool IsExplorerInputSite(HWND inputSite, DWORD expectedExplorerPid)
    {
        DWORD explorerPid = 0;
        DWORD inputPid = 0;
        const HWND taskbar = FindWindowW(L"Shell_TrayWnd", nullptr);
        if (!taskbar || !GetWindowThreadProcessId(taskbar, &explorerPid) || explorerPid != expectedExplorerPid ||
            !GetWindowThreadProcessId(inputSite, &inputPid) || inputPid != explorerPid ||
            ClassName(inputSite) != L"Windows.UI.Input.InputSite.WindowClass")
        {
            return false;
        }

        const auto root = ClassName(GetAncestor(inputSite, GA_ROOT));
        return root == L"Shell_TrayWnd" || root == L"Shell_SecondaryTrayWnd" ||
            root == L"TopLevelWindowForOverflowXamlIsland" || root == L"NotifyIconOverflowWindow";
    }

    HRESULT ShowAccessibleContextMenu(
        IUnknown* accessible, HWND expectedInputSite, DWORD expectedExplorerPid, HANDLE cancellation)
    {
        if (WaitForSingleObject(cancellation, 0) != WAIT_TIMEOUT) return HRESULT_FROM_WIN32(ERROR_CANCELLED);
        if (!IsExplorerInputSite(expectedInputSite, expectedExplorerPid)) return UIA_E_ELEMENTNOTAVAILABLE;

        ComPtr<IAccessible> button;
        HRESULT status = accessible->QueryInterface(IID_PPV_ARGS(&button));
        if (FAILED(status)) return status;
        HWND inputSite = nullptr;
        status = WindowFromAccessibleObject(button.Get(), &inputSite);
        if (FAILED(status)) return status;
        if (inputSite != expectedInputSite) return UIA_E_ELEMENTNOTAVAILABLE;

        VARIANT self{};
        self.vt = VT_I4;
        self.lVal = CHILDID_SELF;
        PropertyValue role;
        status = button->get_accRole(self, &role.value);
        if (FAILED(status)) return status;
        if (role.value.vt != VT_I4 || role.value.lVal != ROLE_SYSTEM_PUSHBUTTON)
            return UIA_E_ELEMENTNOTAVAILABLE;

        ComPtr<IRawElementProviderSimple> provider;
        status = UiaProviderFromIAccessible(button.Get(), CHILDID_SELF, UIA_PFIA_UNWRAP_BRIDGE, &provider);
        if (FAILED(status)) return status;
        if (!provider) return E_UNEXPECTED;

        PropertyValue automationId, className, controlType, processId, enabled;
        if (FAILED(status = provider->GetPropertyValue(UIA_AutomationIdPropertyId, &automationId.value)) ||
            FAILED(status = provider->GetPropertyValue(UIA_ClassNamePropertyId, &className.value)) ||
            FAILED(status = provider->GetPropertyValue(UIA_ControlTypePropertyId, &controlType.value)) ||
            FAILED(status = provider->GetPropertyValue(UIA_ProcessIdPropertyId, &processId.value)) ||
            FAILED(status = provider->GetPropertyValue(UIA_IsEnabledPropertyId, &enabled.value)))
        {
            return status;
        }
        if (!automationId.TextEquals(L"NotifyItemIcon") ||
            (!className.TextEquals(L"SystemTray.NormalButton") && !className.TextEquals(L"SystemTray.NotifyIconView")) ||
            controlType.value.vt != VT_I4 || controlType.value.lVal != UIA_ButtonControlTypeId ||
            (processId.value.vt != VT_EMPTY &&
                (processId.value.vt != VT_I4 || static_cast<DWORD>(processId.value.lVal) != expectedExplorerPid)))
        {
            return UIA_E_ELEMENTNOTAVAILABLE;
        }
        if (enabled.value.vt != VT_BOOL || enabled.value.boolVal != VARIANT_TRUE)
            return UIA_E_ELEMENTNOTENABLED;

        ComPtr<IRawElementProviderSimple2> contextMenu;
        status = provider.As(&contextMenu);
        if (FAILED(status)) return status;
        if (!IsExplorerInputSite(expectedInputSite, expectedExplorerPid)) return UIA_E_ELEMENTNOTAVAILABLE;
        if (WaitForSingleObject(cancellation, 0) != WAIT_TIMEOUT) return HRESULT_FROM_WIN32(ERROR_CANCELLED);
        return contextMenu->ShowContextMenu();
    }

    HRESULT RuntimeIdEquals(IUIAutomationElement* element, const int* expected, int length, bool& equal)
    {
        equal = false;
        SAFEARRAY* actual = nullptr;
        HRESULT status = element->GetRuntimeId(&actual);
        if (FAILED(status)) return status;
        if (!actual) return E_UNEXPECTED;
        LONG lower = 0, upper = -1;
        if (SafeArrayGetDim(actual) != 1 ||
            FAILED(SafeArrayGetLBound(actual, 1, &lower)) || FAILED(SafeArrayGetUBound(actual, 1, &upper)))
        {
            SafeArrayDestroy(actual);
            return E_UNEXPECTED;
        }
        VARTYPE type = VT_EMPTY;
        status = SafeArrayGetVartype(actual, &type);
        if (SUCCEEDED(status) && type != VT_I4) status = E_UNEXPECTED;
        if (SUCCEEDED(status) && static_cast<long long>(upper) - lower + 1 == length)
        {
            int* values = nullptr;
            status = SafeArrayAccessData(actual, reinterpret_cast<void**>(&values));
            if (SUCCEEDED(status))
            {
                equal = std::equal(expected, expected + length, values);
                SafeArrayUnaccessData(actual);
            }
        }
        SafeArrayDestroy(actual);
        return status;
    }

    struct Search
    {
        IUIAutomation* automation;
        IUIAutomationTreeWalker* walker;
        const int* runtimeId;
        int runtimeIdLength;
        DWORD explorerPid;
        HANDLE cancellation;
        int visited = 0;
        std::vector<ComPtr<IUIAutomationElement>> matches;

        HRESULT Visit(IUIAutomationElement* node, int depth)
        {
            if (WaitForSingleObject(cancellation, 0) != WAIT_TIMEOUT) return HRESULT_FROM_WIN32(ERROR_CANCELLED);
            // Bounded one-shot experiment. A larger tree needs a revised scan strategy; never act on a partial scan.
            if (depth > MaximumDepth || ++visited > MaximumNodes) return HRESULT_FROM_WIN32(ERROR_MORE_DATA);
            Bstr id;
            HRESULT status = node->get_CurrentAutomationId(&id.value);
            if (FAILED(status)) return status;
            if (id.Equals(L"NotifyItemIcon"))
            {
                Bstr name;
                status = node->get_CurrentClassName(&name.value);
                if (FAILED(status)) return status;
                if (name.Equals(L"SystemTray.NotifyIconView") || name.Equals(L"SystemTray.NormalButton"))
                {
                    int processId = 0;
                    status = node->get_CurrentProcessId(&processId);
                    if (FAILED(status)) return status;
                    bool equal = false;
                    if (static_cast<DWORD>(processId) == explorerPid)
                    {
                        status = RuntimeIdEquals(node, runtimeId, runtimeIdLength, equal);
                        if (FAILED(status)) return status;
                    }
                    if (equal)
                    {
                        bool duplicate = false;
                        for (const auto& match : matches)
                        {
                            BOOL same = FALSE;
                            status = automation->CompareElements(match.Get(), node, &same);
                            if (FAILED(status)) return status;
                            if (same) { duplicate = true; break; }
                        }
                        if (!duplicate) matches.emplace_back(node);
                    }
                }
            }
            ComPtr<IUIAutomationElement> child;
            status = walker->GetFirstChildElement(node, &child);
            if (FAILED(status)) return status;
            while (child)
            {
                status = Visit(child.Get(), depth + 1);
                if (FAILED(status)) return status;
                ComPtr<IUIAutomationElement> next;
                status = walker->GetNextSiblingElement(child.Get(), &next);
                if (FAILED(status)) return status;
                child = next;
            }
            return S_OK;
        }
    };

    HRESULT ShowContextMenu(const int* runtimeId, int runtimeIdLength, DWORD expectedExplorerPid, HANDLE cancellation)
    {
        DWORD explorerPid = 0;
        const HWND taskbar = FindWindowW(L"Shell_TrayWnd", nullptr);
        if (!taskbar || !GetWindowThreadProcessId(taskbar, &explorerPid) || explorerPid != expectedExplorerPid)
            return UIA_E_ELEMENTNOTAVAILABLE;
        SurfaceRoots roots{ explorerPid };
        EnumWindows(CollectSurface, reinterpret_cast<LPARAM>(&roots));
        if (FAILED(roots.status)) return roots.status;

        ComPtr<IUIAutomation> automation;
        HRESULT status = CoCreateInstance(__uuidof(CUIAutomation8), nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&automation));
        if (FAILED(status)) return status;
        ComPtr<IUIAutomation2> automation2;
        status = automation.As(&automation2);
        if (FAILED(status)) return status;
        if (FAILED(status = automation2->put_ConnectionTimeout(1500))) return status;
        if (FAILED(status = automation2->put_TransactionTimeout(2000))) return status;
        ComPtr<IUIAutomationTreeWalker> walker;
        if (FAILED(status = automation->get_RawViewWalker(&walker))) return status;
        Search search{ automation.Get(), walker.Get(), runtimeId, runtimeIdLength, explorerPid, cancellation };
        for (int index = 0; index < roots.count; ++index)
        {
            DWORD processId = 0;
            GetWindowThreadProcessId(roots.windows[index], &processId);
            if (processId != explorerPid) return UIA_E_ELEMENTNOTAVAILABLE;
            ComPtr<IUIAutomationElement> root;
            status = automation->ElementFromHandle(roots.windows[index], &root);
            if (FAILED(status)) return status;
            status = search.Visit(root.Get(), 0);
            if (FAILED(status)) return status;
        }
        if (search.matches.empty()) return HRESULT_FROM_WIN32(ERROR_NOT_FOUND);
        if (search.matches.size() != 1) return HRESULT_FROM_WIN32(ERROR_DUP_NAME);
        BOOL enabled = FALSE;
        status = search.matches[0]->get_CurrentIsEnabled(&enabled);
        if (FAILED(status)) return status;
        if (!enabled) return UIA_E_ELEMENTNOTENABLED;
        ComPtr<IUIAutomationElement3> element3;
        status = search.matches[0].As(&element3);
        if (FAILED(status)) return status;
        DWORD currentExplorerPid = 0;
        const HWND currentTaskbar = FindWindowW(L"Shell_TrayWnd", nullptr);
        if (!currentTaskbar || !GetWindowThreadProcessId(currentTaskbar, &currentExplorerPid)
            || currentExplorerPid != expectedExplorerPid)
            return UIA_E_ELEMENTNOTAVAILABLE;
        if (WaitForSingleObject(cancellation, 0) != WAIT_TIMEOUT) return HRESULT_FROM_WIN32(ERROR_CANCELLED);
        return element3->ShowContextMenu();
    }
}

HRESULT WINAPI BingLanTrayShowContextMenu(const int* runtimeId, int runtimeIdLength, DWORD expectedExplorerPid, HANDLE cancellation)
{
    if (!runtimeId || runtimeIdLength < 1 || runtimeIdLength > 64 || !expectedExplorerPid || !cancellation) return E_INVALIDARG;
    const HRESULT initialized = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(initialized)) return initialized;
    HRESULT status;
    try { status = ShowContextMenu(runtimeId, runtimeIdLength, expectedExplorerPid, cancellation); }
    catch (const std::bad_alloc&) { status = E_OUTOFMEMORY; }
    catch (...) { status = E_FAIL; }
    CoUninitialize();
    return status;
}

HRESULT WINAPI BingLanTrayShowAccessibleContextMenu(
    IUnknown* accessible, HWND expectedInputSite, DWORD expectedExplorerPid, HANDLE cancellation)
{
    if (!accessible || !expectedInputSite || !expectedExplorerPid || !cancellation) return E_INVALIDARG;
    try { return ShowAccessibleContextMenu(accessible, expectedInputSite, expectedExplorerPid, cancellation); }
    catch (const std::bad_alloc&) { return E_OUTOFMEMORY; }
    catch (...) { return E_FAIL; }
}

