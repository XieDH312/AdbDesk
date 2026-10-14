# 日志抓取与自有 App 网络调试

## 日志

工具箱选择在线设备 → 日志抓取 → 选择保存目录 → 开始采集。

- 选择应用后点击“查看日志”，会预填包名及 Android 用户。应用模式解析该用户的实际 UID，包含该 UID 的多个进程，应用重启不必重新填写 PID。旧系统不支持 UID 日志筛选时会明确提示，可自行选全设备模式。
- 默认保存 main / system / crash 缓冲区；可选择只采集 crash。开始会包含系统仍保留的日志，不清空手机日志。
- 等级、Tag、关键词只过滤预览。磁盘持续保存完整的选定范围日志，每 20 MiB 分卷，停止时排空管道并关闭文件。
- 每次新建独立目录。logcat-*.txt 是手机日志，adb-diagnostics 子目录是 ADB 诊断输出。预览最多 2500 行 / 512 Ki 字符，完整日志请查看目录。

## 网络抓包快速开始

1. 使用 USB 或已配对的无线 ADB 连接自己的调试手机，在工具箱选择设备，点击“网络抓包”。
2. 默认使用随包附带的 mitmdump.exe，端口 18888。这个端口是抓包端口，与无线 ADB 的 5555 / 随机端口无关。
3. 点击“开始抓包”。程序仅在电脑 127.0.0.1 监听，使用所选设备的 ADB reverse 建立通道。默认设置手机全局 HTTP 代理为 127.0.0.1:18888。
4. 普通 HTTP 应用请求可以直接查看。HTTPS 首次使用需点击“导出公开 CA”，在调试手机的系统设置中安装该 CA 证书，并按下一节配置自己的 Debug App，重新安装调试版后再发请求。
5. 查看请求列表，按关键词、域名、方法、状态组、内容类型、失败请求或 ≥1 秒筛选。域名匹配该域名与子域名；筛选只影响当前列表/历史页。
6. 点击请求后，分别查看概览/参数、请求头、请求正文、响应头、响应正文。正文支持原始文本、JSON 格式化、可折叠 JSON 树和查找；可单独保存原始正文。
7. 实时列表显示最近 200 条，磁盘独立保留会话历史。停止后点击“本次全部记录”，或刷新“历史会话”并打开，按每页 200 条浏览。历史查看不需要设备在线。
8. 停止后可导出整个会话的 HAR 或 JSON，不受页面和筛选影响。请求完整时可复制用于 PowerShell 7 的 cURL。
9. 点击“停止并恢复”。停止/关闭窗口/退出程序会先恢复本次自动修改的代理，再清理本次创建的 ADB reverse 通道和代理进程。

抓包期间手机的其他遵循系统代理的应用也可能被代理。这里没有按包名归属流量的功能，URL 筛选也不能证明请求来自哪个应用。登录令牌、Cookie 和正文可能出现在详情和导出的 JSON 中。

代理引擎首次成功启动后才会生成 CA；导出的 mitmproxy-ca-cert.cer 只有公开证书。私钥保存在当前 Windows 用户的 `%LOCALAPPDATA%\AdbDesk\network-ca`，不要把整个目录发给别人。程序不会自动安装证书，不修改 Windows 信任，不禁用 HTTPS 上游证书校验。

## 历史、正文与导出（beta.5）

- 默认保存在 `%LOCALAPPDATA%\AdbDesk\NetworkCaptures`；每次点击开始新建会话。窗口上方“历史文件夹”可以打开当前会话或历史根目录。
- 每会话最多 256 MiB（含原始正文和元数据），最多 5000 条记录；单个请求或响应正文最多保存 4 MiB。并发捕获上限 128，元数据预留可能使实际可保存并发数更少。达到限制或磁盘写入失败时会提示，后续请求仍转发，但可能不再保存。
- 已结束会话不自动删除；这些限制是**每会话**限制，并非所有历史合计容量。需要清理时，停止抓包后在历史文件夹手动删除不需要的会话目录。删除后引用正文文件的 cURL 也会失效。
- 请求头和响应头分别最多保留 16 Ki 字符 / 256 项，并标记是否完整；重复头不会合并。列表正文预览仍最多约 64 KiB，详情按需读取磁盘正文；解压上限为 4 MiB，避免压缩内容无限展开。
- 界面每个正文最多显示 512 Ki 字符，JSON 树最多 2000 节点 / 32 层；到达显示上限时可保存正文或使用 HAR 查看实际保存的数据。JSON 树中的查找会切换到格式化文本。
- “保存正文”输出原始字节，可能保留 gzip/br 等编码；部分正文以 `-partial.bin` 默认命名。HAR 文本内容会在支持的编码范围内解码，二进制响应使用 Base64。
- HAR 1.2 包含请求/响应头、同名查询参数和可读取正文。无法测量的细分耗时使用 -1；缺失、截断、未完成请求在注释和扩展字段中明确标记。二进制请求正文使用 `_encoding: base64` 扩展，接收工具需理解该扩展。不会把预览文字伪装成原始正文。
- JSON 导出包含所有已保存记录及预览，正文文件仍为本地会话目录中的引用；需要分享正文时使用 HAR。损坏或不安全的历史记录在翻页时提示，导出遇到损坏记录会失败，保留原有目标文件。
- “复制 cURL”只生成命令，不自动发送。命令面向 **PowerShell 7 + curl.exe**，有正文时引用已验证的本地正文文件；保留原始 Content-Encoding。请求头或正文不完整、头部不安全、URL/方法被截断时拒绝生成。cURL 不能复刻原连接的 TLS 会话或 App 的运行上下文。
- 历史中可能包含 Cookie、Authorization、个人数据；内容仅写入所选电脑，不自动上传。分享前请检查导出的内容。

