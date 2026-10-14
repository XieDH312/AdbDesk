# 应用管理

在“工具箱”中选择已授权的在线设备，点击“应用管理”。窗口绑定该设备，查询手机当前 Android 用户；首次打开和点击刷新时更新列表，不持续轮询所有应用。

- 列出当前用户已安装包的包名、系统/第三方、启用状态和 versionCode；支持分类和搜索。
- 查看详情时按需查询 versionName、APK 路径及包详情。不同设备输出不同，缺失信息不伪造；包名不等于桌面的应用名称。
- 支持启动、强制停止、导出 APK、清除数据和当前用户卸载。没有桌面启动入口的应用会提示原因。
- 导出包括 base 与全部 split APK，不包含应用私有数据；拆分包不应作为单个 APK 安装。
- 清除数据与卸载会先显示设备、用户和包名要求确认。系统应用是否允许操作取决于系统权限；错误输出不会当作成功。
- 设备离线、窗口关闭和程序更新会取消未完成的命令。命令已经交给手机处理时，停止等待不等于回滚手机上的操作，应刷新确认最终状态。

应用列表按 Android 用户隔离。若手机当前用户切换，操作会拒绝继续，需重新刷新。`dumpsys package` 原始详情属于全局包元数据，可能包含其他 Android 用户的状态；实际修改仍只指定所选用户。

实现参考 [App Manager](https://github.com/MuntashirAkon/AppManager) 的信息组织方式，命令依据 [Android ADB 文档](https://developer.android.com/tools/adb) 与 [AOSP PackageManagerShellCommand](https://android.googlesource.com/platform/frameworks/base/+/refs/heads/main/services/core/java/com/android/server/pm/PackageManagerShellCommand.java)。没有集成其 Android SDK 或复制项目源码。

后续文件管理可参考 [ADB Explorer](https://github.com/Alex4SSB/ADB-Explorer)；布局交互可参考 [Appium Inspector](https://github.com/appium/appium-inspector)。本版先完成应用管理与数字版本更新。
