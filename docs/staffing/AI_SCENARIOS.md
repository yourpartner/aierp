# 人才派遣版 AI 应用场景详解

## 设计原则

### 第一性原理

人才派遣行业的本质是**人力资源与客户需求的匹配**。派遣公司的核心价值：

1. **消除信息不对称** - 客户不知道哪里有合适的人，人才不知道哪里有合适的机会
2. **降低交易成本** - 招聘、筛选、合同、薪资等繁琐事务
3. **风险转移** - 用工风险、合规风险的承担

### AI 定位

```
┌─────────────────────────────────────────────────────────────────────────┐
│                                                                         │
│   AI 不是替代营业，而是让营业从 "执行者" 变成 "决策者"                   │
│                                                                         │
│   传统营业：                                                            │
│   阅读邮件 → 整理需求 → 搜索简历 → 写推荐邮件 → 协调时间 → 跟进结果     │
│   ↑─────────────────── 60% 时间在执行 ───────────────────↑              │
│                                                                         │
│   AI 赋能后：                                                           │
│   [AI处理] → 营业确认 → [AI执行] → 营业决策 → [AI跟进]                  │
│   ↑─────────────────── 80% 时间在决策和关系维护 ─────────↑              │
│                                                                         │
└─────────────────────────────────────────────────────────────────────────┘
```

### 不做什么

| 伪需求 | 为什么不做 |
|-------|-----------|
| AI 自动生成合同 | 法务风险大，人必须确认 |
| AI 替代面试 | 客户和候选人都不接受 |
| 全自动匹配（无人确认）| 匹配错误成本高，信任问题 |
| Chatbot 替代营业沟通 | B2B 场景客户期望对人 |

---

## 场景一：智能案件解析

### 痛点

客户的需求邮件是非结构化的，营业需要：
1. 阅读邮件理解需求（10分钟）
2. 整理成结构化信息（15分钟）
3. 登记到系统（10分钟）

而且可能因为忙而延迟处理。

### 解决方案

```
客户邮件到达
      │
      ▼
┌─────────────────────────────────────────────────────────────┐
│  AI 自动解析                                                 │
│                                                             │
│  输入：                                                      │
│  ─────────────────────────────────────────────────────────  │
│  件名：【案件】Java開発エンジニア募集                         │
│                                                             │
│  本文：                                                      │
│  お世話になっております。                                    │
│  以下の案件でエンジニアを探しております。                     │
│                                                             │
│  ・必要スキル：Java, Spring Boot, AWS                        │
│  ・経験年数：5年以上                                         │
│  ・開始時期：来月から                                        │
│  ・勤務地：渋谷（週2リモート可）                              │
│  ・単価：60-70万円/月                                        │
│                                                             │
│  ご確認のほど、よろしくお願いいたします。                     │
│  ─────────────────────────────────────────────────────────  │
│                                                             │
│  输出：                                                      │
│  {                                                          │
│    "project_name": "Java開発エンジニア",                     │
│    "required_skills": ["Java", "Spring Boot", "AWS"],       │
│    "experience_years": 5,                                   │
│    "work_location": "渋谷",                                  │
│    "remote_policy": "hybrid",                               │
│    "start_date": "2024-02-01",                              │
│    "budget_min": 60,                                        │
│    "budget_max": 70,                                        │
│    "urgency": "medium"                                      │
│  }                                                          │
│                                                             │
│  + 匹配发件人 → ABC株式会社（既存客户）                       │
│  + 置信度：85%                                               │
└─────────────────────────────────────────────────────────────┘
      │
      ▼
营业确认（2分钟）→ 自动创建案件 → 触发匹配流程
```

### API

```
POST /staffing/ai/parse-project-request

Request:
{
  "subject": "【案件】Java開発エンジニア募集",
  "content": "お世話になっております...",
  "senderEmail": "tanaka@abc.co.jp"
}

Response:
{
  "parsed": {
    "project_name": "Java開発エンジニア",
    "required_skills": ["Java", "Spring Boot", "AWS"],
    "experience_years": 5,
    ...
  },
  "matchedPartner": {
    "id": "uuid",
    "name": "ABC株式会社"
  },
  "confidence": 0.85,
  "suggestedActions": [
    {"action": "create_project", "label": "案件として登録"},
    {"action": "find_candidates", "label": "候補者を検索"},
    {"action": "reply_confirm", "label": "受領確認を返信"}
  ]
}
```

### 价值

| 指标 | 传统 | AI 辅助 |
|------|------|---------|
| 处理时间 | 35分钟 | 2分钟 |
| 响应延迟 | 可能数小时 | 即时 |
| 漏单风险 | 有 | 无 |

---

## 场景二：语义级人才匹配

### 痛点

