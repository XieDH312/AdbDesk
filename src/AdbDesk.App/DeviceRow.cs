using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using AdbDesk.Core;

namespace AdbDesk.App;

public sealed class DeviceRow : INotifyPropertyChanged
{
    private DeviceInfo _device;
    private bool _isSelected;
    private bool _isMirroring;

    public DeviceRow(DeviceInfo device)
    {
        _device = device;
    }

    public DeviceInfo Device => _device;
    public string Serial => _device.Serial;
    public string Model => string.IsNullOrWhiteSpace(_device.Model) ? "未知设备" : _device.Model;
    public string State => _device.State;
    public bool IsReady => _device.IsReady;
    public bool CanMirrorAction => IsReady || IsMirroring;
    public string StateText => _device.State switch
    {
        "device" => "已连接",
        "unauthorized" => "等待授权",
        "offline" => "设备离线",
        "stale" => "状态未知",
        _ => _device.State
    };

    public Brush StateForeground => _device.State switch
    {
        "device" => Brushes.ForestGreen,
        "unauthorized" => new SolidColorBrush(Color.FromRgb(180, 83, 9)),
        _ => new SolidColorBrush(Color.FromRgb(185, 28, 28))
    };

    public Brush StateBackground => _device.State switch
    {
        "device" => new SolidColorBrush(Color.FromRgb(236, 253, 245)),
        "unauthorized" => new SolidColorBrush(Color.FromRgb(255, 247, 237)),
        _ => new SolidColorBrush(Color.FromRgb(254, 242, 242))
    };

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    public bool IsMirroring
    {
        get => _isMirroring;
        set
        {
            if (SetField(ref _isMirroring, value))
            {
                OnPropertyChanged(nameof(MirrorActionText));
                OnPropertyChanged(nameof(CanMirrorAction));
            }
        }
    }

    public string MirrorActionText => IsMirroring ? "停止投屏" : "开始投屏";

    public void Update(DeviceInfo device)
    {
        _device = device;
        OnPropertyChanged(nameof(Device));
        OnPropertyChanged(nameof(Model));
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(CanMirrorAction));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(StateForeground));
        OnPropertyChanged(nameof(StateBackground));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
