#pragma once
#include <windows.h>

// x64 UI Automation bridge for a selected live Explorer element.
extern "C" __declspec(dllexport) HRESULT WINAPI BingLanTrayShowContextMenu(
    const int* runtimeId, int runtimeIdLength, DWORD expectedExplorerPid, HANDLE cancellation);
