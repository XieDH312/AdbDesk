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
    private bool _isMirrorStarting;
    private bool _isWirelessBusy;
    private bool _isDisconnecting;
    private bool _isCapturing;
    private string _wirelessStatusMessage = string.Empty;

    public DeviceRow(DeviceInfo device)
    {
        _device = device;
    }

    public DeviceInfo Device => _device;
    public string Serial => _device.Serial;
    public string Model => string.IsNullOrWhiteSpace(_device.Model) ? "未知设备" : _device.Model;
    public string DisplayName => string.IsNullOrWhiteSpace(_device.DeviceName) ? Model : _device.DeviceName;
    public string ModelAndAndroid
    {
        get
        {
            var model = $"型号 {Model}";
            return string.IsNullOrWhiteSpace(_device.AndroidVersion)
                ? $"{model} · Android 版本未知"
                : $"{model} · Android {_device.AndroidVersion}";
        }
    }

    public string TransportText => _device.Transport switch
    {
        DeviceTransport.Usb => "USB",
        DeviceTransport.Network => _device.WirelessPort is int port ? $"无线 ADB :{port}" : "无线 ADB",
        DeviceTransport.Emulator => "模拟器",
        _ => "连接方式未知"
    };

    public string SerialAndTransport => $"{Serial}  ·  {TransportText}";
    public bool HasNetworkInfo => IsReady && _device.Transport != DeviceTransport.Emulator;
    public string NetworkText => $"Wi-Fi IP：{(_device.Network.WifiAddress.Length > 0 ? _device.Network.WifiAddress : "未获取")}" +
        (_device.Network.VpnAddresses.Count > 0 ? $"  ·  VPN：{string.Join("、", _device.Network.VpnAddresses.Select(address => address.Address))}" : "");
    public string NetworkTooltip => "点击重新读取并复制实际 Wi-Fi IP。网络信息最多每 10 秒刷新，连接前实时读取。\n" +
        string.Join("\n", _device.Network.WifiAddresses.Select(address => $"Wi-Fi · {address.Interface} · {address.Address}/{address.PrefixLength}")) +
        (_device.Network.VpnAddresses.Count == 0 ? "" : "\n" + string.Join("\n", _device.Network.VpnAddresses.Select(address => $"VPN · {address.Interface} · {address.Address}/{address.PrefixLength}")) + "\nVPN 网卡地址不能代替手机 Wi-Fi 地址。") +
        (_device.Network.Error.Length == 0 ? "" : $"\n读取失败：{_device.Network.Error}");
    public string DetailsTooltip => string.IsNullOrWhiteSpace(_device.DetailsError)
        ? ModelAndAndroid
        : $"设备详情读取失败：{_device.DetailsError}";
    public string State => _device.State;
    public bool IsReady => _device.IsReady;
    public bool IsConnectionBusy => IsWirelessBusy || IsDisconnecting || IsMirrorStarting;
    public bool CanCaptureAction => IsReady && !IsConnectionBusy && !IsCapturing;
    public string CaptureActionText => IsCapturing ? "截图中…" : "截图";
    public bool IsCapturing
    {
        get => _isCapturing;
        set
        {
            if (!SetField(ref _isCapturing, value)) return;
            OnPropertyChanged(nameof(CanCaptureAction));
            OnPropertyChanged(nameof(CaptureActionText));
        }
    }
    public bool CanMirrorAction => (IsReady && !IsConnectionBusy) || IsMirroring || IsMirrorStarting;
    public bool ShowWirelessActions => IsReady && _device.Transport == DeviceTransport.Usb;
    public bool CanWirelessAction => ShowWirelessActions && !IsConnectionBusy;
    public bool ShowDisconnectAction => _device.IsNetworkConnection;
    public bool CanDisconnectAction => ShowDisconnectAction && !IsConnectionBusy;
    public string DisconnectActionText => IsDisconnecting ? "正在断开…" : "断开连接";
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

    public bool IsMirrorStarting
    {
        get => _isMirrorStarting;
        set
        {
            if (!SetField(ref _isMirrorStarting, value)) return;
            OnPropertyChanged(nameof(MirrorActionText));
            OnPropertyChanged(nameof(IsConnectionBusy));
            OnPropertyChanged(nameof(CanMirrorAction));
            OnPropertyChanged(nameof(CanCaptureAction));
            OnPropertyChanged(nameof(CanWirelessAction));
            OnPropertyChanged(nameof(CanDisconnectAction));
        }
    }

    public bool IsWirelessBusy
    {
        get => _isWirelessBusy;
        set
        {
            if (SetField(ref _isWirelessBusy, value))
            {
                OnPropertyChanged(nameof(CanWirelessAction));
                OnPropertyChanged(nameof(WirelessBusyText));
                OnPropertyChanged(nameof(HasWirelessStatus));
                OnPropertyChanged(nameof(IsConnectionBusy));
                OnPropertyChanged(nameof(CanCaptureAction));
                OnPropertyChanged(nameof(CanDisconnectAction));
                OnPropertyChanged(nameof(CanMirrorAction));
            }
        }
    }

    public bool IsDisconnecting
    {
        get => _isDisconnecting;
        set
        {
            if (!SetField(ref _isDisconnecting, value)) return;
            OnPropertyChanged(nameof(IsConnectionBusy));
            OnPropertyChanged(nameof(CanCaptureAction));
            OnPropertyChanged(nameof(CanDisconnectAction));
            OnPropertyChanged(nameof(DisconnectActionText));
            OnPropertyChanged(nameof(CanMirrorAction));
            OnPropertyChanged(nameof(CanWirelessAction));
            OnPropertyChanged(nameof(WirelessBusyText));
            OnPropertyChanged(nameof(HasWirelessStatus));
        }
    }

    public string WirelessBusyText => IsDisconnecting ? "正在断开当前无线连接…" : IsWirelessBusy ? "正在切换连接方式…" : WirelessStatusMessage;

    public string WirelessStatusMessage
    {
        get => _wirelessStatusMessage;
        private set
        {
            if (SetField(ref _wirelessStatusMessage, value))
            {
                OnPropertyChanged(nameof(WirelessBusyText));
                OnPropertyChanged(nameof(HasWirelessStatus));
            }
        }
    }

    public bool HasWirelessStatus => !string.IsNullOrWhiteSpace(WirelessBusyText);

    public string MirrorActionText => IsMirrorStarting ? "取消启动" : IsMirroring ? "停止投屏" : "开始投屏";

    public void SetWirelessStatus(string message) => WirelessStatusMessage = message;

    public void Update(DeviceInfo device)
    {
        _device = device;
        OnPropertyChanged(nameof(Device));
        OnPropertyChanged(nameof(Model));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(ModelAndAndroid));
        OnPropertyChanged(nameof(TransportText));
        OnPropertyChanged(nameof(SerialAndTransport));
        OnPropertyChanged(nameof(HasNetworkInfo));
        OnPropertyChanged(nameof(NetworkText));
        OnPropertyChanged(nameof(NetworkTooltip));
        OnPropertyChanged(nameof(DetailsTooltip));
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(IsReady));
        OnPropertyChanged(nameof(CanCaptureAction));
        OnPropertyChanged(nameof(CanMirrorAction));
        OnPropertyChanged(nameof(ShowWirelessActions));
        OnPropertyChanged(nameof(CanWirelessAction));
        OnPropertyChanged(nameof(ShowDisconnectAction));
        OnPropertyChanged(nameof(CanDisconnectAction));
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
