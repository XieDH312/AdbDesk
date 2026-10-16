using System.Windows;
using System.Windows.Controls;

namespace AdbDesk.App;

public partial class MainWindow
{
    private void InitializeMirrorPreferences()
    {
        var preferences = MirrorPreferences.Load();
        RestoreMirrorSelection(QualityComboBox, preferences.Quality);
        RestoreMirrorSelection(MirrorFitComboBox, preferences.DisplayMode);
        AudioCheckBox.IsChecked = preferences.Audio;
        MirrorFullScreenCheckBox.IsChecked = preferences.FullScreen;

        // Subscribe after restoration so XAML defaults and partial restoration never overwrite saved values.
        QualityComboBox.SelectionChanged += MirrorPreferences_Changed;
        MirrorFitComboBox.SelectionChanged += MirrorPreferences_Changed;
        AudioCheckBox.Checked += MirrorPreferences_Changed;
        AudioCheckBox.Unchecked += MirrorPreferences_Changed;
        MirrorFullScreenCheckBox.Checked += MirrorPreferences_Changed;
        MirrorFullScreenCheckBox.Unchecked += MirrorPreferences_Changed;
    }

    private static void RestoreMirrorSelection(ComboBox comboBox, string? tag)
    {
        var saved = comboBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, tag, StringComparison.Ordinal));
        // Unknown/removed presets keep the current XAML default instead of selecting an invalid index.
        if (saved is not null) comboBox.SelectedItem = saved;
    }

    private void MirrorPreferences_Changed(object sender, RoutedEventArgs e)
    {
        if (_isClosing) return;
        try
        {
            new MirrorPreferences(
                (QualityComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "1920,60",
                AudioCheckBox.IsChecked == true,
                (MirrorFitComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "complete",
                MirrorFullScreenCheckBox.IsChecked == true).Save();
        }
        catch (Exception ex) { SetStatus("投屏偏好未能保存：" + ex.Message, StatusKind.Warning); }
    }
}
