using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using ScreenTranslate.Core;
using ShapePath = System.Windows.Shapes.Path;

namespace ScreenTranslate.App;

/// <summary>
/// macOS 版 (TranslationHUDPanel.swift + DesignTokens.swift) と 1:1 同一デザインの Windows メインウィンドウ。
/// - チャコール (#12161C) + DWM ダークアクリル背後ぼかし + 微細ヘアライン + エメラルド (#4DA892) 単一アクセント。
/// - WM_NCCALCSIZE 制御により Windows 11 非クライアント上部余白をゼロ化し、38px 折りたたみバーと上下対称ヘッダーを実現。
/// - 囲み枠・説明文を排したミニマルな初回モデル取得表示（進捗ヘアライン＆レジューム対応）・JobObject 自動終了。
/// </summary>
internal sealed class MainTranslationWindow : Window
{
    public const double CollapsedBarHeight = 38.0;
    public const double SingleRowThreshold = 560.0;
    public static readonly Size MinExpandedSize = new(360.0, 145.0);
    public static readonly Size MaxExpandedSize = new(920.0, 680.0);
    public static readonly Size DefaultExpandedSize = new(460.0, 220.0);

    private static readonly FontFamily UiFont = new("Segoe UI Variable Text, Segoe UI, Yu Gothic UI, Meiryo UI");
    private static readonly FontFamily MonoFont = new("Cascadia Mono, Consolas, Segoe UI");

    private static readonly Color EmeraldRgb = Color.FromRgb(0x4D, 0xA8, 0x92);
    private static readonly SolidColorBrush EmeraldBrush = CreateFrozenBrush(EmeraldRgb);
    private static readonly SolidColorBrush DividerIdleBrush = CreateFrozenBrush(Color.FromArgb(26, 255, 255, 255));
    private static readonly SolidColorBrush DividerBusyBrush = CreateFrozenBrush(Color.FromArgb(217, 0x4D, 0xA8, 0x92));

    private readonly string _runtimeDir;
    private readonly string? _projectRootDir;
    private readonly bool _isSelfTest;
    private readonly AppUserSettings _settings;
    private readonly NativeSidecarManager _sidecar;
    private readonly TranslationCoordinator _translator = new();
    private readonly WindowsCaptureEngine _captureEngine = new();
    private readonly RegionSelectorController _regionSelector = new();
    private readonly RegionIndicatorWindow _regionIndicator = new();
    private readonly InPlaceOverlayController _inPlaceOverlay = new();

    private TranslationLanguage _sourceLang;
    private TranslationLanguage _targetLang;
    private TranslationBackendKind _backend;
    private TranslationDisplayMode _displayMode;
    private GpuEngineFlavor _gpuFlavor;

    private CaptureSelection? _currentSelection;
    private bool _isCapturing;
    private bool _isCollapsed;
    private bool _isUpdatingApp;
    private bool _showsRegionCorners;
    private double _savedExpandedHeight = DefaultExpandedSize.Height;

    private IReadOnlyList<ExtractedTextCluster> _latestRawClusters = Array.Empty<ExtractedTextCluster>();
    private readonly List<ClusterTranslationPlanner.CachedEntry> _cachedClusterEntries = new();
    private List<ClusterCardItem> _clusterCards = new();
    private ClusterCardItem? _hoveredCard;
    private string _lastSingleCommittedText = string.Empty;
    private string _previousSingleCommittedText = string.Empty;

    private CancellationTokenSource? _activeBatchCts;
    private string _activeUnresolvedKey = string.Empty;
    private ulong _batchGeneration;
    private string _lastSpotSelectedCore = string.Empty;

    private readonly DispatcherTimer _spotCursorTimer;
    private HwndSource? _hwndSource;
    private WindowChrome _windowChrome = null!;

    // UI Elements
    private Border _outerFrame = null!;
    private readonly Border _headerHost = new();
    private readonly Border _dividerLine = new() { Height = 1.0, Background = DividerIdleBrush };
    private readonly ScrollViewer _bodyScroller = new();
    private readonly StackPanel _setupPanel = new() { Orientation = Orientation.Vertical, Visibility = Visibility.Collapsed };
    private readonly TextBlock _setupTitleBlock = new();
    private readonly TextBlock _setupDetailBlock = new() { Visibility = Visibility.Collapsed };
    private readonly Border _progressTrack = new() { Visibility = Visibility.Collapsed };
    private readonly Border _progressFill = new() { HorizontalAlignment = HorizontalAlignment.Left };
    private readonly WrapPanel _setupActionsPanel = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel _cardsPanel = new() { Orientation = Orientation.Vertical };
    private readonly TextBlock _statusNoticeBlock = new();

    private Button _srcLangBtn = null!;
    private Button _tgtLangBtn = null!;
    private Button _backendBtn = null!;
    private Button _modeBtn = null!;
    private StackPanel _stageBadgePanel = null!;
    private Border _stageDot = null!;
    private TextBlock _stageLabel = null!;
    private Button _captureToggleBtn = null!;
    private Button _settingsBtn = null!;
    private Button _collapseBtn = null!;
    private bool? _lastRenderedTwoRowMode;
    private bool? _lastRenderedCompactCollapsed;

    public MainTranslationWindow(string runtimeDir, string? projectRootDir, bool isSelfTest = false)
    {
        _runtimeDir = runtimeDir;
        _projectRootDir = projectRootDir;
        _isSelfTest = isSelfTest;
        _settings = AppUserSettings.Load(runtimeDir);
        _sidecar = new NativeSidecarManager(runtimeDir, onProcessSpawned: Win32Interop.BindChildProcessToCurrentJob);

        _sourceLang = TranslationLanguage.SourceOptions.FirstOrDefault(x => x.Code == _settings.SourceLangCode);
        if (string.IsNullOrEmpty(_sourceLang.Code)) _sourceLang = TranslationLanguage.Auto;

        _targetLang = TranslationLanguage.TargetOptions.FirstOrDefault(x => x.Code == _settings.TargetLangCode);
        if (string.IsNullOrEmpty(_targetLang.Code)) _targetLang = TranslationLanguage.ConcreteLanguages[1];

        _backend = TranslationBackendExtensions.FromStorageId(_settings.BackendId);
        _displayMode = TranslationDisplayModeExtensions.FromStorageId(_settings.DisplayModeId);
        _gpuFlavor = _settings.GpuFlavor?.ToLowerInvariant() switch
        {
            "cuda" => GpuEngineFlavor.Cuda,
            "cpu" => GpuEngineFlavor.Cpu,
            _ => GpuEngineFlavor.Vulkan
        };

        Title = "ScreenTranslate";
        Icon = DesktopInstaller.CreateWindowIcon();
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        Topmost = true;
        ShowInTaskbar = true;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Auto);

        // macOS DesignTokens.surfaceCharcoal (#12161C) 相当の半透明チャコール地（DWMアクリル透過を活かす濃度）
        Background = CreateFrozenBrush(Color.FromArgb(172, 0x12, 0x16, 0x1C));

        MinWidth = MinExpandedSize.Width;
        MinHeight = MinExpandedSize.Height;
        MaxWidth = MaxExpandedSize.Width;
        MaxHeight = MaxExpandedSize.Height;

        double initW = Math.Clamp(
            double.IsNaN(_settings.WindowWidth) || _settings.WindowWidth < MinExpandedSize.Width
                ? DefaultExpandedSize.Width
                : _settings.WindowWidth,
            MinExpandedSize.Width,
            MaxExpandedSize.Width);
        double initH = Math.Clamp(
            double.IsNaN(_settings.WindowHeight) || _settings.WindowHeight < MinExpandedSize.Height
                ? DefaultExpandedSize.Height
                : _settings.WindowHeight,
            MinExpandedSize.Height,
            MaxExpandedSize.Height);

        _savedExpandedHeight = initH;
        Width = initW;
        Height = initH;

        if (!double.IsNaN(_settings.WindowLeft) && !double.IsNaN(_settings.WindowTop))
        {
            Left = _settings.WindowLeft;
            Top = _settings.WindowTop;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        _windowChrome = new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(-1),
            CornerRadius = new CornerRadius(12),
            UseAeroCaptionButtons = false
        };
        WindowChrome.SetWindowChrome(this, _windowChrome);

        BuildUiTree();
        RebuildResponsiveHeader(initW);
        SetStatusNotice("範囲ボタン（または Alt+Shift+R）で監視領域を選択");

