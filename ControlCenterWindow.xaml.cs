using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.IO;

namespace LiveDanmakuOverlay;

public partial class ControlCenterWindow : Window
{
    private readonly AppSettings _settings;
    private readonly MessageFilter _filter;
    private readonly HistoryStore _history;
    private readonly Action _settingsChanged;
    private readonly IPlatformAccountProvider _account;
    private readonly Action _accountChanged;
    private CancellationTokenSource? _loginCts;
    private bool _initializing = true;

    public ControlCenterWindow(AppSettings settings, MessageFilter filter, HistoryStore history,
        IPlatformAccountProvider account, Action settingsChanged, Action accountChanged)
    {
        _settings = settings;
        _filter = filter;
        _history = history;
        _settingsChanged = settingsChanged;
        _account = account;
        _accountChanged = accountChanged;
        InitializeComponent();
        RefreshKeywords();
        RefreshBlockedUsers();
        SaveBlockedCheck.IsChecked = settings.SaveBlockedMessages;
        FreshnessSlider.Value = settings.FreshnessSeconds;
        DuplicateSlider.Value = settings.DuplicateWindowSeconds;
        RetentionCombo.SelectedIndex = settings.HistoryRetentionDays switch { 7 => 0, 90 => 2, 0 => 3, _ => 1 };
        UpdateStrategyLabels();
        _initializing = false;
        Loaded += async (_, _) => { await RefreshHistoryStatusAsync(); await RefreshAccountStatusAsync(); };
        Closed += (_, _) => { _loginCts?.Cancel(); _loginCts?.Dispose(); };
    }

