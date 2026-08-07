# RUN-STATE-KEEP-01｜运行页方案、项目与检测快照保留及复位成功清理执行方案

> 文档状态：代码已实现，真实设备验收待执行  
> 编制日期：2026-08-06  
> 适用项目：GM 和 E78 USB 焊接不良检查装置系统  
> 主要修改对象：ViewModels/TestPageViewModel.cs  
> 验证对象：Tests/MinimumLoopTests、真实 WPF 运行页、PLC、DMM、继电器及扫描枪  

---

## 1. 文档目的

本方案用于修复运行页在一轮检测结束后错误丢失方案、项目列表、项目测量结果、测试时间和顶部结果的问题，并统一下一轮启动与复位成功后的清理边界。

本次改造完成后，运行页需要同时满足以下目标：

1. 未输入机种名称时，方案框展示全部方案，可人工下拉选择，选择后显示对应项目列表。
2. 扫码或手工输入机种名称时，方案框只展示该机种的方案，并自动选择方案、显示项目列表。
3. 每轮检测结束后，无论正常、NG、停止、急停还是设备异常，只清空机种名称和序列号。
4. 在未收到复位、且下一轮尚未真正启动前，保留当前方案、项目列表和本轮检测快照。
5. 正常完成态进入下一轮时，只能在完整启动校验通过、万用表通信正常且 DT234 写入成功后，才清除上一轮检测快照。
6. 复位流程只有最终成功后，才清除产品身份和检测快照；任何中途失败不得提前清掉界面快照。
7. 复位成功后仍保留当前方案和项目定义，只把每个项目的运行结果恢复为未检测。
8. 不改变 PLC 地址、检测引擎步骤、CSV 格式、方案目录结构和设备驱动。

本方案只解决运行页状态保留和清理时机，不扩大为新的状态机、方案服务或运行会话框架。

---

## 2. 需求来源与最终确认口径

### 2.1 方案选择需求

运行页方案选择遵循以下规则：

- 机种名称为空：
  - 方案框加载全部方案。
  - 全量方案使用“系列 / 机种名称 / 方案名称”显示，避免不同机种下同名方案无法区分。
  - 用户选择方案后，显示该方案的项目列表。

- 扫码或手工输入机种名称：
  - 方案框只保留该机种的所有方案。
  - 自动选择该机种的方案。
  - 显示该方案的项目列表。
  - 一个系列下的一个机种正常只保留一个方案。
  - 若现场暂时存在多个方案，程序选择确定的一项并记录告警，用户后续删除多余方案。

- 方案目录：
  - 设置/方案设置/{系列}/{机种名称}/{方案名称}.json

- 方案数量：
  - 现场方案总量预计不大。
  - 运行页进入时加载一次完整方案目录并保存在内存中，不在每次机种输入时重新扫描磁盘。

### 2.2 产品身份需求

产品身份包括：

- 机种名称。
- 序列号。

产品身份规则：

- 用户扫码或手工输入机种名称、序列号后，继续沿用现有 DT312 通知机制。
- 启动前必须确认本轮机种名称和序列号均为当前输入。
- 每轮检测任务结束后，无论正常还是异常，都清空机种名称和序列号。
- 复位最终成功后，也清空机种名称和序列号。
- 清空产品身份不得连带清空当前方案、方案身份键和项目列表。

### 2.3 检测结束后的保留需求

以下场景在“检测已结束、尚未成功复位或尚未开始下一轮”的阶段，均需要保留当前检测快照：

1. 正常 OK。
2. 正常 NG。
3. 单项 NG 提前结束。
4. PLC 检测异常。
5. DMM 检测异常。
6. 继电器切换或等待异常。
7. 检测中按停止。
8. 检测中急停。
9. 设备断线。
10. 安装异常或安装拒绝导致本轮中止。
11. 其他检测任务返回的非预期异常。

需要保留的内容：

- 当前已选方案。
- 当前方案唯一身份。
- 方案项目列表。
- 已完成项目的测量显示值。
- 已完成项目的 OK/NG 判定。
- 尚未执行项目的未检测状态。
- 已冻结的测试时间。
- 顶部最终 OK/NG，或当前停止、急停、异常、等待复位状态。

检测结束时只清：

- 机种名称。
- 序列号。
- 本轮机种和序列号确认状态。
- 与本轮产品身份绑定的防抖、重复记录查询和 DT312 通知缓存。

### 2.4 下一轮启动后的清理需求

对于正常 OK、正常 NG、单项 NG 提前结束这三类完成态：

- 在下一轮产品身份尚未输入时，保留上一轮检测快照。
- 输入下一轮产品身份但启动校验未通过时，仍保留可保留的上一轮快照。
- 万用表 Ping 失败时，不得清上一轮快照。
- DT234 写入失败时，不得清上一轮快照。
- 重复记录确认未完成或用户取消时，不得清上一轮快照。
- 只有完整启动复核通过、DT234 写入成功，并正式提交进入新一轮检测时，才清除上一轮运行结果、最终判定和测试时间。

对于停止、急停、设备异常、安装异常等必须复位的状态：

- 不允许绕过复位直接开始下一轮。
- 因此这些状态的检测快照由“复位最终成功”负责清除。

### 2.5 复位成功后的清理需求

复位最终成功后清除：

- 机种名称。
- 序列号。
- 本轮产品身份确认状态。
- 测试时间，恢复为 0.0。
- 每个项目的测量结果。
- 每个项目的记录结果。
- 每个项目的 OK/NG 判定。
- 顶部最终 OK/NG。

复位最终成功后保留：

- 当前方案。
- 当前方案唯一身份。
- 方案下拉中的选中项。
- 项目列表及其项目定义。
- 检查方式、上下限等方案静态信息。
- 已写入内存的检测引擎方案配置。

复位成功后的项目行应继续存在，但运行结果显示为“未检测”。

### 2.6 复位失败后的保留需求

复位在任意步骤失败时：

- 不得提前清除项目测量结果。
- 不得提前清除顶部 FinalJudgment。
- 不得提前归零测试时间。
- 不得清除当前方案和项目列表。
- 产品身份是否已经由检测结束阶段清空，不做恢复。
- 顶部运行状态可以显示 ResetFailed，便于操作员判断复位失败。
- 上一轮 FinalJudgment、项目结果和时间作为底层快照继续保留，待下一次复位成功再清。

该规则用于避免“复位弹窗显示失败，但上一轮现场证据已经消失”的问题。

---

## 3. 最终状态矩阵

### 3.1 检测结束与复位前

