# ADB 命令库

当前内置 **120 条预设、10 个分类**。命令预设会随版本更新，实际支持取决于手机系统和附带 ADB 版本。

左侧点击分类箭头展开，点击命令填入，点击右侧 ☆ 收藏；收藏和最近使用会显示在分类之前。搜索支持名称、用途、命令、要求和关键词，空格分隔的多个词须全部匹配。点击预设不会立即执行。

收藏和最近 12 个已执行预设仅保存命令编号到 `%LOCALAPPDATA%/AdbDesk/console-library.json`，不保存参数、文字、地址或凭据。重新选择预设时仍需填写参数。原命令历史是独立功能，继续仅记录保守白名单中的命令。

选择设备后，带包名参数的预设可点击“选择”读取该手机当前 Android 用户的已安装应用。支持按包名搜索，也可以直接手动输入。清空、卸载、停用及运行时权限等预设明确使用 `--user current`；`dumpsys package` 等全局包元数据查询会在说明中标注。

端口、坐标、PID、包名、URI、路径和分辨率等会在执行前验证。所有预设参数作为单个参数引用，不会把填入文字当作模板或额外命令展开。自定义命令保留 Android shell 语法；编辑预设命令后进入自定义模式。

卸载、清数据、删除文件、修改设置和调试连接等预设会显示确认，包含具体目标和命令；取消不会运行。常见手动输入或历史中的清数据、卸载、重启等命令也会确认。此提示不解析所有复杂 shell 脚本，自定义命令由使用者核对。

Root 仅适用于允许 adb root 的 userdebug/eng 等固件，普通量产机通常不支持；run-as 需要 debuggable 应用。按系统要求显示的命令可能被厂商限制或返回权限错误，请以设备输出为准。

## 手动验收

1. 在 900×650 及高 DPI 窗口下，展开/折叠分类、搜索“前台 Activity”“内存”“端口”，清空搜索，确认右侧执行区与输出可访问。
2. 收藏两条命令、取消一条、重启程序，确认收藏持久化；执行几个只读预设，确认最近使用顺序及去重。
3. 选择两台不同设备，分别打开包名选择器，确认列表来自打开时的设备；加载期间取消或拔线，界面应可关闭并显示失败。
4. 测试端口 0/65536/中文数字、缺失包名、错误尺寸，确认没有运行；输入带空格和单引号的本地路径，确认预览正确。
5. 手动编辑一个预设，再点击同一预设，确认能恢复参数表单。取消清数据/卸载/重启确认，确认输出中没有执行记录。
6. 持续日志中切换页签、停止、断线或关闭程序；原日志/取消/隐私功能应保持。危险操作仅对专用测试应用或设备验收。

## 分类与命令

| 分类 | 数量 |
|---|---:|
| 设备与系统 | 15 |
| 应用管理 | 16 |
| Activity 与进程 | 10 |
| 权限管理 | 7 |
| 文件操作 | 9 |
| 屏幕与输入 | 14 |
| 日志与诊断 | 9 |
| 性能分析 | 9 |
| 网络诊断 | 19 |
| ADB 服务 | 12 |

### 设备与系统

| 名称 | 模板（可省略 adb） | 参数 |
|---|---|---|
| 已连接设备 | `devices -l` | — |
| 设备状态 | `get-state` | — |
| 设备序列号 | `get-serialno` | — |
| 设备型号 | `shell getprop ro.product.model` | — |
| 设备品牌 | `shell getprop ro.product.brand` | — |
| Android 版本 | `shell getprop ro.build.version.release` | — |
| SDK API 级别 | `shell getprop ro.build.version.sdk` | — |
| 安全补丁日期 | `shell getprop ro.build.version.security_patch` | — |
| CPU 架构列表 | `shell getprop ro.product.cpu.abilist` | — |
| 系统构建指纹 | `shell getprop ro.build.fingerprint` | — |
| 全部系统属性 | `shell getprop` | — |
| 电池信息 | `shell dumpsys battery` | — |
| 系统运行时间 | `shell uptime` | — |
| 当前 Android 用户 | `shell am get-current-user` | — |
| 重启手机 | `reboot` | — |

### 应用管理

