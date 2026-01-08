# 销售订单报表功能 - 决策记录

本文档记录实施过程中做出的所有决策，待用户确认。

---

## 决策 #1: 税率默认值

- **问题**: 销售订单明细中的税率字段，默认值应该是多少？
- **采用方案**: 默认税率设为 10（日本标准消费税率10%），同时支持 8（轻减税率）和 0（免税）
- **原因**: 日本2019年10月起消费税率为10%，是最常用的税率

---

## 决策 #2: 销项税科目查找条件

- **问题**: 销售收入科目的查找条件中，`taxType = 'OUTPUT_TAX'` 是否正确？
- **采用方案**: 使用 `taxType = 'OUTPUT_TAX'` 作为查找销售收入科目的条件
- **原因**: 根据现有账户 schema 定义，OUTPUT_TAX 表示销项税关联科目

---

## 决策 #3: 请求书编号格式

- **问题**: 请求书编号的格式应该是什么？
- **采用方案**: `INV{年份4位}{月份2位}{序号4位}`，例如 `INV202412-0001`
- **原因**: 与现有的纳品书编号格式 `DN{YYYYMM}{NNNN}` 保持一致

---

## 决策 #4: 生命周期追踪的请求书节点

- **问题**: 请求书是可选的，如何在生命周期中处理？
- **采用方案**: 生命周期显示5个节点，请求书节点标记为"可选"，跳过时不影响后续节点显示
- **原因**: 部分客户（一手交钱一手交货）不需要请求书

---

## 决策 #5: 客户流失预警阈值

- **问题**: 判断客户流失的默认阈值是多少天？
- **采用方案**: 支持用自然语言描述给 Agent，由 Agent 解析并执行。例如"最近一个月没有下单的老客户"
- **原因**: 不同场景对"流失"的定义不同，自然语言交互更灵活

---

## 决策 #6: 库存不足预警的日均入库计算周期

- **问题**: 计算日均生产入库量时，应该参考多少天的历史数据？
- **采用方案**: 默认参考过去30天的入库数据计算日均值
- **原因**: 30天更能反映近期的生产节奏

---

## 决策 #7: 超期未纳品的告警阈值

- **问题**: 希望纳期过后多少天开始告警？
- **采用方案**: 默认纳期当天（0天）即开始告警
- **原因**: 纳期是承诺给客户的日期，一旦超过就应该引起注意

---

## 决策 #8: 应收账款超期告警阈值

- **问题**: 支付期限过后多少天开始告警？
- **采用方案**: 默认1个工作日（跳过周末和日本节假日）
- **原因**: 工作日更准确，避免周末/节假日造成的误报

---

## 决策 #9: AI销售分析使用的模型

- **问题**: AI自然语言分析使用哪个模型？
- **采用方案**: 暂时使用 gpt-4，后续计划切换到 Claude Opus
- **原因**: gpt-4 更稳定可靠，Opus 待 API Key 获取后切换

---

## 决策 #10: ECharts 主题配色

- **问题**: 图表使用什么配色方案？
- **采用方案**: 使用 Element Plus 默认配色（蓝色系为主色调）
- **原因**: 与现有UI风格保持一致

---

## 实施完成清单

以下功能已实施完成：

### 后端文件
1. ✅ `server-dotnet/sql/update_sales_order_schema.sql` - 销售订单 Schema 更新（税率字段）
2. ✅ `server-dotnet/sql/update_business_partner_schema.sql` - 客户 Schema 更新（偏好科目）
3. ✅ `server-dotnet/sql/create_sales_invoice_table.sql` - 请求书表创建
4. ✅ `server-dotnet/sql/create_sales_alerts_table.sql` - 告警和任务表创建
5. ✅ `server-dotnet/Modules/AccountSelectionService.cs` - 科目选择服务
6. ✅ `server-dotnet/Modules/SalesInvoiceModule.cs` - 请求书模块
7. ✅ `server-dotnet/Modules/PaymentMatchingService.cs` - 入金匹配服务
8. ✅ `server-dotnet/Modules/SalesOrderLifecycleService.cs` - 生命周期追踪服务
9. ✅ `server-dotnet/Modules/SalesAnalyticsModule.cs` - 销售分析模块
10. ✅ `server-dotnet/Modules/SalesAnalyticsAiService.cs` - AI 自然语言分析
11. ✅ `server-dotnet/Modules/SalesMonitorBackgroundService.cs` - 后台监控服务
12. ✅ `server-dotnet/Modules/WeComNotificationService.cs` - 企业微信通知服务
13. ✅ `server-dotnet/Modules/SalesAlertModule.cs` - 告警 API 模块
14. ✅ `server-dotnet/Modules/DeliveryNoteModule.cs` - 出库生成应收账款凭证（已修改）

### 前端文件
1. ✅ `web/src/views/SalesInvoicesList.vue` - 请求书列表页面
2. ✅ `web/src/views/SalesAnalytics.vue` - 销售分析仪表板
3. ✅ `web/src/views/SalesAlertTasks.vue` - 告警任务面板
4. ✅ `web/src/components/SalesOrderLifecycle.vue` - 生命周期追踪组件
5. ✅ `web/src/views/SalesOrdersList.vue` - 添加进度显示（已修改）

### 新增路由
- `/sales-invoices` - 请求书列表
- `/sales-analytics` - 销售分析
- `/sales-alerts` - 告警任务

---

## 待用户执行

1. **运行 SQL 脚本更新数据库**：
   ```bash
   psql -f server-dotnet/sql/update_sales_order_schema.sql
   psql -f server-dotnet/sql/update_business_partner_schema.sql
   psql -f server-dotnet/sql/create_sales_invoice_table.sql
   psql -f server-dotnet/sql/create_sales_alerts_table.sql
   ```

2. **配置企业微信**（可选，用于推送告警通知）：
   在 `appsettings.json` 中添加：
   ```json
   {
     "WeComNotification": {
       "CorpId": "企业ID",
       "AgentId": "应用ID",
       "Secret": "应用Secret"
     }
   }
   ```
   或使用 Webhook 方式：
   ```json
   {
     "WeComNotification": {
       "WebhookUrl": "https://qyapi.weixin.qq.com/cgi-bin/webhook/send?key=xxx"
     }
   }
   ```

3. **重启后端服务**使新功能生效

---

*以上决策待用户确认，如需修改请提出。*