| 场景 | 机种 | 序列号 | 当前方案 | 项目列表 | 项目结果 | 测试时间 | 顶部状态/结果 |
|---|---|---|---|---|---|---|---|
| 正常 OK | 清空 | 清空 | 保留 | 保留 | 保留 | 保留 | 保留 OK |
| 正常 NG | 清空 | 清空 | 保留 | 保留 | 保留 | 保留 | 保留 NG |
| 单项 NG 提前结束 | 清空 | 清空 | 保留 | 保留 | 保留 | 保留 | 保留 NG |
| PLC 检测异常 | 清空 | 清空 | 保留 | 保留 | 保留 | 保留 | 保留 Error/等待复位 |
| DMM 检测异常 | 清空 | 清空 | 保留 | 保留 | 保留 | 保留 | 保留 Error/等待复位 |
| 继电器异常 | 清空 | 清空 | 保留 | 保留 | 保留 | 保留 | 保留 Error/等待复位 |
| 检测中停止 | 清空 | 清空 | 保留 | 保留 | 保留 | 保留 | 保留 Paused/等待复位 |
| 检测中急停 | 清空 | 清空 | 保留 | 保留 | 保留 | 保留 | 保留 EmergencyStop |
| 设备断线 | 清空 | 清空 | 保留 | 保留 | 保留 | 保留 | 保留 Error/等待复位 |
| 安装异常 | 清空 | 清空 | 保留 | 保留 | 保留 | 保留 | 保留 Error/等待复位 |

### 3.2 正常完成态尝试启动下一轮

| 启动阶段 | 当前方案 | 项目列表 | 上一轮项目结果 | 上一轮时间 | 顶部结果 |
|---|---|---|---|---|---|
| 未输入完整产品身份 | 保留 | 保留 | 保留 | 保留 | 保留 |
| 产品身份未确认 | 保留 | 保留 | 保留 | 保留 | 保留 |
| 重复记录待确认或取消 | 保留 | 保留 | 保留 | 保留 | 保留 |
| 万用表 Ping 失败 | 保留 | 保留 | 保留 | 保留 | 保留 |
| DT234 写入失败 | 保留 | 保留 | 保留 | 保留 | 保留 |
| DT234 成功，提交下一轮启动 | 保留 | 保留 | 清除 | 清零 | 清除 |

### 3.3 复位场景

| 复位阶段 | 产品身份 | 当前方案 | 项目列表 | 项目结果 | 测试时间 | FinalJudgment |
|---|---|---|---|---|---|---|
| 收到 DT121 或按复位按钮 | 保持当时状态 | 保留 | 保留 | 保留 | 保留 | 保留 |
| Resetting 过程中 | 保持当时状态 | 保留 | 保留 | 保留 | 保留 | 保留 |
| 任一步骤失败 | 不恢复已清身份 | 保留 | 保留 | 保留 | 保留 | 保留 |
| 最终 DT121 清除成功 | 清空 | 保留 | 保留 | 清为未检测 | 清零 | 清空 |
| 完成态刷新 | 清空 | 保留 | 保留 | 未检测 | 0.0 | 空 |

说明：

- Resetting 和 ResetFailed 是顶部运行状态，不等同于把上一轮 FinalJudgment 数据提前置空。
- 复位成功后顶部进入 Ready 或 CanStart，不再显示上一轮 OK/NG。

---

## 4. 当前代码基线与问题链

### 4.1 当前已存在的方案缓存

TestPageViewModel 当前已经具备：

- _allRunPlans：运行页完整方案缓存。
- _runPlansLoaded：标记完整方案是否已经加载。
- _runPlanOptionLookup：全量显示文本到 PlanModel 的映射。
- _selectedPlanIdentityKey：当前方案的完整身份键。
- _planLoadVersion：方案异步加载版本号。
- RefreshPlanNameOptionsAsync：刷新当前方案下拉选项。
- GetPlansForMachineType：按机种从内存缓存过滤方案。
- BuildPlanOptionDisplayName：生成全量或机种模式下的显示文本。
- ResolvePlanForSchemeName：把方案框文本解析为唯一 PlanModel。
- ApplySelectedPlanDisplayWithoutReload：只调整显示、不重新加载项目。

本次不重做方案缓存服务，继续使用现有结构。

### 4.2 当前 NG 后方案和项目丢失的直接调用链

当前实际链路为：

1. RunInspectionAsync 进入 finally。
2. finally 调用 ClearCurrentProductIdentity。
3. ClearCurrentProductIdentity 把 ModelName 设置为空。
4. OnModelNameChanged 被触发。
5. HandleModelNameChangedAsync 调用 RefreshPlanNameOptionsAsync。
6. RefreshPlanNameOptionsAsync 执行 PlanNameOptions.Clear。
7. WPF ComboBox 的 ItemsSource、SelectedItem、Text 均为双向联动。
8. 下拉集合清空时，ComboBox 临时失去选中项。
9. ComboBox 把空文本回写给 SchemeName。
10. OnSchemeNameChanged 收到空字符串。
11. OnSchemeNameChanged 清除 SelectedPlanName。
12. OnSchemeNameChanged 清除 _selectedPlanIdentityKey。
13. OnSchemeNameChanged 执行 TestItems.Clear。
14. 后续机种空值分支已经找不到原方案身份，无法恢复方案和项目。

因此，根因不是 ClearCurrentProductIdentity 直接清了方案，而是方案下拉集合重建期间发生了 WPF 双向绑定回写。

### 4.3 当前复位提前清结果的问题

ExecuteResetFlowAsync 当前在以下步骤之间调用 ClearTestItemsForRestart：

- 已停止检测引擎。
- 已清瞬时运行输出。
- 已尝试清 PLC 最终结果。
- 但尚未完成 DT122 最终确认。
- 尚未完成第一次 DT121 清除。
- 尚未完成复位完成快照验证。
- 尚未完成成功提示后的最终 DT121 清除。

如果后续任一步骤失败，界面项目结果已经被清除，无法恢复。

当前 ResetElapsedTime 位于流程末尾，但项目结果和测试时间不是同一个提交点，导致复位清理缺乏原子性。

### 4.4 当前下一轮启动清理的正确基础

PrepareCompletedRunForNextStartAsync 已经具备较正确的时序：

1. 启动校验通过。
2. 万用表 Ping 成功。
3. DT234 写入成功。
4. 清除上一轮 PLC 最终结果。
5. 清项目运行结果。
6. 清 FinalJudgment。
7. 归零测试时间。

本次保留这一基本时序，只补充方案和项目列表不被误删的保护，并检查所有启动拒绝分支是否仍保留上一轮快照。

### 4.5 当前产品身份清理的正确基础

ClearCurrentProductIdentity 已经集中处理：

- 取消产品身份通知。
- 清产品身份通知缓存。
- 抑制重复记录查询。
- 使当前机种和序列号确认失效。
- 清 SerialNumber。
- 清 ModelName。

该方法设计方向正确，本次不把方案清理放入此方法，只修复它触发的方案下拉重建副作用。

---

## 5. 修改范围

### 5.1 必须修改

1. ViewModels/TestPageViewModel.cs

修改内容：

- 方案下拉选项重建保护。
- 方案身份和项目列表保留。
- 产品身份清理后的显示恢复。
- 相同方案与不同方案输入的处理边界。
- 检测任务结束清理路径统一。
- 复位成功清理提交点调整。
- 复位失败快照保留。
- 下一轮启动清理边界复核。
- 审计日志补充。

