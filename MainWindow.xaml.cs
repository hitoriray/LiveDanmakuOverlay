using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace LiveDanmakuOverlay;

public partial class MainWindow : Window
{
    private const int HotkeyToggleVisibility = 0x4101;
    private const int HotkeyToggleLock = 0x4102;
    private const int WmHotkey = 0x0312;
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExNoActivate = 0x08000000;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;

    private readonly BilibiliAccountProvider _bilibiliAccount = new();
    private readonly BilibiliDanmakuClient _client;
    private readonly AppSettings _settings;
    private readonly HistoryStore _historyStore = new();
    private readonly MessageFilter _messageFilter;
    private readonly SyncCoordinator _syncCoordinator;
    private BarrageRenderer? _barrageRenderer;
    private Forms.NotifyIcon? _trayIcon;
    private HwndSource? _source;
    private bool _allowClose;
    private bool _isLocked;
    private string _connectionStatus = "尚未连接";
    private string _currentRoom = "";
    private ControlCenterWindow? _controlCenter;
    private WindowState _stateBeforeMinimize = WindowState.Normal;
    private bool _isDraggingWindow;
    private System.Drawing.Point _dragStartCursor;
    private double _dragStartLeft;
    private double _dragStartTop;
    private bool _initializing = true;

    public MainWindow() : this(AppSettings.Load()) { }

    internal MainWindow(AppSettings settings)
    {
        _settings = settings;
        _client = new BilibiliDanmakuClient(_bilibiliAccount);
        _messageFilter = new MessageFilter(_settings);
        _syncCoordinator = new SyncCoordinator(_settings, _messageFilter,
            () => Dispatcher.Invoke(SaveSettings), ApplySyncedSettings);
        InitializeComponent();
        _client.MessageReceived += Client_MessageReceived;
        _client.StatusChanged += Client_StatusChanged;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ApplySavedSettings();
        _initializing = false;
        _barrageRenderer = new BarrageRenderer(BarrageCanvas, _settings.FontSize,
            BarrageRenderer.PercentToPxPerSecond(_settings.ScrollSpeedPercent), _settings.TextOpacity);
        _barrageRenderer.SetEnabled(_settings.DanmakuEnabled);
        _barrageRenderer.FreshnessSeconds = _settings.FreshnessSeconds;
        _barrageRenderer.DuplicateWindowSeconds = _settings.DuplicateWindowSeconds;
        _barrageRenderer.PendingCountChanged += BarrageRenderer_PendingCountChanged;
        _barrageRenderer.StatisticsChanged += BarrageRenderer_StatisticsChanged;
        CreateTrayIcon();
        _syncCoordinator.Start();

        var handle = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(WindowProc);
        RegisterHotKey(handle, HotkeyToggleVisibility, ModControl | ModAlt, (uint)KeyInterop.VirtualKeyFromKey(Key.D));
        RegisterHotKey(handle, HotkeyToggleLock, ModControl | ModAlt, (uint)KeyInterop.VirtualKeyFromKey(Key.L));

        SetLocked(_settings.IsLocked);
        try { await _bilibiliAccount.RefreshStatusAsync(); }
        catch { /* Account status failure falls back to anonymous mode. */ }
        if (_settings.HistoryRetentionDays > 0)
        {
            try { await _historyStore.CleanupOlderThanAsync(_settings.HistoryRetentionDays, compact: false); }
            catch { /* History maintenance must not prevent startup. */ }
        }
        if (!string.IsNullOrWhiteSpace(_settings.Room))
            _ = ConnectAsync();
    }

