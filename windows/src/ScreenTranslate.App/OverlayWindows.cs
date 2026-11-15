using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using ScreenTranslate.Core;
using ShapePath = System.Windows.Shapes.Path;

namespace ScreenTranslate.App;

/// <summary>
/// ドラッグで監視範囲を指定する全画面オーバーレイ（ESCキャンセル対応・WDA_EXCLUDEFROMCAPTURE適用）。
/// </summary>
internal sealed class RegionSelectorController
{
    private RegionSelectorWindow? _activeWindow;

    public void BeginSelection(Action<CaptureSelection?> onComplete)
    {
        Cancel();
        _activeWindow = new RegionSelectorWindow(sel =>
        {
            _activeWindow = null;
            onComplete(sel);
        });
        _activeWindow.Show();
        _activeWindow.Activate();
    }

    public void Cancel()
    {
        if (_activeWindow is not null)
        {
            var w = _activeWindow;
            _activeWindow = null;
            w.Close();
        }
    }

    private sealed class RegionSelectorWindow : Window
    {
        private readonly Action<CaptureSelection?> _onFinish;
        private readonly Canvas _canvas = new();
        private readonly ShapePath _scrimPath = new();
        private readonly Rectangle _hairlineRect = new() { Visibility = Visibility.Collapsed };
        private readonly ShapePath _bracketPath = new() { Visibility = Visibility.Collapsed };
        private readonly Border _sizeBadge = new() { Visibility = Visibility.Collapsed };
        private readonly TextBlock _sizeText = new();

        private Point? _dragStart;
        private Point? _currentPoint;
        private bool _completed;

        public RegionSelectorWindow(Action<CaptureSelection?> onFinish)
        {
            _onFinish = onFinish;

            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            Cursor = Cursors.Cross;

            Left = SystemParameters.VirtualScreenLeft;
            Top = SystemParameters.VirtualScreenTop;
            Width = SystemParameters.VirtualScreenWidth;
            Height = SystemParameters.VirtualScreenHeight;

            _scrimPath.Fill = new SolidColorBrush(Color.FromArgb(108, 10, 15, 20));
            _canvas.Children.Add(_scrimPath);

            _hairlineRect.Stroke = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255));
            _hairlineRect.StrokeThickness = 1.0;
            _canvas.Children.Add(_hairlineRect);

            _bracketPath.Stroke = new SolidColorBrush(Color.FromRgb(0x4D, 0xA8, 0x92));
            _bracketPath.StrokeThickness = 1.6;
            _canvas.Children.Add(_bracketPath);

            _sizeText.FontFamily = new FontFamily("Consolas, Yu Gothic UI");
            _sizeText.FontSize = 11.5;
            _sizeText.FontWeight = FontWeights.SemiBold;
            _sizeText.Foreground = new SolidColorBrush(Color.FromRgb(0x4D, 0xA8, 0x92));

            _sizeBadge.Background = new SolidColorBrush(Color.FromArgb(215, 14, 18, 24));
            _sizeBadge.CornerRadius = new CornerRadius(4);
            _sizeBadge.Padding = new Thickness(6, 2, 6, 2);
            _sizeBadge.Child = _sizeText;
            _canvas.Children.Add(_sizeBadge);

            Content = _canvas;

