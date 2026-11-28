#pragma once
#include <windows.h>
#include <unknwn.h>

// x64 UI Automation bridge for a selected live Explorer element.
extern "C" __declspec(dllexport) HRESULT WINAPI BingLanTrayShowContextMenu(
    const int* runtimeId, int runtimeIdLength, DWORD expectedExplorerPid, HANDLE cancellation);

// The caller owns a live MSAA button in the current COM apartment for the duration of the call.
extern "C" __declspec(dllexport) HRESULT WINAPI BingLanTrayShowAccessibleContextMenu(
    IUnknown* accessible, HWND expectedInputSite, DWORD expectedExplorerPid, HANDLE cancellation);
