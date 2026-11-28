# PhoneRemote · 电脑遥控与 iPad 副屏

PhoneRemote 支持 Windows 与 Mac 之间遥控、两台 Windows 或 Mac 互控，以及 Android 控制 Windows。同一可信局域网可直连，异地可通过两端的 Tailscale 连接。

Windows **2.7.0** 新增「iPad 副屏」。同一局域网内的 iPad 可以显示 Windows 扩展桌面。已配对的 iPad 可继续使用原来的主屏幕图标。Mac 使用 **2.6.0**，Android 使用 **2.3.0**。

2.6.0 版已在 Windows 验证协议、客户端和控制页面。Mac 主控的 AppKit 界面、被控端的系统授权与双机键鼠仍待 Mac 实机验证。

## 下载和安装

- **Windows**：下载 `PhoneRemote-v2.7.0-Windows-x64.zip`，完整解压到固定目录后运行 `PhoneRemote.exe`。请保留同目录的 `modules` 文件夹。发布页也提供源码及 SHA-256 校验清单。[Windows 2.7.0 发布页](https://github.com/SodiumMonoxide/PhoneRemote/releases/tag/v2.7.0)
- **Mac**：`PhoneRemote-v2.6.0-macOS-Apple-Silicon.zip` 和 `PhoneRemote-v2.6.0-macOS-Intel.zip` 分别适用于 Apple 芯片和 Intel 芯片的 Mac，要求 macOS 14 或更高版本。解压后将 `PhoneRemote.app` 拖入「应用程序」。应用内置控制端、被控端和运行时，采用本地签名，尚未经过 Apple 公证。[Mac 2.6.0 发布页](https://github.com/SodiumMonoxide/PhoneRemote/releases/tag/v2.6.0)
- **Android**：下载 `PhoneRemote-v2.3.0-Android.apk`。已配对的手机可继续连接。[Android 2.3.0 发布页](https://github.com/SodiumMonoxide/PhoneRemote/releases/tag/v2.3.0)

在 PowerShell 运行 `Get-FileHash .\PhoneRemote-v2.7.0-Windows-x64.zip -Algorithm SHA256`，与发布页的校验清单比对。若 Windows 因下载标记而拒绝运行副屏模块脚本，请先核对散列，再打开原 ZIP 的「属性 → 解除锁定」并重新完整解压。可保留现有的 PowerShell 执行策略。

Windows 版适用于 Windows 11 x64，Android 版需要 Android 10 及以上。Windows 包自带运行时。被控电脑须开机、联网、用户桌面已解锁，且已启动遥控服务。Windows EXE 尚未进行 Authenticode 签名。

## 第一次连接

### 在 Windows 上把 iPad 用作扩展屏

在新电脑上首次使用副屏，需要 Windows 11 x64 和 Python 3.12 x64。打开 PhoneRemote，选择「iPad 副屏」以启动服务，首次配置时按提示点「配置驱动和网络」。已安装独立副屏的电脑可以沿用原环境。让 iPad 与电脑接入同一局域网，从原来的「Wi-Fi 副屏」图标点「连接屏幕」，即可沿用已保存的配对。

首次配对时，在标签页打开配对控制台，按提示设置 iPad，并用六位「今日配对码」连接。使用电脑键鼠把窗口拖过主屏右边缘，即可移到 iPad。轻点 iPad 画面可以打开全屏和操作说明。详细步骤见[使用指南](docs/使用指南.md#ipad-副屏)。

### Mac 与 Windows 互控、两台 Mac 互控

1. 在**被控电脑**启动共享。Windows 选择网络地址并点「启动连接」。Mac 点「打开被控服务」，在终端选择地址，按系统提示授予屏幕录制和辅助功能权限。
2. 在被控电脑复制 HTTPS 地址和证书指纹。Windows 点「复制连接信息」，Mac 在服务终端输入 `c` 再回车。将这两行交给控制电脑。
3. 在**控制电脑**打开 PhoneRemote。Windows 点「控制 Windows 或 Mac」，Mac 使用首页「控制另一台电脑」。粘贴连接信息并核对，在配对页输入被控端显示的 8 位码。
4. 在 Mac 控制端点击远程画面即可使用鼠标、常用按键和键盘文字输入。Esc 把焦点移回操作面板。输入中文或较长文本可展开「输入文字」。Mac 主控每次建立新会话都需要配对码，被控 Mac 可在终端输入 `n` 生成新码。

Windows 键盘控制 Mac 时，Windows 徽标键对应 Command，Alt 对应 Option。完整的安装、断开和系统授权步骤见 [Mac 互控指南](docs/Mac互控指南.md)。

### Windows 控制 Windows

1. 两台电脑均运行 Windows 版。被控电脑在主界面选择局域网或 Tailscale 并启动连接。
2. 被控电脑点「复制连接信息」。控制电脑点「控制 Windows 或 Mac」，选择对应模式，点「粘贴连接信息」并核对两项内容。
3. 填写被控电脑显示的配对码后连接。若已经配对手机，先结束当前会话，再点「生成新码」添加电脑；原手机配对会保留。
4. 配对后直接使用鼠标和键盘操作。展开顶部工具栏可全屏、退出键鼠控制或准备粘贴本机文本；拖动栏头可改变位置。记住后可直接重连，两种模式独立保存。

**Ctrl+Alt+F12** 退出键鼠控制并恢复普通窗口，保持画面连接；**Ctrl+Alt+Enter** 切换全屏。进入控制后，普通 Tab、Esc 和 F11 发给被控电脑。

跨网络时两台电脑均需连接同一 Tailscale 网络。控制电脑只需主动连接，不必启动本机被控服务。本机连接强制只读预览。详细操作与限制见[双 Windows 指南](docs/Windows电脑遥控指南.md)。

### Android 控制 Windows

1. 在电脑打开应用，选择「局域网」或「Tailscale」，选中对应地址，点「启动连接」。同网使用局域网；异地使用 Tailscale，两端需先加入同一个 Tailscale 网络。
2. 在电脑点「复制连接信息」，把其中的 HTTPS 地址和证书 SHA-256 指纹两行传到手机剪贴板。
3. 打开安卓应用，点对应的「设置 局域网」或「设置 Tailscale」，再点「粘贴连接信息」。核对与电脑一致后点「保存并连接」。
4. 输入电脑同一模式显示的 8 位配对码，保留「记住这部手机」后连接。配对码有效 5 分钟。

两种方式需要分别完成首次配对。电脑分别启动两个服务后，手机在家点「局域网」，在办公室或外出时打开 Tailscale 后点「Tailscale」。应用恢复上次选择，无须反复输入配对码。

## 关闭窗口和退出应用

Windows 提供两种操作：

- **后台运行**：关闭操作窗口，已经启动的局域网和 Tailscale 服务继续运行。右下角通知区域的应用图标显示状态，双击图标可恢复窗口，右键可查看两种服务状态并退出。
- **退出应用**：关闭 Windows 控制窗口、停止两种服务、结束当前遥控并移除托盘图标。已保存的配对仍然保留。

点击窗口右上角的关闭按钮会显示选择。托盘图标可能在 Windows 的「显示隐藏的图标」里，可以将它拖到通知区域。重新打开同一程序会恢复已运行的窗口。

后台运行仅在当前 Windows 登录会话内有效。系统注销、关机或手动退出后，需要再次打开应用并启动所需连接。该版本不会自动设置开机启动。

## 指南

- [连接、切换、后台运行和故障排查](docs/使用指南.md)
- [Windows 电脑控制 Windows 电脑](docs/Windows电脑遥控指南.md)
- [Windows 虚拟机验证结果与覆盖范围](docs/Windows虚拟机验证.md)
- [从源码构建和签名](docs/构建说明.md)
- [版本变更](CHANGELOG.md)
- [安全说明](SECURITY.md)
- [回归检查](tests/README.md)

电脑端和手机端均有内置「使用指南」。跨网络连接需要自行安装 Tailscale：[Windows](https://tailscale.com/docs/install/windows)、[Android](https://tailscale.com/docs/install/android)。

## 代码结构

```text
windows/       Windows Forms 界面、HTTPS 服务、输入控制和 Web 页面
macos/         AppKit 控制端、Mac 主机和 ScreenCaptureKit / Quartz 适配
android/       原生 Android 客户端与 HTTPS 证书指纹校验
scripts/       Windows 构建脚本
docs/          安装、连接和构建文档
third_party/   发行包所用运行时的许可和第三方声明
```

仓库不包含设备配对、用户证书、APK 签名私钥或本地 SDK。第三方运行时许可见 [third_party](third_party/README.md)。