2. Tests/MinimumLoopTests/Program.cs

修改内容：

- 增加本需求的源码契约和最小闭环检查。
- 修正现有测试中与新清理边界冲突的断言。
- 不改与本需求无关的历史测试。

### 5.2 视实际代码需要修改

1. Views/TestPageView.xaml

原则：

- 优先在 ViewModel 解决 WPF 选项重建回写问题。
- 若现有 Text 与 SelectedItem 双绑定仍存在不可控竞态，才做最小绑定调整。
- 不重做 ComboBox 模板，不改变页面布局和视觉样式。

2. Services/Inspection/InspectionStartValidator.cs

原则：

- 当前机种名称和序列号启动确认逻辑若已满足需求，不再改动。
- 只有新测试暴露清理后确认状态未正确失效时，才做最小修正。

### 5.3 明确不修改

本次不修改：

- InspectionEngine 的检测步骤和判定算法。
- IPlcDevice 接口。
- PLC 地址映射。
- DT120、DT121、DT122、DT123、DT234、DT304、DT305、DT312 的协议含义。
- PLC、DMM、扫描枪驱动。
- 继电器控制实现。
- 方案 JSON 结构。
- 方案目录结构。
- CSV 字段与写入格式。
- 操作员与参照信息功能。
- 导航框架。
- 设备设置界面。

---

## 6. 总体设计原则

### 6.1 分离三类状态

运行页需要明确分开以下三类状态：

1. 产品身份
   - ModelName。
   - SerialNumber。
   - 当前产品身份确认状态。
   - 重复记录查询。
   - DT312 通知缓存。

2. 方案上下文
   - SchemeName。
   - SelectedPlanName。
   - _selectedPlanIdentityKey。
   - TestItems 中的项目定义。
   - InspectionEngine 当前配置。

3. 检测快照
   - TestItems 各行的 CheckResult。
   - TestItems 各行的 RecordResult。
   - TestItems 各行的 Judgment。
   - FinalJudgment。
   - ElapsedSecondsText 和计时器状态。
   - UiState。

产品身份清空，不等于方案上下文清空，也不等于检测快照清空。

### 6.2 不增加新的大型架构

本次不新增：

- 运行会话仓储。
- 新状态机。
- 方案缓存服务接口。
- UI 快照持久化。
- 数据库表。
- 额外后台线程。

优先复用现有字段、方法和加载版本机制。

### 6.3 清理必须有明确提交点

允许清理检测快照的提交点只有两个：

1. 正常完成态下一轮有效启动提交。
2. 完整复位最终成功提交。

除这两个提交点外，任何 finally、异常处理、停止、急停、断线和安装异常路径都不得清除检测快照。

### 6.4 项目列表清空与项目结果清空必须分开

两种操作含义不同：

- TestItems.Clear：
  - 删除整个项目列表。
  - 只允许在用户选择了不同方案、当前方案无效、或页面正常卸载时发生。

- ClearTestItemsForRestart：
  - 保留项目行。
  - 仅重置 CheckResult、RecordResult 和 Judgment。
  - 用于下一轮启动提交或复位成功提交。

本次所有调用点都要按这一边界重新审计。

---

## 7. 阶段 A：固定方案下拉重建与选中方案保留

### A1. 目标

解决机种名称被系统清空时，PlanNameOptions.Clear 触发 ComboBox 回写空 SchemeName，进而清空方案身份和项目列表的问题。

### A2. 修改 RefreshPlanNameOptionsAsync

在重建 PlanNameOptions 前保存：

- 当前 _selectedPlanIdentityKey。
- 当前 SchemeName。
- 当前 SelectedPlanName。
- 当前方案 PlanModel。
- 当前 TestItems.Count。

在集合重建期间开启内部抑制标志。

优先复用现有 _suppressSchemeNameReload；若它无法准确表达“方案选项正在系统重建”，则增加一个含义明确的私有布尔字段，例如：

- _isRebuildingPlanOptions。

字段仅用于屏蔽系统重建期间的 ComboBox 临时回写，不屏蔽用户真实输入。

集合重建顺序：

1. 确认 expectedLoadVersion 仍为当前版本。
2. 保存当前方案完整身份。
3. 开启系统重建抑制。
4. 清 PlanNameOptions。
5. 清 _runPlanOptionLookup。
6. 按当前机种从 _allRunPlans 过滤方案。
7. 重新生成显示文本和映射。
8. 如果原方案身份仍属于新的候选集合，则恢复选中显示。
9. 如果机种为空，恢复为“系列 / 机种 / 方案”显示文本。
10. 如果机种非空，恢复为方案名称显示文本。
11. 恢复 _selectedPlanIdentityKey。
12. 关闭系统重建抑制。
13. 完成加载状态。

整个过程不得调用 TestItems.Clear。

### A3. 修改 OnSchemeNameChanged

OnSchemeNameChanged 需要区分两类空值：

1. 用户真实清空方案框：
   - 清 SelectedPlanName。
   - 清 _selectedPlanIdentityKey。
   - 清 TestItems。
   - 更新启动状态。

2. 方案选项系统重建期间 ComboBox 临时回写空值：
   - 立即返回。
   - 不清 SelectedPlanName。
   - 不清 _selectedPlanIdentityKey。
   - 不清 TestItems。
   - 不记录“方案名称已清空”的用户日志。

不得通过固定延时判断，也不得依赖 Dispatcher 延迟碰运气。

### A4. 修改 HandleModelNameChangedAsync 的空机种分支

机种为空时：

- 方案候选切换为全部方案。
- 如果 _selectedPlanIdentityKey 能在缓存中找到：
  - 恢复完整身份显示文本。
  - 保留 TestItems 当前对象和所有运行结果。
  - 不重新调用 LoadPlanItemsAsync。
  - 不重新配置检测引擎。
- 如果没有选中方案：
  - 只展示全量候选。
  - 项目列表按当前真实状态处理，不从 ComboBox 临时状态推断。

### A5. 同机种、同方案输入规则

检测结束后机种为空，但方案和项目仍保留。

当下一块产品输入的机种仍对应当前 _selectedPlanIdentityKey 时：

- 方案框从完整身份显示切回普通方案名。
- 不清 TestItems。
- 不重新创建项目行。
- 保留上一轮项目结果直到下一轮有效启动提交。
- 更新产品身份校验和启动条件。

这样可以满足“输入下一块产品身份时仍看到上一轮结果，真正启动后才清结果”。

### A6. 不同机种或不同方案输入规则

若新机种自动匹配到的方案与当前 _selectedPlanIdentityKey 不同：

- 按原需求立即切换到新方案。
- 重新加载新方案项目列表。
- 新项目行显示未检测。
- 顶部上一轮 FinalJudgment 和测试时间暂不因机种输入而清除。
- 真正启动新一轮后，再按启动提交点统一清顶部结果和测试时间。

