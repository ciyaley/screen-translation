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
/// スタンドアロン Windows デスクトップ版 ScreenTranslate メインウィンドウ。
/// - 単一 .exe ダブルクリック起動・アプリ内モデル自動ダウンローダー（進捗バー＆レジューム対応）・JobObject 自動終了。
/// - DWM ダークアクリル背後ぼかし + WDA_EXCLUDEFROMCAPTURE 自己除外 + カラフル装飾ゼロ。
/// - 最小幅 360px からの動的 1段 ⇄ 2段折り返しヘッダー + 3表示モード（カード / 直接重畳 / 近傍のみ）。
/// </summary>
internal sealed class MainTranslationWindow : Window
{
    public const double CollapsedBarHeight = 38.0;
    public const double SingleRowThreshold = 520.0;
    public static readonly Size MinExpandedSize = new(360.0, 155.0);
    public static readonly Size MaxExpandedSize = new(920.0, 680.0);

    private static readonly Color EmeraldRgb = Color.FromRgb(0x4D, 0xA8, 0x92);
    private static readonly SolidColorBrush EmeraldBrush = CreateFrozenBrush(EmeraldRgb);
    private static readonly SolidColorBrush DividerIdleBrush = CreateFrozenBrush(Color.FromArgb(26, 255, 255, 255));
    private static readonly SolidColorBrush DividerBusyBrush = CreateFrozenBrush(Color.FromArgb(215, 0x4D, 0xA8, 0x92));

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
    private double _savedExpandedHeight = 235.0;

    private IReadOnlyList<ExtractedTextCluster> _latestRawClusters = Array.Empty<ExtractedTextCluster>();
    private readonly List<ClusterTranslationPlanner.CachedEntry> _cachedClusterEntries = new();
    private List<ClusterCardItem> _clusterCards = new();
    private ClusterCardItem? _hoveredCard;

    private CancellationTokenSource? _activeBatchCts;
    private string _activeUnresolvedKey = string.Empty;
    private ulong _batchGeneration;
    private string _lastSpotSelectedCore = string.Empty;

    private readonly DispatcherTimer _spotCursorTimer;
    private HwndSource? _hwndSource;

    // UI Elements
    private readonly Border _headerHost = new();
    private readonly Border _dividerLine = new() { Height = 1.0, Background = DividerIdleBrush };
    private readonly ScrollViewer _bodyScroller = new();
    private readonly Border _setupCardBorder = new() { Visibility = Visibility.Collapsed };
    private readonly TextBlock _setupTitleBlock = new();
    private readonly TextBlock _setupDetailBlock = new();
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
        _gpuFlavor = string.Equals(_settings.GpuFlavor, "cuda", StringComparison.OrdinalIgnoreCase)
            ? GpuEngineFlavor.Cuda
            : GpuEngineFlavor.Vulkan;

        Title = "ScreenTranslate";
        Icon = DesktopInstaller.CreateWindowIcon();
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        Topmost = true;
        ShowInTaskbar = true;
        Background = CreateFrozenBrush(Color.FromArgb(208, 0x12, 0x16, 0x1C));

        MinWidth = MinExpandedSize.Width;
        MinHeight = MinExpandedSize.Height;
        MaxWidth = MaxExpandedSize.Width;
        MaxHeight = MaxExpandedSize.Height;

