# PhoneRemote · iPad 副屏

在 Windows 版 PhoneRemote 选择「iPad 副屏」即可启动发送端。将 iPad 和电脑接入可互访的同一局域网。已配对的 iPad 从原来的「Wi-Fi 副屏」图标进入，轻点「连接屏幕」。电脑与 iPad 界面都提供「操作说明」。

首次连接新 iPad 时，从电脑控制台展开「首次安装到 iPad」，在 Safari 下载描述文件，按屏幕指引安装并信任对应的本地证书。在 iPad 输入电脑显示的六位配对码，回电脑点「允许连接」。北京时间每天零点更新配对码。原凭据继续有效。

## 首次配置新电脑

1. 使用 Windows 11 x64，安装 Python 3.12 x64，安装时勾选 Python Launcher。选择「iPad 副屏」会在当前用户目录准备 Python 环境，首次下载依赖需要网络。已安装独立副屏的电脑会复用现有环境、驱动与配对。
2. 打开模块的电脑配置功能，按系统提示授权安装虚拟显示器及局域网防火墙规则。手动配置时，在本目录打开管理员 PowerShell，运行以下命令，把示例地址换成本机局域网 IPv4。

   ~~~powershell
   .\Configure-PC.ps1 -Address 192.168.1.10
   ~~~

3. 回到 PhoneRemote 打开控制台。横放 iPad 并连接后，用电脑鼠标将窗口拖过主屏右侧边缘即可在 iPad 上看到它。触碰 iPad 画面会显示全屏、画面缩放和断开等按钮。

## 安装文件和端口

分发时将整个 Windows 程序目录一同解压，保留 PhoneRemote.exe 旁的 modules 文件夹。接收器与证书保存在当前 Windows 用户的 %LOCALAPPDATA%\iPadWiFiDisplay\session。副屏控制台使用 TCP 8442，新安装的副屏视频使用 TCP 8453。已安装旧版 Web Clip 的电脑继续使用原 HTTPS 端口 8443，让主屏幕图标与 Safari 已保存的配对继续可用。如果旧副屏和 PhoneRemote 遥控服务使用同一个局域网地址和 8443，请先停止其中一个服务再切换。WebRTC 在同一局域网通过 UDP 传输，兼容连接通过 HTTPS 传输。

模块文件包含发送端、接收页以及经过签名和散列校验的驱动安装脚本。驱动由安装脚本获取。Python 包的精确版本见 host/requirements.txt 和 requirements-setup.txt。请保留旧版副屏使用的 Python 环境，或在移除旧版后重新启动本模块以建立新的环境，并在新环境使用不同解释器时重新运行电脑配置。
