# MajDHTProber 测试

本项目是 .NET 10 的 xUnit v3 测试工程，引用探测器项目与子模块中的 `MajDHTNetwork` 协议库。集成测试使用真实的回环 TCP / UDP 套接字，并把身份工作量设为 0，避免为每个短生命周期节点计算身份。

```powershell
dotnet test src/MajDHTProber.Tests/MajDHTProber.Tests.csproj -c Release
dotnet test src/MajDHTProber.Tests/MajDHTProber.Tests.csproj -c Release --filter "FullyQualifiedName~UiRenderingTests"
```

## 测试清单

| 文件 | 覆盖内容 |
| --- | --- |
| `src/LogSinkTests.cs` | 日志级别过滤、有界缓冲、警告 / 错误计数、异常摘要与监听器隔离。 |
| `src/ParsingTests.cs` | 端点解析与主机名解析、设置到 `DHTNodeOptions` 的转换与校验、本地节点设置的派生。 |
| `src/FormattingTests.cs` | 显示格式、十六进制转储、文本预览、XOR 公共前缀、集合原地同步、进度流。 |
| `src/PeerTrackingTests.cs` | 连接状态判定、节点目录合并、追踪路由表的注入 / 观察 / 移除及监听器隔离。 |
| `src/StorageTests.cs` | 身份持久化与删除、仅内存身份、设置 JSON 往返、键与记录码解析。 |
| `src/EndpointProberTests.cs` | TCP / UDP 端点探测、关闭端口的失败报告、临时客户端接入并下载。 |
| `src/NodeSessionTests.cs` | 会话启动停止、无效设置、按端点添加节点、拒绝添加自身、探测已知节点与路由探测、会话间传输、撤回与停止提供。 |
| `src/LocalNetworkTests.cs` | 本地测试网络的互相引介、两种端到端通信测试、移除本地节点、设置保存校验。 |
| `src/ViewModelTests.cs` | 节点列表筛选与详情、添加表单校验、设置页校验与保存、日志页筛选 / 暂停 / 清空、概览启动与内容页发布拉取。 |
| `src/UiRenderingTests.cs` | 用 Avalonia Headless（Skia）在浅色与深色主题下渲染每个页面和每个调试工具标签页，并断言没有绑定错误。 |

设置环境变量 `MAJDHTPROBER_SCREENSHOTS` 为一个目录后，渲染测试会把每个页面的截图保存为 PNG，便于检查界面。截图不属于仓库内容。
