// 测试发票 OCR 识别结果
// 使用方法: node test_invoice_ocr.js

import { readFileSync } from 'fs';
import { fileURLToPath } from 'url';
import { dirname, join } from 'path';

const __filename = fileURLToPath(import.meta.url);
const __dirname = dirname(__filename);

const OPENAI_API_KEY = process.env.OPENAI_API_KEY;

if (!OPENAI_API_KEY) {
  console.error('请设置 OPENAI_API_KEY 环境变量');
  process.exit(1);
}

// 发票图片路径
const imagePath = 'C:\\Users\\ryuho\\.cursor\\projects\\d-yanxia\\assets\\c__Users_ryuho_AppData_Roaming_Cursor_User_workspaceStorage_56f28be48163d20dfaaf1d42a5f6d53b_images_image-6770ecd0-3c6e-4985-b61b-5d5fd33f07bb.png';

// 与系统相同的提示词（日语版）- 已添加年号转换规则
const extractPrompt = `あなたは会計証憑の解析アシスタントです。ユーザーが提供する証憑（画像またはテキスト）に基づき、次の JSON を出力してください：
- documentType: ドキュメント種別（例: 'invoice'、'receipt'）
- category: 証憑カテゴリ。'dining'、'transportation'、'misc' のいずれかを必ず選択し、内容に基づいて判断すること（会食関連は 'dining'、交通費は 'transportation'、その他は 'misc'）。
- issueDate: 発行日または利用日（YYYY-MM-DD）
- partnerName: 取引先／支払先名
- totalAmount: 税込金額（数値）
- taxAmount: 税額（数値）
- currency: 通貨コード。既定は JPY
- taxRate: 税率（パーセンテージ、整数）
- items: 明細配列。各要素は description と amount を含む
- invoiceRegistrationNo: ^T\\d{13}$ に一致する番号があれば記載
- guestCount: 飲食の人数(証憑に2名様やX名等の記載があれば数値として抽出、なければ0)
- headerSummarySuggestion: 伝票ヘッダーに適したサマリー（例：「交通費 | 手段/会社名 | 起点→終点」「会議費 | 店名 | 用途」）。情報不足なら空文字
- lineMemoSuggestion: 主要仕訳行の簡潔なメモ（例：「タクシー料金 8/9 墨田→上野」）。情報不足なら空文字
- memo: その他の補足

【重要】和暦から西暦への変換ルール（必ず正確に変換すること）：
- 令和元年 = 2019年（令和N年 = 2018 + N 年、例：令和7年 = 2025年）
- 平成元年 = 1989年（平成N年 = 1988 + N 年）
- 昭和元年 = 1926年（昭和N年 = 1925 + N 年）

判別できない項目は空文字または 0 を返し、決して推測で値を作らないこと。category は必ず上記のいずれかを設定してください。`;

async function testInvoiceOCR() {
  try {
    // 读取图片并转为 base64
    const imageBuffer = readFileSync(imagePath);
    const base64Image = imageBuffer.toString('base64');
    
    console.log('=== 测试发票 OCR 识别 ===');
    console.log(`图片路径: ${imagePath}`);
    console.log(`图片大小: ${imageBuffer.length} bytes`);
    console.log('\n正在调用 GPT-4o...\n');

    const response = await fetch('https://api.openai.com/v1/chat/completions', {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'Authorization': `Bearer ${OPENAI_API_KEY}`
      },
      body: JSON.stringify({
        model: 'gpt-4o',
        temperature: 0.1,
        response_format: { type: 'json_object' },
        messages: [
          { role: 'system', content: extractPrompt },
          { 
            role: 'user', 
            content: [
              { 
                type: 'image_url', 
                image_url: { 
                  url: `data:image/png;base64,${base64Image}` 
                } 
              }
            ] 
          }
        ]
      })
    });

    if (!response.ok) {
      const errorText = await response.text();
      console.error('API 调用失败:', response.status, errorText);
      return;
    }

    const data = await response.json();
    const content = data.choices[0].message.content;
    
    console.log('=== GPT-4o 返回的原始 JSON ===');
    console.log(content);
    
    try {
      const parsed = JSON.parse(content);
      console.log('\n=== 解析后的关键字段 ===');
      console.log(`发行日期 (issueDate): ${parsed.issueDate}`);
      console.log(`供应商名称 (partnerName): ${parsed.partnerName}`);
      console.log(`总金额 (totalAmount): ${parsed.totalAmount}`);
      console.log(`税额 (taxAmount): ${parsed.taxAmount}`);
      console.log(`文档类型 (documentType): ${parsed.documentType}`);
      console.log(`类别 (category): ${parsed.category}`);
      console.log(`备注 (memo): ${parsed.memo}`);
      
      // 验证日期
      if (parsed.issueDate) {
        const year = parseInt(parsed.issueDate.split('-')[0]);
        console.log(`\n=== 日期验证 ===`);
        console.log(`识别的年份: ${year}`);
        if (year < 2000 || year > 2030) {
          console.log(`⚠️ 警告: 年份 ${year} 不在合理范围内 (2000-2030)`);
          console.log(`发票上显示 "令和7年12月18日" 应该是 2025-12-18`);
        } else {
          console.log(`✓ 年份在合理范围内`);
        }
      }
    } catch (e) {
      console.error('JSON 解析失败:', e.message);
    }

  } catch (error) {
    console.error('测试失败:', error);
  }
}

testInvoiceOCR();