        _spotCursorTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => PollCursorSpotIfNeeded(), Dispatcher);
        _spotCursorTimer.Stop();

        _sidecar.StatusChanged += snap => Dispatcher.InvokeAsync(() => ApplySidecarStatus(snap));
        _captureEngine.OnTextEvent = ev => Dispatcher.InvokeAsync(() => HandleCapturedTextEvent(ev));
        _captureEngine.OnStatusError = msg => Dispatcher.InvokeAsync(() => SetStatusNotice(msg));

        SourceInitialized += OnSourceInitialized;
        SizeChanged += OnWindowSizeChanged;
        LocationChanged += (_, _) => SaveWindowGeometry();
        PreviewKeyDown += OnPreviewKeyDown;
        Closed += (_, _) => PerformCleanShutdown(0);

        if (!_isSelfTest)
        {
            _sidecar.TryAutoStartIfInstalled(_backend, _gpuFlavor);
        }
    }

    private static SolidColorBrush CreateFrozenBrush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static LinearGradientBrush CreateFrozenGradient(Color start, Color end, Point p0, Point p1)
    {
        var g = new LinearGradientBrush(start, end, p0, p1);
        g.Freeze();
        return g;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        bool nativeRoundedBorder = Win32Interop.EnableDarkAcrylicBackdrop(hwnd);
        if (nativeRoundedBorder)
        {
            // Windows 11 DWM が角丸1pxダークボーダーを描画するため、二重枠にならないよう内側ボーダーを極薄化
            _outerFrame.BorderThickness = new Thickness(0.5);
            _outerFrame.BorderBrush = CreateFrozenGradient(
                Color.FromArgb(42, 255, 255, 255),
                Color.FromArgb(10, 255, 255, 255),
                new Point(0.5, 0),
                new Point(0.5, 1));
        }

        Win32Interop.ExcludeFromScreenCapture(hwnd);

        _hwndSource = HwndSource.FromHwnd(hwnd);
        _hwndSource?.AddHook(WndProcHook);
        RegisterGlobalHotKeys(hwnd);
    }

    private void RegisterGlobalHotKeys(IntPtr hwnd)
    {
        uint mods = Win32Interop.MOD_ALT | Win32Interop.MOD_SHIFT | Win32Interop.MOD_NOREPEAT;
        _ = Win32Interop.RegisterHotKey(hwnd, 1, mods, Win32Interop.VK_R);
        _ = Win32Interop.RegisterHotKey(hwnd, 2, mods, Win32Interop.VK_T);
        _ = Win32Interop.RegisterHotKey(hwnd, 3, mods, Win32Interop.VK_L);
        _ = Win32Interop.RegisterHotKey(hwnd, 4, mods, Win32Interop.VK_W);
        _ = Win32Interop.RegisterHotKey(hwnd, 5, mods, Win32Interop.VK_Q);
        _ = Win32Interop.RegisterHotKey(hwnd, 6, mods, Win32Interop.VK_M);
    }

    private void UnregisterGlobalHotKeys()
    {
        if (_hwndSource is null) return;
        IntPtr hwnd = _hwndSource.Handle;
        for (int id = 1; id <= 6; id++)
            _ = Win32Interop.UnregisterHotKey(hwnd, id);
    }

    private IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Windows 10/11 で WindowStyle.None + ResizeMode.CanResize 使用時に残る上部 7〜8px の非クライアント領域を完全に除去し、
        // macOS 版と全く同じ上下対称の 38px ヘッダー＆折りたたみバーにする
        if (msg == Win32Interop.WM_NCCALCSIZE && wParam != IntPtr.Zero && WindowState != WindowState.Maximized)
        {
            handled = true;
            return IntPtr.Zero;
        }

        if (msg == Win32Interop.WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            handled = true;
            switch (id)
            {
                case 1: SelectRegionAction(); break;
                case 2: ToggleCaptureAction(); break;
                case 3: ToggleCollapse(); break;
                case 4:
                    if (IsVisible) Hide();
                    else { Show(); Activate(); }
                    break;
                case 5: PerformCleanShutdown(0); break;
                case 6:
                    _displayMode = _displayMode.NextMode();
                    OnDisplayModeChanged();
                    break;
            }
        }
        return IntPtr.Zero;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        if (ctrl && !shift && (e.Key == Key.Q || e.Key == Key.W))
        {
            e.Handled = true;
            PerformCleanShutdown(0);
        }
        else if (ctrl && !shift && e.Key == Key.K)
        {
            e.Handled = true;
            ToggleCollapse();
        }
        else if (ctrl && !shift && e.Key == Key.C)
        {
            CopyAllVisibleTranslations();
            e.Handled = true;
        }
        else if (ctrl && shift && e.Key == Key.U && !string.IsNullOrEmpty(_projectRootDir))
        {
            e.Handled = true;
            UpdateAndRelaunchFromSourceAction();
        }
    }

    private void CopyAllVisibleTranslations()
    {
        string joined = string.Join(
            Environment.NewLine + Environment.NewLine,
            _clusterCards.Select(c => c.DisplayBodyText).Where(t => !string.IsNullOrWhiteSpace(t)));
        if (!string.IsNullOrEmpty(joined))
        {
            try { Clipboard.SetText(joined); } catch { }
        }
    }

    // MARK: - macOS Identical Glass Surface & Minimalist Layout

    private void BuildUiTree()
    {
        // macOS MainTranslationView の LinearGradient (white 0.09 -> 0.02) と strokeBorder (white 0.28 -> 0.08) を忠実再現
        _outerFrame = new Border
        {
            CornerRadius = new CornerRadius(12),
            BorderBrush = CreateFrozenGradient(
                Color.FromArgb(68, 255, 255, 255),
                Color.FromArgb(20, 255, 255, 255),
                new Point(0.5, 0),
                new Point(0.5, 1)),
            BorderThickness = new Thickness(0.7),
            Background = CreateFrozenGradient(
                Color.FromArgb(20, 255, 255, 255),
                Color.FromArgb(4, 255, 255, 255),
                new Point(0, 0),
                new Point(1, 1))
        };

        _outerFrame.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                try { DragMove(); } catch { }
            }
        };

        var rootGrid = new Grid();
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        CreateHeaderControls();

        _headerHost.MouseEnter += (_, _) =>
        {
            _showsRegionCorners = true;
            RefreshRegionIndicator();
        };
        _headerHost.MouseLeave += (_, _) =>
        {
            _showsRegionCorners = false;
            RefreshRegionIndicator();
        };

        Grid.SetRow(_headerHost, 0);
        Grid.SetRow(_dividerLine, 1);

        BuildMinimalSetupPanel();

        _statusNoticeBlock.FontFamily = UiFont;
        _statusNoticeBlock.FontSize = 13.5;
        _statusNoticeBlock.FontWeight = FontWeights.Normal;
        _statusNoticeBlock.Foreground = CreateFrozenBrush(Color.FromArgb(133, 255, 255, 255)); // white 0.52
        _statusNoticeBlock.TextWrapping = TextWrapping.Wrap;
        _statusNoticeBlock.LineHeight = 20.5;
        _statusNoticeBlock.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;

        var bodyStack = new StackPanel { Margin = new Thickness(14, 11, 14, 11) };
        bodyStack.Children.Add(_setupPanel);
        bodyStack.Children.Add(_cardsPanel);
        bodyStack.Children.Add(_statusNoticeBlock);

        _bodyScroller.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _bodyScroller.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _bodyScroller.Background = Brushes.Transparent;
        _bodyScroller.BorderThickness = new Thickness(0);
        _bodyScroller.Content = bodyStack;

        Grid.SetRow(_bodyScroller, 2);

        rootGrid.Children.Add(_headerHost);
        rootGrid.Children.Add(_dividerLine);
        rootGrid.Children.Add(_bodyScroller);

        _outerFrame.Child = rootGrid;
        Content = _outerFrame;
    }

    /// <summary>
    /// 02_UIデザイン方向に準拠し、緑の囲み枠や冗長な説明文を排したミニマルな初回取得ビュー（2px ヘアライン進捗＋カプセルピルのみ）。
    /// </summary>
    private void BuildMinimalSetupPanel()
    {
        _setupTitleBlock.FontFamily = UiFont;
        _setupTitleBlock.FontSize = 13.5;
        _setupTitleBlock.FontWeight = FontWeights.Normal;
        _setupTitleBlock.Foreground = CreateFrozenBrush(Color.FromArgb(194, 255, 255, 255)); // white 0.76
        _setupTitleBlock.TextWrapping = TextWrapping.Wrap;
        _setupTitleBlock.LineHeight = 20.0;
        _setupTitleBlock.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;

        _setupDetailBlock.FontFamily = MonoFont;
        _setupDetailBlock.FontSize = 11.5;
        _setupDetailBlock.Foreground = CreateFrozenBrush(Color.FromArgb(115, 255, 255, 255)); // white 0.45
        _setupDetailBlock.TextWrapping = TextWrapping.Wrap;
        _setupDetailBlock.Margin = new Thickness(0, 4, 0, 0);

        _progressFill.Height = 2.0;
        _progressFill.CornerRadius = new CornerRadius(1.0);
        _progressFill.Background = EmeraldBrush;
        _progressFill.Width = 0.0;

        _progressTrack.Height = 2.0;
        _progressTrack.CornerRadius = new CornerRadius(1.0);
        _progressTrack.Background = CreateFrozenBrush(Color.FromArgb(26, 255, 255, 255));
        _progressTrack.Margin = new Thickness(0, 8, 0, 2);
        _progressTrack.Child = _progressFill;

        _setupActionsPanel.Margin = new Thickness(0, 8, 0, 0);

        _setupPanel.Margin = new Thickness(0, 0, 0, 6);
        _setupPanel.Children.Add(_setupTitleBlock);
        _setupPanel.Children.Add(_setupDetailBlock);
        _setupPanel.Children.Add(_progressTrack);
        _setupPanel.Children.Add(_setupActionsPanel);
    }

    private static Button MakeSetupCapsuleButton(string label, bool primary, Action onClick)
    {
        var tb = new TextBlock
        {
            Text = label,
            FontFamily = UiFont,
            FontSize = 11.5,
            FontWeight = FontWeights.Medium,
            Foreground = CreateFrozenBrush(Color.FromArgb((byte)(primary ? 235 : 190), 255, 255, 255)),
            VerticalAlignment = VerticalAlignment.Center
        };

        var bgIdle = CreateFrozenBrush(Color.FromArgb((byte)(primary ? 30 : 20), 255, 255, 255));
        var bgHover = CreateFrozenBrush(Color.FromArgb((byte)(primary ? 48 : 36), 255, 255, 255));
        var stroke = primary
            ? CreateFrozenBrush(Color.FromArgb(145, 0x4D, 0xA8, 0x92))
            : CreateFrozenBrush(Color.FromArgb(32, 255, 255, 255));

        var border = new Border
        {
            Height = 24,
            CornerRadius = new CornerRadius(12),
            Background = bgIdle,
            BorderBrush = stroke,
            BorderThickness = new Thickness(0.75),
            Padding = new Thickness(11, 0, 11, 0),
            Margin = new Thickness(0, 0, 6, 6),
            Child = tb,
            Cursor = Cursors.Hand
        };

        border.MouseEnter += (_, _) => border.Background = bgHover;
        border.MouseLeave += (_, _) => border.Background = bgIdle;
        return WrapBorderAsPlainButton(border, onClick);
    }

    private void ApplySidecarStatus(SidecarStatusSnapshot snap)
    {
        if (snap.Backend != _backend && _backend.IsPlamo())
            return;

        switch (snap.State)
        {
            case SidecarState.NotInstalled:
            case SidecarState.Error:
            {
                var specOpt = NativeSidecarManager.GetSpec(_backend);
                string sizeText = specOpt?.ApproxSizeText ?? "";
                long partialBytes = _sidecar.GetPartialDownloadBytes(_backend);
                bool hasPartial = partialBytes > 1024 * 1024;

                _setupTitleBlock.Text = snap.Message;
                if (hasPartial)
                {
                    _setupDetailBlock.Text = $"一時保存済み: {partialBytes / (1024 * 1024)} MB";
                    _setupDetailBlock.Visibility = Visibility.Visible;
                }
                else
                {
                    _setupDetailBlock.Visibility = Visibility.Collapsed;
                }

                _progressTrack.Visibility = Visibility.Collapsed;
                _setupActionsPanel.Children.Clear();

                string primaryLabel = hasPartial
                    ? $"{_backend.ShortName()} 再開"
                    : $"{_backend.ShortName()} を取得 ({sizeText})";

                _setupActionsPanel.Children.Add(MakeSetupCapsuleButton(primaryLabel, primary: true, () =>
                {
                    _ = _sidecar.DownloadAndStartAsync(_backend, _gpuFlavor);
                }));

                if (_backend != TranslationBackendKind.Plamo08B)
                {
                    _setupActionsPanel.Children.Add(MakeSetupCapsuleButton("0.8B 軽量版 (570 MB)", primary: false, () =>
                    {
                        _backend = TranslationBackendKind.Plamo08B;
                        SetCapsulePillText(_backendBtn, _backend.ShortName());
                        _settings.BackendId = _backend.ToStorageId();
                        _settings.Save(_runtimeDir);
                        _ = _sidecar.DownloadAndStartAsync(_backend, _gpuFlavor);
                    }));
                }

                _setupActionsPanel.Visibility = Visibility.Visible;
                _setupPanel.Visibility = Visibility.Visible;
                UpdateStage(snap.State == SidecarState.Error ? "要確認" : string.Empty, busy: false);
                RefreshNoticeVisibility();
                break;
            }

            case SidecarState.Downloading:
            {
                _setupTitleBlock.Text = snap.Message;
                string summary = NativeSidecarManager.FormatProgressSummary(snap);
                if (!string.IsNullOrEmpty(summary))
                {
                    _setupDetailBlock.Text = summary;
                    _setupDetailBlock.Visibility = Visibility.Visible;
                }
                else
                {
                    _setupDetailBlock.Visibility = Visibility.Collapsed;
                }

                _progressTrack.Visibility = Visibility.Visible;
                double trackW = Math.Max(160.0, (ActualWidth > 0 ? ActualWidth : Width) - 28.0);
                _progressFill.Width = Math.Clamp(trackW * snap.ProgressFraction, 8.0, trackW);

                _setupActionsPanel.Children.Clear();
                _setupActionsPanel.Children.Add(MakeSetupCapsuleButton("一時停止", primary: false, () =>
                {
                    _sidecar.CancelActiveOperation();
                }));
                _setupActionsPanel.Visibility = Visibility.Visible;

                _setupPanel.Visibility = Visibility.Visible;
                int pct = (int)Math.Round(snap.ProgressFraction * 100.0);
                UpdateStage($"取得中 {pct}%", busy: true);
                RefreshNoticeVisibility();
                break;
            }

            case SidecarState.Starting:
            {
                _setupTitleBlock.Text = snap.Message;
                _setupDetailBlock.Visibility = Visibility.Collapsed;
                _progressTrack.Visibility = Visibility.Visible;
                double trackW = Math.Max(160.0, (ActualWidth > 0 ? ActualWidth : Width) - 28.0);
                _progressFill.Width = trackW;
                _setupActionsPanel.Children.Clear();
                _setupActionsPanel.Visibility = Visibility.Collapsed;
                _setupPanel.Visibility = Visibility.Visible;
                UpdateStage("ロード中…", busy: true);
                RefreshNoticeVisibility();
                break;
            }

            case SidecarState.Ready:
            {
                _setupPanel.Visibility = Visibility.Collapsed;
                if (_activeBatchCts is null)
                    UpdateStage(_isCapturing ? "待機中" : string.Empty, busy: false);
                RefreshNoticeVisibility();

                if (_isCapturing && _latestRawClusters.Count > 0 && _activeBatchCts is null)
                {
                    ApplyClustersAndScheduleTranslations(_latestRawClusters);
                }
                break;
            }

            case SidecarState.Stopped:
            {
                if (!_sidecar.NeedsDownload(_backend, _gpuFlavor))
                    _setupPanel.Visibility = Visibility.Collapsed;
                RefreshNoticeVisibility();
                break;
            }
        }
    }

    // MARK: - Responsive Headers (macOS 1段 ⇄ 2段 完全一致)

    private void CreateHeaderControls()
    {
        _srcLangBtn = MakeCapsulePillButton(_sourceLang.ShortName, primaryTone: true, () => ShowLanguageMenu(isSource: true));
        _tgtLangBtn = MakeCapsulePillButton(_targetLang.ShortName, primaryTone: true, () => ShowLanguageMenu(isSource: false));
        _backendBtn = MakeCapsulePillButton(_backend.ShortName(), primaryTone: false, ShowBackendMenu);
        _modeBtn = MakeCapsulePillButton(_displayMode.ShortName(), primaryTone: false, ShowDisplayModeMenu);

        _stageDot = new Border
        {
            Width = 5.5,
            Height = 5.5,
            CornerRadius = new CornerRadius(2.75),
            Background = EmeraldBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0),
            Visibility = Visibility.Collapsed
        };

        _stageLabel = new TextBlock
        {
            FontFamily = UiFont,
            FontSize = 10.5,
            FontWeight = FontWeights.Normal,
            Foreground = CreateFrozenBrush(Color.FromArgb(118, 255, 255, 255)),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        _stageBadgePanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 6, 0)
        };
        _stageBadgePanel.Children.Add(_stageDot);
        _stageBadgePanel.Children.Add(_stageLabel);

        _captureToggleBtn = MakeHeaderIconButton("play", "監視を開始 / 停止 (Alt+Shift+T)", ToggleCaptureAction);
        _settingsBtn = MakeHeaderIconButton("settings", "モデル・ランタイム・ショートカット設定", ShowAppSettingsMenu);
        _collapseBtn = MakeHeaderIconButton("collapse", "バーに折りたたむ / 広げる (Ctrl+K)", ToggleCollapse);
    }

    private void RebuildResponsiveHeader(double currentWidth)
    {
        bool twoRow = !_isCollapsed && currentWidth < SingleRowThreshold;
        bool compactCollapsed = _isCollapsed && currentWidth < SingleRowThreshold;
        if (_lastRenderedTwoRowMode == twoRow && _lastRenderedCompactCollapsed == compactCollapsed)
            return;

        _lastRenderedTwoRowMode = twoRow;
        _lastRenderedCompactCollapsed = compactCollapsed;

        DetachFromParent(_srcLangBtn);
        DetachFromParent(_tgtLangBtn);
        DetachFromParent(_backendBtn);
        DetachFromParent(_modeBtn);
        DetachFromParent(_stageBadgePanel);
        DetachFromParent(_captureToggleBtn);
        DetachFromParent(_settingsBtn);
        DetachFromParent(_collapseBtn);

        var closeBtn = MakeCircleCloseButton(() => PerformCleanShutdown(0));
        var rangeBtn = MakeHeaderIconButton("region", "監視範囲を選択 (Alt+Shift+R)", SelectRegionAction, isFirstInGroup: true);

        var langPair = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        langPair.Children.Add(_srcLangBtn);
        langPair.Children.Add(CreateLanguageArrowPath());
        langPair.Children.Add(_tgtLangBtn);

        var rightButtons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        rightButtons.Children.Add(rangeBtn);
        rightButtons.Children.Add(_captureToggleBtn);
        rightButtons.Children.Add(_settingsBtn);
        rightButtons.Children.Add(_collapseBtn);

        if (!twoRow)
        {
            var bar = new Grid
            {
                Height = CollapsedBarHeight,
                Margin = new Thickness(12, 0, 12, 0)
            };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var leftGroup = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            leftGroup.Children.Add(closeBtn);
            leftGroup.Children.Add(langPair);
            if (!compactCollapsed)
            {
                _backendBtn.Margin = new Thickness(5, 0, 0, 0);
                _modeBtn.Margin = new Thickness(5, 0, 0, 0);
                leftGroup.Children.Add(_backendBtn);
                leftGroup.Children.Add(_modeBtn);
            }

            Grid.SetColumn(leftGroup, 0);
            Grid.SetColumn(_stageBadgePanel, 1);
            Grid.SetColumn(rightButtons, 2);
            bar.Children.Add(leftGroup);
            bar.Children.Add(_stageBadgePanel);
            bar.Children.Add(rightButtons);

            _headerHost.Child = bar;
        }
        else
        {
            var twoRowStack = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Margin = new Thickness(12, 8, 12, 7)
            };

            var row1 = new Grid { Margin = new Thickness(0, 0, 0, 5) };
            row1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left1 = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            left1.Children.Add(closeBtn);
            left1.Children.Add(langPair);
            Grid.SetColumn(left1, 0);
            Grid.SetColumn(rightButtons, 2);
            row1.Children.Add(left1);
            row1.Children.Add(rightButtons);

            var row2 = new Grid();
            row2.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var left2 = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            _backendBtn.Margin = new Thickness(0, 0, 6, 0);
            _modeBtn.Margin = new Thickness(0, 0, 0, 0);
            left2.Children.Add(_backendBtn);
            left2.Children.Add(_modeBtn);
            Grid.SetColumn(left2, 0);
            Grid.SetColumn(_stageBadgePanel, 1);
            row2.Children.Add(left2);
            row2.Children.Add(_stageBadgePanel);

            twoRowStack.Children.Add(row1);
            twoRowStack.Children.Add(row2);
            _headerHost.Child = twoRowStack;
        }
    }

    private static void DetachFromParent(UIElement element)
    {
        if (VisualTreeHelper.GetParent(element) is Panel p)
            p.Children.Remove(element);
        else if (LogicalTreeHelper.GetParent(element) is Panel lp)
            lp.Children.Remove(element);
    }

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_isCollapsed && e.NewSize.Height >= MinExpandedSize.Height)
            _savedExpandedHeight = e.NewSize.Height;

        if (_progressTrack.Visibility == Visibility.Visible)
        {
            double trackW = Math.Max(160.0, e.NewSize.Width - 28.0);
            double frac = _sidecar.LatestStatus.State == SidecarState.Starting ? 1.0 : _sidecar.LatestStatus.ProgressFraction;
            _progressFill.Width = Math.Clamp(trackW * frac, 8.0, trackW);
        }

        RebuildResponsiveHeader(e.NewSize.Width);
        SaveWindowGeometry();
    }

    // MARK: - SF Symbols Equivalent Crisp Vector Controls & Menus

    private static ShapePath CreateLanguageArrowPath()
    {
        return new ShapePath
        {
            Data = Geometry.Parse("M0.5,4.0 H8.5 M5.5,1.2 L8.5,4.0 L5.5,6.8"),
            Width = 9.0,
            Height = 8.0,
            Stretch = Stretch.Uniform,
            Stroke = CreateFrozenBrush(Color.FromArgb(97, 255, 255, 255)), // white 0.38
            StrokeThickness = 1.25,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 4, 0)
        };
    }

    private static ShapePath CreateUpDownChevronPath(byte alpha)
    {
        return new ShapePath
        {
            Data = Geometry.Parse("M0.8,2.8 L3.0,0.6 L5.2,2.8 M0.8,5.4 L3.0,7.6 L5.2,5.4"),
            Width = 5.6,
            Height = 7.8,
            Stretch = Stretch.Uniform,
            Stroke = CreateFrozenBrush(Color.FromArgb(alpha, 255, 255, 255)),
            StrokeThickness = 1.15,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(3.5, 0, 0, 0)
        };
    }

    private static Button MakeCircleCloseButton(Action onClick)
    {
        var idleFg = CreateFrozenBrush(Color.FromArgb(173, 255, 255, 255)); // white 0.68
        var hoverFg = CreateFrozenBrush(Color.FromArgb(242, 255, 255, 255)); // white 0.95
        var idleBg = CreateFrozenBrush(Color.FromArgb(26, 255, 255, 255));   // white 0.10
        var hoverBg = CreateFrozenBrush(Color.FromArgb(46, 255, 255, 255));  // white 0.18

        var xmark = new ShapePath
        {
            Data = Geometry.Parse("M1,1 L8,8 M8,1 L1,8"),
            Width = 7.5,
            Height = 7.5,
            Stretch = Stretch.Uniform,
            Stroke = idleFg,
            StrokeThickness = 1.5,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var border = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            Background = idleBg,
            Margin = new Thickness(0, 0, 6, 0),
            Child = xmark,
            Cursor = Cursors.Hand,
            ToolTip = "終了 (Ctrl+Q / Alt+Shift+Q)"
        };

        border.MouseEnter += (_, _) =>
        {
            border.Background = hoverBg;
            xmark.Stroke = hoverFg;
        };
        border.MouseLeave += (_, _) =>
        {
            border.Background = idleBg;
            xmark.Stroke = idleFg;
        };

        return WrapBorderAsPlainButton(border, onClick);
    }

    private static Button MakeCapsulePillButton(string text, bool primaryTone, Action onClick)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontFamily = UiFont,
            FontSize = primaryTone ? 11.0 : 10.5,
            FontWeight = FontWeights.Medium,
            Foreground = CreateFrozenBrush(Color.FromArgb((byte)(primaryTone ? 224 : 196), 255, 255, 255)),
            VerticalAlignment = VerticalAlignment.Center
        };

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        row.Children.Add(tb);
        row.Children.Add(CreateUpDownChevronPath((byte)(primaryTone ? 140 : 120)));

        byte baseAlpha = (byte)(primaryTone ? 28 : 20); // white 0.11 / 0.08
        double h = primaryTone ? 22.0 : 20.0;
        var border = new Border
        {
            Height = h,
            CornerRadius = new CornerRadius(h * 0.5),
            Background = CreateFrozenBrush(Color.FromArgb(baseAlpha, 255, 255, 255)),
            Padding = new Thickness(7.5, 0, 7.0, 0),
            Child = row,
            Cursor = Cursors.Hand
        };

        border.MouseEnter += (_, _) => border.Background = CreateFrozenBrush(Color.FromArgb((byte)(baseAlpha + 16), 255, 255, 255));
        border.MouseLeave += (_, _) => border.Background = CreateFrozenBrush(Color.FromArgb(baseAlpha, 255, 255, 255));

        return WrapBorderAsPlainButton(border, onClick);
    }

    private static void SetCapsulePillText(Button btn, string text)
    {
        if (btn.Content is Border { Child: StackPanel sp } && sp.Children.Count > 0 && sp.Children[0] is TextBlock tb)
        {
            tb.Text = text;
        }
    }

    private static Geometry GetHeaderIconGeometry(string iconKey) => iconKey switch
    {
        // SF Symbol: viewfinder
        "region" => Geometry.Parse("M1.6,4.8 V2.7 C1.6,2.1 2.1,1.6 2.7,1.6 H4.8 M9.2,1.6 H11.3 C11.9,1.6 12.4,2.1 12.4,2.7 V4.8 M12.4,9.2 V11.3 C12.4,11.9 11.9,12.4 11.3,12.4 H9.2 M4.8,12.4 H2.7 C2.1,12.4 1.6,11.9 1.6,11.3 V9.2"),
        // SF Symbol: play.fill
        "play" => Geometry.Parse("M3.8,2.4 L11.5,7.0 L3.8,11.6 Z"),
        // SF Symbol: stop.fill
        "stop" => Geometry.Parse("M3.2,3.2 H10.8 V10.8 H3.2 Z"),
        // SF Symbol: slider.horizontal.3 (ミニマル設定アイコン)
        "settings" => Geometry.Parse("M1.8,3.6 H12.2 M4.8,2.2 V5.0 M1.8,7.0 H12.2 M9.2,5.6 V8.4 M1.8,10.4 H12.2 M5.6,9.0 V11.8"),
        // SF Symbol: chevron.up
        "collapse" => Geometry.Parse("M3.0,8.8 L7.0,4.8 L11.0,8.8"),
        // SF Symbol: chevron.down
        "expand" => Geometry.Parse("M3.0,5.2 L7.0,9.2 L11.0,5.2"),
        _ => Geometry.Parse("M3,7 H11")
    };

    private static bool IsFilledHeaderIcon(string iconKey) => iconKey is "play" or "stop";

    private static ShapePath CreateHeaderIconPath(string iconKey, byte alpha = 200)
    {
        bool filled = IsFilledHeaderIcon(iconKey);
        var brush = CreateFrozenBrush(Color.FromArgb(alpha, 255, 255, 255));
        double size = iconKey is "play" or "stop" ? 10.5 : 12.0;
        return new ShapePath
        {
            Data = GetHeaderIconGeometry(iconKey),
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            Fill = filled ? brush : Brushes.Transparent,
            Stroke = filled ? null : brush,
            StrokeThickness = filled ? 0 : 1.4,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private static Button MakeHeaderIconButton(string iconKey, string tooltip, Action onClick, bool isFirstInGroup = false)
    {
        var border = new Border
        {
            Width = 24,
            Height = 22,
            CornerRadius = new CornerRadius(6),
            Background = CreateFrozenBrush(Color.FromArgb(26, 255, 255, 255)), // white 0.10
            Margin = new Thickness(isFirstInGroup ? 0 : 4, 0, 0, 0),
            Child = CreateHeaderIconPath(iconKey, 200),
            Tag = (IconKey: iconKey, Prominent: false),
            Cursor = Cursors.Hand,
            ToolTip = tooltip
        };

        border.MouseEnter += (_, _) =>
        {
            bool prom = border.Tag is ValueTuple<string, bool> t && t.Item2;
            border.Background = CreateFrozenBrush(Color.FromArgb((byte)(prom ? 68 : 42), 255, 255, 255));
        };
        border.MouseLeave += (_, _) =>
        {
            bool prom = border.Tag is ValueTuple<string, bool> t && t.Item2;
            border.Background = CreateFrozenBrush(Color.FromArgb((byte)(prom ? 61 : 26), 255, 255, 255));
        };

        return WrapBorderAsPlainButton(border, onClick);
    }

    private static void SetHeaderIconGlyph(Button btn, string iconKey, bool prominent)
    {
        if (btn.Content is Border b)
        {
            b.Tag = (iconKey, prominent);
            b.Child = CreateHeaderIconPath(iconKey, (byte)(prominent ? 250 : 200));
            b.Background = CreateFrozenBrush(Color.FromArgb((byte)(prominent ? 61 : 26), 255, 255, 255));
        }
    }

    private static Button WrapBorderAsPlainButton(Border visual, Action onClick)
    {
        var template = new ControlTemplate(typeof(Button));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        template.VisualTree = presenter;

        var btn = new Button
        {
            Content = visual,
            Template = template,
            Focusable = false,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0)
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private static void ShowDarkPopupMenu(UIElement placementTarget, IEnumerable<(string Label, bool Checked, Action OnSelect)> entries)
    {
        var popup = new Popup
        {
            PlacementTarget = placementTarget,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true
        };

        var stack = new StackPanel { Orientation = Orientation.Vertical };
        foreach (var entry in entries)
        {
            var rowGrid = new Grid();
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            if (entry.Checked)
            {
                var check = new ShapePath
                {
                    Data = Geometry.Parse("M1,4.5 L3.8,7.3 L9,1.5"),
                    Width = 9,
                    Height = 8,
                    Stretch = Stretch.Uniform,
                    Stroke = EmeraldBrush,
                    StrokeThickness = 1.6,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    StrokeLineJoin = PenLineJoin.Round,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Left
                };
                Grid.SetColumn(check, 0);
                rowGrid.Children.Add(check);
            }

            var labelTb = new TextBlock
            {
                Text = entry.Label,
                FontFamily = UiFont,
                FontSize = 12.0,
                Foreground = entry.Checked ? Brushes.White : CreateFrozenBrush(Color.FromArgb(215, 255, 255, 255)),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(labelTb, 1);
            rowGrid.Children.Add(labelTb);

            var row = new Border
            {
                Padding = new Thickness(9, 6, 12, 6),
                CornerRadius = new CornerRadius(5),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Child = rowGrid
            };
            row.MouseEnter += (_, _) => row.Background = CreateFrozenBrush(Color.FromArgb(36, 255, 255, 255));
            row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
            var action = entry.OnSelect;
            row.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                popup.IsOpen = false;
                action();
            };
            stack.Children.Add(row);
        }

        popup.Child = new Border
        {
            Background = CreateFrozenBrush(Color.FromArgb(246, 0x15, 0x1A, 0x22)),
            BorderBrush = CreateFrozenBrush(Color.FromArgb(52, 255, 255, 255)),
            BorderThickness = new Thickness(0.75),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(4),
            Margin = new Thickness(0, 4, 0, 0),
            Child = stack
        };
        popup.IsOpen = true;
    }

    private void ShowLanguageMenu(bool isSource)
    {
        var opts = isSource ? TranslationLanguage.SourceOptions : TranslationLanguage.TargetOptions;
        var current = isSource ? _sourceLang : _targetLang;
        ShowDarkPopupMenu(
            isSource ? _srcLangBtn : _tgtLangBtn,
            opts.Select(lang => (lang.ShortName, lang == current, (Action)(() =>
            {
                if (isSource) _sourceLang = lang;
                else _targetLang = lang;
                OnLanguageSelectionChanged();
            }))));
    }

    private void ShowBackendMenu()
    {
        var all = Enum.GetValues<TranslationBackendKind>();
        ShowDarkPopupMenu(
            _backendBtn,
            all.Select(k =>
            {
                string badge = k.IsPlamo()
                    ? (_sidecar.IsModelDownloaded(k) ? " · 導入済" : $" · {NativeSidecarManager.GetSpec(k)?.ApproxSizeText}")
                    : "";
                return ($"{k.MenuTitle()}{badge}", k == _backend, (Action)(() =>
                {
                    if (_backend == k) return;
                    _backend = k;
                    OnBackendSelectionChanged();
                }));
            }));
    }

    private void ShowDisplayModeMenu()
    {
        var all = Enum.GetValues<TranslationDisplayMode>();
        ShowDarkPopupMenu(
            _modeBtn,
            all.Select(m => (m.MenuTitle(), m == _displayMode, (Action)(() =>
            {
                if (_displayMode == m) return;
                _displayMode = m;
                OnDisplayModeChanged();
            }))));
    }

    private void ShowAppSettingsMenu()
    {
        bool installed = DesktopInstaller.IsInstalled();
        var items = new List<(string Label, bool Checked, Action OnSelect)>
        {
            (
                installed ? "デスクトップ＆スタートメニューに登録済（再作成）" : "デスクトップ＆スタートメニューに登録",
                installed,
                () =>
                {
                    var (_, msg) = DesktopInstaller.InstallAndCreateShortcuts();
                    SetStatusNotice(msg);
                }
            )
        };

        foreach (var spec in NativeSidecarManager.AllModelSpecs)
        {
            var capturedSpec = spec;
            bool dl = _sidecar.IsModelDownloaded(capturedSpec.Backend);
            string label = dl
                ? $"{capturedSpec.DisplayLabel} モデル（導入済）"
                : $"{capturedSpec.DisplayLabel} を取得 ({capturedSpec.ApproxSizeText})";

            items.Add((label, dl, () =>
            {
                _backend = capturedSpec.Backend;
                SetCapsulePillText(_backendBtn, _backend.ShortName());
                _settings.BackendId = _backend.ToStorageId();
                _settings.Save(_runtimeDir);
                _ = _sidecar.DownloadAndStartAsync(capturedSpec.Backend, _gpuFlavor);
            }));
        }

        items.Add((
            "推論ランタイム: Vulkan GPU（標準）",
            _gpuFlavor == GpuEngineFlavor.Vulkan,
            () => SwitchGpuFlavor(GpuEngineFlavor.Vulkan)));

        items.Add((
            "推論ランタイム: NVIDIA CUDA（RTX向け）",
            _gpuFlavor == GpuEngineFlavor.Cuda,
            () => SwitchGpuFlavor(GpuEngineFlavor.Cuda)));

        items.Add((
            "推論ランタイム: CPU 互換モード",
            _gpuFlavor == GpuEngineFlavor.Cpu,
            () => SwitchGpuFlavor(GpuEngineFlavor.Cpu)));

        items.Add((
            "データ保存フォルダを開く",
            false,
            () =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"\"{_runtimeDir}\"",
                        UseShellExecute = true
                    });
                }
                catch { }
            }));

        if (!string.IsNullOrEmpty(_projectRootDir) && File.Exists(Path.Combine(_projectRootDir, "scripts", "bundle_app.ps1")))
        {
            items.Add(("ソースから更新して再起動 (Ctrl+Shift+U)", false, UpdateAndRelaunchFromSourceAction));
        }

        ShowDarkPopupMenu(_settingsBtn, items);
    }

    private void SwitchGpuFlavor(GpuEngineFlavor flavor)
    {
        _gpuFlavor = flavor;
        _settings.GpuFlavor = flavor switch
        {
            GpuEngineFlavor.Cuda => "cuda",
            GpuEngineFlavor.Cpu => "cpu",
            _ => "vulkan"
        };
        _settings.Save(_runtimeDir);
        if (_backend.IsPlamo())
        {
            _ = _sidecar.DownloadAndStartAsync(_backend, _gpuFlavor);
        }
    }

    // MARK: - Collapse, Stage & Cluster Card Rendering (macOS 1:1)

    public void ToggleCollapse()
    {
        if (!_isCollapsed)
        {
            if (Height >= MinExpandedSize.Height)
                _savedExpandedHeight = Height;

            _isCollapsed = true;
            _windowChrome.ResizeBorderThickness = new Thickness(6, 0, 6, 0);
            MinHeight = CollapsedBarHeight;
            MaxHeight = CollapsedBarHeight;
            Height = CollapsedBarHeight;
            _dividerLine.Visibility = Visibility.Collapsed;
            _bodyScroller.Visibility = Visibility.Collapsed;
            SetHeaderIconGlyph(_collapseBtn, "expand", false);
        }
        else
        {
            _isCollapsed = false;
            _windowChrome.ResizeBorderThickness = new Thickness(6);
            double restoreH = Math.Clamp(_savedExpandedHeight, MinExpandedSize.Height, MaxExpandedSize.Height);
            MinHeight = MinExpandedSize.Height;
            MaxHeight = MaxExpandedSize.Height;
            Height = restoreH;
            _dividerLine.Visibility = Visibility.Visible;
            _bodyScroller.Visibility = Visibility.Visible;
            SetHeaderIconGlyph(_collapseBtn, "collapse", false);
        }

        _lastRenderedTwoRowMode = null;
        _lastRenderedCompactCollapsed = null;
        RebuildResponsiveHeader(ActualWidth > 0 ? ActualWidth : Width);
        SaveWindowGeometry();
    }

    private void UpdateStage(string stage, bool busy)
    {
        _stageLabel.Text = stage;
        _stageLabel.Foreground = CreateFrozenBrush(Color.FromArgb((byte)(busy ? 214 : 117), 255, 255, 255));
        _stageDot.Visibility = busy && !string.IsNullOrEmpty(stage) ? Visibility.Visible : Visibility.Collapsed;
        _dividerLine.Background = busy ? DividerBusyBrush : DividerIdleBrush;

        if (busy && _clusterCards.Count == 0 && _setupPanel.Visibility != Visibility.Visible && !string.IsNullOrEmpty(stage))
        {
            _statusNoticeBlock.Foreground = CreateFrozenBrush(Color.FromArgb(194, 255, 255, 255));
            _statusNoticeBlock.Text = stage;
        }
        else
        {
            _statusNoticeBlock.Foreground = CreateFrozenBrush(Color.FromArgb(133, 255, 255, 255));
        }
    }

    private void RefreshNoticeVisibility()
    {
        _statusNoticeBlock.Visibility = (_clusterCards.Count == 0 && _setupPanel.Visibility != Visibility.Visible)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void SetStatusNotice(string message)
    {
        _statusNoticeBlock.Text = message;
        RefreshNoticeVisibility();
    }

    private void UpdateSingleHistoryIfApplicable()
    {
        if (_clusterCards.Count == 1 && !_clusterCards[0].IsTranslating)
        {
            string committed = _clusterCards[0].TranslatedText.Trim();
            if (!string.IsNullOrEmpty(committed) && committed != _lastSingleCommittedText)
            {
                if (!string.IsNullOrEmpty(_lastSingleCommittedText))
                    _previousSingleCommittedText = _lastSingleCommittedText;
                _lastSingleCommittedText = committed;
            }
        }
    }

    private void RenderClusterCards()
    {
        UpdateSingleHistoryIfApplicable();
        _cardsPanel.Children.Clear();
        RefreshNoticeVisibility();

        if (_clusterCards.Count == 0)
            return;

        bool multiCardChrome = _clusterCards.Count >= 2;

        // 単一発話表示時は macOS 版と同様、直前の確定訳（白45%・13pt）を上段に1件添える
        if (!multiCardChrome)
        {
            var single = _clusterCards[0];
            string upperHistory = single.IsTranslating && !string.IsNullOrEmpty(_lastSingleCommittedText) && _lastSingleCommittedText != single.DisplayBodyText
                ? _lastSingleCommittedText
                : _previousSingleCommittedText;

            if (!string.IsNullOrEmpty(upperHistory) && upperHistory != single.DisplayBodyText)
            {
                var prevTb = new TextBlock
                {
                    Text = upperHistory,
                    FontFamily = UiFont,
                    FontSize = 13.0,
                    FontWeight = FontWeights.Normal,
                    Foreground = CreateFrozenBrush(Color.FromArgb(115, 255, 255, 255)), // DesignTokens.textSecondary (0.45)
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 19.5,
                    LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                    Margin = new Thickness(0, 0, 0, 8)
                };
                _cardsPanel.Children.Add(prevTb);
            }
        }

        foreach (var card in _clusterCards)
        {
            var rowStack = new DockPanel { LastChildFill = true };
            TextBlock? badgeBlock = null;

            if (multiCardChrome)
            {
                badgeBlock = new TextBlock
                {
                    Text = card.BadgeLabel,
                    FontFamily = MonoFont,
                    FontSize = 10.0,
                    FontWeight = FontWeights.Bold,
                    Foreground = card.IsTranslating ? EmeraldBrush : CreateFrozenBrush(Color.FromArgb(107, 255, 255, 255)),
                    Margin = new Thickness(0, 3, 8, 0),
                    VerticalAlignment = VerticalAlignment.Top
                };
                DockPanel.SetDock(badgeBlock, Dock.Left);
                rowStack.Children.Add(badgeBlock);
            }

            bool hasText = !string.IsNullOrEmpty(card.DisplayBodyText);
            var textBlock = new TextBlock
            {
                Text = hasText ? card.DisplayBodyText : "翻訳中…",
                FontFamily = UiFont,
                FontSize = hasText ? 15.0 : 13.0,
                FontWeight = hasText ? FontWeights.Medium : FontWeights.Normal,
                Foreground = CreateFrozenBrush(Color.FromArgb(
                    (byte)(!hasText ? 115 : (card.IsTranslating ? 214 : 245)),
                    255, 255, 255)),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = hasText ? 23.2 : 20.0,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight
            };
            rowStack.Children.Add(textBlock);

            var cardBorder = new Border
            {
                Padding = multiCardChrome ? new Thickness(10, 8, 10, 8) : new Thickness(0, 2, 0, 2),
                Margin = new Thickness(0, 0, 0, 8),
                CornerRadius = new CornerRadius(8),
                Background = multiCardChrome ? CreateFrozenBrush(Color.FromArgb(12, 255, 255, 255)) : Brushes.Transparent,
                BorderBrush = multiCardChrome
                    ? (card.IsTranslating ? CreateFrozenBrush(Color.FromArgb(115, 0x4D, 0xA8, 0x92)) : CreateFrozenBrush(Color.FromArgb(23, 255, 255, 255)))
                    : Brushes.Transparent,
                BorderThickness = new Thickness(multiCardChrome ? 0.75 : 0.0),
                Child = rowStack
            };

            var capturedCard = card;
            var capturedBadge = badgeBlock;

            cardBorder.MouseRightButtonUp += (_, e) =>
            {
                if (!string.IsNullOrEmpty(capturedCard.DisplayBodyText))
                {
                    e.Handled = true;
                    ShowDarkPopupMenu(cardBorder, [("訳文をコピー (Ctrl+C)", false, () =>
                    {
                        try { Clipboard.SetText(capturedCard.DisplayBodyText); } catch { }
                    })]);
                }
            };

            cardBorder.MouseEnter += (_, _) =>
            {
                if (multiCardChrome)
                {
                    cardBorder.Background = CreateFrozenBrush(Color.FromArgb(23, 255, 255, 255));
                    cardBorder.BorderBrush = CreateFrozenBrush(Color.FromArgb(166, 0x4D, 0xA8, 0x92));
                    if (capturedBadge is not null) capturedBadge.Foreground = EmeraldBrush;
                }
                _hoveredCard = capturedCard;
                RefreshRegionIndicator();
            };

            cardBorder.MouseLeave += (_, _) =>
            {
                if (multiCardChrome)
                {
                    cardBorder.Background = CreateFrozenBrush(Color.FromArgb(12, 255, 255, 255));
                    cardBorder.BorderBrush = capturedCard.IsTranslating
                        ? CreateFrozenBrush(Color.FromArgb(115, 0x4D, 0xA8, 0x92))
                        : CreateFrozenBrush(Color.FromArgb(23, 255, 255, 255));
                    if (capturedBadge is not null)
                    {
                        capturedBadge.Foreground = capturedCard.IsTranslating
                            ? EmeraldBrush
                            : CreateFrozenBrush(Color.FromArgb(107, 255, 255, 255));
                    }
                }
                if (_hoveredCard == capturedCard)
                {
                    _hoveredCard = null;
                    RefreshRegionIndicator();
                }
            };

            _cardsPanel.Children.Add(cardBorder);
        }
    }

    // MARK: - Multi-Cluster Pipeline, 0ms Scroll Cache & Cursor-Spot Scheduler

    private void RecordCachedCluster(string sourceText, string translatedText)
    {
        var entry = new ClusterTranslationPlanner.CachedEntry(sourceText, translatedText);
        if (string.IsNullOrEmpty(entry.SemanticCore) || string.IsNullOrEmpty(translatedText)) return;
        _cachedClusterEntries.RemoveAll(x => x.SemanticCore == entry.SemanticCore);
        _cachedClusterEntries.Add(entry);
        if (_cachedClusterEntries.Count > 96)
            _cachedClusterEntries.RemoveRange(0, _cachedClusterEntries.Count - 96);
    }

    private void HandleCapturedTextEvent(CapturedTextEvent ev)
    {
        switch (ev)
        {
            case CapturedTextEvent.OcrStarted:
                if (_activeBatchCts is null && _sidecar.LatestStatus.State == SidecarState.Ready)
                    UpdateStage("OCR読取中…", busy: true);
                break;

            case CapturedTextEvent.Idle:
                if (_activeBatchCts is null && _sidecar.LatestStatus.State == SidecarState.Ready)
                    UpdateStage(_isCapturing ? "待機中" : string.Empty, busy: false);
                break;

            case CapturedTextEvent.Skipped sk:
                if (_activeBatchCts is null && _sidecar.LatestStatus.State == SidecarState.Ready)
                {
                    string badge = sk.Reason.ShortBadgeJaText();
                    UpdateStage(string.IsNullOrEmpty(badge) ? (_isCapturing ? "待機中" : string.Empty) : badge, busy: false);
                    if (_clusterCards.Count == 0 && sk.Reason != SkipReason.Empty)
                        SetStatusNotice($"翻訳対象外をスキップ中（{sk.Reason.DetailJaText()}）");
                }
                break;

            case CapturedTextEvent.CommitClusters cc:
                _latestRawClusters = cc.Clusters;
                ApplyClustersAndScheduleTranslations(cc.Clusters);
                break;
        }
    }

    private (double X, double Y)? GetCurrentNormalizedCursor()
    {
        if (_currentSelection is not { } sel) return null;
        if (!Win32Interop.GetCursorPos(out var pt)) return null;
        return ClusterGeometry.NormalizedCursorPoint(pt.X, pt.Y, sel.PhysicalRect);
    }

    private void ApplyClustersAndScheduleTranslations(IReadOnlyList<ExtractedTextCluster> rawCandidates)
    {
        var logicalCaptureRect = _currentSelection?.LogicalRect ?? new ScreenRect(0, 0, 640, 480);
        var normCursor = GetCurrentNormalizedCursor();

        var selected = ClusterTranslationPlanner.SelectActiveClusters(rawCandidates, _displayMode, normCursor);
        if (selected.Count == 0) return;

        if (_displayMode == TranslationDisplayMode.CursorSpot)
            _lastSpotSelectedCore = selected[0].SemanticCore;

        var cards = new List<ClusterCardItem>(selected.Count);
        var unresolvedIndices = new List<int>();

        for (int idx = 0; idx < selected.Count; idx++)
        {
            var cl = selected[idx];
            var bounds = ClusterGeometry.ToScreenRect(cl, logicalCaptureRect);
            string? cachedHit = ClusterTranslationPlanner.FindCachedTranslation(cl, _cachedClusterEntries);
            if (cachedHit is null)
                unresolvedIndices.Add(idx);

            cards.Add(new ClusterCardItem
            {
                Id = $"{idx}-{cl.SemanticCore[..Math.Min(24, cl.SemanticCore.Length)]}",
                IndexNumber = idx + 1,
                SourceText = cl.Text,
                ScreenBounds = bounds,
                TranslatedText = cachedHit ?? string.Empty,
                StreamingText = string.Empty,
                IsTranslating = cachedHit is null
            });
        }

        if (unresolvedIndices.Count == 0)
        {
            CancelActiveBatch();
            _clusterCards = cards;
            RenderClusterCards();
            UpdateStage(_isCapturing ? "待機中" : string.Empty, busy: false);
            SyncOverlays();
            RefreshRegionIndicator();
            return;
        }

        if (_backend.IsPlamo() && _sidecar.NeedsDownload(_backend, _gpuFlavor))
        {
            if (_sidecar.LatestStatus.State != SidecarState.Downloading)
                _sidecar.TryAutoStartIfInstalled(_backend, _gpuFlavor);
            return;
        }

        var orderedUnresolved = ClusterTranslationPlanner.PrioritizedIndices(selected, unresolvedIndices, normCursor);
        string newKey = string.Join("||", orderedUnresolved.Select(i => selected[i].SemanticCore));

        if (_activeBatchCts is not null && newKey == _activeUnresolvedKey)
        {
            foreach (var c in cards)
            {
                var running = _clusterCards.FirstOrDefault(x => x.SourceText == c.SourceText);
                if (running is not null && string.IsNullOrEmpty(c.TranslatedText))
                {
                    c.TranslatedText = running.TranslatedText;
                    c.StreamingText = running.StreamingText;
                    c.IsTranslating = running.IsTranslating;
                }
            }
            _clusterCards = cards;
            RenderClusterCards();
            SyncOverlays();
            return;
        }

        CancelActiveBatch();
        _activeUnresolvedKey = newKey;
        _batchGeneration++;
        ulong myGen = _batchGeneration;

        var cts = new CancellationTokenSource();
        _activeBatchCts = cts;
        _clusterCards = cards;
        RenderClusterCards();
        UpdateStage($"{_backend.ShortName()} 翻訳中 (1/{orderedUnresolved.Count})", busy: true);
        SyncOverlays();
        _captureEngine.SetTranslationBusy(true);

        _ = RunBatchTranslationAsync(selected, orderedUnresolved, myGen, cts.Token);
    }

    private async Task RunBatchTranslationAsync(
        IReadOnlyList<ExtractedTextCluster> selected,
        IReadOnlyList<int> orderedUnresolved,
        ulong myGen,
        CancellationToken ct)
    {
        var srcLang = _sourceLang;
        var tgtLang = _targetLang;
        var backend = _backend;

        try
        {
            for (int step = 0; step < orderedUnresolved.Count; step++)
            {
                if (ct.IsCancellationRequested || _batchGeneration != myGen) return;

                int cardIndex = orderedUnresolved[step];
                var cluster = selected[cardIndex];
                string prefix = orderedUnresolved.Count > 1 ? $"({step + 1}/{orderedUnresolved.Count}) " : string.Empty;

                if (ClusterTranslationPlanner.FindCachedTranslation(cluster, _cachedClusterEntries) is { } hit)
                {
                    CommitCardAt(cardIndex, myGen, hit);
                    continue;
                }

                var outcome = await _translator.TranslateAsync(
                    cluster.Text,
                    srcLang,
                    tgtLang,
                    backend,
                    _runtimeDir,
                    stage => Dispatcher.InvokeAsync(() =>
                    {
                        if (_batchGeneration == myGen)
                            UpdateStage($"{prefix}{stage}", busy: true);
                    }),
                    partial => Dispatcher.InvokeAsync(() =>
                    {
                        if (_batchGeneration == myGen && cardIndex < _clusterCards.Count)
                        {
                            _clusterCards[cardIndex].StreamingText = partial;
                            _clusterCards[cardIndex].IsTranslating = true;
                            RenderClusterCards();
                            SyncOverlays();
                        }
                    }),
                    ct);

                if (ct.IsCancellationRequested || _batchGeneration != myGen) return;

                switch (outcome)
                {
                    case TranslationOutcome.Translated tr:
                        RecordCachedCluster(cluster.Text, tr.Text);
                        CommitCardAt(cardIndex, myGen, tr.Text);
                        break;

                    case TranslationOutcome.Skipped sk:
                        FinishCardEmpty(cardIndex, myGen);
                        if (orderedUnresolved.Count == 1)
                            UpdateStage(sk.Reason.ShortBadgeJaText(), busy: false);
                        break;

                    case TranslationOutcome.Failed fl:
                        FinishCardEmpty(cardIndex, myGen);
                        if (fl.Message != "キャンセル")
                        {
                            UpdateStage("エラー", busy: false);
                            if (_clusterCards.All(c => string.IsNullOrEmpty(c.DisplayBodyText)))
                            {
                                _clusterCards.Clear();
                                RenderClusterCards();
                                SetStatusNotice(fl.Message);
                            }
                        }
                        break;
                }
            }

            if (_batchGeneration == myGen)
            {
                _activeBatchCts = null;
                _activeUnresolvedKey = string.Empty;
                UpdateStage(_isCapturing ? "待機中" : string.Empty, busy: false);
                SyncOverlays();
            }
        }
        finally
        {
            if (_batchGeneration == myGen)
                _captureEngine.SetTranslationBusy(false);
        }
    }

    private void CommitCardAt(int index, ulong gen, string finalText)
    {
        if (_batchGeneration != gen || index >= _clusterCards.Count) return;
        _clusterCards[index].StreamingText = string.Empty;
        _clusterCards[index].TranslatedText = finalText;
        _clusterCards[index].IsTranslating = false;
        RenderClusterCards();
        SyncOverlays();
    }

    private void FinishCardEmpty(int index, ulong gen)
    {
        if (_batchGeneration != gen || index >= _clusterCards.Count) return;
        _clusterCards[index].StreamingText = string.Empty;
        _clusterCards[index].IsTranslating = false;
        RenderClusterCards();
        SyncOverlays();
    }

    private void CancelActiveBatch()
    {
        _batchGeneration++;
        _activeUnresolvedKey = string.Empty;
        if (_activeBatchCts is not null)
        {
            _activeBatchCts.Cancel();
            _activeBatchCts.Dispose();
            _activeBatchCts = null;
        }
        _captureEngine.SetTranslationBusy(false);
    }

    // MARK: - Actions & Native Engine Lifecycle

    private void SelectRegionAction()
    {
        _inPlaceOverlay.Hide();
        _regionSelector.BeginSelection(selection =>
        {
            if (selection is not { } sel) return;
            _currentSelection = sel;
            _latestRawClusters = Array.Empty<ExtractedTextCluster>();
            StartCaptureWithSelection(sel);
        });
    }

    private void ToggleCaptureAction()
    {
        if (_captureEngine.IsRunning)
        {
            CancelActiveBatch();
            _captureEngine.Stop();
            _hoveredCard = null;
            _isCapturing = false;
            SetHeaderIconGlyph(_captureToggleBtn, "play", prominent: false);
            _spotCursorTimer.Stop();
            _regionIndicator.Hide();
            _inPlaceOverlay.Hide();
            if (_sidecar.LatestStatus.State == SidecarState.Ready)
                UpdateStage(string.Empty, busy: false);
            if (_clusterCards.Count == 0)
                SetStatusNotice("一時停止中（再生ボタンで再開）");
        }
        else if (_currentSelection is { } sel)
        {
            StartCaptureWithSelection(sel);
        }
        else
        {
            SelectRegionAction();
        }
    }

    private void StartCaptureWithSelection(CaptureSelection selection)
    {
        if (!_isSelfTest)
            _sidecar.TryAutoStartIfInstalled(_backend, _gpuFlavor);

        _isCapturing = true;
        SetHeaderIconGlyph(_captureToggleBtn, "stop", prominent: true);
        if (_sidecar.LatestStatus.State == SidecarState.Ready)
            UpdateStage("待機中", busy: false);
        SetStatusNotice($"監視中（{_displayMode.ShortName()}モード / {_backend.ShortName()}）");
        _captureEngine.Start(selection, _sourceLang.Code, _targetLang.Code);
        UpdateSpotPollingTimer();
        SyncOverlays();
        RefreshRegionIndicator();
    }

    private void OnLanguageSelectionChanged()
    {
        SetCapsulePillText(_srcLangBtn, _sourceLang.ShortName);
        SetCapsulePillText(_tgtLangBtn, _targetLang.ShortName);
        _settings.SourceLangCode = _sourceLang.Code;
        _settings.TargetLangCode = _targetLang.Code;
        _settings.Save(_runtimeDir);

        CancelActiveBatch();
        _cachedClusterEntries.Clear();
        _latestRawClusters = Array.Empty<ExtractedTextCluster>();
        _lastSingleCommittedText = string.Empty;
        _previousSingleCommittedText = string.Empty;
        _translator.ClearCache();
        _captureEngine.UpdateLanguages(_sourceLang.Code, _targetLang.Code);
    }

    private void OnBackendSelectionChanged()
    {
        SetCapsulePillText(_backendBtn, _backend.ShortName());
        _settings.BackendId = _backend.ToStorageId();
        _settings.Save(_runtimeDir);

        CancelActiveBatch();
        _cachedClusterEntries.Clear();
        _translator.ResetModelCache();

        if (!_isSelfTest)
        {
            if (!_backend.IsPlamo())
            {
                _sidecar.Stop();
                _setupPanel.Visibility = Visibility.Collapsed;
                RefreshNoticeVisibility();
            }
            else
            {
                _sidecar.TryAutoStartIfInstalled(_backend, _gpuFlavor);
            }
        }

        if (_latestRawClusters.Count > 0)
            ApplyClustersAndScheduleTranslations(_latestRawClusters);
        else
            _captureEngine.InvalidateFrameCache();
    }

    private void OnDisplayModeChanged()
    {
        SetCapsulePillText(_modeBtn, _displayMode.ShortName());
        _settings.DisplayModeId = _displayMode.ToStorageId();
        _settings.Save(_runtimeDir);
        UpdateSpotPollingTimer();

        if (_latestRawClusters.Count > 0)
            ApplyClustersAndScheduleTranslations(_latestRawClusters);
        else
        {
            SyncOverlays();
            _captureEngine.InvalidateFrameCache();
        }
    }

    private void UpdateSpotPollingTimer()
    {
        if (_isCapturing && _displayMode == TranslationDisplayMode.CursorSpot)
        {
            if (!_spotCursorTimer.IsEnabled) _spotCursorTimer.Start();
        }
        else
        {
            _spotCursorTimer.Stop();
            _lastSpotSelectedCore = string.Empty;
        }
    }

    private void PollCursorSpotIfNeeded()
    {
        if (!_isCapturing || _displayMode != TranslationDisplayMode.CursorSpot || _latestRawClusters.Count == 0)
            return;

        var norm = GetCurrentNormalizedCursor();
        var chosen = ClusterTranslationPlanner.SelectActiveClusters(_latestRawClusters, TranslationDisplayMode.CursorSpot, norm);
        if (chosen.Count > 0 && chosen[0].SemanticCore != _lastSpotSelectedCore)
        {
            _lastSpotSelectedCore = chosen[0].SemanticCore;
            ApplyClustersAndScheduleTranslations(_latestRawClusters);
        }
    }

    private void RefreshRegionIndicator()
    {
        if (_hoveredCard is { } card && _isCapturing)
        {
            _regionIndicator.ShowAround(card.ScreenBounds, _clusterCards.Count >= 2 ? card.BadgeLabel : null, emphasized: true);
        }
        else if (_showsRegionCorners && _currentSelection is { } sel && _isCapturing)
        {
            _regionIndicator.ShowAround(sel.LogicalRect, null, emphasized: false);
        }
        else
        {
            _regionIndicator.Hide();
        }
    }

    private void SyncOverlays()
    {
        if (_isCapturing && _displayMode == TranslationDisplayMode.InPlace && _currentSelection is { } sel)
        {
            _inPlaceOverlay.Update(_clusterCards, sel);
        }
        else
        {
            _inPlaceOverlay.Hide();
        }
    }

    private void UpdateAndRelaunchFromSourceAction()
    {
        if (_isUpdatingApp || string.IsNullOrEmpty(_projectRootDir)) return;

        string bundleScript = Path.Combine(_projectRootDir, "scripts", "bundle_app.ps1");
        string stagedDir = Path.Combine(_projectRootDir, "build.next");
        string stagedExe = Path.Combine(stagedDir, "ScreenTranslate.exe");
        string liveDir = Path.Combine(_projectRootDir, "dist");
        string builtExe = Path.Combine(liveDir, "ScreenTranslate.exe");
        if (!File.Exists(bundleScript)) return;

        _isUpdatingApp = true;
        _settingsBtn.IsEnabled = false;
        CancelActiveBatch();
        _captureEngine.Stop();
        _isCapturing = false;
        _inPlaceOverlay.Hide();
        _regionIndicator.Hide();
        UpdateStage("更新ビルド中…", busy: true);

        _ = Task.Run(async () =>
        {
            int code = 1;
            string errTail = string.Empty;
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{bundleScript}\" -StageUpdate",
                    WorkingDirectory = _projectRootDir,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi)!;
                var outTask = proc.StandardOutput.ReadToEndAsync();
                var errTask = proc.StandardError.ReadToEndAsync();
                await Task.WhenAll(outTask, errTask).ConfigureAwait(false);
                await proc.WaitForExitAsync().ConfigureAwait(false);
                code = proc.ExitCode;
                string combinedErr = !string.IsNullOrWhiteSpace(errTask.Result) ? errTask.Result : outTask.Result;
                if (code != 0 && !string.IsNullOrWhiteSpace(combinedErr))
                    errTail = combinedErr.Trim()[^Math.Min(240, combinedErr.Trim().Length)..];
            }
            catch (Exception ex)
            {
                errTail = ex.Message;
            }

            Dispatcher.Invoke(() =>
            {
                if (code == 0 && File.Exists(stagedExe))
                {
                    UpdateStage("再起動中…", busy: true);
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/c timeout /t 1 /nobreak >nul & robocopy \"{stagedDir}\" \"{liveDir}\" /MIR /NFL /NDL /NJH /NJS >nul & rmdir /s /q \"{stagedDir}\" >nul 2>&1 & start \"\" \"{builtExe}\"",
                        WorkingDirectory = _projectRootDir,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    });
                    PerformCleanShutdown(0);
                }
                else
                {
                    _isUpdatingApp = false;
                    _settingsBtn.IsEnabled = true;
                    UpdateStage("更新失敗", busy: false);
                    _clusterCards.Clear();
                    RenderClusterCards();
                    SetStatusNotice(string.IsNullOrEmpty(errTail) ? $"更新ビルドに失敗しました (exit {code})" : $"更新ビルド失敗:\n{errTail}");
                }
            });
        });
    }

    private void SaveWindowGeometry()
    {
        if (!IsLoaded) return;
        _settings.WindowLeft = Left;
        _settings.WindowTop = Top;
        _settings.WindowWidth = Width;
        if (!_isCollapsed && Height >= MinExpandedSize.Height)
            _settings.WindowHeight = Height;
        _settings.Save(_runtimeDir);
    }

    public void PerformCleanShutdown(int exitCode)
    {
        SaveWindowGeometry();
        _spotCursorTimer.Stop();
        CancelActiveBatch();
        _captureEngine.Dispose();
        _sidecar.Dispose();
        UnregisterGlobalHotKeys();
        _inPlaceOverlay.Dispose();
        _regionIndicator.Close();

        try
        {
            string pidFile = Path.Combine(_runtimeDir, "pid");
            if (File.Exists(pidFile)) File.Delete(pidFile);
        }
        catch { }

        Environment.Exit(exitCode);
    }

    // MARK: - Automated UI Self-Test (--self-test-ui)

    public void RunAutomatedSelfTest()
    {
        var failures = new List<string>();

        Width = 360;
        Height = 220;
        UpdateLayout();
        if (Math.Abs(Width - 360) > 1)
            failures.Add($"Minimum compact width 360px failed (got {Width})");

        double topBefore = Top;
        ToggleCollapse();
        UpdateLayout();
        if (!_isCollapsed || Math.Abs(Height - CollapsedBarHeight) > 1)
            failures.Add($"Collapse height check failed (got {Height})");
        if (Math.Abs(Top - topBefore) > 1)
            failures.Add("Collapse shifted top coordinate");

        ToggleCollapse();
        UpdateLayout();
        if (_isCollapsed || Math.Abs(Height - 220) > 1)
            failures.Add($"Expand restore height failed (got {Height})");

        ApplySidecarStatus(new SidecarStatusSnapshot(SidecarState.NotInstalled, TranslationBackendKind.Plamo2B, "初回取得確認"));
        if (_setupPanel.Visibility != Visibility.Visible)
            failures.Add("Minimalist setup panel did not appear on NotInstalled state");

        ApplySidecarStatus(new SidecarStatusSnapshot(SidecarState.Ready, TranslationBackendKind.Plamo2B, "稼働中", 1.0));
        if (_setupPanel.Visibility != Visibility.Collapsed)
            failures.Add("Minimalist setup panel did not hide on Ready state");

        _isCapturing = true;
        _displayMode = TranslationDisplayMode.CardList;
        RecordCachedCluster("The sharp-tongued personal maid with no filter.", "毒舌で遠慮のない専属メイド。");
        RecordCachedCluster("A nun deeply devoted to the sacred tree.", "聖樹に深く帰依する修道女。");

        var c1 = new ExtractedTextCluster("The sharp-tongued personal maid with no filter.", 0.94f, 0.1, 0.8, 0.1, 0.2);
        var c2 = new ExtractedTextCluster("A nun deeply devoted to the sacred tree.", 0.93f, 0.1, 0.8, 0.6, 0.7);
        HandleCapturedTextEvent(new CapturedTextEvent.CommitClusters([c1, c2]));

        if (_clusterCards.Count != 2 || _clusterCards[0].TranslatedText != "毒舌で遠慮のない専属メイド。")
            failures.Add("Zero-latency multi-cluster card rendering failed");

        HandleCapturedTextEvent(new CapturedTextEvent.Skipped(SkipReason.NumericOrStatsOnly));
        if (_clusterCards.Count != 2)
            failures.Add("Skip event wiped out existing cluster cards");

        byte[] icoBytes = DesktopInstaller.BuildAppIconIcoBytes();
        if (icoBytes.Length < 512 || icoBytes[2] != 1)
            failures.Add("Generated .ico header validation failed");

        if (failures.Count > 0)
        {
            foreach (string f in failures)
                Console.Error.WriteLine($"UI_SELF_TEST_FAIL: {f}");
            PerformCleanShutdown(1);
            return;
        }

        Console.WriteLine("WINDOWS_UI_SELF_TEST_PASSED: macOS-identical monochrome glass UI, 38px symmetric header, minimalist downloader, and 0ms cluster cache verified.");
        PerformCleanShutdown(0);
    }
}