| 名称 | 模板（可省略 adb） | 参数 |
|---|---|---|
| 全部已安装应用 | `shell pm list packages --user current` | — |
| 第三方应用 | `shell pm list packages --user current -3` | — |
| 系统应用 | `shell pm list packages --user current -s` | — |
| 已停用应用 | `shell pm list packages --user current -d` | — |
| 已启用应用 | `shell pm list packages --user current -e` | — |
| 应用版本号列表 | `shell pm list packages --user current --show-versioncode` | — |
| APK 安装路径 | `shell pm path --user current {0}` | 应用包名 |
| 应用详细信息 | `shell dumpsys package {0}` | 应用包名 |
| 覆盖安装 APK | `install -r {0}` | 电脑 APK 文件 |
| 安装测试版 APK | `install -r -t {0}` | 电脑 APK 文件 |
| 强制停止应用 | `shell am force-stop --user current {0}` | 应用包名 |
| 清空应用数据 | `shell pm clear --user current {0}` | 应用包名 |
| 卸载当前用户应用 | `shell pm uninstall --user current {0}` | 应用包名 |
| 停用应用 | `shell pm disable-user --user current {0}` | 应用包名 |
| 恢复启用应用 | `shell pm enable --user current {0}` | 应用包名 |
| 查询桌面启动入口 | `shell pm resolve-activity --brief --user current -a android.intent.action.MAIN -c android.intent.category.LAUNCHER -p {0}` | 应用包名 |

### Activity 与进程

| 名称 | 模板（可省略 adb） | 参数 |
|---|---|---|
| 当前前台 Activity | `shell dumpsys activity top` | — |
| Activity 与任务栈 | `shell dumpsys activity activities` | — |
| 最近任务 | `shell dumpsys activity recents` | — |
| 应用进程详情 | `shell dumpsys activity processes` | — |
| 运行服务 | `shell dumpsys activity services` | — |
| 全部 Linux 进程 | `shell ps -A` | — |
| 应用进程 PID | `shell pidof {0}` | 应用包名 |
| 启动指定 Activity | `shell am start --user current -n {0}` | 组件名 |
| 打开 Deep Link | `shell am start --user current -a android.intent.action.VIEW -d {0}` | 链接 URI |
| 发送广播 | `shell am broadcast --user current -a {0}` | 广播 Action |

### 权限管理

| 名称 | 模板（可省略 adb） | 参数 |
|---|---|---|
| 系统权限列表 | `shell pm list permissions -g -d` | — |
| 应用权限与包状态 | `shell pm dump {0}` | 应用包名 |
| 授予运行时权限 | `shell pm grant --user current {0} {1}` | 应用包名、权限名称 |
| 撤销运行时权限 | `shell pm revoke --user current {0} {1}` | 应用包名、权限名称 |
| 查看应用 AppOps | `shell cmd appops get --user current {0}` | 应用包名 |
| 设置应用 AppOp | `shell cmd appops set --user current {0} {1} {2}` | 应用包名、操作名称、模式 |
| AppOps 命令帮助 | `shell cmd appops help` | — |

### 文件操作

| 名称 | 模板（可省略 adb） | 参数 |
|---|---|---|
| 浏览手机目录 | `shell ls -la {0}` | 手机目录 |
| 手机文件信息 | `shell stat {0}` | 手机文件路径 |
| 存储空间 | `shell df -h` | — |
| 目录占用大小 | `shell du -h -d 1 {0}` | 手机目录 |
| 上传文件 | `push {0} {1}` | 电脑文件、手机目标路径 |
| 下载手机文件 | `pull {0} {1}` | 手机文件路径、电脑保存目录 |
| 创建手机目录 | `shell mkdir -p {0}` | 手机目录 |
| 删除手机文件 | `shell rm -- {0}` | 手机文件路径 |
| 浏览调试应用私有目录 | `shell run-as {0} --user {1} ls -la .` | 应用包名、Android 用户编号 |

### 屏幕与输入

| 名称 | 模板（可省略 adb） | 参数 |
|---|---|---|
| 查看屏幕分辨率 | `shell wm size` | — |
| 查看显示密度 | `shell wm density` | — |
| 修改显示分辨率 | `shell wm size {0}` | 宽x高 |
| 恢复默认分辨率 | `shell wm size reset` | — |
| 修改显示密度 | `shell wm density {0}` | DPI |
| 恢复默认密度 | `shell wm density reset` | — |
| 点击屏幕坐标 | `shell input tap {0} {1}` | X 坐标、Y 坐标 |
| 长按屏幕坐标 | `shell input swipe {0} {1} {0} {1} 1000` | X 坐标、Y 坐标 |
| 滑动屏幕 | `shell input swipe {0} {1} {2} {3} 300` | 起点 X、起点 Y、终点 X、终点 Y |
| 输入文字 | `shell input text {0}` | 输入内容 |
| 返回桌面 | `shell input keyevent 3` | — |
| 返回上一页 | `shell input keyevent 4` | — |
| 打开最近任务 | `shell input keyevent 187` | — |
| 录制手机屏幕 | `shell screenrecord --time-limit {1} {0}` | 手机 MP4 路径、录制秒数 |

