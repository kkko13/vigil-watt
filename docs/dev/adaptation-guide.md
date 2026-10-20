# 2.4G 设备适配指南（从扫描不到到上线完整流程）

> 本文档面向"给一台从未支持过的 2.4G 设备写电量插件"的完整工作流。
> 案例全部来自真实适配：英菲克 F1（公版 `1EA7:0066` dongle，2026-10-20 一天内完成逆向 + 插件 + 真机验证）。
> 另一台电脑上做新设备适配时，从"环境准备"开始按顺序执行即可。

---

## 0. 背景知识：这个软件怎么"看见"设备

VigilWatt 读 2.4G 设备电量的链路：

```
USB 接收器(dongle)
  → Windows 把 dongle 枚举成若干 HID 顶层集合(top-level collection)
    (鼠标、键盘、consumer、以及 1~N 个 vendor-defined 集合 usage page 0xFFxx)
  → HidDeviceScanner 枚举出每个集合的 VID/PID/UsagePage/Usage/报告长度/Report ID
  → 每个插件(BatteryPluginBase 子类)按 VID/PID/集合特征"认领"接口
  → 认领后被动监听 或 主动发查询帧,解析出电量百分比
```

关键事实：

- **dongle  ≠ 设备本身**。PC 只和 dongle 说话；dongle 和鼠标之间是无线私有协议。
  电量数据要么由 dongle 被动推送（如英菲克 ZHEN 每 2 秒一帧），要么藏在 dongle 的
  寄存器里、需要发查询帧（如 F1 必须发 `B5 01` 才回答）。
- **电量不是 HID 标准字段**。每个品牌/型号的帧格式都不同，必须逐个逆向。
  没有任何"通用读法"，这是适配工作的本质。
- **vendor-defined 集合（UsagePage 0xFF00–0xFFFF）是主要战场**。
  标准集合（鼠标 0x0001/0x0002、键盘 0x0001/0x0006、consumer 0x000C）被系统独占，
  打不开也读不到，电量极少藏在里面。
- 一个 dongle 通常枚举出 4~6 个集合，**插件必须精确匹配其中一个**，
  匹配条件写少了可能认领别家硬件，写多了换个固件就失效（参考 `GanssCodec` 的注释）。

判断一台设备"能不能适配"的依据，按优先级：
1. 枚举结果里有没有 vendor-defined 集合且有输入/输出报告长度 > 0；
2. 被动嗅探能不能收到含 0–100 稳定值的帧；
3. 能不能通过查询帧让设备开口（本指南 §3–§4）;
4. 都没有 → 该 dongle 不向 PC 暴露电量（如 README 里的弱水时砂炽天使先例），硬件层面无解。

---

## 1. 环境准备（新电脑 checklist）

| 依赖 | 版本 | 说明 |
|---|---|---|
| Windows | 10/11 x64 | 必须 x64 |
| .NET SDK | 8.0 或更高 | 本机用 10.0.302 可正常构建 net8 目标；`dotnet --version` 确认 |
| Git | 任意 | fork 的克隆/推送 |

```powershell
# 一次性验证
dotnet --version          # 有输出即可
git --version
```

克隆（用你的 fork）：

```powershell
git clone https://github.com/kkko13/vigil-watt.git
cd vigil-watt
dotnet build VigilWatt.sln -v q    # 首次约 3 分钟；之后增量构建几秒
dotnet test                        # 全部测试应通过（当前 525 个）
```

**本 fork 相对上游的增量**（都在 `main` 分支）：
- `src/tests/VigilWatt.HidSniff/` —— 抓包/探测工具（本指南的主角）；
- `src/VigilWatt.Core/Plugins/Inphic/` 里的 `InphicF1Plugin` / `InphicF1Codec` —— 参考实现；
- `src/tests/VigilWatt.Core.Tests/InphicF1CodecTests.cs` —— 参考单测。

---

## 2. 第一步：确认设备身份（VID/PID）

插上目标设备的 2.4G 接收器，PowerShell 执行：

```powershell
Get-PnpDevice -PresentOnly | Where-Object {
    $_.InstanceId -match 'HID' -and $_.InstanceId -match 'VID_[0-9A-F]{4}&PID_[0-9A-F]{4}'
} | Select-Object FriendlyName, InstanceId | Format-List
```