原因：

- “机种输入后方案和项目列表立即跟随”与“旧方案项目行一直保留到启动”在跨机种时无法同时展示。
- 本方案以已确认的机种方案联动需求为准。
- 同方案场景严格保留旧项目结果到启动。
- 不同方案场景允许项目列表因用户输入产生真实切换，但不得由检测结束清理或 ComboBox 临时回写造成丢失。

### A7. 异步版本保护

继续使用 _planLoadVersion，确保：

- 快速输入 A 机种后马上改成 B 机种，A 的旧任务不得覆盖 B。
- 系统清空机种后，之前正在执行的非空机种加载不得晚到清列表。
- 扫码与手工输入交错时，只接受最后一次机种值。
- 页面离开后，晚到任务不得修改已失效页面状态。

不得增加固定 Task.Delay 作为同步手段。

### A8. 阶段自检

静态确认：

- PlanNameOptions.Clear 期间有明确抑制保护。
- 系统重建回写空 SchemeName 不进入破坏性分支。
- 清空 ModelName 不直接或间接执行 TestItems.Clear。
- 空机种恢复方案时不调用 LoadPlanItemsAsync。
- _selectedPlanIdentityKey 在系统切换为全量选项时不丢失。

运行确认：

- 空机种下拉展示全部方案。
- 选择方案后项目正常显示。
- 清空机种后方案和项目保持。
- 方案显示切换为完整身份。
- 项目结果不闪空、不重建、不归零。

---

## 8. 阶段 B：统一检测任务结束后的产品身份清理

### B1. 目标

保证所有检测任务结束路径都只清产品身份，不清方案上下文和检测快照。

### B2. 保留 RunInspectionAsync.finally 作为总兜底

RunInspectionAsync.finally 继续承担所有检测任务结束的统一产品身份清理。

覆盖路径：

- 正常 OK。
- 正常 NG。
- 单项 NG 提前结束。
- InspectionResult.IsAborted。
- PLC 写入异常。
- DMM 测量异常。
- 继电器等待超时。
- 停止导致的任务返回。
- 急停导致的任务返回。
- 设备断线导致的任务返回。
- 安装异常导致的任务返回。
- 未分类异常。

finally 内只允许调用产品身份清理，不允许调用：

- TestItems.Clear。
- ClearTestItemsForRestart。
- FinalJudgment = null。
- ResetElapsedTime。
- SchemeName = string.Empty。
- SelectedPlanName = null。
- _selectedPlanIdentityKey = null。

### B3. 处理正常完成路径的重复清理

当前正常完成流程中存在 ClearSerialForNextBoardAfterCompletion，RunInspectionAsync.finally 又会执行一次 ClearCurrentProductIdentity。

执行时采用以下最小方案：

- 以 RunInspectionAsync.finally 为最终兜底。
- 审计 ClearSerialForNextBoardAfterCompletion 是否还有独立时序价值。
- 若无独立价值，删除正常完成路径的重复调用和只被该路径使用的方法。
- 若保留，则必须保持幂等，第二次不得触发方案刷新、重复日志或重复通知。

优先目标是每轮只产生一条明确的产品身份清理审计日志。

### B4. 产品身份清理方法不承担 UI 快照清理

ClearCurrentProductIdentity 只处理：

- CancelProductIdentityNotification。
- _lastNotifiedProductIdentityKey。
- _suppressDuplicateCheck。
- InvalidateCurrentSerialVerification。
- SerialNumber。
- ModelName。

该方法不接收“是否清方案”“是否清结果”之类的布尔参数，避免一个方法承担多种清理语义。

### B5. 检测结束日志

增加或调整审计日志字段：

- Reason。
- UiState。
- FinalJudgment。
- PlanIdentityKey。
- PlanDisplayName。
- TestItemCount。
- ElapsedSecondsText。

检测任务结束后记录：

- 产品身份已清。
- 方案仍存在。
- 项目行数仍存在。
- 检测快照仍存在。

如果清理后方案身份或项目数意外丢失，记录 Warning，便于现场定位。

### B6. 阶段自检

逐个检查 RunInspectionAsync 中所有 return 分支：

- 正常完成前已经设置 FinalJudgment。
- 单项 NG 已完整收口为最终 NG。
- 控制类停止不覆盖 Paused、EmergencyStop 或 AwaitingReset。
- 非控制异常进入 Error。
- finally 始终清机种和序列号。
- finally 不清方案、项目、结果和时间。

---

## 9. 阶段 C：固定下一轮有效启动的清理提交点

### C1. 目标

上一轮正常完成快照只有在下一轮真正获得启动许可后才清除，任何启动拒绝均保留。

### C2. 启动前校验顺序

继续沿用以下顺序：

1. 判断当前 UI 状态是否允许启动。
2. 判断控制动作是否空闲。
3. 校验 PLC、DMM、扫描枪等必要连接状态。
4. 校验当前方案有效。
5. 校验项目列表非空。
6. 校验机种名称非空。
7. 校验序列号非空。
8. 校验本轮机种名称和序列号已确认。
9. 校验重复记录判断已完成。
10. 万用表 Ping。
11. 写 DT234=1。
12. 对上一轮完成态执行快照清理。
13. 锁定本轮机种、序列号和操作员。
14. 启动 InspectionEngine。

### C3. 启动失败不清快照

以下失败点都不得调用 ClearTestItemsForRestart、ResetElapsedTime 或清 FinalJudgment：

- 启动状态不允许。
- 方案无效。
- 项目为空。
- 机种名称为空。
- 序列号为空。
- 本轮产品身份未确认。
- 重复记录判断仍在执行。
- 重复记录确认被取消。
- PLC 未连接。
- DMM 未连接。
- DMM Ping 失败。
- DT234 写入失败。
- Starting 被停止、复位或急停取消。

### C4. 完成态清理提交

PrepareCompletedRunForNextStartAsync 保留为正常完成态的快照清理入口。

清理内容：

- 清 PLC 上一轮最终结果。
- ClearTestItemsForRestart。
- FinalJudgment = null。
- _inspectionCompletionInProgress = false。
- _inspectionStarted = false。
- _ignoreInspectionCallbacksUntilNextStart = false。
- 递增 _inspectionRunVersion。
- ResetElapsedTime。

明确不清：

- SchemeName。
- SelectedPlanName。
- _selectedPlanIdentityKey。
- TestItems 集合本身。
- 检测引擎当前方案配置。

### C5. 启动清理原子性

UI 清理动作统一放在 Dispatcher 中执行。

顺序：

1. 先确认 PLC 上一轮最终结果清理成功。
2. 再在 UI 线程一次性清项目运行结果、FinalJudgment 和时间。
3. 清理完成后才允许新一轮回调写入项目行。

如果 PLC 最终结果清理失败：

- 返回 false。
- 清 DT234。
- 尝试清 DT120。
- 保留上一轮界面快照。
- 提示操作员复位或检查 PLC。

