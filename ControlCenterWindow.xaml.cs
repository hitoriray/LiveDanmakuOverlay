using System.Windows;
using System.Windows.Input;

namespace LiveDanmakuOverlay;

public partial class ControlCenterWindow : Window
{
    private readonly AppSettings _settings;
    private readonly MessageFilter _filter;
    private readonly HistoryStore _history;
    private readonly Action _settingsChanged;
    private bool _initializing = true;

    public ControlCenterWindow(AppSettings settings, MessageFilter filter, HistoryStore history, Action settingsChanged)
    {
        _settings = settings;
        _filter = filter;
        _history = history;
        _settingsChanged = settingsChanged;
        InitializeComponent();
        RefreshKeywords();
        SaveBlockedCheck.IsChecked = settings.SaveBlockedMessages;
        FreshnessSlider.Value = settings.FreshnessSeconds;
        DuplicateSlider.Value = settings.DuplicateWindowSeconds;
        UpdateStrategyLabels();
        _initializing = false;
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
            var results = await _history.SearchAsync(SearchInput.Text.Trim());
            HistoryGrid.ItemsSource = results;
            SearchStatus.Text = $"显示 {results.Count} 条（最多 500 条）";
        }
        catch (Exception ex) { SearchStatus.Text = $"搜索失败：{ex.Message}"; }
    }

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
}