从输出里找到目标设备（按 FriendlyName 或拔掉/插上对比）。记下：
- `VID_XXXX`（厂商 ID，16 进制）
- `PID_YYYY`（产品 ID）
- 同一 dongle 会有多条（每个 HID 集合一条），VID/PID 都一样，靠 `MI_XX&COLxx` 区分。

已知 VID 速查：英菲克自家 `1D57`；公版 combo dongle `1EA7`（F1 用的，很多品牌贴用）；
雷柏 `24AE`；罗技 `046D`……新设备先搜一下 VID 属于谁。

对照上游 README 的设备支持表，确认该 VID/PID 是否已被支持。已被支持却扫不到 = 报 bug；
没被支持 = 开始适配。

---

## 3. 第二步：被动嗅探（零风险，先做）

被动嗅探只读不发任何帧，可以无限期开着。目的：确认( a ) 哪个集合有数据流，( b ) 设备会不会主动推电量帧。

```powershell
cd src/tests/VigilWatt.HidSniff
dotnet run -- --vid 1EA7 --pid 0066 --seconds 180
```

参数：`--vid`/`--pid` 十六进制；`--seconds` 嗅探时长。

**嗅探期间必须真实使用设备**：移动、点击、按 DPI 键、拨电源开关、切模式。
设备静置时大部分 dongle 是不发帧的。

### 结果判读

**情况 A：有帧，且某字节是 0–100 的稳定值**（典型：英菲克 ZHEN `03 76 41 <flag> <percent>`）。
恭喜，纯被动协议。直接看 §6 写被动插件。

**情况 B：收到帧但找不到电量字节**。帧可能是心跳/活动通知（雷柏 VT3 就是
`status-report7` 心跳里藏电量）。把多帧并列对比：哪个字节随时间缓慢变化、范围 0–100？
变化太快的（每次采样都变）是计数器，别选。

**情况 C：全程零帧**（F1 就是这样，180 秒 × 3 轮全零）。说明设备完全沉默，
走 §4 主动探测。零帧不代表没戏——F1 就是零帧设备。

### F1 案例实录（情况 C）

```
扫描到 7 个 HID 集合，匹配 4 个：
  2.4G Mouse [VID_1EA7&PID_0066] usage=0xFFB5/0x0001 in=8 out=8 feat=0 outId=0xB5   ← 唯一厂商通道
  2.4G Mouse [VID_1EA7&PID_0066] usage=0x000C/0x0001 in=3 out=0 feat=0 outId=0x01   consumer
  2.4G Mouse [VID_1EA7&PID_0066] usage=0x0001/0x0080 in=2 out=0 feat=0 outId=0x03   系统控制
  2.4G Mouse [VID_1EA7&PID_0066] usage=0xFFBC/0x0088 in=2 out=0 feat=0 outId=0x04   另一个厂商集合
```
判读：`0xFFB5` 集合 in=8 **且 out=8**（双向，可写可问）是主嫌疑；`0xFFBC` 只读，次要嫌疑。
consumer/系统控制是标准集合，基本排除。

---

## 4. 第三步：主动探测（会写帧，谨慎）

设备沉默时，唯一的办法是替它"官方软件会发的查询帧"。**先想清楚风险**：
探测帧可能改变设备状态（DPI、灯效、配对）。这些都是可恢复的（DPI 键/断电重启），
但不要在别人开会做演示前对主力设备试。

```powershell
dotnet run -- --vid 1EA7 --pid 0066 --probe
```

工具向 dongle 唯一的可写集合发送 14 个常见候选查询帧（`B5 01/02/…/76 41/AA/5A`），
每帧后监听 1.5 秒，全程打印十六进制帧。

### 探测结果判读（F1 实录）