### C6. 初始态启动

对于不是完成态的第一次启动：

- DT234 成功后归零时间。
- 项目本来应处于未检测状态。
- 不额外重建当前方案项目列表。

对于 Paused、EmergencyStop、AwaitingReset、Error、ResetFailed：

- 启动校验必须拒绝。
- 不走初始态捷径。
- 必须先复位成功。

### C7. 阶段自检

- PrepareCompletedRunForNextStartAsync 只从 DT234 成功后的完成态分支调用。
- 启动拒绝分支不清 UI 快照。
- 清结果方法不清 TestItems 集合。
- 新一轮 StepStarted 回调前，旧项目结果已经归零。
- 新一轮仍使用当前保留方案。

---

## 10. 阶段 D：把复位 UI 清理移动到最终成功提交点

### D1. 目标

复位中间步骤只做设备和协议收口，界面快照只在最终 DT121 清除成功后一次性提交清理。

### D2. 删除复位中段的提前清理

从 ExecuteResetFlowAsync 中移除当前中段的：

- ClearTestItemsForRestart。
- “已清空界面检测项目结果”日志。

该位置后面仍可能发生：

- DT122 清理失败。
- 第一次 DT121 清理失败。
- 复位完成快照验证失败。
- 成功提示后的最终 DT121 清理失败。
- 未预期异常。

因此不能在此处破坏上一轮界面快照。

### D3. 新增轻量成功提交方法

在 TestPageViewModel 内增加一个职责单一的私有方法，建议命名：

- ClearRunDisplayAfterSuccessfulReset。

该方法只在 UI 线程调用，内部依次执行：

1. ClearCurrentProductIdentity，原因为“复位完成”。
2. ClearTestItemsForRestart。
3. FinalJudgment = null。
4. ResetElapsedTime。

该方法不执行：

- TestItems.Clear。
- SchemeName = string.Empty。
- SelectedPlanName = null。
- _selectedPlanIdentityKey = null。
- 重新加载方案。
- 重新扫描方案目录。

中文注释明确说明：该方法只清运行结果，保留项目定义和当前方案。

### D4. 成功提交位置

成功提交必须位于：

1. 检测引擎已停止。
2. 瞬时运行输出已清。
3. PLC 最终结果已清。
4. DT122 已完成最终确认。
5. 第一次 DT121 清除成功。
6. ValidateResetCompletionAsync 成功。
7. 成功提示关闭。
8. 弹窗后的最终 DT121 清除成功。
9. 然后在 Dispatcher 中调用 ClearRunDisplayAfterSuccessfulReset。

也就是说，任何能进入 EnterResetFailedAsync 的 return 都发生在 UI 快照清理之前。

### D5. 复位成功后的状态收口

成功清理后继续：

- 更新 _lastPlcInputs，缓存中的 IsResetRequested 置为 false。
- _resetSignalHandled = false。
- _isResetting = false。
- ForceRefreshReadyOrCanStartState。
- 恢复 PLC 轮询。
- 退出 Resetting 控制锁。

最终界面应为：

- 机种名称为空。
- 序列号为空。
- 当前方案仍选中。
- 项目列表仍存在。
- 所有项目结果为未检测。
- 测试时间为 0.0。
- 顶部不显示上一轮 OK/NG。
- 根据现有启动条件进入 Ready 或 CanStart。

由于机种和序列号为空，正常应为 Ready 或等待输入产品身份，不得误判为可立即启动。

### D6. 复位失败状态

EnterResetFailedAsync 不清理：

- TestItems 的项目行。
- 项目测量结果。
- FinalJudgment。
- ElapsedSecondsText。
- 当前方案身份。

复位失败后：

- UiState 显示 ResetFailed。
- 启动门禁拒绝。
- 操作员处理设备或 PLC 后重新执行复位。
- 第二次复位成功时才清快照。

### D7. 复位提示文本

复位成功提示调整为与实际行为一致：

- 当前方案和项目列表已保留。
- 本轮产品身份、项目运行结果、测试时间和顶部结果已清除。

避免使用“已清空所有检测结果”但实际清理尚未最终提交的提前描述。

若不调整弹窗出现位置，则弹窗正文不得声称最终收口已经完成；最终完成日志必须在弹窗后 DT121 清除成功和 UI 提交完成后记录。

### D8. 阶段自检

- ExecuteResetFlowAsync 在最终 DT121 清除前没有 UI 快照清理。
- 任意 EnterResetFailedAsync 分支都能看到上一轮项目结果和时间。
- 成功提交只执行一次。
- 成功提交不触发方案选择丢失。
- 方案选项重建期间不会把 SchemeName 回写为空。

---

## 11. 阶段 E：审计清理调用点

### E1. TestItems.Clear 调用点

逐一审计 TestPageViewModel 内所有 TestItems.Clear。

每个调用点必须归入以下允许场景之一：

- 用户真实清空当前方案。
- 用户选择不同方案。
- 输入机种后匹配到不同方案。
- 当前方案不存在或无效。
- 页面初始化加载一个新方案。
- 页面生命周期确定需要释放数据。

以下场景禁止 TestItems.Clear：

- 检测正常结束。
- 检测异常结束。
- 产品身份清空。
- 停止。
- 急停。
- 设备断线。
- 安装异常。
- 复位开始。
- 复位中间步骤。
- 启动校验拒绝。

### E2. ClearTestItemsForRestart 调用点

最终允许保留的业务调用点：

1. PrepareCompletedRunForNextStartAsync。
2. ClearRunDisplayAfterSuccessfulReset。
3. 明确离开运行页并终止会话的原有导航逻辑，如确有需要。

停止、急停、异常收口不得调用。

### E3. ResetElapsedTime 调用点

最终允许的关键调用点：

- 新一轮启动提交。
- 复位成功提交。
- 初次页面初始化。
- 明确终止并离开运行页。

禁止在：

- 检测任务 finally。
- 停止收口。
- 急停收口。
- 设备断线收口。
- 复位中间步骤。
- 复位失败。

### E4. FinalJudgment = null 调用点

最终允许的业务调用点：

- 下一轮有效启动提交。
- 复位成功提交。
- 页面新建或明确终止离开。

禁止在：

- 机种清空。
- 序列号清空。
- 方案选项集合重建。
- 启动拒绝。
- 停止、急停、异常处理。
- 复位失败。

### E5. SchemeName 和方案身份清理调用点

以下场景可以清方案：

- 用户明确清空方案框。
- 当前机种没有任何方案。
- 用户输入的方案无效且无法解析。
- 用户选择不同方案时更新为新方案。

以下场景不得清方案：

- 系统清空机种名称。
- 系统清空序列号。
- 检测任务结束。
- 停止、急停、设备异常。
- 复位开始、失败或成功。
- 下一轮启动清上一轮检测快照。

---

## 12. 阶段 F：自动化验证

### F1. 源码契约测试