            SourceInitialized += (_, _) =>
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                Win32Interop.ExcludeFromScreenCapture(hwnd);
            };

            Loaded += (_, _) => UpdateVisuals(null);
            KeyDown += OnKeyDown;
            MouseLeftButtonDown += OnMouseDown;
            MouseMove += OnMouseMove;
            MouseLeftButtonUp += OnMouseUp;
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
                Finish(null);
        }

        private void OnMouseDown(object sender, MouseButtonEventArgs e)
        {
            var pt = e.GetPosition(_canvas);
            _dragStart = pt;
            _currentPoint = pt;
            CaptureMouse();
            UpdateVisuals(new Rect(pt, new Size(0, 0)));
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (_dragStart is not { } start) return;
            var pt = e.GetPosition(_canvas);
            _currentPoint = pt;
            UpdateVisuals(MakeRect(start, pt));
        }

        private void OnMouseUp(object sender, MouseButtonEventArgs e)
        {
            ReleaseMouseCapture();
            if (_dragStart is not { } start || _currentPoint is not { } end)
            {
                Finish(null);
                return;
            }

            var localRect = MakeRect(start, end);
            _dragStart = null;
            _currentPoint = null;

            if (localRect.Width < 24 || localRect.Height < 16)
            {
                Finish(null);
                return;
            }

            var dpi = VisualTreeHelper.GetDpi(this);
            double scaleX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
            double scaleY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;

            var logicalGlobal = new ScreenRect(
                Left + localRect.X,
                Top + localRect.Y,
                localRect.Width,
                localRect.Height);

            var pxTopLeft = _canvas.PointToScreen(localRect.TopLeft);
            var pxBottomRight = _canvas.PointToScreen(localRect.BottomRight);
            var physicalGlobal = new ScreenRect(
                Math.Round(Math.Min(pxTopLeft.X, pxBottomRight.X)),
                Math.Round(Math.Min(pxTopLeft.Y, pxBottomRight.Y)),
                Math.Max(24.0, Math.Round(Math.Abs(pxBottomRight.X - pxTopLeft.X))),
                Math.Max(16.0, Math.Round(Math.Abs(pxBottomRight.Y - pxTopLeft.Y))));

            var virtualBounds = new ScreenRect(
                SystemParameters.VirtualScreenLeft,
                SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth,
                SystemParameters.VirtualScreenHeight);

            Finish(new CaptureSelection(physicalGlobal, logicalGlobal, virtualBounds, scaleX, scaleY));
        }

        private void Finish(CaptureSelection? selection)
        {
            if (_completed) return;
            _completed = true;
            Close();
            _onFinish(selection);
        }

        private static Rect MakeRect(Point a, Point b)
            => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

        private void UpdateVisuals(Rect? selection)
        {
            var outer = new RectangleGeometry(new Rect(0, 0, Math.Max(1, ActualWidth), Math.Max(1, ActualHeight)));
            if (selection is { Width: > 1, Height: > 1 } sel)
            {
                var combined = new CombinedGeometry(GeometryCombineMode.Exclude, outer, new RectangleGeometry(sel));
                _scrimPath.Data = combined;

                _hairlineRect.Visibility = Visibility.Visible;
                _hairlineRect.Width = sel.Width;
                _hairlineRect.Height = sel.Height;
                Canvas.SetLeft(_hairlineRect, sel.X);
                Canvas.SetTop(_hairlineRect, sel.Y);

                _bracketPath.Visibility = Visibility.Visible;
                _bracketPath.Data = BuildCornerBracketsGeometry(sel, 12.0);

                var dpi = VisualTreeHelper.GetDpi(this);
                int pw = (int)Math.Round(sel.Width * dpi.DpiScaleX);
                int ph = (int)Math.Round(sel.Height * dpi.DpiScaleY);
                _sizeText.Text = $"{pw} × {ph}";
                _sizeBadge.Visibility = Visibility.Visible;
                Canvas.SetLeft(_sizeBadge, sel.X);
                Canvas.SetTop(_sizeBadge, Math.Max(6, sel.Y - 24));
            }
            else
            {
                _scrimPath.Data = outer;
                _hairlineRect.Visibility = Visibility.Collapsed;
                _bracketPath.Visibility = Visibility.Collapsed;
                _sizeBadge.Visibility = Visibility.Collapsed;
            }
        }
    }

    public static Geometry BuildCornerBracketsGeometry(Rect r, double arm)
    {
        double l = Math.Min(arm, Math.Min(r.Width, r.Height) * 0.4);
        var sg = new StreamGeometry();
        using (var ctx = sg.Open())
        {
            ctx.BeginFigure(new Point(r.Left, r.Top + l), false, false);
            ctx.LineTo(new Point(r.Left, r.Top), true, false);
            ctx.LineTo(new Point(r.Left + l, r.Top), true, false);

            ctx.BeginFigure(new Point(r.Right - l, r.Top), false, false);
            ctx.LineTo(new Point(r.Right, r.Top), true, false);
            ctx.LineTo(new Point(r.Right, r.Top + l), true, false);

            ctx.BeginFigure(new Point(r.Left, r.Bottom - l), false, false);
            ctx.LineTo(new Point(r.Left, r.Bottom), true, false);
            ctx.LineTo(new Point(r.Left + l, r.Bottom), true, false);

            ctx.BeginFigure(new Point(r.Right - l, r.Bottom), false, false);
            ctx.LineTo(new Point(r.Right, r.Bottom), true, false);
            ctx.LineTo(new Point(r.Right, r.Bottom - l), true, false);
        }
        sg.Freeze();
        return sg;
    }
}

/// <summary>
/// 監視領域またはホバー中のカードに対応する画面座標をエメラルド四隅ブラケット＋番号バッジで示す軽量クリック透過窓。
/// </summary>
internal sealed class RegionIndicatorWindow : Window
{
    private readonly Canvas _canvas = new();
    private readonly Rectangle _fillBox = new();
    private readonly ShapePath _bracketPath = new();
    private readonly Border _badge = new();
    private readonly TextBlock _badgeText = new();

