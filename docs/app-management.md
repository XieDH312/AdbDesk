# 应用管理

在“工具箱”中选择已授权的在线设备，点击“应用管理”。窗口绑定该设备，查询手机当前 Android 用户；首次打开和点击刷新时更新列表，不持续轮询所有应用。

- 列出当前用户已安装包的名称、包名、系统/第三方、启用状态和 versionCode；名称和包名支持不区分大小写的模糊搜索，应用类型与启用状态可组合筛选。
- 先显示完整包名列表，再补充应用名称，读取期间可以搜索或筛选列表。名称读取结束后自动更新搜索结果；停止读取或读取失败不会丢失包名列表，具体原因显示在列表下方。名称会出现在详情标题及“复制信息”中，操作仍使用准确包名。
- 选择应用后按需读取 versionName、versionCode、最低/目标 SDK、安装/更新时间、权限和 APK 路径；右侧分为基本信息、权限、原始输出三个页签。系统没有返回的字段显示“无法读取”或“未知”，不猜测。
- 权限区分申请列表、安装时授权与当前 Android 用户的运行时授权。未报告授权结果不等于未授予；授权标记也不代表 AppOps 等其他系统限制已允许访问。
- 支持启动、强制停止、打开手机应用设置、复制包名/完整信息、导出 APK、清除数据和当前用户卸载。没有桌面启动入口的应用会提示原因，但仍可查看详情和打开设置。
- “抓取此应用日志”打开日志窗口，带入该设备、包名和 Android 用户，点击“开始采集”后执行。复用同设备的空闲窗口；正在采集时不覆盖采集范围，其他设备的窗口需先关闭。
- 导出包括 base 与全部 split APK，不包含应用私有数据；拆分包不应作为单个 APK 安装。
- 清除数据与卸载会先显示设备、用户和包名要求确认。系统应用是否允许操作取决于系统权限；错误输出不会当作成功。
- 设备离线、窗口关闭和程序更新会取消未完成的命令。命令已经交给手机处理时，停止等待不等于回滚手机上的操作，应刷新确认最终状态。

应用列表按 Android 用户隔离。若手机当前用户切换，操作会拒绝继续，需重新刷新。`dumpsys package` 原始详情属于全局包元数据，可能包含其他 Android 用户的状态；实际修改仍只指定所选用户。

详情缓存限定在当前设备窗口内，按 Android 用户、包名和列表中的版本号区分；刷新应用列表或设备离线会清除缓存。修改手机权限后可以点击“刷新详情”重新读取。设备离线后的列表仅供参考，重连后需要刷新才能继续操作。

名称读取复用随程序附带的 scrcpy `--list-apps`：通过 ADB 临时运行其现有服务读取名称，不安装辅助 App，也不下载手机上的 APK。其他包信息和操作继续使用手机自带的 ADB shell 命令。图标暂不实现；APK 下载只在用户点击“导出 APK”后进行。

scrcpy 4.1 的名称列表只覆盖已启用且有启动入口的应用，系统组件、已停用应用等可能没有名称，此时名称列显示“未读取”，包名与原有功能照常保留。名称是系统返回的应用标签，可能与桌面快捷方式的自定义名称不同。为避免跨用户混用名称，目前只为 Android 用户 0 补充名称；其他用户仍可按包名管理，并显示原因。

名称保存在当前设备窗口的这一次应用列表快照中，不共用其他设备或用户的缓存。每次“刷新应用”重新读取，因此手机语言、应用版本或名称变化后可以刷新获取；不会每两秒重复读取名称。

实现参考 [App Manager](https://github.com/MuntashirAkon/AppManager) 的信息组织方式，命令依据 [Android ADB 文档](https://developer.android.com/tools/adb) 与 [AOSP PackageManagerShellCommand](https://android.googlesource.com/platform/frameworks/base/+/refs/heads/main/services/core/java/com/android/server/pm/PackageManagerShellCommand.java)。没有集成其 Android SDK 或复制项目源码。

名称读取及格式依据 [scrcpy 4.1 的应用命令](https://github.com/Genymobile/scrcpy/blob/v4.1/doc/device.md#start-android-app)、[Device.listApps](https://github.com/Genymobile/scrcpy/blob/v4.1/server/src/main/java/com/genymobile/scrcpy/device/Device.java) 和 [LogUtils](https://github.com/Genymobile/scrcpy/blob/v4.1/server/src/main/java/com/genymobile/scrcpy/util/LogUtils.java)。

后续文件管理可参考 [ADB Explorer](https://github.com/Alex4SSB/ADB-Explorer)；布局交互可参考 [Appium Inspector](https://github.com/appium/appium-inspector)。本版先完成应用管理与数字版本更新。
