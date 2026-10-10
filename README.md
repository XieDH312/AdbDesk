# AdbDesk 安卓设备助手

Windows 10/11 x64 安卓设备管理工具。本仓库用于发布便携包和更新说明。

## 当前版本

v1.0.0-beta.7（versionCode 10007）：修复多台 USB 手机显示连接方式未知；显示手机真实 Wi-Fi 和 VPN 地址，支持指定 Wi-Fi IP 扫码／配对码／手动连接，识别 Windows 电脑移动热点，并增加 TCP 端口检测及连接失败提示。保留 120 条 ADB 预设和网络抓包工作台。beta.2 及以上可在应用内更新，需开启“接收测试版本”。

## 下载

打开 [Releases](https://github.com/XieDH312/AdbDesk/releases)，下载对应版本的 `AdbDesk-*-win-x64.zip`。

1. 退出旧版 AdbDesk。
2. 完整解压 ZIP，保留运行库和 tools 文件夹。
3. 运行 AdbDesk.exe，连接已授权的 Android 设备。

标有 Pre-release 的版本为测试版本。请下载发行版附件中的 AdbDesk ZIP；GitHub 自动生成的 Source code 压缩包只包含本发行仓库的说明文件。

## 功能

- USB / 无线 ADB 设备管理、扫码及配对码连接。
- 多设备独立投屏、键鼠控制、APK 安装和手机截图。
- ADB 控制台、运行应用与已安装应用管理、可视化布局查看。
- 日志持续保存和自有 Debug App 的 HTTP/HTTPS 网络调试。

HTTPS 调试需要安装公开 CA 并配置自己的 Debug App。具体步骤见便携包中的 docs/network-capture.md。

## 发布与源码

- 源码仓库：[Gitee / AdbTools](https://gitee.com/xiedh/AdbTools)
- 发行版附带更新说明及 SHA256 校验文件。
- v1.0.0-beta.2 起支持应用内检查、下载和替换更新。beta.1 用户请先完整解压 beta.2 一次；之后可在右下角「检查更新」。启动时默认后台检查一次，下载与重启需要用户点击确认。
- 更新包提供 RSA-PSS 签名清单和文件完整性校验；这不是 Windows 代码签名，系统智能应用控制策略仍然适用。
- 第三方组件许可保留在发行包的 THIRD-PARTY-NOTICES.md、licenses 和 tools 中。
