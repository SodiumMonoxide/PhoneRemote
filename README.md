# 手机遥控电脑 · PhoneRemote

在安卓手机上查看 Windows 电脑画面，并操作鼠标、键盘和文字输入。同一个可信局域网可以直接连接，异地可通过两端的 Tailscale 连接。手机分别记住局域网和 Tailscale 的配对，之后点顶部按钮切换。

本次版本：**Windows 2.3.0 + Android 2.3.0**。手机协议兼容，安卓 2.2.0 仍可连接，安卓 2.3.0 同步更新内置指南。覆盖升级保留原有配对。

## 下载和安装

在本仓库的 **Releases** 页面下载：

- `PhoneRemote-v2.3.0-Windows-x64.zip`：Windows 便携版，完整解压到固定目录后运行 `PhoneRemote.exe`。
- `PhoneRemote-v2.3.0-Android.apk`：安卓客户端，在手机上安装。覆盖升级须使用相同签名的安装包。
- `PhoneRemote-v2.3.0-SHA256.txt`：下载校验值。

当前支持 Windows 11 x64、Android 10 及以上。Windows 包自带运行时。电脑须开机、联网、用户桌面可用，遥控服务须已启动。锁屏、休眠或 UAC 安全桌面期间不提供普通桌面遥控。Windows EXE 尚未进行 Authenticode 签名。

## 第一次连接

1. 在电脑打开应用，选择「局域网」或「Tailscale」，选中对应地址，点「启动连接」。同网使用局域网；异地使用 Tailscale，两端需先加入同一个 Tailscale 网络。
2. 在电脑点「复制连接信息」，把其中的 HTTPS 地址和证书 SHA-256 指纹两行传到手机剪贴板。
3. 打开安卓应用，点对应的「设置 局域网」或「设置 Tailscale」，再点「粘贴连接信息」。核对与电脑一致后点「保存并连接」。
4. 输入电脑同一模式显示的 8 位配对码，保留「记住这部手机」后连接。配对码有效 5 分钟。

两种方式需要分别完成首次配对。电脑分别启动两个服务后，手机在家点「局域网」，在办公室或外出时打开 Tailscale 后点「Tailscale」。应用恢复上次选择，无须反复输入配对码。

## 关闭窗口和退出应用

Windows 2.3.0 提供两种操作：

- **后台运行**：关闭操作窗口，已经启动的局域网和 Tailscale 服务继续运行。右下角通知区域的应用图标显示状态，双击图标可恢复窗口，右键可查看两种服务状态并退出。
- **退出应用**：停止两种服务、结束当前遥控并移除托盘图标。已保存的配对仍然保留。

点击窗口右上角的关闭按钮会显示选择。托盘图标可能在 Windows 的「显示隐藏的图标」里，可以将它拖到通知区域。重新打开同一程序会恢复已运行的窗口。

后台运行仅在当前 Windows 登录会话内有效。系统注销、关机或手动退出后，需要再次打开应用并启动所需连接。该版本不会自动设置开机启动。

## 指南

- [连接、切换、后台运行和故障排查](docs/使用指南.md)
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