传统的关键词匹配存在问题：
- 搜索 "Java" 会漏掉写 "Kotlin" 的优秀候选人
- 无法理解 "大规模系统经验" 和 "日均PV 1亿" 的关联
- 好的匹配依赖营业的记忆力和经验

### 解决方案

```
传统匹配：
案件要求 "Java開発5年以上"
      ↓ 关键词搜索
SQL: WHERE skills LIKE '%Java%'
      ↓
漏掉：用 Kotlin/Scala 的优秀候选人

───────────────────────────────────────────────

AI 语义匹配：
案件要求 "大規模システム開発経験必須"
      ↓ 语义理解
候选人简历 "日均PV 1億のECサイトのバックエンド開発を担当"
      ↓ 
匹配！这就是大规模系统经验
```

### 匹配算法

```python
def calculate_match_score(project, candidate):
    """
    计算匹配分数
    
    维度：
    1. 技能匹配（语义相似度）
    2. 经验匹配（年数 + 内容相关性）
    3. 隐性因素（历史成功记录、客户偏好）
    """
    
    # 技能匹配 - 语义相似度
    skill_score = 0
    for required_skill in project.required_skills:
        best_match = max([
            semantic_similarity(required_skill, cs) 
            for cs in candidate.skills
        ])
        skill_score += best_match
    skill_score /= len(project.required_skills)
    
    # 经验匹配
    experience_score = evaluate_experience(
        candidate.experience_summary,
        project.job_description
    )
    
    # 综合分数
    overall = skill_score * 0.6 + experience_score * 0.4
    
    return {
        "overall": overall,
        "skill_match": skill_score,
        "experience_match": experience_score,
        "recommendation": generate_reason(...)
    }
```

### API

```
POST /staffing/ai/match-candidates

Request:
{
  "requiredSkills": ["Java", "Spring Boot", "AWS"],
  "experienceYears": 5,
  "description": "大規模ECサイトのバックエンド開発",
  "budgetMax": 700000,
  "limit": 10
}

Response:
{
  "candidates": [
    {
      "id": "uuid",
      "resourceCode": "RS-0042",
      "displayName": "山田太郎",
      "resourceType": "employee",
      "skills": ["Java", "Kotlin", "Spring Boot", "AWS", "MySQL"],
      "monthlyRate": 650000,
      "availabilityStatus": "available",
      "matchScore": {
        "overall": 0.92,
        "skillMatch": 0.95,
        "experienceMatch": 0.87
      },
      "recommendation": "Java, Spring Boot, AWSのスキルがマッチ（適合度92%）"
    },
    ...
  ],
  "totalMatched": 8
}
```

### 价值

| 指标 | 传统 | AI 辅助 |
|------|------|---------|
| 搜索时间 | 30分钟 | 3秒 |
| 匹配精准度 | 依赖经验 | 数据驱动 |
| 发现隐藏人才 | 困难 | 自动 |

---

## 场景三：自动沟通 Agent

### 痛点

营业 40%+ 的时间花在重复性沟通上：
- 向候选人介绍案件
- 确认意向
- 协调面试时间
- 跟进结果

### 解决方案

```
┌─────────────────────────────────────────┐
│           AI 沟通 Agent                  │
└─────────────────────────────────────────┘
                    │
    ┌───────────────┼───────────────┐
    ↓               ↓               ↓
┌─────────┐   ┌─────────┐   ┌─────────┐
│ 候选人   │   │ 客户    │   │ 内部    │
│         │   │         │   │         │
│ 案件推荐 │   │ 进度更新 │   │ 状态同步 │
│ 意向确认 │   │ 候选推荐 │   │ 异常提醒 │
│ 时间协调 │   │ 面试安排 │   │ 自动催办 │
└─────────┘   └─────────┘   └─────────┘
```

### 邮件生成示例

**场景：向候选人推荐案件**

```
输入：
- 模板类型：project_intro
- 候选人：山田太郎
- 案件：EC系统开发
- 客户：大手EC企業

输出：
───────────────────────────────────────────────
件名：【案件のご紹介】EC系統開発

山田様

お世話になっております。

現在、山田様のご経験・スキルにマッチする
案件がございます。

【案件概要】
・案件名：EC系統開発
・クライアント：大手EC企業
・必要スキル：Java, Spring Boot, AWS
　（山田様のご経験にマッチしています）
・勤務形態：ハイブリッド（週2リモート可）
・開始時期：来月から
・期間：6ヶ月〜（長期の可能性あり）

ご興味がございましたら、詳細をご説明
させていただきます。

ご都合の良い日時をお知らせいただけ
ますでしょうか。

何卒よろしくお願いいたします。
───────────────────────────────────────────────
```

### API