        double initW = Math.Clamp(double.IsNaN(_settings.WindowWidth) ? 470.0 : _settings.WindowWidth, MinExpandedSize.Width, MaxExpandedSize.Width);
        double initH = Math.Clamp(double.IsNaN(_settings.WindowHeight) ? 235.0 : _settings.WindowHeight, MinExpandedSize.Height, MaxExpandedSize.Height);
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

        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(-1),
            CornerRadius = new CornerRadius(12),
            UseAeroCaptionButtons = false
        });

        BuildUiTree();
        RebuildResponsiveHeader(initW);
        SetStatusNotice("範囲ボタン（または Alt+Shift+R）で翻訳したい画面範囲をドラッグ選択");

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

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        Win32Interop.EnableDarkAcrylicBackdrop(hwnd);
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
        else if (ctrl && shift && e.Key == Key.U && !string.IsNullOrEmpty(_projectRootDir))
        {
            e.Handled = true;
            UpdateAndRelaunchFromSourceAction();
        }
    }

    // MARK: - Responsive UI & In-App Model Downloader Card

    private void BuildUiTree()
    {
        var outerFrame = new Border
        {
            CornerRadius = new CornerRadius(12),
            BorderBrush = CreateFrozenBrush(Color.FromArgb(52, 255, 255, 255)),
            BorderThickness = new Thickness(0.8),
            Background = CreateFrozenBrush(Color.FromArgb(18, 255, 255, 255))
        };

        outerFrame.MouseLeftButtonDown += (_, e) =>
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

        BuildSetupCard();

        _statusNoticeBlock.FontFamily = new FontFamily("Yu Gothic UI, Meiryo UI, Segoe UI");
        _statusNoticeBlock.FontSize = 13.5;
        _statusNoticeBlock.Foreground = CreateFrozenBrush(Color.FromArgb(135, 255, 255, 255));
        _statusNoticeBlock.TextWrapping = TextWrapping.Wrap;
        _statusNoticeBlock.LineHeight = 21.0;

        var bodyStack = new StackPanel { Margin = new Thickness(14, 11, 14, 11) };
        bodyStack.Children.Add(_setupCardBorder);
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

        outerFrame.Child = rootGrid;
        Content = outerFrame;
    }

    private void BuildSetupCard()
    {
        _setupTitleBlock.FontFamily = new FontFamily("Yu Gothic UI, Segoe UI");
        _setupTitleBlock.FontSize = 13.0;
        _setupTitleBlock.FontWeight = FontWeights.SemiBold;
        _setupTitleBlock.Foreground = CreateFrozenBrush(Color.FromArgb(240, 255, 255, 255));
        _setupTitleBlock.TextWrapping = TextWrapping.Wrap;

        _setupDetailBlock.FontFamily = new FontFamily("Yu Gothic UI, Consolas, Segoe UI");
        _setupDetailBlock.FontSize = 11.5;
        _setupDetailBlock.Foreground = CreateFrozenBrush(Color.FromArgb(165, 255, 255, 255));
        _setupDetailBlock.TextWrapping = TextWrapping.Wrap;
        _setupDetailBlock.Margin = new Thickness(0, 4, 0, 8);

        _progressFill.Height = 5.0;
        _progressFill.CornerRadius = new CornerRadius(2.5);
        _progressFill.Background = EmeraldBrush;
        _progressFill.Width = 0.0;

        _progressTrack.Height = 5.0;
        _progressTrack.CornerRadius = new CornerRadius(2.5);
        _progressTrack.Background = CreateFrozenBrush(Color.FromArgb(28, 255, 255, 255));
        _progressTrack.Margin = new Thickness(0, 0, 0, 9);
        _progressTrack.Child = _progressFill;

        var inner = new StackPanel { Orientation = Orientation.Vertical };
        inner.Children.Add(_setupTitleBlock);
        inner.Children.Add(_setupDetailBlock);
        inner.Children.Add(_progressTrack);
        inner.Children.Add(_setupActionsPanel);

        _setupCardBorder.Padding = new Thickness(12, 10, 12, 10);
        _setupCardBorder.Margin = new Thickness(0, 0, 0, 10);
        _setupCardBorder.CornerRadius = new CornerRadius(9);
        _setupCardBorder.Background = CreateFrozenBrush(Color.FromArgb(20, 255, 255, 255));
        _setupCardBorder.BorderBrush = CreateFrozenBrush(Color.FromArgb(110, 0x4D, 0xA8, 0x92));
        _setupCardBorder.BorderThickness = new Thickness(0.9);
        _setupCardBorder.Child = inner;
    }

    private Button MakeActionPillButton(string label, bool accent, Action onClick)
    {
        var tb = new TextBlock
        {
            Text = label,
            FontFamily = new FontFamily("Yu Gothic UI, Segoe UI"),
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = accent ? Brushes.White : CreateFrozenBrush(Color.FromArgb(215, 255, 255, 255)),
            VerticalAlignment = VerticalAlignment.Center
        };

        var bgIdle = accent
            ? CreateFrozenBrush(Color.FromArgb(62, 0x4D, 0xA8, 0x92))
            : CreateFrozenBrush(Color.FromArgb(24, 255, 255, 255));
        var bgHover = accent
            ? CreateFrozenBrush(Color.FromArgb(98, 0x4D, 0xA8, 0x92))
            : CreateFrozenBrush(Color.FromArgb(44, 255, 255, 255));

        var border = new Border
        {
            Height = 26,
            CornerRadius = new CornerRadius(6),
            Background = bgIdle,
            BorderBrush = accent ? EmeraldBrush : CreateFrozenBrush(Color.FromArgb(48, 255, 255, 255)),
            BorderThickness = new Thickness(0.8),
            Padding = new Thickness(10, 0, 10, 0),
            Margin = new Thickness(0, 0, 7, 6),
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
                string modelName = specOpt?.DisplayLabel ?? _backend.ShortName();
                string sizeText = specOpt?.ApproxSizeText ?? "";
                long partialBytes = _sidecar.GetPartialDownloadBytes(_backend);
                bool hasPartial = partialBytes > 1024 * 1024;

                _setupTitleBlock.Text = snap.State == SidecarState.Error
                    ? "翻訳エンジンのセットアップ確認"
                    : "翻訳モデルの初回ダウンロード（アプリ内完結）";

                _setupDetailBlock.Text = hasPartial
                    ? $"{snap.Message}\n一時保存済みデータ ({partialBytes / (1024 * 1024)} MB) から続きを再開できます。"
                    : $"{snap.Message}\n一度ダウンロードすると次回起動からはオフラインで即座に開始されます。";

                _progressTrack.Visibility = Visibility.Collapsed;
                _setupActionsPanel.Children.Clear();

                string primaryLabel = hasPartial
                    ? $"続きからダウンロード再開 ({_backend.ShortName()})"
                    : $"{_backend.ShortName()} をダウンロードして開始 ({sizeText})";

                _setupActionsPanel.Children.Add(MakeActionPillButton(primaryLabel, accent: true, () =>
                {
                    _ = _sidecar.DownloadAndStartAsync(_backend, _gpuFlavor);
                }));

                if (_backend != TranslationBackendKind.Plamo08B)
                {
                    _setupActionsPanel.Children.Add(MakeActionPillButton("高速・軽量 0.8B (約570 MB) で開始", accent: false, () =>
                    {
                        _backend = TranslationBackendKind.Plamo08B;
                        SetCapsulePillText(_backendBtn, _backend.ShortName());
                        _settings.BackendId = _backend.ToStorageId();
                        _settings.Save(_runtimeDir);
                        _ = _sidecar.DownloadAndStartAsync(_backend, _gpuFlavor);
                    }));
                }

                _setupCardBorder.Visibility = Visibility.Visible;
                UpdateStage(snap.State == SidecarState.Error ? "要確認" : "未導入", busy: false);
                RefreshNoticeVisibility();
                break;
            }

            case SidecarState.Downloading:
            {
                _setupTitleBlock.Text = snap.Message;
                _setupDetailBlock.Text = NativeSidecarManager.FormatProgressSummary(snap);
                _progressTrack.Visibility = Visibility.Visible;

                double trackW = Math.Max(180.0, (ActualWidth > 0 ? ActualWidth : Width) - 56.0);
                _progressFill.Width = Math.Clamp(trackW * snap.ProgressFraction, 6.0, trackW);

                _setupActionsPanel.Children.Clear();
                _setupActionsPanel.Children.Add(MakeActionPillButton("一時停止", accent: false, () =>
                {
                    _sidecar.CancelActiveOperation();
                }));

                _setupCardBorder.Visibility = Visibility.Visible;
                int pct = (int)Math.Round(snap.ProgressFraction * 100.0);
                UpdateStage($"DL中 {pct}%", busy: true);
                RefreshNoticeVisibility();
                break;
            }

            case SidecarState.Starting:
            {
                _setupTitleBlock.Text = snap.Message;
                _setupDetailBlock.Text = "GPU メモリへ翻訳モデルを展開しています…";
                _progressTrack.Visibility = Visibility.Visible;
                double trackW = Math.Max(180.0, (ActualWidth > 0 ? ActualWidth : Width) - 56.0);
                _progressFill.Width = trackW;
                _setupActionsPanel.Children.Clear();
                _setupCardBorder.Visibility = Visibility.Visible;
                UpdateStage("GPUロード中…", busy: true);
                RefreshNoticeVisibility();
                break;
            }

            case SidecarState.Ready:
            {
                _setupCardBorder.Visibility = Visibility.Collapsed;
                if (_activeBatchCts is null)
                    UpdateStage(_isCapturing ? "待機中" : "準備完了", busy: false);
                RefreshNoticeVisibility();

                // 監視中にモデルDLが完了した場合、保留中のクラスタがあれば自動で翻訳開始
                if (_isCapturing && _latestRawClusters.Count > 0 && _activeBatchCts is null)
                {
                    ApplyClustersAndScheduleTranslations(_latestRawClusters);
                }
                break;
            }

            case SidecarState.Stopped:
            {
                if (!_sidecar.NeedsDownload(_backend, _gpuFlavor))
                    _setupCardBorder.Visibility = Visibility.Collapsed;
                RefreshNoticeVisibility();
                break;
            }
        }
    }

    private void CreateHeaderControls()
    {
        _srcLangBtn = MakeCapsulePillButton(_sourceLang.ShortName, true, () => ShowLanguageMenu(isSource: true));
        _tgtLangBtn = MakeCapsulePillButton(_targetLang.ShortName, true, () => ShowLanguageMenu(isSource: false));
        _backendBtn = MakeCapsulePillButton(_backend.ShortName(), false, ShowBackendMenu);
        _modeBtn = MakeCapsulePillButton(_displayMode.ShortName(), false, ShowDisplayModeMenu);

        _stageDot = new Border
        {
            Width = 6,
            Height = 6,
            CornerRadius = new CornerRadius(3),
            Background = EmeraldBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 5, 0),
            Visibility = Visibility.Collapsed
        };

        _stageLabel = new TextBlock
        {
            FontFamily = new FontFamily("Yu Gothic UI, Segoe UI"),
            FontSize = 10.5,
            Foreground = CreateFrozenBrush(Color.FromArgb(120, 255, 255, 255)),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        _stageBadgePanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 4, 0)
        };
        _stageBadgePanel.Children.Add(_stageDot);
        _stageBadgePanel.Children.Add(_stageLabel);

        _captureToggleBtn = MakeHeaderIconButton("play", "監視を開始 / 停止 (Alt+Shift+T)", ToggleCaptureAction);
        _settingsBtn = MakeHeaderIconButton("settings", "モデル管理・PCショートカット登録", ShowAppSettingsMenu);
        _collapseBtn = MakeHeaderIconButton("collapse", "バーに折りたたむ / 広げる (Ctrl+K)", ToggleCollapse);
    }

    private void RebuildResponsiveHeader(double currentWidth)
    {
        bool twoRow = !_isCollapsed && currentWidth < SingleRowThreshold;
        if (_lastRenderedTwoRowMode == twoRow)
            return;
        _lastRenderedTwoRowMode = twoRow;

        DetachFromParent(_srcLangBtn);
        DetachFromParent(_tgtLangBtn);
        DetachFromParent(_backendBtn);
        DetachFromParent(_modeBtn);
        DetachFromParent(_stageBadgePanel);
        DetachFromParent(_captureToggleBtn);
        DetachFromParent(_settingsBtn);
        DetachFromParent(_collapseBtn);

        var closeBtn = MakeCircleCloseButton(() => PerformCleanShutdown(0));
        var rangeBtn = MakeHeaderIconButton("region", "監視範囲を選択 (Alt+Shift+R)", SelectRegionAction);

        var langPair = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        langPair.Children.Add(_srcLangBtn);
        langPair.Children.Add(new TextBlock
        {
            Text = "→",
            FontSize = 10.5,
            Foreground = CreateFrozenBrush(Color.FromArgb(100, 255, 255, 255)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 4, 0)
        });
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
                Margin = new Thickness(11, 0, 11, 0)
            };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var leftGroup = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            leftGroup.Children.Add(closeBtn);
            leftGroup.Children.Add(langPair);
            if (!_isCollapsed || currentWidth >= SingleRowThreshold)
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
                Margin = new Thickness(11, 7, 11, 6)
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
            _modeBtn.Margin = new Thickness(0, 0, 6, 0);
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

        RebuildResponsiveHeader(e.NewSize.Width);
        SaveWindowGeometry();
    }

    // MARK: - Custom Monochrome Controls & Menus

    private static Button MakeCircleCloseButton(Action onClick)
    {
        var label = new TextBlock
        {
            Text = "✕",
            FontSize = 9.5,
            FontWeight = FontWeights.Bold,
            Foreground = CreateFrozenBrush(Color.FromArgb(180, 255, 255, 255)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var border = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            Background = CreateFrozenBrush(Color.FromArgb(26, 255, 255, 255)),
            Margin = new Thickness(0, 0, 6, 0),
            Child = label,
            Cursor = Cursors.Hand,
            ToolTip = "終了 (Ctrl+Q / Alt+Shift+Q)"
        };

        border.MouseEnter += (_, _) => border.Background = CreateFrozenBrush(Color.FromArgb(48, 255, 255, 255));
        border.MouseLeave += (_, _) => border.Background = CreateFrozenBrush(Color.FromArgb(26, 255, 255, 255));

        return WrapBorderAsPlainButton(border, onClick);
    }

    private static Button MakeCapsulePillButton(string text, bool primaryTone, Action onClick)
    {
        var tb = new TextBlock
        {
            Text = $"{text} ▾",
            FontFamily = new FontFamily("Yu Gothic UI, Segoe UI"),
            FontSize = primaryTone ? 11.0 : 10.5,
            FontWeight = FontWeights.Medium,
            Foreground = CreateFrozenBrush(Color.FromArgb((byte)(primaryTone ? 226 : 198), 255, 255, 255)),
            VerticalAlignment = VerticalAlignment.Center
        };

        byte baseAlpha = (byte)(primaryTone ? 28 : 20);
        var border = new Border
        {
            Height = primaryTone ? 22 : 20,
            CornerRadius = new CornerRadius(11),
            Background = CreateFrozenBrush(Color.FromArgb(baseAlpha, 255, 255, 255)),
            Padding = new Thickness(8, 0, 8, 0),
            Child = tb,
            Cursor = Cursors.Hand
        };

        border.MouseEnter += (_, _) => border.Background = CreateFrozenBrush(Color.FromArgb((byte)(baseAlpha + 18), 255, 255, 255));
        border.MouseLeave += (_, _) => border.Background = CreateFrozenBrush(Color.FromArgb(baseAlpha, 255, 255, 255));

        return WrapBorderAsPlainButton(border, onClick);
    }

    private static void SetCapsulePillText(Button btn, string text)
    {
        if (btn.Content is Border { Child: TextBlock tb })
            tb.Text = $"{text} ▾";
    }

    private static Geometry GetHeaderIconGeometry(string iconKey) => iconKey switch
    {
        "region" => Geometry.Parse("M2,5 V2 H5 M10,2 H13 V5 M13,10 V13 H10 M5,13 H2 V10"),
        "play" => Geometry.Parse("M4,2.5 L12,7.5 L4,12.5 Z"),
        "stop" => Geometry.Parse("M3.5,3.5 H11.5 V11.5 H3.5 Z"),
        "settings" => Geometry.Parse("M7.5,5.2 A2.3,2.3 0 1,1 7.49,5.2 Z M7.5,1.5 V3 M7.5,12 V13.5 M1.5,7.5 H3 M12,7.5 H13.5 M3.3,3.3 L4.4,4.4 M10.6,10.6 L11.7,11.7 M11.7,3.3 L10.6,4.4 M4.4,10.6 L3.3,11.7"),
        "collapse" => Geometry.Parse("M3.5,9.5 L7.5,5.5 L11.5,9.5"),
        "expand" => Geometry.Parse("M3.5,5.5 L7.5,9.5 L11.5,5.5"),
        _ => Geometry.Parse("M3,7.5 H12")
    };

    private static bool IsFilledHeaderIcon(string iconKey) => iconKey is "play" or "stop";

    private static ShapePath CreateHeaderIconPath(string iconKey)
    {
        bool filled = IsFilledHeaderIcon(iconKey);
        var brush = CreateFrozenBrush(Color.FromArgb(215, 255, 255, 255));
        return new ShapePath
        {
            Data = GetHeaderIconGeometry(iconKey),
            Width = 13,
            Height = 13,
            Stretch = Stretch.Uniform,
            Fill = filled ? brush : Brushes.Transparent,
            Stroke = filled ? null : brush,
            StrokeThickness = filled ? 0 : 1.5,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private static Button MakeHeaderIconButton(string iconKey, string tooltip, Action onClick)
    {
        var border = new Border
        {
            Width = 26,
            Height = 22,
            CornerRadius = new CornerRadius(6),
            Background = CreateFrozenBrush(Color.FromArgb(26, 255, 255, 255)),
            Margin = new Thickness(3, 0, 0, 0),
            Child = CreateHeaderIconPath(iconKey),
            Cursor = Cursors.Hand,
            ToolTip = tooltip
        };

        border.MouseEnter += (_, _) => border.Background = CreateFrozenBrush(Color.FromArgb(46, 255, 255, 255));
        border.MouseLeave += (_, _) => border.Background = CreateFrozenBrush(Color.FromArgb(26, 255, 255, 255));

        return WrapBorderAsPlainButton(border, onClick);
    }

    private static void SetHeaderIconGlyph(Button btn, string iconKey, bool prominent)
    {
        if (btn.Content is Border b)
        {
            b.Child = CreateHeaderIconPath(iconKey);
            b.Background = CreateFrozenBrush(Color.FromArgb((byte)(prominent ? 62 : 26), 255, 255, 255));
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
            var row = new Border
            {
                Padding = new Thickness(10, 6, 12, 6),
                CornerRadius = new CornerRadius(5),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Child = new TextBlock
                {
                    Text = entry.Checked ? $"✓  {entry.Label}" : $"    {entry.Label}",
                    FontFamily = new FontFamily("Yu Gothic UI, Segoe UI"),
                    FontSize = 12.0,
                    Foreground = entry.Checked ? EmeraldBrush : Brushes.White
                }
            };
            row.MouseEnter += (_, _) => row.Background = CreateFrozenBrush(Color.FromArgb(36, 255, 255, 255));
            row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
            var action = entry.OnSelect;
            row.MouseLeftButtonUp += (_, _) =>
            {
                popup.IsOpen = false;
                action();
            };
            stack.Children.Add(row);
        }

        popup.Child = new Border
        {
            Background = CreateFrozenBrush(Color.FromArgb(248, 20, 25, 33)),
            BorderBrush = CreateFrozenBrush(Color.FromArgb(60, 255, 255, 255)),
            BorderThickness = new Thickness(0.8),
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
                    ? (_sidecar.IsModelDownloaded(k) ? " [導入済]" : $" [{NativeSidecarManager.GetSpec(k)?.ApproxSizeText}]")
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
                installed ? "デスクトップ＆スタートメニューに登録済み（再作成）" : "デスクトップ＆スタートメニューに登録",
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
                ? $"{capturedSpec.DisplayLabel} [ダウンロード済]"
                : $"{capturedSpec.DisplayLabel} をダウンロード ({capturedSpec.ApproxSizeText})";

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
            "GPUランタイム: Vulkan (標準・ドライバ不要)",
            _gpuFlavor == GpuEngineFlavor.Vulkan,
            () => SwitchGpuFlavor(GpuEngineFlavor.Vulkan)));

        items.Add((
            "GPUランタイム: NVIDIA CUDA (RTX向け高速版)",
            _gpuFlavor == GpuEngineFlavor.Cuda,
            () => SwitchGpuFlavor(GpuEngineFlavor.Cuda)));

        items.Add((
            "モデル・設定保存フォルダを開く",
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
            items.Add(("開発ソースから更新ビルド (Ctrl+Shift+U)", false, UpdateAndRelaunchFromSourceAction));
        }

        ShowDarkPopupMenu(_settingsBtn, items);
    }

    private void SwitchGpuFlavor(GpuEngineFlavor flavor)
    {
        _gpuFlavor = flavor;
        _settings.GpuFlavor = flavor == GpuEngineFlavor.Cuda ? "cuda" : "vulkan";
        _settings.Save(_runtimeDir);
        if (_backend.IsPlamo())
        {
            _ = _sidecar.DownloadAndStartAsync(_backend, _gpuFlavor);
        }
    }

    // MARK: - Collapse, Stage & Cluster Card Rendering

    public void ToggleCollapse()
    {
        if (!_isCollapsed)
        {
            if (Height >= MinExpandedSize.Height)
                _savedExpandedHeight = Height;

            _isCollapsed = true;
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
            double restoreH = Math.Clamp(_savedExpandedHeight, MinExpandedSize.Height, MaxExpandedSize.Height);
            MinHeight = MinExpandedSize.Height;
            MaxHeight = MaxExpandedSize.Height;
            Height = restoreH;
            _dividerLine.Visibility = Visibility.Visible;
            _bodyScroller.Visibility = Visibility.Visible;
            SetHeaderIconGlyph(_collapseBtn, "collapse", false);
        }

        _lastRenderedTwoRowMode = null;
        RebuildResponsiveHeader(ActualWidth > 0 ? ActualWidth : Width);
        SaveWindowGeometry();
    }

    private void UpdateStage(string stage, bool busy)
    {
        _stageLabel.Text = stage;
        _stageLabel.Foreground = CreateFrozenBrush(Color.FromArgb((byte)(busy ? 216 : 120), 255, 255, 255));
        _stageDot.Visibility = busy && !string.IsNullOrEmpty(stage) ? Visibility.Visible : Visibility.Collapsed;
        _dividerLine.Background = busy ? DividerBusyBrush : DividerIdleBrush;
    }

    private void RefreshNoticeVisibility()
    {
        _statusNoticeBlock.Visibility = (_clusterCards.Count == 0 && _setupCardBorder.Visibility != Visibility.Visible)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void SetStatusNotice(string message)
    {
        _statusNoticeBlock.Text = message;
        RefreshNoticeVisibility();
    }

    private void RenderClusterCards()
    {
        _cardsPanel.Children.Clear();
        RefreshNoticeVisibility();

        bool multiCardChrome = _clusterCards.Count >= 2;
        foreach (var card in _clusterCards)
        {
            var rowStack = new DockPanel { LastChildFill = true };

            if (multiCardChrome)
            {
                var badge = new TextBlock
                {
                    Text = card.BadgeLabel,
                    FontFamily = new FontFamily("Consolas, Yu Gothic UI"),
                    FontSize = 10.5,
                    FontWeight = FontWeights.Bold,
                    Foreground = card.IsTranslating ? EmeraldBrush : CreateFrozenBrush(Color.FromArgb(110, 255, 255, 255)),
                    Margin = new Thickness(0, 3, 9, 0),
                    VerticalAlignment = VerticalAlignment.Top
                };
                DockPanel.SetDock(badge, Dock.Left);
                rowStack.Children.Add(badge);
            }

            string bodyText = !string.IsNullOrEmpty(card.DisplayBodyText) ? card.DisplayBodyText : "翻訳中…";
            var textBox = new TextBox
            {
                Text = bodyText,
                IsReadOnly = true,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                FontFamily = new FontFamily("Yu Gothic UI, Meiryo UI, Segoe UI"),
                FontSize = 15.0,
                FontWeight = FontWeights.Medium,
                Foreground = CreateFrozenBrush(Color.FromArgb(
                    (byte)(string.IsNullOrEmpty(card.DisplayBodyText) ? 115 : (card.IsTranslating ? 214 : 245)),
                    255, 255, 255)),
                TextWrapping = TextWrapping.Wrap,
                Padding = new Thickness(0),
                Cursor = Cursors.IBeam
            };
            rowStack.Children.Add(textBox);

            var cardBorder = new Border
            {
                Padding = multiCardChrome ? new Thickness(10, 8, 10, 8) : new Thickness(0, 2, 0, 2),
                Margin = new Thickness(0, 0, 0, 8),
                CornerRadius = new CornerRadius(8),
                Background = multiCardChrome ? CreateFrozenBrush(Color.FromArgb(13, 255, 255, 255)) : Brushes.Transparent,
                BorderBrush = multiCardChrome
                    ? (card.IsTranslating ? CreateFrozenBrush(Color.FromArgb(115, 0x4D, 0xA8, 0x92)) : CreateFrozenBrush(Color.FromArgb(24, 255, 255, 255)))
                    : Brushes.Transparent,
                BorderThickness = new Thickness(multiCardChrome ? 0.8 : 0.0),
                Child = rowStack
            };

            var capturedCard = card;
            cardBorder.MouseEnter += (_, _) =>
            {
                if (multiCardChrome)
                {
                    cardBorder.Background = CreateFrozenBrush(Color.FromArgb(24, 255, 255, 255));
                    cardBorder.BorderBrush = CreateFrozenBrush(Color.FromArgb(170, 0x4D, 0xA8, 0x92));
                }
                _hoveredCard = capturedCard;
                RefreshRegionIndicator();
            };

            cardBorder.MouseLeave += (_, _) =>
            {
                if (multiCardChrome)
                {
                    cardBorder.Background = CreateFrozenBrush(Color.FromArgb(13, 255, 255, 255));
                    cardBorder.BorderBrush = capturedCard.IsTranslating
                        ? CreateFrozenBrush(Color.FromArgb(115, 0x4D, 0xA8, 0x92))
                        : CreateFrozenBrush(Color.FromArgb(24, 255, 255, 255));
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
            return;
        }

        // 未ダウンロードの場合はダウンロードカードを表示して待機（完了後に自動で翻訳実行）
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
                SetStatusNotice("一時停止中（再生ボタンまたは Alt+Shift+T で再開）");
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
                _setupCardBorder.Visibility = Visibility.Collapsed;
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
        string liveDir = Path.Combine(_projectRootDir, "build");
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
        Height = 235;
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
        if (_isCollapsed || Math.Abs(Height - 235) > 1)
            failures.Add($"Expand restore height failed (got {Height})");

        // 初回セットアップカードの描画テスト
        ApplySidecarStatus(new SidecarStatusSnapshot(SidecarState.NotInstalled, TranslationBackendKind.Plamo2B, "初回ダウンロード確認"));
        if (_setupCardBorder.Visibility != Visibility.Visible)
            failures.Add("In-app model setup card did not appear on NotInstalled state");

        ApplySidecarStatus(new SidecarStatusSnapshot(SidecarState.Ready, TranslationBackendKind.Plamo2B, "稼働中", 1.0));
        if (_setupCardBorder.Visibility != Visibility.Collapsed)
            failures.Add("In-app model setup card did not hide on Ready state");

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

        Console.WriteLine("WINDOWS_UI_SELF_TEST_PASSED: Standalone desktop UI, in-app downloader card, 360px header, and 0ms cluster cache verified.");
        PerformCleanShutdown(0);
    }
}
