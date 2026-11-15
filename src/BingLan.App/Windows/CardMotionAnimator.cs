using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using BingLan.Core.Models;
using Brushes = System.Windows.Media.Brushes;
using Image = System.Windows.Controls.Image;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace BingLan.App.Windows;

/// <summary>
/// 桌面卡片 120–200 ms 共享过渡动效：正文折叠/展开与条目/图标增删时的淡入滑入、淡出滑出。
/// 始终遵循系统“动画效果”开关，系统关闭动画时立即切换且不启动过渡时钟。
/// </summary>
internal static class CardMotionAnimator
{
    private static readonly ConditionalWeakTable<UIElement, StrongBox<int>> RevealFlights = new();

    /// <summary>
    /// 读取系统是否允许窗口过渡动画；测试可替换该委托，在任意宿主系统设置下覆盖有动画与无动画分支。
    /// </summary>
    internal static Func<bool> AreSystemAnimationsEnabled { get; set; } =
        () => SystemParameters.ClientAreaAnimation;

    internal static bool IsAnimationEnabled =>
        CardMotionRules.IsAnimated(AreSystemAnimationsEnabled());

    internal static IEasingFunction CreateEase() =>
        new QuadraticEase { EasingMode = EasingMode.EaseOut };

    internal static void AnimateItemEnter(FrameworkElement container)
    {
        if (!IsAnimationEnabled || PresentationSource.FromVisual(container) is null)
        {
            return;
        }

        var duration = new Duration(CardMotionRules.ResolveItemDuration(true));
        var transform = new TranslateTransform(0d, -CardMotionRules.ItemSlideOffsetDip);
        container.RenderTransform = transform;
        container.Opacity = 0d;

        var fade = new DoubleAnimation(0d, 1d, duration) { EasingFunction = CreateEase() };
        var slide = new DoubleAnimation(-CardMotionRules.ItemSlideOffsetDip, 0d, duration)
        {
            EasingFunction = CreateEase()
        };
        fade.Completed += (_, _) =>
        {
            if (!ReferenceEquals(container.RenderTransform, transform))
            {
                return;
            }
            container.BeginAnimation(UIElement.OpacityProperty, null);
            transform.BeginAnimation(TranslateTransform.YProperty, null);
            container.Opacity = 1d;
            container.RenderTransform = Transform.Identity;
        };

        container.BeginAnimation(UIElement.OpacityProperty, fade);
        transform.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    internal static void AnimateItemExit(FrameworkElement itemVisual, Canvas overlayHost)
    {
        if (!IsAnimationEnabled
            || !itemVisual.IsLoaded
            || !itemVisual.IsVisible
            || !overlayHost.IsVisible
            || overlayHost.Children.Count >= 8
            || PresentationSource.FromVisual(itemVisual) is null
            || PresentationSource.FromVisual(overlayHost) is null)
        {
            return;
        }

        var width = itemVisual.ActualWidth;
        var height = itemVisual.ActualHeight;
        if (!double.IsFinite(width) || !double.IsFinite(height) || width < 1d || height < 1d)
        {
            return;
        }

        var offset = itemVisual.TranslatePoint(new Point(0d, 0d), overlayHost);
        var dpi = VisualTreeHelper.GetDpi(itemVisual);
        var pixelWidth = Math.Max(1, (int)Math.Ceiling(width * dpi.DpiScaleX));
        var pixelHeight = Math.Max(1, (int)Math.Ceiling(height * dpi.DpiScaleY));

        // 通过 VisualBrush 绘制离屏快照，避免把容器自身的祖先 VisualOffset 带进快照。
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var brush = new VisualBrush(itemVisual)
            {
                Viewbox = new Rect(0d, 0d, width, height),
                ViewboxUnits = BrushMappingMode.Absolute,
                Stretch = Stretch.None,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top
            };
            context.DrawRectangle(brush, null, new Rect(0d, 0d, width, height));
        }

        var bitmap = new RenderTargetBitmap(
            pixelWidth,
            pixelHeight,
            dpi.PixelsPerInchX,
            dpi.PixelsPerInchY,
            PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();

        var transform = new TranslateTransform(0d, 0d);
        var ghost = new Image
        {
            Source = bitmap,
            Width = width,
            Height = height,
            IsHitTestVisible = false,
            RenderTransform = transform
        };
        Canvas.SetLeft(ghost, offset.X);
        Canvas.SetTop(ghost, offset.Y);
        overlayHost.Children.Add(ghost);

        var duration = new Duration(CardMotionRules.ResolveItemDuration(true));
        var fade = new DoubleAnimation(1d, 0d, duration) { EasingFunction = CreateEase() };
        var slide = new DoubleAnimation(0d, -CardMotionRules.ItemSlideOffsetDip, duration)
        {
            EasingFunction = CreateEase()
        };
        fade.Completed += (_, _) => overlayHost.Children.Remove(ghost);

        ghost.BeginAnimation(UIElement.OpacityProperty, fade);
        transform.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    internal static void AnimateEmptyStateReveal(FrameworkElement emptyState)
    {
        var flightBox = RevealFlights.GetOrCreateValue(emptyState);
        var flight = ++flightBox.Value;

        if (!IsAnimationEnabled)
        {
            emptyState.BeginAnimation(UIElement.OpacityProperty, null);
            emptyState.Opacity = 1d;
            return;
        }

        emptyState.Opacity = 0d;
        var duration = new Duration(CardMotionRules.ResolveItemDuration(true));
        var fade = new DoubleAnimation(0d, 1d, duration) { EasingFunction = CreateEase() };
        fade.Completed += (_, _) =>
        {
            if (flightBox.Value != flight)
            {
                return;
            }
            emptyState.BeginAnimation(UIElement.OpacityProperty, null);
            emptyState.Opacity = 1d;
        };
        emptyState.BeginAnimation(UIElement.OpacityProperty, fade);
    }
}

/// <summary>
/// 在透明分层窗口内执行卡片正文的折叠与展开过渡，避免逐帧调整 HWND 窗口大小产生边缘抖动。
/// </summary>
internal sealed class CardFoldAnimator
{
    private readonly WidgetWindowBase _window;
    private readonly Grid _bodySurface;
    private int _flight;
    private bool _isAnimating;
    private IReadOnlyList<UIElement> _trackedElements = [];

    internal CardFoldAnimator(WidgetWindowBase window, Grid bodySurface)
    {
        _window = window;
        _bodySurface = bodySurface;
        _bodySurface.SizeChanged += BodySurface_SizeChanged;
        _window.Closed += Window_Closed;
    }

    internal bool IsAnimating => _isAnimating;

    internal void BeginCollapse(
        double collapsedHeight,
        IReadOnlyList<UIElement> bodyElements,
        Action onCollapsedCommitted)
    {
        if (!CardMotionAnimator.IsAnimationEnabled)
        {
            Snap();
            onCollapsedCommitted();
            return;
        }

        var startHeight = _isAnimating && _bodySurface.ActualHeight > 0d
            ? _bodySurface.ActualHeight
            : _bodySurface.ActualHeight > 0d
                ? _bodySurface.ActualHeight
                : _window.ActualHeight > 0d
                    ? _window.ActualHeight
                    : _window.Height;

        if (!double.IsFinite(startHeight) || startHeight <= collapsedHeight + 0.5d)
        {
            Snap();
            onCollapsedCommitted();
            return;
        }

        var wasAnimating = _isAnimating;
        var flight = ++_flight;
        _isAnimating = true;
        _trackedElements = bodyElements;

        _bodySurface.VerticalAlignment = VerticalAlignment.Top;
        _bodySurface.Height = startHeight;
        UpdateBodyClip(_bodySurface.ActualWidth > 0d ? _bodySurface.ActualWidth : _window.ActualWidth, startHeight);

        var duration = new Duration(CardMotionRules.ResolveFoldDuration(true));
        foreach (var element in bodyElements)
        {
            var startOpacity = wasAnimating ? element.Opacity : 1d;
            element.Opacity = 1d;
            element.BeginAnimation(
                UIElement.OpacityProperty,
                new DoubleAnimation(startOpacity, 0d, duration) { EasingFunction = CardMotionAnimator.CreateEase() });
        }

        var heightAnim = new DoubleAnimation(startHeight, collapsedHeight, duration)
        {
            EasingFunction = CardMotionAnimator.CreateEase()
        };
        heightAnim.Completed += (_, _) =>
        {
            if (flight != _flight)
            {
                return;
            }
            Snap();
            onCollapsedCommitted();
        };
        _bodySurface.BeginAnimation(FrameworkElement.HeightProperty, heightAnim);
    }

    internal void BeginExpand(
        double collapsedHeight,
        double targetHeight,
        IReadOnlyList<UIElement> bodyElements)
    {
        if (!CardMotionAnimator.IsAnimationEnabled)
        {
            Snap();
            return;
        }

        var wasAnimating = _isAnimating;
        var startHeight = wasAnimating && _bodySurface.ActualHeight > 0d
            ? Math.Clamp(_bodySurface.ActualHeight, collapsedHeight, Math.Max(collapsedHeight, targetHeight))
            : collapsedHeight;

        if (!double.IsFinite(targetHeight) || targetHeight <= startHeight + 0.5d)
        {
            Snap();
            return;
        }

        var initialOpacities = new double[bodyElements.Count];
        for (var i = 0; i < bodyElements.Count; i++)
        {
            initialOpacities[i] = wasAnimating ? bodyElements[i].Opacity : 0d;
        }

        var flight = ++_flight;
        _isAnimating = true;
        _trackedElements = bodyElements;

        _bodySurface.VerticalAlignment = VerticalAlignment.Top;
        _bodySurface.Height = startHeight;
        UpdateBodyClip(_bodySurface.ActualWidth > 0d ? _bodySurface.ActualWidth : _window.ActualWidth, startHeight);

        var duration = new Duration(CardMotionRules.ResolveFoldDuration(true));
        for (var i = 0; i < bodyElements.Count; i++)
        {
            var element = bodyElements[i];
            element.Opacity = 1d;
            element.BeginAnimation(
                UIElement.OpacityProperty,
                new DoubleAnimation(initialOpacities[i], 1d, duration)
                {
                    EasingFunction = CardMotionAnimator.CreateEase()
                });
        }

        var heightAnim = new DoubleAnimation(startHeight, targetHeight, duration)
        {
            EasingFunction = CardMotionAnimator.CreateEase()
        };
        heightAnim.Completed += (_, _) =>
        {
            if (flight == _flight)
            {
                Snap();
            }
        };
        _bodySurface.BeginAnimation(FrameworkElement.HeightProperty, heightAnim);
    }

    internal void Snap()
    {
        _flight++;
        _isAnimating = false;
        _bodySurface.BeginAnimation(FrameworkElement.HeightProperty, null);
        _bodySurface.Height = double.NaN;
        _bodySurface.VerticalAlignment = VerticalAlignment.Stretch;
        _bodySurface.Clip = null;

        foreach (var element in _trackedElements)
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = 1d;
        }
        _trackedElements = [];
    }

    private void BodySurface_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_isAnimating)
        {
            return;
        }
        UpdateBodyClip(e.NewSize.Width, e.NewSize.Height);
    }

    private void UpdateBodyClip(double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0d || height <= 0d)
        {
            _bodySurface.Clip = null;
            return;
        }

        var radius = Math.Min(_window.WidgetCornerRadius, Math.Min(width, height) / 2d);
        _bodySurface.Clip = radius <= 0d
            ? null
            : new RectangleGeometry(new Rect(0d, 0d, width, height), radius, radius);
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        Snap();
        _bodySurface.SizeChanged -= BodySurface_SizeChanged;
        _window.Closed -= Window_Closed;
    }
}
