# WeightCheckerCS：状态机称重判定（工控版，C#）

这是 WeightCheckerPlus（Python/PyQt6）的 C# 重写版，目标是在工控机上长期稳定运行。

- 判定算法：沿用 WeightCheckerPlus 的六状态机（STABLE / WEAK_DOWN_1 / WEAK_DOWN_2 / STRONG_DOWN / WEAK_UP / STRONG_UP）。中心值为去尾均值，带粗大异常排除、重锁和偏离保护；合格区间按状态倾斜，宽度限制在 7.4~9.0 g。各 SKU 的调优参数原样保留。
- 一致性：用 `sku_csv` 中 5 个 SKU 共 2,092,146 盒做了回放，C# 版与 Python 版逐盒完全一致（中心、上下限、状态、判定均相同）。
- 运行环境：Windows 自带 .NET Framework 4.8，x64，无需安装任何运行库。

## 目录

```
dist\WeightCheckerCS\
  WeightCheckerCS.exe        图形界面（双击运行）
  WeightCheckerCS-cli.exe    命令行版（无界面运行 / 回放 / 自测）
  config\settings.json       程序设置（首次启动自动生成）
  config\configs.json        各 SKU 状态机参数 + 剔除输出参数
  iolib\mezio_c.dll          DIO 板卡接口（x64）
  weight_checker_cs_data\    数据目录（csv\ 每日称重记录，logs\ 运行日志）
```

## 配置

### settings.json（程序级，所有 SKU 共用）

| 字段 | 说明 |
|---|---|
| `host` / `port` | 称重机地址 |
| `sku` | 当前 SKU，填 configs.json 中的块名（如 `"36-pack-240mm"`）。由界面下拉框自动保存 |
| `product_pad_num` | 兼容旧版：`sku` 为空时取 configs.json 中第一个 pack 数相同的 SKU |
| `dio_dll_path` | **mezio_c.dll 路径，全部 SKU 共用**。默认 `iolib\mezio_c.dll`，相对路径以 exe 所在目录为基准 |
| `sku_configs_path` | SKU 参数文件，默认 `config\configs.json` |
| `state_machine_enabled` / `reject_output_enabled` / `csv_enabled` | 功能开关 |
| `ui_refresh_ms` / `realtime_rows` / `abnormal_rows` | 界面刷新间隔与表格行数上限 |

### configs.json（每个 SKU 一节）

#### 块名规则

块名就是 SKU 标识，统一写成 `pack数-pack-尺寸`，例如 `9-pack-270mm`、`36-pack-240mm`。这样同一 pack 数、不同尺寸的产品可以并存，参数互不影响。现有块名：

| 块名 | 产品 |
|---|---|
| `9-pack-270mm` / `10-pack-270mm` / `16-pack-270mm` / `32-pack-270mm` | 270mm |
| `36-pack-240mm` | 240mm |

块名检查（只在运行日志里警告，不影响加载）：

- 块名和该块的 `product_pad_num` / `pad_size` 不一致时，提示建议的规范块名。
- 同一个块名在文件里出现两次时，提示“只有最后一个生效”。
- 两个块的 pack 数和尺寸完全相同时，提示可能重复配置。

兼容旧版：

- 旧的块名是 `9-pack`、`36-pack` 这种只有 pack 数的写法。settings.json 里如果还是旧块名，或者只有 `product_pad_num`，程序会自动对应到同 pack 数的新块名，并把新块名写回 settings.json，之后就固定使用新块名。
- 同 pack 数有多个尺寸时，取 configs.json 中排在最前面的块，并在日志里提示到下拉框确认。

#### 新增 SKU

产品 SKU 下拉框由 configs.json 动态生成，新增 SKU 不需要改代码或重新打包。下面以新增 9 片 240mm 为例：

1. 打开 exe 旁边的 `config\configs.json`，复制 `"9-pack-270mm"` 整块，粘贴到文件末尾。
2. 把块名改为 `"9-pack-240mm"`，块内的 `"sku"` 也改为 `"9-pack-240mm"`（或者删掉 `sku` 这一行），`"pad_size"` 改为 `"240mm"`。
3. 按 240mm 的实际重量修改 `initial_center_g`、`safety_lower_g`、`physical_min_g` / `physical_max_g` 和区间参数。如果改了 `half_width_g`，要保证 2 × `half_width_g` 在 `min_window_width_g` ~ `max_window_width_g` 之间。
4. 保存文件。程序运行时会自动热加载，下拉框里立即出现 `9-pack 240mm`，原来的 `9-pack 270mm` 不受影响，不需要重启。

