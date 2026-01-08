# 系统概要说明

## 业务概览

- **系统定位**：企业 ERP 门户，集成会计凭证、发票识别、员工管理、库存管理、AI 任务助手等多模块功能。
- **主要角色**：
  - 一般业务用户：处理会计凭证、库存品目、销售订单等日常业务。
  - 财务人员：审核凭证、核对税号、管理公司设定。
  - AI 助手：基于会话上下文自动生成凭证、销售订单或发票识别任务。
- **关键业务流程**：
  - 上传票据 → AI 解析 → 生成待办任务 → 用户确认 → 自动创建会计凭证。
  - 自然语言描述销售需求 → AI 场景识别 → 调用 `create_sales_order` 工具 → 生成销售订单并回写任务面板。
  - 维护库存主数据（品目、仓库、批次等）→ 通过动态表单新增 → 列表按 schema 配置展示。

## 技术架构概览

- **前端**：Vue 3 + TypeScript，使用 Element Plus 作为组件库，借助 `DynamicForm` 根据后端 schema 动态渲染表单。i18n 覆盖日语/中文/英文。
- **后端**：ASP.NET Core Minimal API，连接 PostgreSQL（Npgsql）。大量领域功能集中在 `Program.cs` 及 `Modules` 子目录。
- **存储与附件**：Azure Blob Storage 负责文件上传，生成 SAS 链接供前端预览。
- **认证与会话**：JWT + CORS，自定义 header（`x-company-code`、`x-openai-key`）实现租户隔离与 OpenAI 工具调用。
- **AI 代理**：`AgentKitService` 负责场景匹配、消息管理、工具执行。与 `AgentScenarioService`、`SalesOrderTaskService` 等配合输出任务数据给前端。

## 近期重点变更（业务 + 技术）

### 库存主数据修复

- 业务影响：保证新建品目能立即在品目列表中出现，避免“创建成功但列表空白”的问题。
- 技术实现：
  - `InventoryModule` 现在插入/更新时自动剥离 `{ payload: ... }` 包裹，避免 schema 生成列（`payload->>'code'`）失效。
  - 应用启动时执行归一化 SQL，将历史数据矫正为纯 payload 结构。
  - 前端 `MaterialForm.vue` 保存接口改为直接提交扁平化 payload，加载时兼容旧格式并重置模型默认值。

### AI 销售订单场景增强

- 业务影响：支持用户通过自然语言下单，任务面板展示销售订单详情（客户、金额、行项目等）。
- 技术实现：
  - `AgentKitService` 新增 `create_sales_order` 工具和上下文控制，自动生成订单号并写入 `sales_orders` 表。
  - `SalesOrderTaskService` 负责记录与更新 AI 任务状态，前端 `ChatKit.vue` 展示销售订单卡片。
  - 场景选择逻辑优先匹配包含“受注/订单/下单”等关键词的消息，自动加载销售订单指引。

### 会话与任务面板优化

- 业务影响：发票识别与销售订单任务同时显示，时间轴排序正确，并隐藏上传确认类信息。
- 技术实现：
  - 前端 `ChatKit.vue` 调整任务归类 `pending/completed` 逻辑，新增销售订单模板；i18n 完整覆盖。
  - 后端 `/ai/sessions/{sessionId}/tasks` 汇总 invoice 与 sales order 任务，返回统一结构。

### 安全与兼容性调整

- 统一 `ProtectedData` 调用，仍在 Windows 环境使用 DPAPI；跨平台回退策略避免构建错误。
- 前端请求统一走 `/api` 前缀，GET 请求加缓存破坏参数；缺失 token 时自动跳转登录。

## 环境与部署要点

- **运行端口**：默认 `http://127.0.0.1:5179`。
- **启动顺序**：
  1. 停止已有 `dotnet` 进程：`taskkill /IM dotnet.exe /F`
  2. 构建：`dotnet build`
  3. 启动：`dotnet run --urls http://127.0.0.1:5179`
- **常见警告**：`Cronos`、`JsonSchema.Net` 版本自动提升的 NU1603 仅为包版本提示，可忽略。
- **日志**：运行输出建议重定向到 `server-run.log`，方便排查端口监听、数据库迁移等信息。

## 未决事项与后续方向

- `AgentScenarioService` 中存在部分 Nullability 警告，建议逐步完善参数判空。
- `ProtectedData` 在非 Windows 环境仍需进一步评估（当前默认假设 Windows）。
- `agent_scenarios` 表迁移存在空 `company_code` 数据，需确认业务是否允许全局场景或补齐数据。

---

> 本文档根据近期聊天记录与代码改动整理，旨在为后续交接或回顾提供系统级概览。