在 MinimumLoopTests 增加独立测试组，建议标识：

- RUN-STATE-KEEP-01。

最小契约包括：

1. RunInspectionAsync.finally 调用 ClearCurrentProductIdentity。
2. ClearCurrentProductIdentity 不包含 TestItems.Clear。
3. ClearCurrentProductIdentity 不包含 ClearTestItemsForRestart。
4. ClearCurrentProductIdentity 不清 FinalJudgment。
5. ClearCurrentProductIdentity 不调用 ResetElapsedTime。
6. ClearCurrentProductIdentity 不清 SchemeName。
7. ClearCurrentProductIdentity 不清 _selectedPlanIdentityKey。
8. 方案选项重建期间存在明确抑制保护。
9. OnSchemeNameChanged 在系统重建期间不进入空方案破坏分支。
10. 空机种恢复当前方案时使用完整身份。
11. 空机种恢复当前方案时不重新加载 TestItems。
12. 复位中段不再调用 ClearTestItemsForRestart。
13. 复位成功提交方法包含 ClearTestItemsForRestart。
14. 复位成功提交方法清 FinalJudgment。
15. 复位成功提交方法调用 ResetElapsedTime。
16. 复位成功提交方法不调用 TestItems.Clear。
17. 复位成功提交位置在最终 DT121 清除成功之后。
18. PrepareCompletedRunForNextStartAsync 在 DT234 成功后调用。
19. 启动拒绝路径不清上一轮快照。
20. ClearTestItemsForRestart 保留项目行，只重置运行字段。

源码契约只用于防止回归，不替代真实 WPF 和 PLC 验收。

### F2. 纯逻辑最小测试

若现有 MinimumLoopTests 能实例化或反射相关方法，补充：

- 方案完整身份键在全量模式与机种模式间保持一致。
- 同名方案按系列、机种、方案三字段区分。
- 空机种返回全部方案。
- 非空机种只返回目标机种方案。
- 同机种多个方案选择顺序稳定。
- 旧的异步 loadVersion 不得覆盖新的机种输入。

不为测试方便引入新的生产接口或大型抽象。

### F3. 静态检查

执行：

1. git diff --check。
2. 检查 XAML 绑定名称仍与 ViewModel 属性一致。
3. 检查 CommunityToolkit 源生成属性名称。
4. 检查所有新增中文日志模板参数数量一致。
5. 检查所有 Dispatcher 更新都在 UI 线程。
6. 检查 CancellationToken 和 _planLoadVersion 旧任务退出。

### F4. 构建

执行：

1. dotnet build。
2. 如项目已有 Release 构建要求，再执行 Release 构建。
3. 构建失败时先区分：
   - 本次修改引入。
   - 当前工作区已有失败。
   - SDK 或本机环境失败。

不得把构建成功描述为真实设备验收通过。

### F5. 最小测试执行

执行现有 MinimumLoopTests。

报告时分开列出：

- RUN-STATE-KEEP-01 新增测试结果。
- 与方案联动相关测试结果。
- 与产品身份启动门禁相关测试结果。
- 完整测试结果。
- 已知继承失败。

若仍出现历史上的 UI 样式或正无穷显示基线失败，必须单独标注，不得掩盖，也不得扩展本次范围顺手修复。

---

## 13. 真实界面与设备验收

自动化检查无法模拟 WPF ComboBox 双向绑定的全部行为，也无法替代 PLC、DMM、继电器、急停和断线验收。

以下 U 项必须在真实程序中逐项执行。

### U1：空机种选择全部方案

前置：

- 打开运行页。
- 机种名称为空。
- 序列号为空。

操作：

1. 展开方案框。
2. 检查全部系列、机种下的方案。
3. 选择任意一个方案。

预期：

- 方案显示为“系列 / 机种 / 方案”。
- 项目列表显示对应方案项目。
- 不需要先输入机种名称。
- 选择过程无明显卡顿。
- 不重复扫描磁盘。

### U2：扫码或手工输入机种自动选择方案

操作：

1. 清空或重新进入运行页。
2. 扫描包含机种名称和序列号的有效条码，或手工输入机种与序列号。

预期：

- 方案候选只保留该机种方案。
- 自动选中方案。
- 项目列表显示正确。
- DT312 通知仍按现有规则执行。
- 启动前要求本轮机种名称和序列号确认。

### U3：正常 OK 后不复位

前置：

- 保证实体 PLC DT121 不被自动置 1。

操作：

1. 完成一轮 OK 检测。
2. 不按复位。
3. 等待至少一个 PLC 轮询周期和 OK 自动清 PLC 输出周期。

预期：

- 机种名称清空。
- 序列号清空。
- 方案保留。
- 项目列表保留。
- 项目测量结果保留。
- 测试时间保留。
- 顶部 UI 保留 OK。
- PLC 的 OK 输出按原有延时规则清除不影响 UI 快照。

### U4：正常 NG 后不复位

前置：

- 保证 DT121 保持 0。

操作：

1. 完成一轮最终 NG。
2. 不按复位。

预期：

- 机种和序列号清空。
- 方案和项目列表保留。
- 各项目测量结果保留。
- 测试时间保留。
- 顶部 NG 保留。
- PLC NG 输出仍按原有规则保持至复位。

### U5：单项 NG 提前结束

前置：

- 关闭“单项 NG 后继续测试”。

操作：

1. 让中间某一项目产生 NG。
2. 等待本轮提前结束。
3. 不复位。

预期：

- 已检测项目保留测量值和判定。
- NG 项保留 NG。
- 后续未执行项目保留未检测。
- 顶部显示最终 NG。
- 时间冻结。
- 机种和序列号清空。
- 方案和项目列表保留。

### U6：PLC、DMM、继电器检测异常

分别制造可恢复的：

- PLC 写入或读取异常。
- DMM 测量异常。
- 继电器切换等待异常。

预期：

- 本轮结束后机种和序列号清空。
- 方案和项目列表保留。
- 异常发生前的测量结果保留。
- 当前异常项目显示既有错误结果。
- 时间冻结并保留。
- 顶部进入 Error 或等待复位状态。
- 不自动回到空白 Ready。

### U7：检测中按停止

操作：

1. 在第二项或中间项目检测时触发 DT122 或运行页停止入口。
2. 等待停止收口完成。
3. 不复位。

预期：

- 机种和序列号清空。
- 方案和项目列表保留。
- 已检测项目结果保留。
- 当前项目按现有停止逻辑收口。
- 未检测项目保留未检测。
- 时间冻结并保留。
- 顶部保持 Paused 或要求复位状态。
- 不能直接启动下一轮。

### U8：检测中急停

操作：

1. 检测中触发 DT123。
2. 确认急停提示。
3. 解除急停，但不执行复位。

预期：

- 机种和序列号清空。
- 方案和项目列表保留。
- 已有项目结果和时间保留。
- 顶部先为 EmergencyStop，解除后进入 AwaitingReset。
- 不自动清空快照。
- 不自动续跑旧项目。

