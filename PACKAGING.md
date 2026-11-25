# FastGrep 固定打包规范

每次打包必须由用户明确指定版本号，例如 `1.1`。不得沿用上一次版本，也不得自行推测版本；用户未提供版本号时必须先询问。指定版本为 `<VERSION>` 时，必须按本文档执行，并一次生成以下三个文件：

1. `Release/<VERSION>/FastGrep-<VERSION>-linux-x64.tar.gz`
2. `Release/<VERSION>/FastGrep-<VERSION>-windows-x64.zip`
3. `Release/<VERSION>/FastGrep-<VERSION>-macos-arm64.zip`

例如版本 `1.1` 的输出目录固定为 `Release/1.1/`，文件名必须包含 `1.1`。

其中用户口头所说的 `windows-x64`、`macos-aarch` 分别对应 .NET RID `win-x64`、`osx-arm64`。

## 固定要求

- 使用 `Release` 配置和 .NET 自包含发布，目标 RID 固定为 `linux-x64`、`win-x64`、`osx-arm64`。
- 版本号只接受 `主版本.次版本` 或 `主版本.次版本.修订号` 的纯数字形式，例如 `1.1`、`1.1.2`。
- 将指定版本同步写入 .NET 应用版本以及 macOS `CFBundleShortVersionString`、`CFBundleVersion`，不能只修改压缩包名称。
- 桌面主程序必须使用自包含单文件发布，确保从压缩包临时启动时不会缺失延迟加载的运行时程序集。
- 发布桌面项目 `src/FastGrep.Desktop/FastGrep.Desktop.csproj`，不要只发布 CLI。
- 每次都从 `src/FastGrep.Desktop/Assets/app-icon.svg` 重新生成 `app-icon.png`、`app-icon.ico`、`app-icon.icns`，并通过 `packaging/generate-icons.py` 去除 Quick Look 产生的不透明白底、增加 macOS 安全边距，保证三个平台使用同一个最新图标。
- 生成后必须检查 `app-icon.png` 四角 alpha 为 `0`；不得把带不透明白底的 Quick Look 缩略图直接写入 ICO 或 ICNS。
- Windows 压缩包必须包含带 ICO 图标的 `FastGrep.Desktop.exe`。
- macOS 压缩包必须包含标准 `FastGrep.app`，其 `Info.plist` 使用 `packaging/macos/Info.plist`，图标位于 `Contents/Resources/app-icon.icns`。
- Linux 压缩包必须保留 `FastGrep.Desktop` 的可执行权限。
- 发布包不包含 PDB 调试符号。
- 正式打包前必须运行项目测试；测试失败时不得交付新包。
- 三个平台的最终包必须分别执行 `FastGrep.Desktop --smoke-test`，实际验证文件发现、六路并行搜索和结果文件写入。
- 只允许覆盖同一 `Release/<VERSION>/` 目录中的三个同名包，不得覆盖其他版本目录，也不生成时间戳或随机后缀。
- 打包完成后检查三个文件均非空、可列出压缩内容，并报告文件大小与 SHA-256。

## 标准命令

在 macOS 上从仓库目录执行：

```bash
./package-release.sh 1.1
```

版本参数是必填项；省略或格式不合法时脚本必须在构建前直接退出。

脚本需要以下工具：.NET 10 SDK、macOS `qlmanage`、Python 3 与 Pillow、`tar`、`zip`、`xattr`。

可用环境变量 `FASTGREP_RUNTIME_VERSION` 指定自包含运行时的补丁版本，例如
`FASTGREP_RUNTIME_VERSION=10.0.15 ./package-release.sh 1.7`。压缩包附带 FastGrep 的许可证和依赖包的许可证声明。

## 发布目录边界

最终只交付 `Release/<VERSION>/` 目录中的三个带版本号压缩包。构建中间目录必须使用临时目录，不把 `bin`、`obj` 或临时发布目录复制到版本目录。
