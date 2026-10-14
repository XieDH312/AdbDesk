using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using AdbDesk.Core;
using Microsoft.Win32;

namespace AdbDesk.App;

public partial class ConsolePage : UserControl
{
    private sealed record TargetChoice(string? Serial, string Label, bool IsReady);
    private readonly object _outputLock = new();
    private readonly StringBuilder _pendingOutput = new();
    private readonly DispatcherTimer _outputTimer;
    private readonly ConsoleCommandHistoryStore _historyStore = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AdbDesk", "command-history.json"));
    private readonly ConsoleLibraryStore _libraryStore = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AdbDesk", "console-library.json"));
    private ConsoleLibraryState _libraryState = new([], []);
    private IReadOnlyList<ConsolePresetGroup> _groups = [];
    private IReadOnlyList<ConsoleParameterInput> _parameters = [];
    private ConsolePackagePickerWindow? _packagePicker;
    private string? _lastDeviceSerial;
    private IReadOnlyList<string> _history = [];
    private IReadOnlyList<DeviceInfo> _devices = [];
    private ToolPaths? _paths;
    private CancellationTokenSource? _operation;
    private ConsolePreset? _preset;
    private bool _ready;
    private bool _updating;
    private bool _pendingTrimmed;
    private bool _closed;
    public bool IsRunning => _operation is not null;
    public event EventHandler? RefreshRequested;

