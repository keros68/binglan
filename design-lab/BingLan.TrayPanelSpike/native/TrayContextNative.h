#pragma once
#include <windows.h>

// Experimental x64 UIA bridge for a selected live Explorer element.
extern "C" __declspec(dllexport) HRESULT WINAPI TraySpikeShowContextMenu(
    const int* runtimeId, int runtimeIdLength, DWORD expectedExplorerPid, HANDLE cancellation);
