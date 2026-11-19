# PhoneRemote · 手机和电脑遥控 Windows

在安卓手机或另一台 Windows 电脑上查看 Windows 桌面，并操作鼠标、常用按键和文字输入。同一个可信局域网可以直接连接，异地可通过两端的 Tailscale 连接。两种方式分别保存配对。

本次版本：**Windows 2.5.0 + Android 2.3.0**。Windows 控制端支持直接键鼠、纯画面全屏及可拖动的顶部折叠工具栏。两台 Windows 均升级至 2.5.0 后使用新键盘功能；手机不必升级或重新配对。尚无第二台实体设备，真实双 Windows 遥控和跨 Wi-Fi 测试待完成。

## 下载和安装

在本仓库的 **Releases** 页面下载：

- `PhoneRemote-v2.5.0-Windows-x64.zip`：Windows 便携版，完整解压到固定目录后运行 `PhoneRemote.exe`，同时包含控制端和被控端。
- `PhoneRemote-v2.3.0-Android.apk`：安卓客户端，在手机上安装。覆盖升级须使用相同签名的安装包。
- `PhoneRemote-v2.5.0-SHA256.txt`：新版 Windows 包与源码的下载校验值。

当前支持 Windows 11 x64、Android 10 及以上。Windows 包自带运行时。电脑须开机、联网、用户桌面可用，遥控服务须已启动。锁屏、休眠或 UAC 安全桌面期间不提供普通桌面遥控。Windows EXE 尚未进行 Authenticode 签名。

## 第一次连接

### Windows 控制 Windows

1. 两台电脑均运行 Windows 2.5.0 版。被控电脑在主界面选择局域网或 Tailscale 并启动连接。
2. 被控电脑点「复制连接信息」。控制电脑点「控制另一台电脑」，选择对应模式，点「粘贴连接信息」并核对两项内容。
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
- [从源码构建和签名](docs/构建说明.md)
- [版本变更](CHANGELOG.md)
- [安全说明](SECURITY.md)
- [回归检查](tests/README.md)

电脑端和手机端均有内置「使用指南」。跨网络连接需要自行安装 Tailscale：[Windows](https://tailscale.com/docs/install/windows)、[Android](https://tailscale.com/docs/install/android)。

## 代码结构

```text
windows/       Windows Forms 界面、HTTPS 服务、输入控制和 Web 页面
android/       原生 Android 客户端与 HTTPS 证书指纹校验
scripts/       Windows 构建脚本
docs/          安装、连接和构建文档
third_party/   发行包所用运行时的许可和第三方声明
```

仓库不包含设备配对、用户证书、APK 签名私钥或本地 SDK。第三方运行时许可见 [third_party](third_party/README.md)。