```json
"9-pack-240mm": {
  "sku": "9-pack-240mm",
  "pad_size": "240mm",
  "product_pad_num": 9,
  "initial_center_g": 61.4,
  ...
}
```

下拉框显示规则和注意事项：

- 显示为 `pack数-pack 尺寸`，例如 `9-pack 240mm`、`9-pack 270mm`。
- 想自定义显示文字，可以加 `"display_name": "32片 日用 240mm"`。
- 两个块显示名相同时，会在后面加 `[块名]` 区分。
- `sku` 字段可以省略。如果填了，必须和块名一致。
- 列表按 pack 数、尺寸排序。
- 某个块格式错误（缺字段、字段名拼错、参数越界）时，只跳过这一块，在运行日志里写明原因，其余 SKU 正常加载。
- 当前正在使用的 SKU 块如果改坏了，会拒绝这次热加载，继续用旧参数。

#### 剔除输出

剔除（喷气）输出写在每个 SKU 的 `reject_output` 中：

```json
"reject_output": {
  "active_pins": [2],
  "active_ms": 200
}
```

- `active_pins`：判定不合格时同时激活的 DO 引脚号（0~31，可以填多个，如 `[2, 3]`）。
- `active_ms`：引脚保持激活的时长，单位为毫秒（1~60000）。

旧版格式 `"do_out": {"chs": [...], "t": 秒, "dll": "..."}` 仍可读取。程序启动或热加载时会自动把它改写为 `reject_output`，原文件备份为 `configs.json.bak`。旧格式中的 `dll` 字段不再使用，DLL 路径只在 settings.json 中统一指定。

## DLL 加载与喷气

- mezio_c.dll 在**程序启动时只加载一次**（LoadLibrary + Init_DIO），之后每次剔除只调用 `DO_WritePortChecked`。
- DLL 依赖的板卡驱动 `WDT_DIO64.dll` 和 VC++ 2015-2022 x64 运行库需要已安装，或放在 mezio_c.dll 同目录。缺失时界面会提示“错误码 126（依赖 DLL 缺失）”。
- 找不到 DLL 或初始化失败时，采集和判定照常进行，只是不喷气，界面和日志会显示原因。日志中这类失败最多每 10 秒记一条。
- 喷气在独立线程中执行，采用高精度计时。队列上限 100，满时丢弃并计数，不会阻塞采集。
- 在“设置（热加载）”页可以：
  - 修改激活时长和引脚，点“保存喷气参数”后立即生效，并写回当前 SKU 的 configs.json；
  - 点“测试喷气一次”做现场验证；
  - 修改 DLL 路径，点“保存并加载 DLL”即可切换，无需重启。

## 热加载（无需重启）

- **configs.json**：被外部修改时自动检测（防抖 400 ms），先整体校验再生效。
  - 同一 SKU 只调参数时，保留中心历史和当前状态。
  - JSON 整体格式错误时拒绝加载，继续使用旧配置，并记录日志。单个 SKU 块有错时只跳过该块（当前 SKU 除外，见上文）。
- **settings.json**：在设置页修改后点“保存并应用”立即生效。
  - 改地址或端口会自动重连。
  - 切换 SKU 或开关状态机会重建判定器。
  - 改 DLL 路径会重新加载 DLL。
- 两个参数表格都可以直接编辑，点“保存并应用”后立即生效，点“还原”放弃修改。

## 稳定性与界面效率

- **线程划分**：采集（Socket）、判定、喷气、CSV 写入、日志各自独立，互不阻塞。
- **单行容错**：任何一行数据出错都只记日志，不影响后续采集；状态机异常时会自动重建。
- **界面刷新**：界面只用一个定时器（默认 100 ms）批量取出数据，只刷新有变化的文字，不按盒逐条刷新界面。
  - 表格使用虚拟模式（VirtualMode）和环形缓冲，行数固定，内存不增长。
  - 区间图采用双缓冲 GDI+，画笔预先创建，超出范围的值会被截断，绘制过程有异常保护。
- **队列上限**：UI 队列 5000 条，CSV 队列 20000 条，DIO 队列 100 条，满时丢弃并计数，不会撑爆内存。
- **全局保护**：捕获全局异常（ThreadException / UnhandledException），且同一时间只允许运行一个实例。关闭窗口时最小化到托盘。
- **实测**：模拟称重机连续高速发送 30 秒，界面始终响应，内存稳定在约 64 MB。
- **数据存储**：只写 CSV（UTF-8 BOM，按天分文件），不使用 SQLite，因为 .NET Framework 自带环境没有 SQLite 库。

## 命令行

