# 第三方声明

Windows 便携发行包包含 .NET、Windows Desktop 和 ASP.NET Core 运行时。`runtime/` 保留本次发行所用 Microsoft .NET 10.0.15、Windows Desktop 10.0.15 与 ASP.NET Core 10.0.15 的许可证及第三方声明，构建脚本将它们复制到发行包的 `Runtime Notices` 目录。Windows Desktop 许可证来自运行时 NuGet 包，附带声明来自相同 SDK 10.0.404 的 Windows Desktop SDK 目录。

这些文件适用于相应第三方组件，不是对本项目源码的许可证声明。更换所打包的运行时版本时，应核对并更新这些声明。

Android 客户端依赖设备系统提供的 Android API 和 WebView，没有随 APK 分发 SDK、JDK 或 Tailscale。构建所需工具应从各自官方渠道获取并遵守其条款。