## 连接诊断

展开“连接诊断”分别查看本机引擎、ADB reverse 通道、手机代理、是否观察到 HTTP 流量，以及最近的 TLS/存储错误。错误也会出现在底部提示。尚未收到请求不等于 VPN 导致失败；请结合 Debug App 的代理与证书信任设置判断。引擎启动、CA 文件存在均不能证明 App 已信任证书。

## Android Debug 版信任配置

Android 新版本 App 通常不会自动信任手动安装的用户 CA。若已有 network_security_config，请合并到现有配置，保留已有域名限制和其他安全设置。

下面是仅给 Debug 变体增加信任的示例。

在 `app/src/debug/AndroidManifest.xml` 中为自己的 Debug 构建添加配置引用（若已有该属性，请复用其资源名称）：

```xml
<manifest xmlns:android="http://schemas.android.com/apk/res/android">
    <application android:networkSecurityConfig="@xml/debug_network_security_config" />
</manifest>
```

在 `app/src/debug/res/xml/debug_network_security_config.xml` 中写入：

```xml
<?xml version="1.0" encoding="utf-8"?>
<network-security-config>
    <debug-overrides>
        <trust-anchors>
            <certificates src="user" />
        </trust-anchors>
    </debug-overrides>
</network-security-config>
```

这要求 App 是 `android:debuggable=true` 的调试构建，并已在手机安装导出的公开 CA。也可把公开 CA 作为 Debug 的 raw 资源，只信任指定 CA；不要把私钥放入 App。不要为此编写 trust-all TrustManager 或把调试信任合入 Release 构建。

若已有主 manifest 的 networkSecurityConfig 导致 manifest 合并冲突，优先在 `src/debug/res/xml/` 下提供与主配置同名的调试资源并加入 debug-overrides，保留主 manifest 引用。请先检查最终合并 manifest 和配置。

自定义 OkHttp CertificatePinner、原生 TLS、自定义信任管理等不一定遵循此配置；需要你在自己的 Debug 代码中提供对应的调试配置。本工具不自动处理证书固定。

## 范围与限制

- 本版是 HTTP/HTTPS 显式代理，不是全部 IP 数据包抓取。自定义 socket、忽略系统代理的库、UDP/QUIC、WebSocket 消息体可能无法在列表显示。
- 请求/响应实时预览最多约 64 KiB，原始正文另以每个 4 MiB 上限保存；完整性和保存错误单独显示。请求未结束或引擎中断时，可能只有部分正文，没有最终状态。
- 内容筛选不影响手机实际流量。“清空显示”不删除磁盘历史、不重发、不取消请求。
- VPN 或 App 自定义代理可能改变流量路径。如果没有请求，先确认自己的 App 遵循系统 HTTP 代理，必要时在 Debug 网络客户端显式指定 `127.0.0.1:18888`；不要直接关闭全机安全设置。
- 已有活动全局代理、PAC 或排除列表时，自动配置会拒绝覆盖。可取消自动配置，由你在自有 Debug App 中手动设置代理；手动修改由你负责恢复。
- 手机系统/企业策略若禁止 ADB 改代理，会显示失败。不要把“引擎启动”理解为 HTTPS 配置已经完成。

## 断线后的恢复

电脑中保留 `%LOCALAPPDATA%\AdbDesk\network-recovery-*.json`（代理原值）和 `network-reverse-*.json`（本次反向端口）直到恢复成功。不要提前删除它们。

手机掉线时可能无法立即恢复。以相同 ADB 序列号重新连接，进入“网络抓包”，点击“恢复遗留代理”。遇到用户/其他程序修改后的不同代理，工具会保留记录并拒绝覆盖。若无线 ADB 端口已变化，使序列号不同，请先根据恢复记录中的 `Serial`、原设置和端口确认设备；本版不会把一个序列号的恢复记录自动应用到另一设备。

如果通道创建命令在手机已执行但电脑未收到确认时被取消/超时，记录会保持“创建结果不确定”。重新检查后若端口不存在，工具可清除记录；若仍存在，因无法证明归属而不会自动删除。请按提示核对该端口的实际用途，手动移除自己确认的通道，再点击恢复。

人工恢复时先查看对应 JSON 的 `Original`，用 `adb -s 当前设备 shell settings get global 键名` 核对。只恢复自己确认的键值；`null` 表示原键不存在，空字符串表示存在但为空，两者不同。如果不确定，不要用通用“清空所有代理”脚本。ADB reverse 可用 `adb -s 当前设备 reverse --list` 查看，只移除本次记录的端口。

退出抓包后若 App 仍沿用旧网络连接，请在自己的调试 App 中重新建立请求或重启 App。系统/手机断线时的实际恢复行为仍需真机确认。

## 开发环境准备

仓库不存放工具二进制。运行 `scripts/setup-network-capture.ps1` 下载官方 mitmproxy 12.2.3 Windows x64 便携包，校验固定 SHA256 并只提取 mitmdump.exe。构建后脚本和引擎会复制到输出目录；使用者无需安装 Python。

引擎独立进程由本程序按需启动。系统若拦截未签名程序，请按 Windows 的组织/设备策略处理；本项目不提供绕过安全控制的启动方式。

参考：[Android Network Security Configuration](https://developer.android.com/privacy-and-security/security-config)、[logcat](https://developer.android.com/tools/logcat)、[mitmproxy 证书](https://docs.mitmproxy.org/stable/concepts/certificates/)、[mitmproxy 安装](https://docs.mitmproxy.org/stable/overview/installation/)。