    public RegionIndicatorWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;

        _fillBox.Fill = new SolidColorBrush(Color.FromArgb(22, 0x4D, 0xA8, 0x92));
        _fillBox.RadiusX = 4;
        _fillBox.RadiusY = 4;
        _canvas.Children.Add(_fillBox);

        _bracketPath.Stroke = new SolidColorBrush(Color.FromArgb(230, 0x4D, 0xA8, 0x92));
        _bracketPath.StrokeThickness = 1.6;
        _canvas.Children.Add(_bracketPath);

        _badgeText.FontFamily = new FontFamily("Consolas, Yu Gothic UI");
        _badgeText.FontSize = 10.5;
        _badgeText.FontWeight = FontWeights.Bold;
        _badgeText.Foreground = Brushes.White;

        _badge.Background = new SolidColorBrush(Color.FromArgb(230, 18, 24, 32));
        _badge.CornerRadius = new CornerRadius(4);
        _badge.Padding = new Thickness(5, 1, 5, 1);
        _badge.Child = _badgeText;
        _canvas.Children.Add(_badge);

        Content = _canvas;

        SourceInitialized += (_, _) =>
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            Win32Interop.MakeClickThroughOverlay(hwnd);
        };
    }

    public void ShowAround(ScreenRect logicalRect, string? badgeLabel, bool emphasized)
    {
        double topPad = !string.IsNullOrEmpty(badgeLabel) ? 20.0 : 4.0;
        Left = logicalRect.X - 4.0;
        Top = logicalRect.Y - topPad;
        Width = Math.Max(28.0, logicalRect.Width + 8.0);
        Height = Math.Max(24.0, logicalRect.Height + topPad + 4.0);

        var box = new Rect(4.0, topPad, Math.Max(20.0, logicalRect.Width), Math.Max(14.0, logicalRect.Height));

        _fillBox.Visibility = emphasized ? Visibility.Visible : Visibility.Collapsed;
        _fillBox.Width = box.Width;
        _fillBox.Height = box.Height;
        Canvas.SetLeft(_fillBox, box.X);
        Canvas.SetTop(_fillBox, box.Y);

        _bracketPath.Stroke = new SolidColorBrush(Color.FromArgb((byte)(emphasized ? 245 : 165), 0x4D, 0xA8, 0x92));
        _bracketPath.StrokeThickness = emphasized ? 1.8 : 1.2;
        _bracketPath.Data = RegionSelectorController.BuildCornerBracketsGeometry(box, 9.0);

        if (!string.IsNullOrEmpty(badgeLabel))
        {
            _badge.Visibility = Visibility.Visible;
            _badgeText.Text = badgeLabel;
            Canvas.SetLeft(_badge, 4.0);
            Canvas.SetTop(_badge, Math.Max(0, topPad - 18.0));
        }
        else
        {
            _badge.Visibility = Visibility.Collapsed;
        }

        if (!IsVisible)
            Show();
    }
}

/// <summary>
/// ゲーム画面の該当クラスタ位置に直接モノクロすりガラス訳文チップを浮かべる完全クリック透過オーバーレイ。
/// WDA_EXCLUDEFROMCAPTURE によりキャプチャから除外され、マウス接近時は該当チップを α=0.12 に自動退避する。
/// </summary>
internal sealed class InPlaceOverlayController : IDisposable
{
    private OverlaySurfaceWindow? _window;
    private readonly DispatcherTimer _cursorTimer;
    private readonly List<(Border Visual, ScreenRect LogicalChipBounds)> _activeChips = new();
    private double _dpiScaleX = 1.0;
    private double _dpiScaleY = 1.0;