```
>> 发送 B5-01-00-00-00-00-00-00  ok
<< 收到 B5-01-01-38-46-02-02-00          ← 一条干净的应答!
>> 发送 B5-02-00-... 
<< 收到 B5-02-00-04-00-00-00-00          ← 然后收到 64 条地址递增的帧
<< 收到 B5-02-04-04-00-00-00-00              = 内存/寄存器转储!
<< 收到 B5-02-08-04-... (共 64 条)
>> 发送 B5-04-00-...
<< 收到 B5-04-01-00-00-00-00-00          ← 短应答
<< 收到 B5-05-00-04-00-3E-08-00          ← 自发状态帧(只出现一次)
>> 发送 B5-03/05/06/08/09/0A/0B/10/76 41/AA/5A  全部无应答
```

经验法则：
- **有问有答的命令是配置/查询通道**（`01`）；**一次喷 64 条的是内存转储**（`02`/`05`），
  转储本身一般不含直接可读的电量，但可以挖充电标志、设备状态等。
- 首字节 echo 命令字、第三字节起是数据。`B5 01 01 38 ...` 里 `0x38=56`，
  范围 0–100 且多轮稳定 → 电量字节候选。
- 其它命令无应答很正常，别纠结。

### 轮询确认候选字节

```powershell
dotnet run -- --vid 1EA7 --pid 0066 --watch --seconds 150
```

每 3 秒发一次 `B5 01` 和 `B5 04` 并打印应答。判据：
- 候选字节在 0–100 内、多轮稳定或缓慢下降 → 基本确认是电量；
- **拿设备自己的电量指示对照**：F1 机身有电量灯，56% 与三格灯大致吻合；
- 有条件就做充放电对照（充电时缓慢上升）。F1 的 56% 在充电全程保持稳定上升前不变，
  但低至 20% 阈值提醒功能依然可用。

---

## 5. 进阶：充电标志位与内存转储 diff 法

充电动画/"充电中"状态是最难的部分，很多设备根本没有（F1 经过三重实验确认不存在）：

1. **响应帧对比**：充电/不充电各抓一份查询响应，逐字节 diff。F1：完全一致。
2. **转储 diff**：`--probe --frames "B5 02,B5 05"` 各抓一份（128 行），diff。
   F1 结果：`02` 转储是常量（无差异），`05` 转储全是计时器类易变数据
   （同状态重复抓也会变），形不成稳定标志。
3. **事件嗅探**：插拔充电线瞬间跑被动嗅探（`--seconds 120`），看有没有通知帧。F1：零帧。

三个实验都阴性 → 硬件不向 PC 汇报充电状态，插件恒报 `ChargingState.Unknown` 即可，
这和上游 GANSS 插件的现状一致，不是缺陷。**在协议文档里如实写清楚验证过程**，
别留下"待确认"的含糊状态。

如果 diff 出了候选字节（比如某位稳定地 充电=1/断电=0，且重复实验可复现），
写进 codec 的 `TryParse`，对照 ZHEN 的 `chargeFlag`（`0x01` 放电 / `0x03` 充电）实现。

### 有官方软件的设备（更省事的路线）

如果目标设备有官方 Windows 驱动/配置软件，别盲猜——直接抓官方软件的通信：
- USB 层：装 USBPcap + Wireshark，选 Root Hub 抓 URB_INTERRUPT，过滤 `usb.src/dst`；
- HID 层：官方"网页驱动"（很多国产新品牌是 WebHID 网页版）用仓库自带的
  `VigilWatt.ProtocolCapture webhid`（需要 Edge/Chrome 加 `--remote-debugging-port=9222 --remote-allow-origins=*` 启动，
  先打开官方网页驱动页面，再跑 `dotnet run --project src/tests/VigilWatt.ProtocolCapture -- webhid --target <页面URL片段>`），
  官方软件发的每一帧都会被旁路记录下来。

---

## 6. 第四步：写插件

以 `InphicF1Plugin`（F1，主动查询型）和上游 `InphicPlugin`（ZHEN，被动监听型）为模板。
两个文件 + 一处注册：

### 6.1 Codec（纯逻辑，可单测）——`Plugins/<Brand>/<Brand>Codec.cs`

硬性约定（测试项目靠"链接源码文件"编译，见 `VigilWatt.Core.Tests.csproj` 头注释）：
**codec 必须是零依赖纯逻辑**——只用基元类型，不引用 `HidDeviceInfo` 等 Core 类型。
接口判定写成基元参数形式：

