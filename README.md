# MajDHTProber

MajDHTProber 是 [MajDHTNetwork](modules/MajDHTNetwork)（MajDHT v1 协议）的图形化探测与调试工具，基于 **.NET 10** 与 **Avalonia 12**。它在本进程内托管一个或多个 `MajDHTNode`，用来查看网络中的节点、手动接入节点、检查连接状态，并提供文件拉取、节点间通信测试等调试功能。

## 功能

| 页面 | 内容 |
| --- | --- |
| 概览 | 当前节点的 ID、能力、可达性、外部地址、NAT 映射、提示节点、监听端点与健康指标；启动 / 停止 / 引导 / 刷新邻居；创建本地测试网络（多个回环节点）。 |
| 节点 | 网络内所有已知节点（路由表、查找结果、提供者、提示、手动添加、局域网扫描、本地节点），按状态筛选与搜索；手动按端点添加节点；扫描局域网（UDP 广播签名 Hello）并一键添加应答的节点；节点详情：连接状态、观察到的端点、提示的 Traversal 节点与 Forwarder、身份工作量、租期、传输统计；端点探测、路由探测、登记 Traversal 节点、建立转发会话、经 Traversal 节点打洞。 |
| 内容 | 发布文本 / 文件、发布别名、重新公告、撤回、停止提供；按键或记录码拉取文件（内存预览或保存到文件，带进度与完整校验）；解析键、查找提供者；导入他人内容的本地副本。 |
| 调试工具 | 端点探测（类 ping：签名 Hello + 加密 Ping，统计丢失率、往返时间与对方观察到的本机地址）、路由查找（XOR 距离）、单块读取（指定提供者与 Forwarder，十六进制预览）、Traversal 节点登记、转发会话与 TCP / UDP 打洞、端到端通信测试、按节点与路由的传输统计。 |
| 日志 | 协议库与探测器的实时日志，按级别 / 来源 / 关键字筛选，暂停、跟随、导出。 |
| 设置 | 监听地址、端口、是否已知公网可达、优先传输、能力（下载、提供、Traversal、Forwarder）、引导端点、身份工作量、超时、维护间隔与重新公告间隔等协议参数；身份持久化与重置；路由表持久化；主题与日志级别。 |

### 连接状态

| 状态 | 含义 |
| --- | --- |
| 已连接 | 节点是当前节点路由表中的有效条目，可作为查找跳点。 |
| 可达 | 最近 5 分钟内有经过认证的交互或探测成功，但不在路由表中（如私有节点、路由表未接纳）。 |
| 已发现 | 仅从查找结果或引用得知，尚未直接交互。 |
| 已断开 | 上次成功之后发生过请求失败或探测失败。 |
| 已过期 | 节点签名联系人的租期已过期。 |

## 实现要点

探测器只使用 MajDHTNetwork 的公开 API 与扩展点，不修改子模块：