```
POST /staffing/ai/generate-outreach

Request:
{
  "templateType": "project_intro",
  "resourceId": "uuid",
  "projectId": "uuid"
}

Response:
{
  "subject": "【案件のご紹介】EC系統開発",
  "body": "山田様\n\nお世話になっております...",
  "to": "yamada@example.com",
  "variables": {
    "resourceName": "山田太郎",
    "projectName": "EC系統開発",
    "clientName": "大手EC企業",
    "requiredSkills": ["Java", "Spring Boot", "AWS"]
  }
}
```

### 价值

| 指标 | 传统 | AI 辅助 |
|------|------|---------|
| 每封邮件 | 10-15分钟 | 2分钟确认 |
| 日处理量 | 10-20封 | 50-100封 |
| 个性化程度 | 模板化 | 动态个性化 |

---

## 场景四：市场行情分析

### 痛点

营业在报价时：
- 依赖经验和感觉
- 缺乏数据支撑
- 可能报高丢单，报低损失利润

### 解决方案

基于历史成交数据分析市场行情，给出定价建议。

```
输入：
- 技能：Java, Spring Boot
- 经验：5年

输出：
┌────────────────────────────────────────────────────┐
│ 市場分析レポート                                    │
├────────────────────────────────────────────────────┤
│                                                    │
│ サンプル数: 47件（過去12ヶ月）                      │
│                                                    │
│ 単価レンジ:                                         │
│ ├─────────────┬─────────────┬─────────────┤        │
│ │    55万     │    63万     │    72万     │        │
│ │   (25%)     │   (中央)    │   (75%)     │        │
│ └─────────────┴─────────────┴─────────────┘        │
│                                                    │
│ 推奨単価:                                          │
│                                                    │
│  ┌──────────────────────────────────────┐         │
│  │ 強気    70万円/月   成約率 60%        │         │
│  │ 適正    65万円/月   成約率 80%        │         │
│  │ 確実    58万円/月   成約率 95%        │         │
│  └──────────────────────────────────────┘         │
│                                                    │
│ 市場トレンド: 安定                                  │
│ 需給バランス: 0.85（需要優位）                      │
│ 季節性: 4月開始案件が多く、強気設定可              │
│                                                    │
└────────────────────────────────────────────────────┘
```

### API

```
POST /staffing/ai/market-analysis

Request:
{
  "skills": ["Java", "Spring Boot"],
  "experienceYears": 5
}

Response:
{
  "sampleSize": 47,
  "priceRange": {
    "min": 450000,
    "percentile25": 550000,
    "median": 630000,
    "percentile75": 720000,
    "max": 850000,
    "average": 625000
  },
  "recommendations": [
    {"price": 700000, "probability": 60, "label": "強気"},
    {"price": 650000, "probability": 80, "label": "適正"},
    {"price": 580000, "probability": 95, "label": "確実"}
  ],
  "marketTrend": "stable",
  "supplyDemandRatio": 0.85,
  "seasonalNote": "4月開始案件が多く、強気設定可"
}
```

### 价值

| 指标 | 传统 | AI 辅助 |
|------|------|---------|
| 定价依据 | 经验 | 数据驱动 |
| 报价信心 | 低 | 高 |
| 利润优化 | 难 | 可量化 |

---

## 场景五：流失预测与预警

### 痛点

派遣员工突然离职：
- 客户抱怨服务不稳定
- 重新招人成本高
- 关系受损

### 解决方案

监测多维信号，提前预警。

```
监测信号：
├── 合同即将到期（30天/7天）
├── 残业过多（>40h/月，连续2个月）
├── 长期未调薪（>12个月活跃但未调整）
├── 工时异常（突然减少）
└── 负面反馈（面谈记录、评价）

预警级别：
├── Critical（紧急）：7天内到期、严重异常
├── High（要对应）：30天内到期、持续异常
└── Medium（确认推荐）：潜在风险信号
```

### 预警示例

```
┌─────────────────────────────────────────────────────┐
│ ⚠️ Critical - 契約満期                              │
├─────────────────────────────────────────────────────┤
│                                                     │
│ 山田太郎（派遣中@ABC株式会社）                       │
│                                                     │
│ 契約終了日: 2024-02-15（5日後）                      │
│ 現在単価: ¥650,000/月                               │
│                                                     │
│ 推奨アクション:                                     │
│ [更新確認] [次案件検索] [面談設定]                  │
│                                                     │
└─────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────┐
│ ⚠️ High - 残業過多                                  │
├─────────────────────────────────────────────────────┤
│                                                     │
│ 佐藤花子（派遣中@XYZ株式会社）                       │
│                                                     │
│ 先月残業: 52時間                                     │
│ 実労働時間: 212時間                                  │
│                                                     │
│ 推奨アクション:                                     │
│ [状況確認] [クライアント相談]                       │
│                                                     │
└─────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────┐
│ ℹ️ Medium - 単価未見直                              │
├─────────────────────────────────────────────────────┤
│                                                     │
│ 鈴木一郎（派遣中@DEF株式会社）                       │
│                                                     │
│ 稼働期間: 18ヶ月                                     │
│ 現在単価: ¥600,000/月（変更なし）                   │
│                                                     │
│ 推奨アクション:                                     │
│ [単価見直し] [面談設定]                             │
│                                                     │
└─────────────────────────────────────────────────────┘
```