```csharp
public static bool IsVendorInterface(ushort vid, ushort pid, ushort usagePage, ushort usage,
    ushort inputLen, ushort outputLen, byte? reportId)
```

必备元素（对照 `InphicF1Codec.cs`）：
- `VendorId`/`ProductId`/集合特征常量；
- `CreateQuery()`：完整报告字节数组，buffer[0] = report ID（**Windows WriteFile 要求**，
  与 WebHID 的"reportId 自动加首字节"不同，照抄网页抓包时必须处理这个差异，见 protocols.md §4.3）;
- `TryParse(byte[] input, out int percent, ...)`：严格校验 report ID/命令字/长度，
  电量范围 0–100，**多滤不会漏设备，少滤可能把噪声当电量**；
- 充电标志位：确认了就解析（ZHEN 的 `chargeFlag` 模式），没确认就输出 `ChargingState.Unknown`。

### 6.2 Plugin ——`Plugins/<Brand>/<Brand>Plugin.cs`

继承 `BatteryPluginBase`，必读基类头注释（`BatteryPluginBase.cs`，
里面有离线策略/熔断/被动监听/写开关的完整语义）。最小实现四个成员：

| 成员 | 主动查询型（F1/GANSS） | 被动监听型（ZHEN/雷柏） |
|---|---|---|
| `IsCandidate` | 调 codec 的基元接口判定 | 同左 |
| `ProbeWrites` | 默认 true | 覆写 `false` |
| `Probe/ProbeAsync` | 发查询帧验证协议 + 拿首读数 | 监听窗口等一帧 |
| `PollDevice` | `CanWrite` 守门 → 查询 → `ReportPollFailure/Answered` | 直接 `null`（后台监听负责） |

其余按情况覆写：
- `SupportsPassiveListen => true` + `TryParsePassiveInput`：被动型必备；
- `Offline`：被动型用 `Confirm/Silence`（ZHEN：6s/90s），主动查询型用
  `new() { CountPollFailures = false }` 并自己报账；
- `BuildDisplayName`：产品字符串是 "2.4G Mouse" 这类通用名时，返回具体型号名。

### 6.3 注册默认开关 ——`Settings/AppSettings.cs`

`PluginEnabled` 字典加一行。**会写帧的插件一律默认 `false`**（安全规则 R1：
写入必须能被一个开关挡住），纯被动插件默认 `true`。
设置页的插件开关 UI 是反射自动生成的，不用改界面代码。

### 6.4 自动发现，不用注册

`DeviceManager.DiscoverPlugins()` 反射扫描程序集里所有 `IBatteryPlugin` 实现——
新插件类写完即生效，无注册表/无 DI 配置。

### 6.5 单测 ——`src/tests/VigilWatt.Core.Tests/<Brand>CodecTests.cs`

两条硬性要求：
1. 在 `VigilWatt.Core.Tests.csproj` 里加 `<Compile Include="..\..\VigilWatt.Core\Plugins\<Brand>\<Brand>Codec.cs" />`;
2. 用**真实抓包帧**做样本（像 `InphicF1CodecTests` 里 `B501013800000000` → 56），
   外加边界测试：长度错、report ID 错、命令字错、电量 >100、同 VID/PID 的其它集合必须拒绝。

---

## 7. 第五步：端到端真机验证

1. 打开 `%LocalAppData%\VigilWatt\settings.json`，`PluginEnabled` 里把新插件名设为 `true`
   （首次运行前可能只有上游默认的几个键，手动补键）。
2. 启动应用：
   ```powershell
   dotnet build VigilWatt.sln -v q
   start src\VigilWatt.App\bin\x64\Debug\net8.0-windows10.0.19041.0\win-x64\VigilWatt.App.exe
   ```
   （图标在任务栏托盘，可能收在 `^` 溢出区。）
3. 看日志 `%LocalAppData%\VigilWatt\logs\app-YYYY-MM-DD.log`，验收标准按顺序：
   - `插件发现完成: ... <新插件名> ...` —— 反射发现成功；
   - `认领<品牌>设备: ... proto=... 电量=<N>` —— 探测 + 首读数成功；
   - `托盘提示已更新: ... <N>%` —— UI 链路通；
   - 之后每分钟一条查询日志，数值合理 —— 轮询稳定。