    private void ApplySavedSettings()
    {
        RefreshSavedRooms();
        RoomInput.Text = _settings.Room;
        FontSizeCombo.SelectedIndex = ClosestIndex(_settings.FontSize, 14, 18, 24);
        SpeedCombo.SelectedIndex = ClosestIndex(_settings.ScrollSpeedPercent, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100);
        DisplayAreaCombo.SelectedIndex = ClosestIndex(_settings.DisplayAreaPercent, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100);
        OpacitySlider.Value = Math.Clamp(Math.Round(_settings.BackgroundOpacity * 10, MidpointRounding.AwayFromZero) / 10, 0, 1);
        TextOpacitySlider.Value = Math.Clamp(Math.Round(_settings.TextOpacity * 10, MidpointRounding.AwayFromZero) / 10, 0.1, 1);

        if (_settings.Width >= MinWidth) Width = _settings.Width;
        if (_settings.Height >= MinHeight) Height = _settings.Height;
        var virtualBounds = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (_settings.HasWindowPlacement &&
            WindowPlacement.IsVisible(new System.Windows.Point(_settings.Left, _settings.Top), virtualBounds))
        {
            Left = _settings.Left;
            Top = _settings.Top;
        }
        else
        {
            var position = WindowPlacement.TopRight(SystemParameters.WorkArea, Width, Height, 24);
            Left = position.X;
            Top = position.Y;
        }
        BackgroundOpacityValue.Text = $"{OpacitySlider.Value:P0}";
        TextOpacityValue.Text = $"{TextOpacitySlider.Value:P0}";
        UpdateSurfaceColor();
        UpdateDanmakuToggleButton();
    }

    private static int ClosestIndex(double value, params double[] choices) =>
        choices.Select((choice, index) => (Distance: Math.Abs(choice - value), Index: index))
            .MinBy(item => item.Distance).Index;

    private async void ConnectButton_Click(object sender, RoutedEventArgs e) => await ConnectAsync();