### API

```
GET /staffing/ai/churn-alerts

Response:
{
  "alerts": [
    {
      "type": "contract_expiring",
      "severity": "critical",
      "contractId": "uuid",
      "contractNo": "CT-2024-0042",
      "resourceId": "uuid",
      "resourceName": "山田太郎",
      "clientName": "ABC株式会社",
      "endDate": "2024-02-15",
      "daysRemaining": 5,
      "billingRate": 650000,
      "message": "山田太郎の契約が5日後に終了します",
      "suggestedActions": ["更新確認", "次案件検索", "面談設定"]
    },
    ...
  ],
  "summary": {
    "critical": 2,
    "high": 5,
    "medium": 12,
    "total": 19
  }
}
```

### 价值

| 指标 | 传统 | AI 辅助 |
|------|------|---------|
| 预警时间 | 事后 | 提前30天 |
| 离职率 | 基准 | -15-20% |
| 客户满意度 | - | 提升 |

---

## 完整工作流示例

### 场景：客户发来新需求邮件

```
时间线：
────────────────────────────────────────────────────────────────────

T+0分    客户发送邮件
         │
         ▼
T+1分    [AI] 自动解析邮件
         - 提取技能要求、经验年数、预算等
         - 匹配发件人到既存客户
         - 创建待确认案件
         │
         ▼
T+3分    营业收到通知，确认案件信息
         │
         ▼
T+5分    [AI] 自动匹配候选人
         - 语义匹配 TOP 10 候选人
         - 计算匹配分数
         - 生成推荐理由
         │
         ▼
T+8分    营业确认 TOP 3 候选人
         │
         ▼
T+10分   [AI] 为 3 位候选人生成个性化推荐邮件
         │
         ▼
T+12分   营业确认并发送
         │
         ▼
T+1天    候选人 A 回复有兴趣
         │
         ▼
T+1天    [AI] 自动发送面试时间协调邮件
         │
         ▼
T+2天    协调完成，安排面试
         │
         ▼
T+5天    面试通过，[AI] 生成契约确认邮件
         │
         ▼
...

────────────────────────────────────────────────────────────────────

传统方式同样流程需要：5-7天
AI 辅助缩短至：2-3天
```

---

## 技术实现要点

### 1. AI 服务集成

```csharp
// 调用 OpenAI/Claude 进行解析
public async Task<ProjectParseResult> ParseProjectRequest(string emailContent)
{
    var prompt = BuildParsingPrompt(emailContent);
    var response = await _aiClient.ChatCompletion(prompt);
    return JsonSerializer.Deserialize<ProjectParseResult>(response);
}
```

### 2. 语义相似度计算

```csharp
// 使用 Embedding 计算语义相似度
public async Task<double> SemanticSimilarity(string text1, string text2)
{
    var embedding1 = await _aiClient.GetEmbedding(text1);
    var embedding2 = await _aiClient.GetEmbedding(text2);
    return CosineSimilarity(embedding1, embedding2);
}
```

### 3. 邮件模板引擎

```csharp
// 模板变量替换
public string RenderTemplate(string template, Dictionary<string, string> variables)
{
    foreach (var (key, value) in variables)
    {
        template = template.Replace($"{{{{{key}}}}}", value);
    }
    return template;
}
```

### 4. 预警规则引擎

```csharp
// 流失风险评估
public async Task<List<Alert>> EvaluateChurnRisk()
{
    var alerts = new List<Alert>();
    
    // 规则 1: 合同到期
    var expiringContracts = await GetExpiringContracts(30);
    foreach (var c in expiringContracts)
    {
        alerts.Add(new Alert
        {
            Type = "contract_expiring",
            Severity = c.DaysRemaining <= 7 ? "critical" : "high",
            ...
        });
    }
    
    // 规则 2: 残业过多
    var overtimeAlerts = await GetOvertimeAlerts(40);
    ...
    
    return alerts;
}
```

---

## 持续优化

### 数据收集

持续收集以下数据用于模型优化：

1. **匹配反馈** - 营业是否采纳推荐、面试结果
2. **邮件效果** - 打开率、回复率
3. **定价准确性** - 实际成交价 vs 建议价
4. **预警准确性** - 预警 vs 实际离职

### 模型迭代

```
收集数据 → 分析效果 → 调整模型 → A/B测试 → 上线
    ↑                                        │
    └────────────────────────────────────────┘
```