```
WeightCheckerCS-cli.exe --selftest                       自测
WeightCheckerCS-cli.exe --headless [--seconds N]         无界面采集
WeightCheckerCS-cli.exe --replay in.csv (--sku 块名 | --pad 36) --out out.csv [--configs p]   离线回放（仅状态机）
任意模式可加 --settings 路径 指定 settings.json
```

`--headless` 的测试用选项（供端到端回放工具使用，正常运行不需要）：

| 选项 | 说明 |
|---|---|
| `--dio-sim [instant\|real]` | 不加载 mezio_c.dll，剔除脉冲只计数；`real` 按激活时长计时 |
| `--use-machine-time` | 称重机时间字段为完整 ISO 时间时，用它作为记录时间（停机重置按数据时间计算） |
| `--expect N` | 收到 N 行后自动停止（配合 `--seconds` 作为超时，超时退出码 3） |
| `--quiet` | 不逐盒打印 |
| `--summary 路径` | 退出时写出统计 JSON（记录数、错误、过轻/超重、剔除、DIO 脉冲、CSV 丢弃） |

## 称重机模拟器（tools\weigh_sim.py）

在本机 50001 端口扮演称重机（程序是 TCP 客户端，模拟器是服务端），读取 `tools\test_data\` 下的 CSV，默认每秒发送一条：

```
python tools\weigh_sim.py                                   默认 test_data\36-sku.csv，127.0.0.1:50001，1 秒一条
python tools\weigh_sim.py tools\test_data\36-sku.csv --interval 1 --start 100 --count 50 --loop --host 0.0.0.0
```

报文格式与默认字段序号一致（序号=2，时间=3，原始判定=5，重量=6），例如：

```
WC,01,1,2026-09-24T17:55:25.427+08:00,0,E,212.8
```

把 settings.json 的 `host` 设为 `127.0.0.1`、`port` 设为 `50001` 后启动程序（界面或 `--headless`）即可接收。程序断开后模拟器会等待重连，并从断开处继续发送。

## 端到端回放测试（tools\replay_e2e.py）

用 `tools\test_data\` 下的 CSV（列：`received_at,weight[,qualification_field_value]`）模拟称重机，测试整个称重程序：

1. 本机起 TCP 服务扮演称重机，把每行转成协议行发送；
2. 用临时 settings.json 启动 `WeightCheckerCS-cli.exe --headless --dio-sim`，走完整链路：TCP 接收 → 解析 → 状态机 → 剔除队列/DIO 线程 → CSV 记录 → 日志；
3. 用 `--replay` 计算期望结果，逐盒核对 CSV 记录（判定、中心、上下限、状态、标记、剔除输出、时间、重量），并核对计数器、DIO 脉冲数与引脚、CSV 丢弃数和运行日志。

```
build.bat
python tools\replay_e2e.py                                   回放 test_data 下全部 CSV
python tools\replay_e2e.py tools\test_data\36-sku.csv --sku 36-pack-240mm
  --rate 1           发送速率（行/秒，默认 1；0=不限速压力测试）
  --limit N          只回放前 N 行
  --dio-sim real     DIO 按激活时长计时（默认 instant）
  --timeout 秒       超时（默认按 行数/速率 + 120 自动计算）
  --out 目录         输出目录（默认 tools\out）
```

- 默认 1 行/秒：完整的 `36-sku.csv`（39,161 行）约需 10.9 小时。快速验证可用 `--limit 60`，或用 `--rate 2000` 在约 20 秒内跑完全部数据。
- 未指定 `--sku` / `--pad` 时，从文件名开头的数字推断 pack 数（`36-sku.csv` → 36）。
- 每次运行在 `tools\out\<文件名>_<时间>\` 下生成 `report.md`、`summary.json`、程序写出的 `data\csv`、日志和期望结果；不会改动 dist 下的配置和数据。
- 报告还给出“称重机判定 vs 状态机判定”对照表。全部检查通过时退出码为 0。
- 实测 `36-sku.csv`（39,161 盒）全部通过。注意：`--rate 0` 不限速（约 1.8 万行/秒）时，CSV 队列（上限 20000）会按设计丢弃少量记录，报告会显示为失败。

## 构建

不需要安装 .NET SDK 或 Visual Studio，直接运行 `build.bat`，它会调用系统自带的 `csc.exe`（C# 5 / .NET Framework 4.8 / x64）。

- 产物输出到 `dist\WeightCheckerCS\`。
- dist 中已存在的 `config\configs.json` 不会被覆盖。

`tools\replay_parity.py` 用 Python 版算法回放同一份 CSV，可以和 `--replay` 的输出逐行比对。