### 日志与诊断

| 名称 | 模板（可省略 adb） | 参数 |
|---|---|---|
| 读取已有日志 | `logcat -d -v time` | — |
| 实时日志 | `logcat -v threadtime` | — |
| 读取崩溃日志 | `logcat -b crash -d -v threadtime` | — |
| 实时错误日志 | `logcat -v threadtime *:E` | — |
| 按 PID 抓取日志 | `logcat -v threadtime --pid={0}` | 进程 PID |
| 按 TAG 抓取日志 | `logcat -v threadtime {0}:V *:S` | 日志 TAG |
| 清空日志缓冲区 | `logcat -c` | — |
| 最近 ANR 信息 | `shell dumpsys activity lastanr` | — |
| 导出完整诊断报告 | `bugreport {0}` | 电脑保存目录 |

### 性能分析

| 名称 | 模板（可省略 adb） | 参数 |
|---|---|---|
| CPU 使用情况 | `shell dumpsys cpuinfo` | — |
| 进程资源快照 | `shell top -b -n 1` | — |
| 系统内存概要 | `shell dumpsys meminfo` | — |
| 应用内存详情 | `shell dumpsys meminfo {0}` | 应用包名 |
| 应用绘制帧统计 | `shell dumpsys gfxinfo {0} framestats` | 应用包名 |
| 重置应用帧统计 | `shell dumpsys gfxinfo {0} reset` | 应用包名 |
| 测量 Activity 启动耗时 | `shell am start --user current -W -n {0}` | Activity 组件名 |
| 磁盘性能与空间统计 | `shell dumpsys diskstats` | — |
| 系统内存原始信息 | `shell cat /proc/meminfo` | — |

### 网络诊断

| 名称 | 模板（可省略 adb） | 参数 |
|---|---|---|
| 手机 IP 地址 | `shell ip addr` | — |
| 手机路由表 | `shell ip route` | — |
| 手机策略路由规则 | `shell ip rule` | — |
| Wi-Fi 诊断信息 | `shell dumpsys wifi` | — |
| 网络连接与 VPN 状态 | `shell dumpsys connectivity` | — |
| 网络流量统计 | `shell dumpsys netstats` | — |
| DNS 解析器状态 | `shell dumpsys dnsresolver` | — |
| 连接无线 ADB | `connect {0}` | IP:端口 |
| 断开指定无线 ADB | `disconnect {0}` | IP:端口 |
| 发现无线 ADB 服务 | `mdns services` | — |
| 电脑到手机端口转发 | `forward tcp:{0} tcp:{1}` | 电脑端口、手机端口 |
| 查看全部正向转发 | `forward --list` | — |
| 移除指定正向转发 | `forward --remove tcp:{0}` | 电脑端口 |
| 手机到电脑端口转发 | `reverse tcp:{0} tcp:{1}` | 手机端口、电脑端口 |
| 查看手机反向转发 | `reverse --list` | — |
| 移除指定反向转发 | `reverse --remove tcp:{0}` | 手机端口 |
| 查询旧版代理字段 | `shell settings get global http_proxy` | — |
| 查询全局代理主机 | `shell settings get global global_http_proxy_host` | — |
| 查询全局代理端口 | `shell settings get global global_http_proxy_port` | — |

### ADB 服务

| 名称 | 模板（可省略 adb） | 参数 |
|---|---|---|
| ADB 命令帮助 | `help` | — |
| ADB 工具版本 | `version` | — |
| 启动 ADB 服务 | `start-server` | — |
| 停止 ADB 服务 | `kill-server` | — |
| ADB 服务信息 | `server-status` | — |
| ADB 主机能力 | `host-features` | — |
| 实时观察连接变化 | `track-devices -l` | — |
| 开启 TCP 无线调试 | `tcpip {0}` | 监听端口 |
| 恢复 USB 调试模式 | `usb` | — |
| 以 Root 重启 adbd | `root` | — |
| 恢复非 Root adbd | `unroot` | — |
| Android Shell 工具列表 | `shell toybox --long` | — |

## 官方参考

- [Android Debug Bridge](https://developer.android.com/tools/adb)
- [dumpsys](https://developer.android.com/tools/dumpsys)
- [Logcat](https://developer.android.com/tools/logcat)
- [AOSP 包管理命令实现](https://android.googlesource.com/platform/frameworks/base/+/refs/heads/main/services/core/java/com/android/server/pm/PackageManagerShellCommand.java)