    private void AddKeyword_Click(object sender, RoutedEventArgs e) => AddKeyword();
    private void KeywordInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e) { if (e.Key == Key.Enter) AddKeyword(); }
    private void AddKeyword()
    {
        if (_filter.AddKeyword(KeywordInput.Text))
        {
            KeywordInput.Clear();
            RefreshKeywords();
            _settingsChanged();
        }
    }

    private void RemoveKeyword_Click(object sender, RoutedEventArgs e)
    {
        if (KeywordList.SelectedItem is string keyword && _filter.RemoveKeyword(keyword))
        {
            RefreshKeywords();
            _settingsChanged();
        }
    }

    private void RefreshKeywords() => KeywordList.ItemsSource = _filter.GetKeywords();

    private void AddUser_Click(object sender, RoutedEventArgs e) => AddUser();
    private void UserInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e) { if (e.Key == Key.Enter) AddUser(); }

    private void AddUser()
    {
        var userName = UserInput.Text.Trim();
        if (_filter.AddUser(userName))
        {
            UserInput.Clear();
            RefreshBlockedUsers();
            _settingsChanged();
            BlockManagementStatus.Text = $"已屏蔽用户：{userName}";
        }
        else if (userName is "***" or "匿名")
        {
            BlockManagementStatus.Text = "不能屏蔽脱敏或匿名用户名，否则会误伤大量用户。";
        }
    }

    private void RemoveUser_Click(object sender, RoutedEventArgs e)
    {
        if (UserList.SelectedItem is string userName && _filter.RemoveUser(userName))
        {
            RefreshBlockedUsers();
            _settingsChanged();
            BlockManagementStatus.Text = $"已取消屏蔽用户：{userName}";
        }
    }

    private void RefreshBlockedUsers() => UserList.ItemsSource = _filter.GetBlockedUsers();

    private void SaveBlockedCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _settings.SaveBlockedMessages = SaveBlockedCheck.IsChecked == true;
        _settingsChanged();
    }

    private async void Search_Click(object sender, RoutedEventArgs e) => await SearchAsync();
    private async void SearchInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e) { if (e.Key == Key.Enter) await SearchAsync(); }
    private async Task SearchAsync()
    {
        SearchStatus.Text = "正在搜索…";
        try
        {
            bool? blocked = BlockedFilter.SelectedIndex switch { 1 => false, 2 => true, _ => null };
            var results = await _history.SearchAsync(SearchInput.Text.Trim(), blocked: blocked);
            HistoryGrid.ItemsSource = results;
            SearchStatus.Text = $"显示 {results.Count} 条（最多 500 条）";
        }
        catch (Exception ex) { SearchStatus.Text = $"搜索失败：{ex.Message}"; }
    }

    private async void BlockedFilter_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_initializing && IsLoaded) await SearchAsync();
    }

    private void BlockContentFromHistory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { DataContext: HistoryRecord record }) return;
        if (_filter.AddKeyword(record.Text))
        {
            RefreshKeywords();
            _settingsChanged();
            SearchStatus.Text = $"已将该弹幕内容加入屏蔽词：{Shorten(record.Text)}";
        }
        else SearchStatus.Text = "该内容已经在屏蔽词中。";
    }

    private void BlockUserFromHistory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { DataContext: HistoryRecord record }) return;
        if (_filter.AddUser(record.UserName))
        {
            RefreshBlockedUsers();
            _settingsChanged();
            SearchStatus.Text = $"已屏蔽用户：{record.UserName}";
        }
        else SearchStatus.Text = record.UserName is "***" or "匿名"
            ? "不能屏蔽脱敏或匿名用户名，否则会误伤大量用户。"
            : "该用户已经在黑名单中。";
    }

    private static string Shorten(string value) => value.Length <= 24 ? value : value[..24] + "…";

    private void StrategySlider_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing) return;
        _settings.FreshnessSeconds = FreshnessSlider.Value;
        _settings.DuplicateWindowSeconds = DuplicateSlider.Value;
        UpdateStrategyLabels();
        _settingsChanged();
    }

    private void UpdateStrategyLabels()
    {
        if (FreshnessValue is null || DuplicateValue is null) return;
        FreshnessValue.Text = $"{FreshnessSlider.Value:F1} 秒";
        DuplicateValue.Text = $"{DuplicateSlider.Value:F1} 秒";
    }

    private void RetentionCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_initializing || RetentionCombo.SelectedItem is not System.Windows.Controls.ComboBoxItem item ||
            !int.TryParse(item.Tag?.ToString(), out var days)) return;
        _settings.HistoryRetentionDays = days;
        _settingsChanged();
    }

    private async void RefreshHistoryStatus_Click(object sender, RoutedEventArgs e) => await RefreshHistoryStatusAsync();

    private async Task RefreshHistoryStatusAsync()
    {
        HistoryStatus.Text = "正在读取数据库状态…";
        try
        {
            var stats = await _history.GetStatisticsAsync();
            var range = stats.RecordCount == 0 ? "暂无记录" :
                $"{stats.EarliestTimestamp:yyyy-MM-dd HH:mm} 至 {stats.LatestTimestamp:yyyy-MM-dd HH:mm}";
            HistoryStatus.Text = $"共 {stats.RecordCount:N0} 条 · 占用 {FormatBytes(stats.DiskBytes)} · {range}";
        }
        catch (Exception ex) { HistoryStatus.Text = $"读取失败：{ex.Message}"; }
    }

    private async void CleanupHistory_Click(object sender, RoutedEventArgs e)
    {
        if (_settings.HistoryRetentionDays <= 0)
        {
            System.Windows.MessageBox.Show(this, "当前设置为永久保留，没有过期记录需要清理。", "弹幕姬");
            return;
        }
        HistoryStatus.Text = "正在清理并压缩数据库…";
        try
        {
            var deleted = await _history.CleanupOlderThanAsync(_settings.HistoryRetentionDays);
            await SearchAsync();
            await RefreshHistoryStatusAsync();
            System.Windows.MessageBox.Show(this, $"已清理 {deleted:N0} 条过期记录。", "清理完成");
        }
        catch (Exception ex) { HistoryStatus.Text = $"清理失败：{ex.Message}"; }
    }

    private async void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        if (System.Windows.MessageBox.Show(this, "确定清空全部弹幕历史吗？此操作无法撤销。", "清空全部历史",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        HistoryStatus.Text = "正在清空并压缩数据库…";
        try
        {
            var deleted = await _history.ClearAsync();
            HistoryGrid.ItemsSource = Array.Empty<HistoryRecord>();
            SearchStatus.Text = "显示 0 条";
            await RefreshHistoryStatusAsync();
            System.Windows.MessageBox.Show(this, $"已清空 {deleted:N0} 条历史记录。", "清空完成");
        }
        catch (Exception ex) { HistoryStatus.Text = $"清空失败：{ex.Message}"; }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.##} {units[unit]}";
    }

    private async Task RefreshAccountStatusAsync()
    {
        await _account.RefreshStatusAsync();
        AccountStatus.Text = _account.Status.IsLoggedIn
            ? $"已登录：{_account.Status.DisplayName}（UID {_account.Status.UserId}）"
            : _account.Status.DisplayName;
        LogoutButton.IsEnabled = _account.Status.IsLoggedIn;
    }

    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        _loginCts?.Cancel();
        _loginCts?.Dispose();
        _loginCts = new CancellationTokenSource();
        LoginButton.IsEnabled = false;
        try
        {
            AccountStatus.Text = "正在生成二维码…";
            var session = await _account.BeginQrLoginAsync(_loginCts.Token);
            LoginQrImage.Source = LoadBitmap(session.PngBytes);
            while (!_loginCts.IsCancellationRequested)
            {
                var result = await _account.PollQrLoginAsync(session.Key, _loginCts.Token);
                AccountStatus.Text = result.Message;
                if (result.State == QrLoginState.Success)
                {
                    LogoutButton.IsEnabled = true;
                    _accountChanged();
                    break;
                }
                if (result.State == QrLoginState.Expired) break;
                await Task.Delay(1500, _loginCts.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AccountStatus.Text = $"登录失败：{ex.Message}"; }
        finally { LoginButton.IsEnabled = true; }
    }

    private async void Logout_Click(object sender, RoutedEventArgs e)
    {
        _loginCts?.Cancel();
        await _account.LogoutAsync();
        LoginQrImage.Source = null;
        await RefreshAccountStatusAsync();
        _accountChanged();
    }

    private static BitmapImage LoadBitmap(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}
