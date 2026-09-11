using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using AdbDesk.Core;

namespace AdbDesk.App;

public sealed class InstallRow : INotifyPropertyChanged
{
    private string _status;
    private string _detail;

    public InstallRow(Guid id, string serial, string apkPath, string status = "排队中", string detail = "等待安装")
    {
        Id = id;
        Serial = serial;
        ApkPath = apkPath;
        _status = status;
        _detail = detail;
    }

    public Guid Id { get; }
    public string Serial { get; }
    public string ApkPath { get; }
    public string FileName => Path.GetFileName(ApkPath);

    public string Status
    {
        get => _status;
        private set
        {
            if (SetField(ref _status, value))
            {
                OnPropertyChanged(nameof(CanCancel));
            }
        }
    }

    public string Detail
    {
        get => _detail;
        private set => SetField(ref _detail, value);
    }

    public bool CanCancel => Status is "排队中" or "安装中";

    public void Apply(InstallUpdate update)
    {
        Status = update.Status;
        Detail = string.IsNullOrWhiteSpace(update.Detail) ? update.Status : update.Detail;
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