    public InPlaceOverlayController()
    {
        _cursorTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => RefreshCursorAvoidance(), Dispatcher.CurrentDispatcher);
        _cursorTimer.Stop();
    }

    public void Update(IReadOnlyList<ClusterCardItem> items, CaptureSelection selection)
    {
        var ready = items.Where(i => !string.IsNullOrEmpty(i.DisplayBodyText)).ToList();
        if (ready.Count == 0)
        {
            Hide();
            return;
        }

        _dpiScaleX = selection.DpiScaleX > 0 ? selection.DpiScaleX : 1.0;
        _dpiScaleY = selection.DpiScaleY > 0 ? selection.DpiScaleY : 1.0;

        var bounds = selection.MonitorLogicalBounds;
        EnsureWindow(bounds);

        var canvas = _window!.SurfaceCanvas;
        canvas.Children.Clear();
        _activeChips.Clear();

        var cursorLogical = GetLogicalCursorPoint();
        var emeraldStroke = new SolidColorBrush(Color.FromArgb(190, 0x4D, 0xA8, 0x92));
        var calmStroke = new SolidColorBrush(Color.FromArgb(52, 255, 255, 255));
        var chipBg = new SolidColorBrush(Color.FromArgb(230, 15, 20, 28));

        foreach (var item in ready)
        {
            double desiredW = Math.Clamp(item.ScreenBounds.Width + 14.0, 160.0, Math.Min(580.0, bounds.Width - 16.0));
            double centeredX = item.ScreenBounds.MidX - desiredW * 0.5;
            double clampedGlobalX = Math.Clamp(centeredX, bounds.X + 8.0, bounds.Right - desiredW - 8.0);
            double clampedGlobalY = Math.Clamp(item.ScreenBounds.Y, bounds.Y + 4.0, bounds.Bottom - 36.0);

            var tb = new TextBlock
            {
                Text = item.DisplayBodyText,
                FontFamily = new FontFamily("Yu Gothic UI, Meiryo UI, Segoe UI"),
                FontSize = 13.5,
                FontWeight = FontWeights.Medium,
                Foreground = new SolidColorBrush(Color.FromArgb((byte)(item.IsTranslating ? 216 : 245), 255, 255, 255)),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 20.0
            };

            var chip = new Border
            {
                Width = desiredW,
                MinHeight = Math.Clamp(item.ScreenBounds.Height, 26.0, 140.0),
                Background = chipBg,
                BorderBrush = item.IsTranslating ? emeraldStroke : calmStroke,
                BorderThickness = new Thickness(0.9),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(9, 6, 9, 6),
                Child = tb,
                IsHitTestVisible = false
            };

            double estimatedH = Math.Max(chip.MinHeight, ((item.DisplayBodyText.Length / 18) + 1) * 21.0 + 12.0);
            var chipLogicalRect = new ScreenRect(clampedGlobalX, clampedGlobalY, desiredW, estimatedH);
            bool fade = ClusterGeometry.ShouldFadeChipForCursor(chipLogicalRect, cursorLogical.X, cursorLogical.Y);
            chip.Opacity = fade ? 0.12 : 1.0;

            Canvas.SetLeft(chip, clampedGlobalX - bounds.X);
            Canvas.SetTop(chip, clampedGlobalY - bounds.Y);
            canvas.Children.Add(chip);
            _activeChips.Add((chip, chipLogicalRect));
        }

        if (!_cursorTimer.IsEnabled)
            _cursorTimer.Start();
    }

    public void Hide()
    {
        _cursorTimer.Stop();
        _activeChips.Clear();
        if (_window is not null)
        {
            _window.SurfaceCanvas.Children.Clear();
            _window.Hide();
        }
    }

    private void EnsureWindow(ScreenRect bounds)
    {
        if (_window is null)
        {
            _window = new OverlaySurfaceWindow();
        }

        _window.Left = bounds.X;
        _window.Top = bounds.Y;
        _window.Width = bounds.Width;
        _window.Height = bounds.Height;

        if (!_window.IsVisible)
            _window.Show();
    }

    private void RefreshCursorAvoidance()
    {
        if (_activeChips.Count == 0) return;
        var pt = GetLogicalCursorPoint();
        foreach (var (visual, rect) in _activeChips)
        {
            bool fade = ClusterGeometry.ShouldFadeChipForCursor(rect, pt.X, pt.Y);
            double target = fade ? 0.12 : 1.0;
            if (Math.Abs(visual.Opacity - target) > 0.01)
                visual.Opacity = target;
        }
    }

    private (double X, double Y) GetLogicalCursorPoint()
    {
        if (Win32Interop.GetCursorPos(out var p))
            return (p.X / _dpiScaleX, p.Y / _dpiScaleY);
        return (-99999, -99999);
    }

    public void Dispose()
    {
        _cursorTimer.Stop();
        if (_window is not null)
        {
            _window.Close();
            _window = null;
        }
    }

    private sealed class OverlaySurfaceWindow : Window
    {
        public Canvas SurfaceCanvas { get; } = new() { IsHitTestVisible = false };

        public OverlaySurfaceWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            Focusable = false;
            IsHitTestVisible = false;
            Content = SurfaceCanvas;

            SourceInitialized += (_, _) =>
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                Win32Interop.MakeClickThroughOverlay(hwnd);
            };
        }
    }
}
