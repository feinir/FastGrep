# FastGrep

FastGrep 是面向 Windows、macOS、Linux 的海量文本搜索和批处理工具，使用 .NET 10 与 Avalonia 构建。核心引擎不依赖 UI，同时提供桌面端和 CLI。

## 已实现功能

- 与最新版 WPF 一致的 8 个页面：单文件处理、多文件搜索、多文件清理合并、内容去重、文件合并、文件拆分、查找文件、性能测试；右侧为全局提示/结果输出区。
- 单文件与多文件准确搜索：文本框或文件关键字、忽略大小写、三种真实计算模式、输出文件名、运行指标和停止控制。行块模式为大量关键字使用字节版 Aho–Corasick。
- 文本清理：独立的前缀/包含大小写开关、正则、短行、空行、首尾空白、行内空白、分隔字段；被删除的原始行可同步输出到界面或结果文件。
- 多文件清理可合并为一个文件，或按第 1–2 至第 4–5 字符分组；兼容原版 A–Z、0–9、`#` 路由和非 Email 单独输出，同时用 LRU 写入池避免 38×38 常驻写线程。
- 每个输入文件独立去重：保持首次出现顺序的内存模式，以及固定内存预算的外部排序模式；支持原目录后缀或新根目录下保留结构。
- 查找文件：列出/排除类型、无扩展名文件、递归、闭区间大小筛选、列表导入导出，并可复制到其他功能页。
- 保持目录结构复制/移动；移动前确认，跨卷时自动降级为复制后删除。
- 顺序合并与同名/同组目录合并均已实现；按完整行和软大小上限拆分，支持单文件及批量文件清单。
- 局部读取头部、尾部或指定字节偏移；有界 HashSet/SortedSet 和文件扫描性能测试。
- 搜索结果采用批量回调，输出文件串行保序；清理、分组清理、去重和顺序合并采用临时文件后原子提交，取消或失败时不会用半成品覆盖正式结果。
- 搜索按雷电 SSD 优化：同一挂载卷默认最多六个顺序读取流，每流使用 4 MiB 缓冲；NAS 自动建议四路。CLI 可用 `--reads-per-volume`、`--buffer-kb` 和 `--mode` 调整。
- 桌面端结果文件使用持久的 1 MiB 缓冲写入流，任务结束时统一刷新，避免高命中率搜索反复打开和关闭结果文件。

## 构建和运行

```bash
dotnet restore FastGrep.slnx
dotnet build FastGrep.slnx -c Release
dotnet run --project src/FastGrep.Desktop -c Release
dotnet run --project src/FastGrep.Cli -c Release -- help
dotnet run --project tests/FastGrep.Tests -c Release
```

CLI 示例：

```bash
# 搜索
dotnet run --project src/FastGrep.Cli -c Release -- search \
  --dir /data/logs --include '*.log|*.txt' --pattern error --pattern timeout \
  --ignore-case --output matches.txt

# 清洗
dotnet run --project src/FastGrep.Cli -c Release -- clean \
  --file input.txt --output cleaned.txt --remove-prefix DEBUG --ignore-case

# 超内存排序去重
dotnet run --project src/FastGrep.Cli -c Release -- dedup \
  --file input.txt --output distinct.txt --sorted --memory-mb 512

# 拆分
dotnet run --project src/FastGrep.Cli -c Release -- split \
  --file huge.txt --output-dir parts --lines 1000000
```

重复传入 `--file`、`--pattern` 可指定多个值。运行中的 CLI 可用 Ctrl+C 安全取消。

搜索模式：

- `line-block`：按完整行数量成块，使用多关键字自动机；适合大量关键字，超长行不会强制截断。
- `character`：按目标字节量成块，在边界前后 4 KiB 内优先使用自然换行。适合少量关键字。超长行会继续读取到换行或文件末尾，再匹配并输出完整行。
- `single-line`：逐行累计到目标块大小，当前完整行结束后提交；适合中等关键字数量，完整保留每一行。

## 发布

每个平台/架构需要分别发布：

```bash
dotnet publish src/FastGrep.Desktop -c Release -r osx-arm64 --self-contained -o artifacts/osx-arm64
dotnet publish src/FastGrep.Desktop -c Release -r osx-x64 --self-contained -o artifacts/osx-x64
dotnet publish src/FastGrep.Desktop -c Release -r win-x64 --self-contained -o artifacts/win-x64
dotnet publish src/FastGrep.Desktop -c Release -r win-arm64 --self-contained -o artifacts/win-arm64
dotnet publish src/FastGrep.Desktop -c Release -r linux-x64 --self-contained -o artifacts/linux-x64
dotnet publish src/FastGrep.Desktop -c Release -r linux-arm64 --self-contained -o artifacts/linux-arm64
```

macOS 正式分发还需要应用包、签名和公证；Windows 正式分发建议签名并生成 MSIX/安装器；Linux 可进一步制作 AppImage、deb 或 rpm。

## 正确性约定

- 普通关键字直接搜索源字节，不强制输入为 UTF-8；支持常用 UTF-8、UTF-16、GB18030/GBK、Big5 和 Windows 代码页变体。结果输出默认使用 UTF-8。
- 忽略大小写使用精确的 `OrdinalIgnoreCase` 语义；大量 ASCII 关键字使用预构建 Aho–Corasick 自动机，非 ASCII 关键字自动切换到精确比较路径。
- 清洗和顺序合并保持输入顺序。
- 按大小拆分以完整行为单位，因此是软上限。
- 界面保留最近的输出。大量命中超过界面刷新速度时，会淘汰最早的待显示结果，完整结果仍可写入独立文件。
- 外部排序去重按 ordinal 顺序输出，按内存预算分批生成和合并临时文件。极长的单行和 .NET 运行时本身可占用额外内存。稳定去重保持第一次出现顺序。

## 项目结构

- `src/FastGrep.Core`：搜索和全部文件处理能力。
- `src/FastGrep.Cli`：自动化、服务器和脚本入口。
- `src/FastGrep.Desktop`：跨平台 Avalonia GUI。
- `tests/FastGrep.Tests`：无第三方测试框架依赖的端到端验证程序。

## 许可证

FastGrep 源码使用 [MIT 许可证](LICENSE)。随安装包分发的依赖保留各自的许可证。
