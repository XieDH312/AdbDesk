# AdbDesk 设计

用户已批准执行：C# + WPF，Windows 10/11 x64；每台手机独立投屏窗口，主界面统一管理。

- 主界面自动发现 ADB 设备，显示型号、标识、device/offline/unauthorized 状态、勾选框、投屏状态及数量统计。设备插拔刷新不能丢失现有选择。
- 单台或多选启动 scrcpy 4.1，每次明确指定 serial；每台最多一个本程序启动的会话；分别控制、关闭。退出清理本程序会话，不杀系统 ADB server 或其他工具。
- APK 拖进 scrcpy 原生窗口安装到该设备；收集该会话日志显示安装结果。拖进设备行安装到该设备；拖进公共区域或文件选择器，列出选中目标确认后排队。仅支持普通 .apk；不自动卸载、不降级、不使用授权绕过。
- 单设备安装串行、全局最多两个安装并发。逐任务记录阶段和错误，支持取消排队/本地安装进程；取消不能保证撤销已由手机提交的安装。
- 主界面中文，浅色清晰布局，设备区和安装任务区，选中计数、质量选项、帮助、日志。工具与 UI 异步，超时不能卡死主线程。
- 第一版 USB 优先，同时显示 ADB 已连接的 TCP/IP 设备；不自动配置无线配对。手机需启用调试并授权，品牌控制/安装权限由用户在手机确认。
- 官方 scrcpy/ADB 依赖固定版本并校验下载；便携包包含 .NET 运行时和依赖许可。不得复制参考商业软件代码或二进制。
- 不把网络端点或设备标识写入提交记录/文档。测试和文档用虚构标识。
- 验证设备解析、明确目标路由、路径安全、队列串行/并发/取消、会话生命周期；构建发布包并启动验证。真机安装用专门的可卸载测试 APK，若无合适 APK 则如实说明未验证。

## 组件接口（实现与 UI 的约定）
Core 为 net10.0 类库，命名空间 AdbDesk.Core。UI 为 net10.0-windows WPF。

```csharp
public record DeviceInfo(string Serial, string Model, string State) {
    public bool IsReady => State == "device";
}
public record ToolPaths(string Adb, string Scrcpy) {
    public static ToolPaths Discover(string baseDirectory);
}
public record MirrorOptions(int MaxSize = 1280, int MaxFps = 30, bool Audio = false);
public record MirrorEvent(string Serial, string Message, bool IsRunning);
public record InstallUpdate(Guid Id, string Serial, string ApkPath, string Status, string Detail);
public sealed class AdbService(ToolPaths paths) {
    public Task<IReadOnlyList<DeviceInfo>> GetDevicesAsync(CancellationToken cancellationToken = default);
}
public sealed class MirrorService : IDisposable {
    public MirrorService(ToolPaths paths);
    public event Action<MirrorEvent>? Changed;
    public bool IsRunning(string serial);
    public void Start(DeviceInfo device, MirrorOptions options);
    public void Stop(string serial);
    public void Dispose();
}
public sealed class InstallQueue : IDisposable {
    public InstallQueue(ToolPaths paths);
    public event Action<InstallUpdate>? Changed;
    public Guid Enqueue(DeviceInfo device, string apkPath);
    public void Cancel(Guid id);
    public void Dispose();
}
```

Changed 事件可能来自后台线程，UI 用 Dispatcher。安装状态字面值：排队中、安装中、成功、失败、已取消。错误捕获并友好呈现；仅凭进程 exit 0 不断定安装成功，ADB 必须输出 Success。工具发现先 baseDirectory/tools/scrcpy/scrcpy.exe 和 adb.exe，再项目根同一位置；支持 ADBDESK_TOOLS 环境变量覆盖依赖目录。应用输出复制 tools 到输出根。