- **节点与端点**：协议中的签名联系人（`DHTContact`）不携带端点；一个节点所知的另一个节点（`DHTPeer`）是它的签名联系人加上本节点或引荐者从实际数据包中观察到的端点。探测器的节点列表按节点合并各来源报告的观察端点，并单独记录本节点自己最近一次与它交换已认证数据包的端点。
- **节点追踪**：每个托管节点使用 `TrackingRoutingTable`（包装库自带的 `KademliaDHTRoutingTable` 的 `IDHTRoutingTable`），只观察、不干预：记录节点观察到的每个经过认证的联系人（及观察到它的端点）与因失败而移除的节点，汇总到 `PeerDirectory`。联系人是否进入路由表完全由库决定。
- **端点探测**：直接使用库公开的协议层（`SocketTransport`、`Packet`、`HelloMessage` 等编解码器）。每次探测以独立的“探针身份”绑定一个只发不收的传输：先发送签名 Hello 得到端点上节点的签名联系人、往返时间和对方观察到的本机地址，再向同一端点发送一次加密 Ping，分别计时；不回退到另一种传输。探针不应答任何请求，不会进入对方路由表；探测器也会把探针身份从自己托管节点的节点列表中过滤掉。
- **局域网扫描**：`LanDiscovery` 用探针身份在自己的 UDP 套接字上按协议文档的 UDP 一节实现 Cookie 握手（`UdpFrames`）：向本机每个 IPv4 子网的广播地址广播一个宣告签名 Hello 请求（`Packet.SealHello`）摘要的 Hello 帧，每个节点各自回复 Challenge，扫描器再逐个发送携带 Cookie 的请求帧，并在约 1.5 秒窗口内收集应答，用 `Packet.Open` 校验签名与请求 ID 并按节点 ID 去重。任何在该端口监听的 MajDHTNetwork 节点都会应答，无需对方运行探测器。扫描结果只记入节点列表作为线索（来源“局域网”），“添加”仍走下面的 `AddPeerAsync`。限制：只能发现监听同一 UDP 端口的节点；仅 IPv4；监听具体地址（而非 0.0.0.0）的节点在 Linux 上收不到定向广播；防火墙需放行该端口。
- **手动添加节点**：调用库的 `MajDHTNode.AddPeerAsync`：当前节点自己向端点发送签名 Hello（先用所选传输，无应答时在同一端口改用另一种），把应答节点按观察到的端点加入路由表，若其具备 Traversal 或 Forwarder 能力且已启用 NAT 穿透则向它登记，随后经它查找邻居，与引导端点的处理完全一致。
- **NAT 穿透**：声明 `Traversal` 的节点观测登记节点的端点、引介双方打洞并安排可达性回拨；声明 `Forwarder` 的节点托管转发会话并中继端到端加密的信封。探测器分别暴露 `RegisterTraversalAsync`、`RegisterForwarderAsync` 与 `TryPunchAsync`，块读取可指定 Forwarder。
- **本地测试网络**：额外节点监听 `127.0.0.1` 的系统分配端口，经已运行的节点引导，并通过同样的 `AddPeerAsync` 在回环上与每个已运行节点双向互联，便于在无外部网络时测试节点间通信。
- **路由表持久化**：主节点停止时用 `ExportRoutingTable` 把路由表中节点的 ID 与端点保存到用户数据目录，下次启动后在后台用 `RestoreRoutingTableAsync` 重新联系这些节点，只接纳仍以原身份应答的节点；快照不含密钥、签名或内容。本地测试网络的节点端口每次不同，不保存路由表。
- **身份**：节点身份需要满足网络的身份工作量（默认 12 位，生成可能需要数秒到数十秒），因此身份按名称缓存，可选保存到用户数据目录，节点 ID 在重启后保持不变。种子从不写入日志。

## 目录结构

| 路径 | 内容 |
| --- | --- |
| `MajDHTProber.slnx` | 解决方案：探测器、测试与子模块中的协议库。 |
| `src/MajDHTProber/` | 探测器：`Services`（会话、探测、路由追踪、身份、路由表快照、设置）、`Models`、`ViewModels`、`Views`、`Themes`（配色、图标、控件样式）、`Logging`、`Infrastructure`。 |
| `src/MajDHTProber.Tests/` | xUnit v3 测试：单元测试、真实回环套接字的集成测试与 Avalonia Headless 渲染测试，见其 [README](src/MajDHTProber.Tests/README.md)。 |
| `modules/MajDHTNetwork/` | 协议库子模块（`netstandard2.1`）。 |

## 构建与运行

需要 .NET 10 SDK，并先拉取子模块：

```powershell
git submodule update --init
dotnet build MajDHTProber.slnx -c Release
dotnet run --project src/MajDHTProber/MajDHTProber.csproj -c Release
```

运行测试：

```powershell
dotnet test src/MajDHTProber.Tests/MajDHTProber.Tests.csproj -c Release
```

## 数据与配置

设置（`settings.json`）、身份种子（`identities/*.seed`）与主节点的路由表快照（`routing/*.routing`）保存在 `%APPDATA%\MajDHTProber`（Linux / macOS 为对应的应用数据目录）。设置环境变量 `MAJDHTPROBER_HOME` 可改用其他目录，例如便携运行或隔离测试。

默认主节点监听 `0.0.0.0` 的 TCP / UDP `42000` 端口、身份工作量 12 位。其他节点通过观察本节点发出的数据包得知其端点，可达性由独立回拨自动确认；若本机确实可从公网访问（固定公网地址或路由器端口映射），可在设置中勾选“已知公网可达”，节点会直接通告公网可达并跳过回拨检查。身份工作量必须与被探测网络一致。

## 已知限制

- 打洞、可达性检查与转发依赖真实 NAT 环境，仓库中的测试只覆盖回环网络。
- 恢复路由表时，每个不再应答的已保存节点都要等待请求超时，一次最多同时联系 16 个。
- 本地测试网络的节点使用回环地址，只能与本机节点互通。
- 局域网扫描依赖子网广播，仓库中的测试只向回环目标发送 Hello；真实广播需在局域网中手动验证。

## 许可证

MIT，见 [LICENSE](LICENSE)。
