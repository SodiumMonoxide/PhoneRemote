# 回归检查

在 Windows 交互式用户桌面中运行，需要 .NET 10 SDK。

```powershell
dotnet run --project .\tests\session\DualSessionCheck.csproj -c Release
dotnet run --project .\tests\tray\TrayCheck.csproj -c Release
dotnet run --project .\tests\client-transport\ClientTransportCheck.csproj -c Release
dotnet run --project .\tests\client-ui\ClientUiCheck.csproj -c Release
dotnet run --project .\tests\input\InputCheck.csproj -c Release
dotnet run --project .\tests\keyboard-capture\KeyboardCaptureCheck.csproj -c Release
dotnet run --project .\tests\input-wire\InputWireCheck.csproj -c Release
dotnet run --project .\tests\windows-integration\WindowsIntegrationCheck.csproj -c Release
```

会话检查用 `127.0.0.1` 和 `127.0.0.2` 的 TCP 8443 测试两个独立 HTTPS 服务，使用系统临时目录内随机创建的 `PhoneRemote-tests` 子目录保存测试身份。不会读取或改写真实应用的配对文件。

覆盖双端点证书和配对隔离、主动切换、禁止后台抢占、无效凭据不能影响连接、旧会话失效、重启后保留配对、并发重连只允许一个控制者、局域网地址变化后重连、断开或撤销另一模式不影响当前连接。测试会请求桌面帧以检查认证，但不会保存或输出截图。

测试使用本地回环绑定替身模拟两种地址，因此不能替代真实 Wi-Fi/Tailscale 网络连通性测试，也不能代替实际手机覆盖升级测试。实际界面和托盘行为应在交互式桌面中另外验证。

托盘检查使用内存中的服务替身和虚构地址，不连接真实设备。覆盖关闭选择、后台保持服务、状态刷新、恢复窗口、完全退出、启动进行中退出、系统关闭和多种缩放布局。它将仅含替身状态的窗口预览写到系统临时目录中的随机子目录，不读取或保存用户桌面截图。

Windows 控制端相关检查：

- `client-transport`：隔离 HTTPS 测试服务校验证书指纹、地址、有效期、拒绝重定向、请求超时和取消、响应大小上限、输入顺序，以及 DPAPI 加密配置的两种模式隔离。
- `client-ui`：画面比例和黑边坐标、输入队列和鼠标释放、本机只读、全屏、可拖动工具栏、退出键鼠恢复普通窗口，以及粘贴分段。使用事件级测试，不向操作系统注入有效遥控输入。
- `input`：通过注入的输入后端检查键盘按下/松开、修饰键、会话释放与超时清理，不向操作系统发送按键。
- `keyboard-capture`：检查键盘路由、作用域、退出快捷键、注入事件过滤和焦点丢失；测试不会安装真实键盘钩子。
- `input-wire`：生产版 HTTPS 客户端和服务之间传输键盘输入、心跳及释放请求，使用替代输入后端验证结果，不向操作系统发送按键。
- `windows-integration`：生产版控制端直接连接生产版服务，检查配对、画面 JPEG 解码、断开、保存与重启恢复，以及原 Android 协议与旧配对仍有效。测试用回环地址代替真实网卡，两种地址策略均运行，但没有建立真实 Tailscale 隧道。

这些检查不能代替第二台实体 Windows 电脑的鼠标键盘实测、双机 DPI 差异、网络延迟、真实防火墙或跨 Wi-Fi 检查。人工验收步骤见 [双 Windows 指南](../docs/Windows电脑遥控指南.md)。

网络测试共用回环端口 8443，应逐组运行，避免互相占用端口。