4. 拔插接收器，确认设备离线/上线状态翻转正常。
5. 关掉调试实例再测一遍桌面启动脚本路径（如果有）。

**注意**：同一台机器同时只能跑一个实例——调试版和你自己装的 Release 版会抢同一个接收器。

---

## 8. 收尾：文档与提交

1. **README 设备支持表加一行**（这个 fork 里直接改），格式：
   `| <品牌> <型号> · <协议一句话> | 已实现，默认关闭/开启；会写帧/纯被动。<验证范围> |`
2. 如果是给上游提 PR，还要按上游惯例补 `docs/hardware/<brand>.md` 协议文档：
   帧布局表、抓包方法、验证过程（含阴性实验）、未确认项如实列出。参考 `docs/hardware/ganss.md`。
   CONTRIBUTORS.md 的上游流程也要走。
3. 提交（在 fork 上）：
   ```powershell
   git checkout -b feature/<brand>-<model>
   git add <相关文件>
   git commit -m "接入<品牌> <型号> 2.4G 电量（<VID:PID>）"
   git push -u fork feature/<brand>-<model>
   ```
   上游 commit 风格：一句话总结 + 要点列表，中文。
4. 给上游开 PR：fork 页面点 "Contribute → Open pull request"，
   标题 70 字内，正文写验证设备清单和阴性实验结论。

---

## 9. 常见坑速查

| 症状 | 原因 | 处理 |
|---|---|---|
| 嗅探/探测时一个集合都开不了 | 键盘/鼠标标准集合被系统独占 | 正常。只对 vendor-defined 集合能打开 |
| 探测发帧全"失败" | buffer[0] 不是合法 report ID，或集合根本没有输出报告 | 用 `outId=` 确认可写集合；帧数组第一元素必须是 report ID |
| 插件发现成功但永远不认领 | `IsCandidate` 条件写多了 / 固件批次差异 | 看日志里 `接口判据不符，未认领` 的诊断行，对照放宽 |
| 认领成功但电量乱跳/恒 0 | 把噪声字节当电量 | 回到 §3/§4 重新对比多轮数据；codec 校验加严 |
| 读数突变 20+ 点被"过滤" | `BatteryReadingFilter` 异常保护（正常行为） | 真实深度掉电持续 5 分钟会自动采信，日志有 Warn 说明 |
| 写帧插件开了开关也没反应 | `WritesEnabled` 读 `PluginEnabled[插件Name]`，Name 必须和字典键一字不差 | 核对 `Name => "..."` 与 settings.json 键 |
| 换台电脑 dongle 枚举变了 | DevicePath 含接口号 `&MI_01&Col03`，机器间不同 | 不影响：匹配只看 VID/PID/集合特征，path 只用于句柄管理 |

## 10. 关键源码位置速查

| 要找什么 | 位置 |
|---|---|
| 插件基类（语义注释最全） | `src/VigilWatt.Core/Plugins/BatteryPluginBase.cs` |
| 主动查询型参考 | `src/VigilWatt.Core/Plugins/Inphic/InphicF1Plugin.cs` |
| 被动监听型参考 | `src/VigilWatt.Core/Plugins/Inphic/InphicPlugin.cs` |
| 接口判定/解析纯逻辑 | `src/VigilWatt.Core/Plugins/Inphic/InphicF1Codec.cs` |
| 抓包工具 | `src/tests/VigilWatt.HidSniff/Program.cs` |
| 单测参考 | `src/tests/VigilWatt.Core.Tests/InphicF1CodecTests.cs` |
| 插件开关默认值 | `src/VigilWatt.Core/Settings/AppSettings.cs` `PluginEnabled` |
| 插件反射发现 | `src/VigilWatt.Core/Devices/DeviceManager.cs` `DiscoverPlugins` |
| HID 枚举 | `src/VigilWatt.Core/Hid/HidDeviceScanner.cs` |
| 上游协议方法论 | `docs/hardware/protocols.md` |
| 写帧安全规则 | `docs/hardware/safety.md` |
