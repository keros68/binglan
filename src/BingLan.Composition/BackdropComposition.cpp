#include <windows.h>
#include <DispatcherQueue.h>
#include <windows.ui.composition.interop.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.Foundation.Numerics.h>
#include <winrt/Windows.System.h>
#include <winrt/Windows.UI.h>
#include <winrt/Windows.UI.Composition.h>
#include <winrt/Windows.UI.Composition.Desktop.h>
#include <cstdint>
#include <memory>

namespace
{
    using winrt::Windows::Foundation::Numerics::float2;
    using winrt::Windows::System::DispatcherQueue;
    using winrt::Windows::System::DispatcherQueueController;
    using winrt::Windows::UI::ColorHelper;
    using winrt::Windows::UI::Composition::CompositionColorBrush;
    using winrt::Windows::UI::Composition::CompositionRoundedRectangleGeometry;
    using winrt::Windows::UI::Composition::Compositor;
    using winrt::Windows::UI::Composition::ContainerVisual;
    using winrt::Windows::UI::Composition::ShapeVisual;
    using winrt::Windows::UI::Composition::SpriteVisual;
    using winrt::Windows::UI::Composition::Desktop::DesktopWindowTarget;

    // Thread-bound DispatcherQueue kept alive for the UI thread lifetime once created.
    thread_local DispatcherQueueController g_threadQueue{ nullptr };

    void EnsureDispatcherQueue()
    {
        if (DispatcherQueue::GetForCurrentThread() != nullptr)
        {
            return;
        }

        DispatcherQueueOptions options{};
        options.dwSize = sizeof(DispatcherQueueOptions);
        options.threadType = DQTYPE_THREAD_CURRENT;
        options.apartmentType = DQTAT_COM_STA;

        DispatcherQueueController controller{ nullptr };
        winrt::check_hresult(CreateDispatcherQueueController(
            options,
            reinterpret_cast<ABI::Windows::System::IDispatcherQueueController**>(winrt::put_abi(controller))));
        g_threadQueue = controller;
    }

    struct BackdropState
    {
        Compositor compositor{ nullptr };
        DesktopWindowTarget target{ nullptr };
        ContainerVisual root{ nullptr };
        SpriteVisual glass{ nullptr };
        ShapeVisual decoration{ nullptr };
        CompositionRoundedRectangleGeometry geometry{ nullptr };
        CompositionColorBrush tint{ nullptr };
        CompositionColorBrush stroke{ nullptr };

        void Close() noexcept
        {
            try
            {
                if (target) target.Root(nullptr);
                if (decoration) decoration.Close();
                if (glass) glass.Close();
                if (root) root.Close();
                if (geometry) geometry.Close();
                if (tint) tint.Close();
                if (stroke) stroke.Close();
                if (target) target.Close();
                if (compositor) compositor.Close();
            }
            catch (...)
            {
            }
        }
    };
}

extern "C" __declspec(dllexport) HRESULT __stdcall BingLanBackdrop_Attach(HWND hwnd, void** outState) noexcept
{
    if (outState == nullptr)
    {
        return E_POINTER;
    }
    *outState = nullptr;
    if (hwnd == nullptr)
    {
        return E_INVALIDARG;
    }

    try
    {
        EnsureDispatcherQueue();

        auto state = std::make_unique<BackdropState>();
        state->compositor = Compositor();

        auto interop = state->compositor.as<ABI::Windows::UI::Composition::Desktop::ICompositorDesktopInterop>();
        winrt::check_hresult(interop->CreateDesktopWindowTarget(
            hwnd,
            FALSE,
            reinterpret_cast<ABI::Windows::UI::Composition::Desktop::IDesktopWindowTarget**>(winrt::put_abi(state->target))));

        state->root = state->compositor.CreateContainerVisual();
        state->geometry = state->compositor.CreateRoundedRectangleGeometry();

        state->glass = state->compositor.CreateSpriteVisual();
        state->glass.Brush(state->compositor.CreateHostBackdropBrush());
        state->glass.Clip(state->compositor.CreateGeometricClip(state->geometry));
        state->root.Children().InsertAtTop(state->glass);

        state->decoration = state->compositor.CreateShapeVisual();
        auto shape = state->compositor.CreateSpriteShape(state->geometry);
        state->tint = state->compositor.CreateColorBrush();
        state->stroke = state->compositor.CreateColorBrush();
        shape.FillBrush(state->tint);
        shape.StrokeBrush(state->stroke);
        shape.StrokeThickness(1.0f);
        state->decoration.Shapes().Append(shape);
        state->root.Children().InsertAtTop(state->decoration);

        state->target.Root(state->root);
        *outState = state.release();
        return S_OK;
    }
    catch (...)
    {
        return winrt::to_hresult();
    }
}

extern "C" __declspec(dllexport) void __stdcall BingLanBackdrop_Resize(void* rawState, float width, float height) noexcept
{
    if (rawState == nullptr)
    {
        return;
    }

    try
    {
        auto* state = static_cast<BackdropState*>(rawState);
        const float2 size{ width, height };
        state->root.Size(size);
        state->glass.Size(size);
        state->decoration.Size(size);
    }
    catch (...)
    {
    }
}

extern "C" __declspec(dllexport) void __stdcall BingLanBackdrop_Update(
    void* rawState,
    float x,
    float y,
    float width,
    float height,
    float radiusX,
    float radiusY,
    float opacity,
    uint8_t fillA,
    uint8_t fillR,
    uint8_t fillG,
    uint8_t fillB,
    uint8_t borderA,
    uint8_t borderR,
    uint8_t borderG,
    uint8_t borderB) noexcept
{
    if (rawState == nullptr)
    {
        return;
    }

    try
    {
        auto* state = static_cast<BackdropState*>(rawState);
        state->geometry.Offset(float2{ x, y });
        state->geometry.Size(float2{ width, height });
        state->geometry.CornerRadius(float2{ radiusX, radiusY });
        state->root.Opacity(opacity);
        state->tint.Color(ColorHelper::FromArgb(fillA, fillR, fillG, fillB));
        state->stroke.Color(ColorHelper::FromArgb(borderA, borderR, borderG, borderB));
    }
    catch (...)
    {
    }
}

extern "C" __declspec(dllexport) void __stdcall BingLanBackdrop_Destroy(void* rawState) noexcept
{
    if (rawState == nullptr)
    {
        return;
    }

    auto* state = static_cast<BackdropState*>(rawState);
    state->Close();
    delete state;
}