### U9：设备断线或安装异常

分别验证：

- PLC 断线。
- DMM 断线。
- 检测中的安装异常或安装拒绝。

预期：

- 机种和序列号清空。
- 方案和项目列表保留。
- 已测结果和时间保留。
- 顶部保留 Error 或 AwaitingReset。
- 设备恢复后不自动续跑。
- 复位成功前不清快照。

### U10：同机种下一块产品，启动前保留上一轮结果

操作：

1. 完成一轮 OK 或 NG。
2. 输入同一机种的新序列号。
3. 完成本轮产品身份确认。
4. 暂不启动。

预期：

- 自动选中原方案。
- 项目列表对象和上一轮项目结果仍保留。
- 上一轮时间仍保留。
- 上一轮顶部 OK/NG 仍保留。
- 不因机种输入而提前清结果。

继续操作：

5. 触发启动。

预期：

- 启动校验通过。
- DMM Ping 成功。
- DT234 写入成功。
- 此时上一轮项目结果清为未检测。
- 时间归零并开始新一轮计时。
- 顶部上一轮 OK/NG 清除。
- 新一轮从第一项开始。

### U11：下一轮启动被拒绝

在上一轮完成态下分别制造：

- 产品身份未确认。
- 重复记录确认取消。
- DMM Ping 失败。
- DT234 写入失败。

预期：

- 启动被拒绝。
- 方案和项目列表保留。
- 上一轮项目结果保留。
- 上一轮时间保留。
- 上一轮顶部结果保留。

### U12：复位成功

分别从以下状态复位：

- CompletedPass。
- CompletedFail。
- SingleItemNgStopped。
- Paused。
- AwaitingReset。
- Error。

预期：

- 最终 DT121 清除成功。
- 机种名称为空。
- 序列号为空。
- 方案仍选中。
- 项目列表仍存在。
- 所有项目测量结果恢复为未检测。
- 所有项目 Judgment 清空。
- 时间为 0.0。
- FinalJudgment 清空。
- 顶部进入 Ready 或等待输入状态。

### U13：复位后段失败

在可控调试环境中制造：

- DT122 最终确认失败。
- 第一次 DT121 清除失败。
- 复位完成快照验证失败。
- 成功提示后的最终 DT121 清除失败。

预期：

- UiState 为 ResetFailed。
- 方案和项目列表保留。
- 上一轮项目结果保留。
- 上一轮时间保留。
- FinalJudgment 底层值保留。
- 修复通信后再次复位成功，才清结果和时间。

### U14：快速输入竞态

操作：

1. 快速连续输入机种 A、机种 B、清空、再输入机种 C。
2. 快速展开方案框并选择。
3. 同时观察日志和项目列表。

预期：

- 最终只显示机种 C 对应方案。
- 旧异步任务不覆盖新状态。
- 没有项目列表短暂清空后永久不恢复。
- _selectedPlanIdentityKey 与界面方案一致。
- 检测引擎配置与界面项目一致。

### U15：同名方案唯一性

前置：

- 两个不同机种各存在一个相同方案名称。

操作：

1. 机种为空时展开全量方案。
2. 分别选择两个同名方案。

预期：

- 全量显示文本可区分系列和机种。
- 每次加载的项目属于正确 PlanModel。
- 不因方案名相同加载到另一机种。

---

## 14. 日志要求

### 14.1 Information

记录正常业务变化：

- 全量方案缓存加载完成及数量。
- 机种过滤后的方案数量。
- 自动选择的方案。
- 系统清空产品身份。
- 保留的方案身份和项目数。
- 下一轮有效启动清理完成。
- 复位最终成功清理完成。

### 14.2 Warning

用于审计异常或重要边界：

- 一个机种存在多个方案。
- 产品身份清理后方案身份意外丢失。
- 产品身份清理后项目数意外变为 0。
- 启动被拒绝且上一轮快照被保留。
- 复位失败且界面快照被保留。
- 方案异步旧任务被丢弃。
- 方案显示文本无法解析为唯一 PlanModel。

### 14.3 日志字段

关键日志至少包含：

- Reason。
- MachineType。
- SerialNumber 是否为空，不记录不必要的完整敏感值。
- PlanIdentityKey。
- PlanName。
- ItemCount。
- UiState。
- FinalJudgment。
- ElapsedSeconds。
- LoadVersion。

不在 PLC 轮询每周期重复记录相同保留日志，避免日志刷屏。

---

## 15. 风险与控制

### 15.1 WPF ComboBox 双向绑定重入

风险：

- ItemsSource.Clear 会改变 SelectedItem。
- SelectedItem 改变会影响 Text。
- Text 双向回写 SchemeName。
- SchemeName 回调会清方案和项目。

控制：

- 系统重建期间显式抑制。
- 在同一 UI 调度上下文中保存和恢复方案身份。
- 不依赖固定延时。
- 真实 WPF 验收必须覆盖。

### 15.2 方案显示名不是业务唯一键

风险：

- 不同系列或机种可能存在同名方案。

控制：

- 继续使用 Series、MachineType、PlanName 组成 _selectedPlanIdentityKey。
- 全量模式只把完整身份文本作为显示和 lookup 键。
- InspectionEngine 配置使用 PlanModel.PlanName 和对应 Items，不从展示文本拆字符串。

### 15.3 方案切换与旧检测快照冲突

风险：

- 新机种对应不同方案时，旧项目行不能继续作为新方案列表展示。

控制：

- 同方案输入保留旧项目结果到启动。
- 不同方案输入按需求立即切换项目列表。
- 顶部结果和时间仍到有效启动时清除。
- 不引入隐藏的多方案快照缓存。

### 15.4 复位后段失败

风险：

- UI 先清，PLC 后失败，现场证据丢失。

控制：

- 所有 UI 清理移动到最终 DT121 清除成功之后。
- ResetFailed 保留底层快照。
- 第二次复位可继续完成最终清理。

### 15.5 晚到检测回调

风险：

- 复位或下一轮启动后，旧引擎回调重新写入项目结果。

控制：

- 继续使用 _inspectionRunVersion。
- 继续使用 _ignoreInspectionCallbacksUntilNextStart。
- 复位开始先递增版本并屏蔽旧回调。
- 成功清理后再开放下一轮。

### 15.6 停止、急停与 finally 并发

风险：

- 控制流程更新 UiState，同时 RunInspectionAsync.finally 清产品身份并触发方案刷新。

控制：

- 产品身份清理统一切到 Dispatcher。
- 方案选项刷新使用 loadVersion。
- 方案重建期间保护选中项。
- finally 不修改 UiState、FinalJudgment、时间和项目结果。

### 15.7 工作区已有改动

当前工作区已经修改：

- Services/Inspection/InspectionStartValidator.cs。
- Tests/MinimumLoopTests/Program.cs。
- ViewModels/TestPageViewModel.cs。
- Views/TestPageView.xaml。

控制：

