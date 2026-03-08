# AI场景 vs AI技能 — 用途与代码使用说明

## 结论概览

| 项目 | AI场景 (Agent Scenarios) | AI技能 (Agent Skills) |
|------|--------------------------|-------------------------|
| **数据表** | `agent_scenarios` | `agent_skills` + rules/examples |
| **前端页面** | `/ai/agent-scenarios`（AIシナリオ） | `/ai/agent-skills`（AI技能） |
| **在对话中是否决定“用谁”** | **否**：不参与路由 | **是**：由 Skill 匹配决定用哪个技能 |
| **实际使用程度** | **几乎未使用**（仅两处边缘逻辑） | **核心使用**：每条消息都走 Skill |

---

## 1. AI技能 (Agent Skills) — 实际在用

- **作用**：定义「能做什么」和「怎么回应」——系统提示词、抽取提示、规则、示例、可用工具等。
- **对话流程中的位置**：
  1. 用户发消息或上传文件 → 请求 `/ai/agent/message` 或 `/ai/agent/tasks`。
  2. 后端 `AgentKitService` 用 **SkillMatcher** 根据消息内容、文件类型等做 **Skill 匹配**（`_skillMatcher.MatchAsync(...)`）。
  3. 匹配到的 **Skill**（如 `invoice_booking`、`timesheet`、`general_assistant`）决定：
     - 用哪个系统提示（`SkillPromptBuilder2.BuildSystemPromptAsync(matchedSkill, ...)`）
     - 用哪些工具、模型参数等。
  4. 回复内容完全由该 Skill 的配置（DB 里的 skill + rules + examples）驱动。

- **相关代码**（核心）：
  - `Server.Modules.AgentKitService`：调用 `_skillMatcher.MatchAsync`、`_skillPromptBuilder2.BuildSystemPromptAsync(matchedSkill, ...)`。
  - `AgentSkillService`、`SkillMatcher`、`SkillPromptBuilder2`：技能数据与匹配、提示构建。
  - 前端：`/ai/agent-skills` 页面对应 `/ai/agent/skills` 系列 API，对技能进行增删改查。

---

## 2. AI场景 (Agent Scenarios) — 几乎未参与主流程

- **设计含义**：按「何时触发」「做什么」描述的剧本（触发器、动作说明、优先级等），理论上可用于选场景、给提示加上下文。
- **当前实现里**：
  - 场景会被加载并传入执行上下文，但 **不会用来选“用哪个 Skill”**；选 Skill 只依赖 **SkillMatcher + 技能配置**。
  - 后端注释已写明：`allScenarios 仅用于 AgentExecutionContext 构造（后续可完全移除）`。
  - 前端发聊天请求时 **不传 `scenarioKey`**（ChatKit 的 `/ai/agent/message` payload 里没有 scenarioKey）。

- **实际被用到的两处**（边缘逻辑）：
  1. **ShouldForceInvoiceDefaults**（`AgentKitService.cs` 约 3961 行）  
     若当前匹配到的场景里，存在 `scenarioKey` 以 `voucher.` 开头且含 `.receipt` 或 `.invoice`，则强制使用发票相关默认值。  
     → 依赖「有这类 key 的场景被选进上下文」，且选场景依赖关键词/元数据匹配，实际很少触发。
  2. **销售订单任务元数据**（约 5482 行）  
     创建任务时把 `context.Scenarios.FirstOrDefault()?.ScenarioKey` 写入 metadata，仅作记录，不参与路由或提示。

- **相关代码**：
  - `AgentScenarioService`：从 `agent_scenarios` 读数据；`ListActiveAsync` 在 AgentKit 里被调用来构造 `AgentExecutionContext`。
  - `SelectScenariosForMessage` / `SelectScenariosForFile`：按消息或文件选出一批场景，只用于塞进 context，不参与 Skill 选择或主提示生成。
  - 前端：`/ai/agent-scenarios` 页面对应 `/ai/agent-scenarios` 系列 API；`interpret` 仅用于该页「用自然语言生成场景配置」的辅助功能，与主对话无关。

---

## 3. 对比小结

- **谁在“选能力”**：  
  - **AI技能**：每条消息/任务都会通过 **Skill 匹配** 选出一个 Skill，并完全按该 Skill 执行。  
  - **AI场景**：不参与“选谁”；只作为上下文传入，且主流程已迁移到 Skill，场景仅在两处边缘逻辑里出现。

- **是否可能误删**：  
  - 若删除或停用 **AI技能**：会直接影响对话能力（对应技能不可用或回退到 general_assistant）。  
  - 若删除 **AI场景**：当前逻辑下几乎无影响，仅上述两处边缘行为可能变化（且依赖你确实在用带 voucher.receipt/invoice 的场景）。

因此，你的判断成立：**在现有代码逻辑里，AI场景基本没有被当作“主流程”使用；真正在用的是 AI技能。**

如需，我可以再根据你当前菜单/权限设计，给一版「是否保留 AI场景 入口」的建议（例如仅保留配置入口作扩展用，或后续完全迁移到 Skill 再考虑下线场景）。