    public ConsolePage()
    {
        InitializeComponent();
        _outputTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(150) };
        _outputTimer.Tick += (_, _) => FlushOutput();
        _ready = true;
        UpdateDevices([]);
        try { _history = _historyStore.Load(); }
        catch (Exception ex) { ResultText.Text = $"命令历史读取失败：{ex.Message}"; }
        try { _libraryState = _libraryStore.Load(); }
        catch (Exception ex) { ResultText.Text = $"命令库偏好读取失败：{ex.Message}"; }
        RefreshPresetGroups();
        UpdatePreview();
    }

    public void Configure(ToolPaths? paths)
    {
        _operation?.Cancel();
        _packagePicker?.Close();
        _paths = paths;
        UpdateControls();
    }

    public void PrepareCommand(string serial, string command)
    {
        if (IsRunning || _closed) return;
        UpdateDevices(_devices);
        TargetCombo.SelectedItem = (TargetCombo.ItemsSource as IEnumerable<TargetChoice>)?.FirstOrDefault(c => c.Serial == serial);
        UseCustomMode();
        CommandInput.Text = command;
        UpdatePreview();
        TimeoutCombo.SelectedIndex = 3;
        ResultText.Text = "已填入所选进程的日志命令；进程重启后 PID 会变化，请刷新应用重新选择。点击执行开始读取。";
    }

    public void Shutdown()
    {
        _closed = true;
        _packagePicker?.Close();
        _operation?.Cancel();
        _outputTimer.Stop();
        lock (_outputLock) _pendingOutput.Clear();
    }

    public void UpdateDevices(IEnumerable<DeviceInfo> devices)
    {
        _devices = devices.ToArray();
        if (IsRunning) return; // Keep the displayed target fixed for the running command.
        var previous = TargetCombo.SelectedItem as TargetChoice;
        var choices = new List<TargetChoice> { new(null, "全局命令（不指定设备）", true) };
        choices.AddRange(_devices.Where(device => device.IsReady).Select(device => new TargetChoice(device.Serial,
            $"{(string.IsNullOrWhiteSpace(device.DeviceName) ? device.Model : device.DeviceName)} · {device.Serial}", true)));
        if (previous?.Serial is { } serial && choices.All(choice => choice.Serial != serial))
            choices.Add(previous with { Label = $"设备离线 · {serial}", IsReady = false });
        if (TargetCombo.ItemsSource is IReadOnlyList<TargetChoice> old && old.SequenceEqual(choices)) return;
        _updating = true;
        TargetCombo.ItemsSource = choices;
        TargetCombo.SelectedItem = choices.FirstOrDefault(choice => choice.Serial == previous?.Serial) ?? choices[0];
        _updating = false;
        UpdatePreview();
    }

    private void SearchInput_Changed(object sender, TextChangedEventArgs e)
    {
        if (_ready) RefreshPresetGroups();
    }

    private void RefreshPresetGroups()
    {
        var previous = _groups.ToDictionary(g => g.Key, g => g.IsExpanded, StringComparer.Ordinal);
        var query = SearchInput.Text.Trim();
        var matched = ConsolePresetCatalog.All.Where(p => p.Matches(query)).ToArray();
        var favorites = _libraryState.Favorites.ToHashSet(StringComparer.Ordinal);
        ConsolePresetItem Row(ConsolePreset p) => new(p, favorites.Contains(p.Id));
        var groups = new List<ConsolePresetGroup>();
        void Add(string key, string title, IEnumerable<ConsolePreset> items, bool expanded)
        {
            var rows = items.Select(Row).ToArray();
            if (rows.Length > 0) groups.Add(new(key, title, rows, query.Length > 0 || previous.GetValueOrDefault(key, expanded)));
        }
        Add("favorites", "我的收藏", matched.Where(p => favorites.Contains(p.Id)), true);
        Add("recent", "最近使用", _libraryState.Recent.Select(id => matched.FirstOrDefault(p => p.Id == id)).OfType<ConsolePreset>(), true);
        foreach (var category in matched.GroupBy(p => p.Category))
            Add(category.Key, category.Key, category, category.Key == "设备与系统");
        _groups = groups;
        var updating = _updating;
        _updating = true;
        PresetList.ItemsSource = groups;
        _updating = updating;
        LibraryCountText.Text = query.Length == 0 ? $"{matched.Length} 条 · 10 类" : $"找到 {matched.Length} 条";
        LibraryEmptyText.Visibility = matched.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Favorite_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (IsRunning || _closed || sender is not Button { DataContext: ConsolePresetItem item }) return;
        try
        {
            _libraryState = _libraryStore.ToggleFavorite(item.Preset.Id);
            RefreshPresetGroups();
        }
        catch (Exception ex) { ResultText.Text = $"收藏未保存：{ex.Message}"; }
    }

    private void PresetList_Changed(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (!_ready || _updating || IsRunning || e.NewValue is not ConsolePresetItem item) return;
        var preset = item.Preset;
        _preset = preset;
        _updating = true;
        DescriptionText.Text = preset.Description;
        PresetInfoText.Text = $"{(preset.Global ? "全局命令" : "所选设备")} · {preset.Requirements}" +
            (preset.Confirmation.Length > 0 ? " · 执行前确认" : "");
        _parameters = preset.Parameters.Select(p => new ConsoleParameterInput(p)).ToArray();
        foreach (var parameter in _parameters)
            parameter.PropertyChanged += (_, _) => { if (_ready && !_updating && !IsRunning) UpdatePresetCommand(); };
        ParameterPanel.ItemsSource = _parameters;
        ParameterPanel.Visibility = _parameters.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        TimeoutCombo.SelectedIndex = preset.Continuous ? 3 : preset.Id == "screen.record" || preset.Command.StartsWith("bugreport", StringComparison.Ordinal) ? 2 : 1;
        if (TargetCombo.SelectedItem is TargetChoice { Serial: not null, IsReady: true } previousTarget) _lastDeviceSerial = previousTarget.Serial;
        if (preset.Global) TargetCombo.SelectedIndex = 0;
        else if (TargetCombo.SelectedItem is not TargetChoice { Serial: not null, IsReady: true })
            TargetCombo.SelectedItem = (TargetCombo.ItemsSource as IEnumerable<TargetChoice>)?.FirstOrDefault(t => t.IsReady && t.Serial == _lastDeviceSerial && t.Serial is not null)
                ?? (TargetCombo.ItemsSource as IEnumerable<TargetChoice>)?.FirstOrDefault(t => t.IsReady && t.Serial is not null);
        _updating = false;
        UpdatePresetCommand();
    }

    private void UpdatePresetCommand()
    {
        if (_preset is null) return;
        _updating = true;
        CommandInput.Text = _preset.BuildCommand(_parameters.Select(p => p.Text).ToArray(), validate: false);
        _updating = false;
        UpdatePreview();
    }

    private void CommandInput_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_ready || _updating) return;
        UseCustomMode();
        UpdatePreview();
    }

    private void UseCustomMode()
    {
        var updating = _updating;
        _updating = true;
        foreach (var row in _groups.SelectMany(group => group.Items)) row.IsSelected = false;
        _preset = null;
        _parameters = [];
        ParameterPanel.ItemsSource = null;
        ParameterPanel.Visibility = Visibility.Collapsed;
        DescriptionText.Text = "自定义命令 · Ctrl+Enter 执行；shell 后使用 Android 命令语法。";
        PresetInfoText.Text = "自定义命令按原样执行；填写的参数不会加入收藏或最近预设。";
        _updating = updating;
    }

    private void Input_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_ready && !_updating)
        {
            if (TargetCombo.SelectedItem is TargetChoice { Serial: not null, IsReady: true } target) _lastDeviceSerial = target.Serial;
            UpdatePreview();
        }
    }

    private ConsoleCommand ParseCommand() => ConsoleCommand.Parse(CommandInput.Text, (TargetCombo.SelectedItem as TargetChoice)?.Serial);

    private void UpdatePreview()
    {
        if (!_ready || IsRunning) return;
        try
        {
            var command = ParseCommand();
            PreviewText.Text = $"{(command.IsGlobal ? "全局" : "设备")} › {command.Preview}";
            if (!command.CanRemember) PreviewText.Text += "  · 本条不保存历史";
        }
        catch (Exception ex) { PreviewText.Text = ex.Message; }
    }

    private void UpdateControls()
    {
        var idle = !IsRunning && !_closed;
        RunButton.IsEnabled = idle && _paths is not null;
        StopButton.IsEnabled = IsRunning && !_closed;
        TargetCombo.IsEnabled = CommandInput.IsEnabled = HistoryButton.IsEnabled = TimeoutCombo.IsEnabled = idle;
        ParameterPanel.IsEnabled = PresetList.IsEnabled = SearchInput.IsEnabled = idle;
        RunButton.Content = IsRunning ? "执行中…" : "执行";
    }

    private async void Run_Click(object sender, RoutedEventArgs e) => await RunCommandAsync();

    private async void CommandInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || (Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        e.Handled = true;
        await RunCommandAsync();
    }

    private async Task RunCommandAsync()
    {
        if (_closed || IsRunning) return;
        if (_paths is null) { ResultText.Text = "ADB 工具尚未就绪，请在主界面重新检测工具。"; return; }
        ConsoleCommand command;
        try
        {
            if (_preset is not null) _preset.BuildCommand(_parameters.Select(p => p.Text).ToArray());
            command = ParseCommand();
            if (!command.IsGlobal && (TargetCombo.SelectedItem is not TargetChoice { IsReady: true, Serial: not null } target ||
                !_devices.Any(device => device.Serial == target.Serial && device.IsReady)))
                throw new InvalidOperationException("所选设备已离线或未授权，请重新选择在线设备。");
        }
        catch (Exception ex) { ResultText.Text = ex.Message; return; }

        var executedPreset = _preset;
        var originalTarget = (TargetCombo.SelectedItem as TargetChoice)?.Serial;
        var confirmation = !string.IsNullOrEmpty(executedPreset?.Confirmation)
            ? executedPreset.Confirmation : ConsoleCommandConfirmation.Describe(command);
        if (confirmation is not null)
        {
            var targetLabel = command.IsGlobal ? "电脑 ADB / 全局操作" : (TargetCombo.SelectedItem as TargetChoice)?.Label;
            if (MessageBox.Show(Window.GetWindow(this), $"{confirmation}\n\n目标：{targetLabel}\n\n{command.Preview}\n\n是否执行？",
                "确认执行 ADB 命令", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            { ResultText.Text = "已取消，未执行命令。"; return; }
        }
        // Discovery may run while the confirmation dialog is open. Never silently switch its target.
        if (_closed || _paths is null || (!command.IsGlobal &&
            (originalTarget != (TargetCombo.SelectedItem as TargetChoice)?.Serial || !_devices.Any(d => d.Serial == originalTarget && d.IsReady))))
        { ResultText.Text = "目标设备或工具状态已变化，请重新选择后执行。"; return; }

        using var operation = new CancellationTokenSource();
        _operation = operation;
        var seconds = int.Parse(((ComboBoxItem)TimeoutCombo.SelectedItem).Tag.ToString()!);
        var timer = Stopwatch.StartNew();
        UpdateControls();
        PreviewText.Text = command.Preview;
        if (command.StandardInput is not null)
        {
            _updating = true;
            CommandInput.Clear(); // Pairing codes are not retained in the editor after submission.
            _updating = false;
        }
        QueueOutput($"\r\n[{DateTime.Now:HH:mm:ss}] {command.Preview}\r\n", false);
        ResultText.Text = "正在执行…";
        _outputTimer.Start();
        try
        {
            var exitCode = await new ConsoleCommandRunner(_paths).RunAsync(command, QueueOutput,
                seconds == 0 ? null : TimeSpan.FromSeconds(seconds), operation.Token);
            if (_closed) return;
            ResultText.Text = $"{(exitCode == 0 ? "已完成" : "执行失败")} · 退出码 {exitCode} · 耗时 {timer.Elapsed.TotalSeconds:0.0} 秒";
            QueueOutput($"\r\n[{ResultText.Text}]\r\n", false);
            RememberCommand(command, executedPreset);
            RefreshRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            if (!_closed)
            {
                ResultText.Text = $"已停止 · 耗时 {timer.Elapsed.TotalSeconds:0.0} 秒；已完成的操作不会撤销";
                QueueOutput($"\r\n[{ResultText.Text}]\r\n", false);
                RememberCommand(command, executedPreset);
            }
        }
        catch (Exception ex)
        {
            if (!_closed)
            {
                ResultText.Text = command.Redact(ex.Message);
                QueueOutput($"\r\n[失败] {ResultText.Text}\r\n", true);
            }
        }
        finally
        {
            _outputTimer.Stop();
            _operation = null;
            if (!_closed)
            {
                FlushOutput();
                UpdateControls();
                UpdateDevices(_devices);
            }
        }
    }

    private void RememberCommand(ConsoleCommand command, ConsolePreset? preset)
    {
        if (preset is not null)
        {
            try { _libraryState = _libraryStore.Remember(preset.Id); RefreshPresetGroups(); }
            catch (Exception ex) { QueueOutput($"\r\n[最近使用未保存] {ex.Message}\r\n", true); }
        }
        if (!command.CanRemember) return;
        try { _history = _historyStore.Remember(command.HistoryText); }
        catch (Exception ex) { QueueOutput($"\r\n[历史未保存] {ex.Message}\r\n", true); }
    }

    private void QueueOutput(string text, bool isError)
    {
        lock (_outputLock)
        {
            if (_closed) return;
            if (isError) _pendingOutput.Append("[stderr] ");
            _pendingOutput.Append(text);
            if (_pendingOutput.Length > 65536)
            {
                _pendingOutput.Remove(0, _pendingOutput.Length - 65536);
                _pendingTrimmed = true;
            }
        }
    }

    private void FlushOutput()
    {
        if (_closed) return;
        string text;
        lock (_outputLock)
        {
            if (_pendingOutput.Length == 0) return;
            text = (_pendingTrimmed ? "\r\n[输出过快，已省略部分内容]\r\n" : "") + _pendingOutput;
            _pendingOutput.Clear();
            _pendingTrimmed = false;
        }
        if (OutputText.Text.Length + text.Length > 300000)
        {
            var combined = OutputText.Text + text;
            OutputText.Text = "[仅保留最近输出；复制和保存导出当前显示的内容]\r\n" + combined[^240000..];
        }
        else OutputText.AppendText(text);
        if (AutoScroll.IsChecked == true) OutputText.ScrollToEnd();
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _operation?.Cancel();
        if (IsRunning) ResultText.Text = "正在停止…";
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshRequested?.Invoke(this, EventArgs.Empty);

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        FlushOutput();
        try
        {
            if (OutputText.Text.Length == 0) { ResultText.Text = "暂无输出可复制。"; return; }
            Clipboard.SetText(OutputText.Text);
            ResultText.Text = IsRunning ? "正在执行 · 已复制当前输出" : "已复制当前输出";
        }
        catch (Exception ex) { ResultText.Text = $"复制失败：{ex.Message}"; }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        FlushOutput();
        var text = OutputText.Text;
        if (text.Length == 0) { ResultText.Text = "暂无输出可保存。"; return; }
        var dialog = new SaveFileDialog { Filter = "文本日志 (*.txt)|*.txt", FileName = $"AdbDesk-command-{DateTime.Now:yyyyMMdd-HHmmss}.txt", DefaultExt = ".txt" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            await File.WriteAllTextAsync(dialog.FileName, text, Encoding.UTF8);
            if (!_closed) ResultText.Text = $"{(IsRunning ? "正在执行 · " : "")}日志已保存：{dialog.FileName}";
        }
        catch (Exception ex) { if (!_closed) ResultText.Text = $"保存失败：{ex.Message}"; }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        lock (_outputLock) { _pendingOutput.Clear(); _pendingTrimmed = false; }
        OutputText.Clear();
    }

    private void BrowseParameter_Click(object sender, RoutedEventArgs e)
    {
        if (IsRunning || _closed || sender is not Button { DataContext: ConsoleParameterInput input }) return;
        var parameter = input.Definition;
        if (parameter.Kind == "package")
        {
            if (_paths is null || TargetCombo.SelectedItem is not TargetChoice { Serial: not null, IsReady: true } target)
            { ResultText.Text = "请先选择一台在线设备再选择应用。"; return; }
            var picker = new ConsolePackagePickerWindow(_paths, target.Serial, target.Label) { Owner = Window.GetWindow(this) };
            _packagePicker = picker;
            try
            {
                if (picker.ShowDialog() == true && !_closed && _parameters.Contains(input) &&
                    (TargetCombo.SelectedItem as TargetChoice)?.Serial == target.Serial && _devices.Any(d => d.Serial == target.Serial && d.IsReady))
                {
                    _updating = true;
                    foreach (var userInput in _parameters.Where(p => p.Definition.Kind == "user"))
                        userInput.Text = picker.SelectedUserId!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    input.Text = picker.SelectedPackage!;
                    _updating = false;
                    UpdatePresetCommand();
                }
            }
            finally { _packagePicker = null; }
        }
        else if (parameter.Kind == "folder")
        {
            var dialog = new OpenFolderDialog { Title = parameter.Label };
            if (dialog.ShowDialog(Window.GetWindow(this)) == true) input.Text = dialog.FolderName;
        }
        else
        {
            var dialog = new OpenFileDialog { Title = parameter.Label, Filter = parameter.Kind == "apk" ? "Android 应用 (*.apk)|*.apk" : "所有文件 (*.*)|*.*" };
            if (dialog.ShowDialog(Window.GetWindow(this)) == true) input.Text = dialog.FileName;
        }
    }

    private void History_Click(object sender, RoutedEventArgs e)
    {
        if (IsRunning || _closed) return;
        var menu = new ContextMenu { PlacementTarget = HistoryButton, Placement = PlacementMode.Bottom, MaxHeight = 420, Width = 420 };
        PopulateHistory(menu);
        menu.IsOpen = true;
    }

    private void PopulateHistory(ContextMenu menu)
    {
        menu.Items.Clear();
        if (_history.Count == 0) menu.Items.Add(new MenuItem { Header = "暂无历史（配对和敏感命令不记录）", IsEnabled = false });
        foreach (var text in _history)
        {
            var header = new DockPanel { Width = 350 };
            var delete = new Button { Content = "×", Width = 26, Height = 26, Padding = new Thickness(0), ToolTip = "删除这条命令" };
            AutomationProperties.SetName(delete, "删除命令历史");
            DockPanel.SetDock(delete, Dock.Right);
            header.Children.Add(delete);
            header.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0), ToolTip = text });
            var item = new MenuItem { Header = header };
            item.Click += (_, args) => { if (ReferenceEquals(args.OriginalSource, item) && !IsRunning) CommandInput.Text = text; };
            delete.Click += (_, args) =>
            {
                args.Handled = true;
                if (IsRunning) return;
                try { _history = _historyStore.Remove(text); PopulateHistory(menu); }
                catch (Exception ex) { ResultText.Text = $"删除历史失败：{ex.Message}"; }
            };
            menu.Items.Add(item);
        }
        if (_history.Count == 0) return;
        menu.Items.Add(new Separator());
        var clear = new MenuItem { Header = "清空命令历史" };
        clear.Click += (_, _) =>
        {
            if (IsRunning) return;
            try { _historyStore.Clear(); _history = []; }
            catch (Exception ex) { ResultText.Text = $"清空历史失败：{ex.Message}"; }
        };
        menu.Items.Add(clear);
    }
}