- 这些改动属于当前已确认需求链，执行时基于现状做小补丁。
- 不覆盖、不回滚用户已有改动。
- 修改前后使用 git diff 分段核对。
- 不使用 git reset --hard 或 checkout 覆盖文件。

---

## 16. 回滚方案

若阶段 A 出现方案选择异常：

- 只回滚方案选项重建保护相关补丁。
- 保留全量方案缓存和完整身份键功能。
- 恢复前先保存现场日志和复现步骤。

若阶段 D 出现复位异常：

- 只回滚复位成功提交点调整。
- 不回滚已经验证的产品身份清理和方案保留逻辑。
- 复位 PLC 协议顺序不做整体回退。

若测试发现跨机种输入的展示口径不符合现场习惯：

- 只调整“同方案保留、不同方案立即切换”的 UI 策略。
- 不改变检测结束、下一轮启动、复位成功这三个清理边界。

回滚前必须区分：

- UI 展示问题。
- PLC 实际复位问题。
- 设备通信问题。
- 当前工作区历史问题。

---

## 17. 分阶段执行顺序与停止点

### 阶段 0：基线冻结

1. 保存当前 git status。
2. 保存当前 git diff。
3. 记录最新现场日志中 NG 完成和 DT121 触发时间。
4. 跑一次当前 MinimumLoopTests，记录基线。
5. 跑一次当前 build，记录基线。

停止条件：

- 发现工作区文件被外部进程同时修改。
- 当前源码与本文问题链不一致。
- 方案目录结构实际不是 Series/MachineType/PlanName。

### 阶段 A：方案选择保留

1. 加系统重建抑制。
2. 保存和恢复方案身份。
3. 修复空机种分支。
4. 区分同方案与不同方案输入。
5. 增加方案联动契约测试。
6. 构建并运行最小测试。

阶段门：

- U1、U2 的人工界面验证通过。
- 手工清 ModelName 不丢方案和项目。
- NG 后清产品身份不丢快照。

### 阶段 B：检测结束清理统一

1. 审计 RunInspectionAsync 所有返回路径。
2. 固定 finally 只清产品身份。
3. 去除或确认正常完成重复清理。
4. 增加结束场景契约。
5. 构建并运行最小测试。

阶段门：

- U3、U4、U5 的无复位验证通过。

### 阶段 C：下一轮启动提交

1. 审计启动拒绝分支。
2. 固定 DT234 成功后才清上一轮快照。
3. 验证同方案输入不提前清项目结果。
4. 验证启动失败保留。
5. 增加启动门契约测试。

阶段门：

- U10、U11 通过。

### 阶段 D：复位成功提交

1. 删除复位中段的项目结果清理。
2. 增加复位成功 UI 提交方法。
3. 移到最终 DT121 清除成功后调用。
4. 调整成功日志和提示文本。
5. 增加复位成功与失败契约。
6. 构建并运行最小测试。

阶段门：

- U12、U13 通过。

### 阶段 E：异常与竞态验收

1. 验证 PLC、DMM、继电器异常。
2. 验证停止。
3. 验证急停。
4. 验证断线和安装异常。
5. 验证快速机种切换。
6. 验证同名方案。

阶段门：

- U6、U7、U8、U9、U14、U15 通过。

### 阶段 F：最终收口

1. git diff --check。
2. dotnet build。
3. MinimumLoopTests。
4. 汇总真实设备验收。
5. 核对无临时调试配置。
6. 核对未修改协议地址和 CSV 格式。
7. 输出最终修改文件、测试证据和剩余现场项。

---

## 18. 完成判定

只有同时满足以下条件，本需求才可标记完成：

### 18.1 代码条件

- 产品身份清理不清方案和项目列表。
- ComboBox 选项重建不会回写空方案造成破坏。
- 检测结束只清机种和序列号。
- 正常完成快照到下一轮有效启动才清。
- 停止、急停、异常快照到复位最终成功才清。
- 复位失败保留项目结果、时间和 FinalJudgment。
- 复位成功保留方案和项目定义。

### 18.2 自动化条件

- git diff --check 通过。
- 项目构建通过，或失败已确认属于既有基线。
- RUN-STATE-KEEP-01 最小契约通过。
- 相关启动门与方案联动测试通过。
- 完整测试中的继承失败单独列出。

### 18.3 真实验收条件

- U1 至 U15 已执行。
- PLC 未收到 DT121 时，OK/NG 页面快照确实保留。
- 复位成功后，方案和项目列表确实保留。
- 复位后段失败时，项目结果和时间确实不丢。
- 扫码、手输和快速切换没有方案竞态。

自动化通过不能替代真实 PLC、DMM、继电器、扫描枪和 WPF 界面验收。

---

## 19. 执行结果汇报格式

执行完成后按以下格式汇报：

1. 已修改文件。
2. 每个文件的核心修改。
3. 各场景最终保留/清理行为。
4. git diff --check 结果。
5. build 结果。
6. RUN-STATE-KEEP-01 测试结果。
7. 完整测试与继承失败。
8. U1 至 U15 的真实设备验收状态。
9. 未完成项和阻塞原因。
10. 是否保留临时调试配置。

不得使用“全部通过”概括尚未执行的真实设备验收。

---

## 20. 建议提交信息

建议提交标题：

fix: 保留运行页检测快照并在复位成功后清理

建议提交正文：

- 修复机种清空时方案下拉重建导致方案和项目列表丢失
- 检测结束仅清机种与序列号并保留检测快照
- 下一轮有效启动后清理上一轮结果与时间
- 复位最终成功后清结果但保留方案和项目定义
- 复位失败时保留现场检测快照

---

## 21. 执行状态

用户已确认本方案，代码已按“阶段 0 → A → B → C → D → E → F”完成实现。

已完成：

- 方案下拉系统重建保护。
- 空机种全量方案展示及当前方案身份保留。
- 同一方案输入下一块产品时保留项目运行快照。
- 检测任务结束只清机种和序列号。
- 下一轮有效启动后清理上一轮项目结果、时间和最终判定。
- 复位清理移动到最终 DT121 清除成功之后。
- 复位失败保留上一轮项目结果、时间和最终判定。
- 复位成功保留方案和项目定义，只重置项目运行字段。
- RUN-STATE-KEEP-01 最小源码契约测试。

自动化证据：

- git diff --check：通过。
- dotnet build：通过，0 个错误；保留既有编译警告。
- dotnet run --project Tests/MinimumLoopTests/MinimumLoopTests.csproj：
  - RUN-STATE-KEEP-01：通过。
  - 其余大部分测试：通过。
  - 3 项既有基线失败：运行页按钮深度样式、PLC-DT312 地址契约、电阻模式正无穷界面显示。

尚待现场执行：

- U1 至 U15 的真实 WPF、PLC、DMM、继电器、扫描枪、停止、急停、断线和复位成功/失败验收。
- 重点观察 NG 后不复位、复位后项目列表保留、复位后结果清零三个场景。