    private async void RoomInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter) await ConnectAsync();
    }

    private void RoomInput_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (RoomInput.SelectedItem is SavedRoom saved)
            Dispatcher.InvokeAsync(() => RoomInput.Text = saved.Room,
                System.Windows.Threading.DispatcherPriority.Input);
    }

    private async Task ConnectAsync()
    {
        var room = RoomInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(room))
        {
            StatusText.Text = "请输入直播间链接或房间号";
            return;
        }

        ConnectButton.IsEnabled = false;
        _settings.Room = room;
        _currentRoom = room;
        SaveSettings();
        try
        {
            await _client.ConnectAsync(room);
            SaveConnectedRoom(room);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"连接失败：{ex.Message}";
        }
        finally
        {
            ConnectButton.IsEnabled = true;
        }
    }

    private void SaveConnectedRoom(string room)
    {
        var existing = _settings.SavedRooms.FirstOrDefault(item =>
            string.Equals(item.Room, room, StringComparison.OrdinalIgnoreCase));
        if (existing is null) _settings.SavedRooms.Insert(0, new SavedRoom(room, room));
        else
        {
            _settings.SavedRooms.Remove(existing);
            _settings.SavedRooms.Insert(0, existing);
        }
        if (_settings.SavedRooms.Count > 30) _settings.SavedRooms.RemoveRange(30, _settings.SavedRooms.Count - 30);
        RefreshSavedRooms();
        SaveSettings();
        _syncCoordinator.SettingsChanged();
    }

    private void RefreshSavedRooms()
    {
        if (RoomInput is null) return;
        var text = RoomInput.Text;
        RoomInput.ItemsSource = _settings.SavedRooms.OrderByDescending(room => room.IsPinned).ToArray();
        RoomInput.Text = text;
    }

    private void ApplySyncedSettings()
    {
        Dispatcher.InvokeAsync(() =>
        {
            _initializing = true;
            ApplySavedSettings();
            _initializing = false;
            _barrageRenderer?.SetFontSize(_settings.FontSize);
            _barrageRenderer?.SetScrollSpeed(
                BarrageRenderer.PercentToPxPerSecond(_settings.ScrollSpeedPercent));
            _barrageRenderer?.SetContentOpacity(_settings.TextOpacity);
            if (_barrageRenderer is not null)
            {
                _barrageRenderer.FreshnessSeconds = _settings.FreshnessSeconds;
                _barrageRenderer.DuplicateWindowSeconds = _settings.DuplicateWindowSeconds;
                _barrageRenderer.SetEnabled(_settings.DanmakuEnabled);
            }
            UpdateDisplayArea();
            RefreshSavedRooms();
        });
    }

    private void Client_MessageReceived(object? sender, DanmakuMessage message)
    {
        var blocked = _messageFilter.IsBlocked(message, out var reason);
        if (!blocked || _settings.SaveBlockedMessages)
            _historyStore.Record(_currentRoom, message, blocked, reason);
        if (!blocked) _barrageRenderer?.Enqueue(message);
    }

    private void DanmakuToggleButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.DanmakuEnabled = !_settings.DanmakuEnabled;
        _barrageRenderer?.SetEnabled(_settings.DanmakuEnabled);
        UpdateDanmakuToggleButton();
        SaveSettings();
        _syncCoordinator.SettingsChanged();
    }

    private void UpdateDanmakuToggleButton()
    {
        if (DanmakuToggleButton is null) return;
        DanmakuToggleButton.Content = _settings.DanmakuEnabled ? "弹幕开" : "弹幕关";
        DanmakuToggleButton.Background = new SolidColorBrush(_settings.DanmakuEnabled
            ? System.Windows.Media.Color.FromArgb(90, 50, 205, 120)
            : System.Windows.Media.Color.FromArgb(37, 255, 255, 255));
    }

    private void Client_StatusChanged(object? sender, string status) => Dispatcher.InvokeAsync(() =>
    {
        _connectionStatus = status;
        UpdateStatusText(_barrageRenderer?.PendingCount ?? 0);
    });

    private void BarrageRenderer_PendingCountChanged(object? sender, int count) => UpdateStatusText(count);

    private void BarrageRenderer_StatisticsChanged(object? sender, BarrageStatistics stats)
    {
        StatusText.Text = $"{_connectionStatus} · 收到 {stats.Received} / 显示 {stats.Displayed} / 合并 {stats.Merged} / 跳过 {stats.Expired}";
    }

    private void UpdateStatusText(int pendingCount)
    {
        StatusText.Text = pendingCount > 0
            ? $"{_connectionStatus} · 等待滚动 {pendingCount} 条"
            : _connectionStatus;
    }

    private void LockButton_Click(object sender, RoutedEventArgs e) => SetLocked(true);

    private void ControlCenterButton_Click(object sender, RoutedEventArgs e) => ShowControlCenter();

    private void ShowControlCenter()
    {
        if (_controlCenter is null || !_controlCenter.IsLoaded)
        {
            _controlCenter = new ControlCenterWindow(_settings, _messageFilter, _historyStore, _bilibiliAccount, _syncCoordinator, () =>
            {
                _barrageRenderer!.FreshnessSeconds = _settings.FreshnessSeconds;
                _barrageRenderer.DuplicateWindowSeconds = _settings.DuplicateWindowSeconds;
                SaveSettings();
                _syncCoordinator.SettingsChanged();
            }, () => { if (!string.IsNullOrWhiteSpace(RoomInput.Text)) _ = ConnectAsync(); });
        }
        _controlCenter.Show();
        _controlCenter.Activate();
    }

    private void SetLocked(bool locked)
    {
        _isLocked = locked;
        Toolbar.Visibility = locked ? Visibility.Collapsed : Visibility.Visible;
        ConnectionPanel.Visibility = locked ? Visibility.Collapsed : Visibility.Visible;
        StatusPanel.Visibility = locked ? Visibility.Collapsed : Visibility.Visible;
        ResizeThumb.Visibility = locked || WindowState == WindowState.Maximized
            ? Visibility.Collapsed : Visibility.Visible;
        ResizeMode = locked ? ResizeMode.NoResize : ResizeMode.CanResizeWithGrip;
        SetClickThrough(locked);
        _settings.IsLocked = locked;
        SaveSettings();
    }

    private void SetClickThrough(bool enabled)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        var style = GetWindowLong(handle, GwlExStyle);
        style = enabled ? style | WsExTransparent | WsExNoActivate : style & ~(WsExTransparent | WsExNoActivate);
        SetWindowLong(handle, GwlExStyle, style);
    }

    private void HideButton_Click(object sender, RoutedEventArgs e) => Hide();

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        _stateBeforeMinimize = WindowState;
        WindowState = WindowState.Minimized;
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (MaximizeButton is null || Surface is null || ResizeThumb is null) return;
        if (WindowState != WindowState.Minimized) _stateBeforeMinimize = WindowState;
        var maximized = WindowState == WindowState.Maximized;
        MaximizeButton.Content = maximized ? "❐" : "□";
        MaximizeButton.ToolTip = maximized ? "还原" : "最大化";
        Surface.CornerRadius = maximized ? new CornerRadius(0) : new CornerRadius(12);
        ResizeThumb.Visibility = maximized || _isLocked ? Visibility.Collapsed : Visibility.Visible;
        Dispatcher.InvokeAsync(UpdateDisplayArea, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (WindowDragPolicy.CanStart(e.OriginalSource as DependencyObject, _isLocked, WindowState, e.ButtonState))
        {
            _isDraggingWindow = true;
            _dragStartCursor = Forms.Cursor.Position;
            _dragStartLeft = Left;
            _dragStartTop = Top;
            Mouse.Capture(this);
            e.Handled = true;
        }
    }

    private void Window_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isDraggingWindow || e.LeftButton != MouseButtonState.Pressed) return;
        var cursor = Forms.Cursor.Position;
        var dpi = VisualTreeHelper.GetDpi(this);
        Left = _dragStartLeft + (cursor.X - _dragStartCursor.X) / dpi.DpiScaleX;
        Top = _dragStartTop + (cursor.Y - _dragStartCursor.Y) / dpi.DpiScaleY;
    }

    private void Window_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndWindowDrag();
    private void Window_LostMouseCapture(object sender, System.Windows.Input.MouseEventArgs e) => EndWindowDrag();

    private void EndWindowDrag()
    {
        if (!_isDraggingWindow) return;
        _isDraggingWindow = false;
        if (Mouse.Captured == this) Mouse.Capture(null);
        SaveSettings();
    }

    private void FontSizeCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        if (FontSizeCombo.SelectedItem is not System.Windows.Controls.ComboBoxItem item ||
            !double.TryParse(item.Tag?.ToString(), out var value)) return;
        _settings.FontSize = value;
        _barrageRenderer?.SetFontSize(value);
        SaveSettings();
        _syncCoordinator.SettingsChanged();
    }

    private void SpeedCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        if (SpeedCombo.SelectedItem is not System.Windows.Controls.ComboBoxItem item ||
            !double.TryParse(item.Tag?.ToString(), out var value)) return;
        _settings.ScrollSpeedPercent = value;
        _barrageRenderer?.SetScrollSpeed(BarrageRenderer.PercentToPxPerSecond(value));
        SaveSettings();
        _syncCoordinator.SettingsChanged();
    }

    private void DisplayAreaCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        if (DisplayAreaCombo.SelectedItem is not System.Windows.Controls.ComboBoxItem item ||
            !double.TryParse(item.Tag?.ToString(), out var value)) return;
        _settings.DisplayAreaPercent = value;
        UpdateDisplayArea();
        SaveSettings();
        _syncCoordinator.SettingsChanged();
    }

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing) return;
        _settings.BackgroundOpacity = e.NewValue;
        if (BackgroundOpacityValue is not null) BackgroundOpacityValue.Text = $"{e.NewValue:P0}";
        UpdateSurfaceColor();
        SaveSettings();
        _syncCoordinator.SettingsChanged();
    }

    private void TextOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing) return;
        _settings.TextOpacity = e.NewValue;
        if (TextOpacityValue is not null) TextOpacityValue.Text = $"{e.NewValue:P0}";
        _barrageRenderer?.SetContentOpacity(e.NewValue);
        SaveSettings();
        _syncCoordinator.SettingsChanged();
    }

    private void UpdateSurfaceColor()
    {
        if (Surface is null) return;
        var alpha = (byte)Math.Clamp(_settings.BackgroundOpacity * 255, 0, 255);
        Surface.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(alpha, 24, 26, 32));
    }

    private void BarrageAreaHost_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateDisplayArea(e.NewSize.Height);

    private void UpdateDisplayArea() => UpdateDisplayArea(BarrageAreaHost?.ActualHeight ?? 0);

    private void UpdateDisplayArea(double hostHeight)
    {
        if (BarrageAreaHost is null || BarrageCanvas is null || hostHeight <= 0) return;
        var percent = Math.Clamp(_settings.DisplayAreaPercent, 10, 100) / 100.0;
        if (percent >= 1)
        {
            BarrageCanvas.ClearValue(HeightProperty);
            BarrageCanvas.VerticalAlignment = VerticalAlignment.Stretch;
        }
        else
        {
            BarrageCanvas.VerticalAlignment = VerticalAlignment.Top;
            BarrageCanvas.Height = Math.Max(1, hostHeight * percent);
        }
        _barrageRenderer?.RefreshLanes();
    }

    private void ResizeThumb_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        Width = Math.Max(MinWidth, ActualWidth + e.HorizontalChange);
        Height = Math.Max(MinHeight, ActualHeight + e.VerticalChange);
    }

    private void CreateTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("显示/隐藏 (Ctrl+Alt+D)", null, (_, _) => ToggleVisibility());
        menu.Items.Add("锁定/解锁 (Ctrl+Alt+L)", null, (_, _) => ToggleLock());
        menu.Items.Add("功能与历史搜索", null, (_, _) => Dispatcher.Invoke(ShowControlCenter));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, async (_, _) => await ExitAsync());

        _trayIcon = new Forms.NotifyIcon
        {
            Text = "直播弹幕悬浮窗",
            Icon = System.Drawing.SystemIcons.Information,
            Visible = true,
            ContextMenuStrip = menu
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ToggleVisibility);
    }

    private void ToggleVisibility()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = _stateBeforeMinimize;
            Show();
            Topmost = true;
            Activate();
        }
        else if (IsVisible) Hide();
        else { Show(); Topmost = true; Activate(); }
    }

    private void ToggleLock()
    {
        if (!IsVisible) Show();
        SetLocked(!_isLocked);
        if (!_isLocked) Activate();
    }

    private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey)
        {
            if (wParam.ToInt32() == HotkeyToggleVisibility) ToggleVisibility();
            if (wParam.ToInt32() == HotkeyToggleLock) ToggleLock();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private async Task ExitAsync()
    {
        _allowClose = true;
        SaveSettings();
        _barrageRenderer?.Dispose();
        await _client.DisposeAsync();
        await _historyStore.DisposeAsync();
        await _syncCoordinator.DisposeAsync();
        _bilibiliAccount.Dispose();
        _trayIcon?.Dispose();
        System.Windows.Application.Current.Shutdown();
    }

    private void SaveSettings()
    {
        if (IsLoaded && WindowState == WindowState.Normal)
        {
            _settings.Left = Left;
            _settings.Top = Top;
            _settings.Width = ActualWidth;
            _settings.Height = ActualHeight;
            _settings.HasWindowPlacement = true;
        }
        _settings.Save();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            SaveSettings();
            Hide();
        }
        else
        {
            var handle = new WindowInteropHelper(this).Handle;
            UnregisterHotKey(handle, HotkeyToggleVisibility);
            UnregisterHotKey(handle, HotkeyToggleLock);
            _source?.RemoveHook(WindowProc);
        }
    }

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int newLong);
}
