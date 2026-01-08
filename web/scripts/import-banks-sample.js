// 导入少量示例银行/支店数据，便于本地联调
// 运行：node web/scripts/import-banks-sample.js
import axios from 'axios'

const BACKEND = process.env.BACKEND_BASE || 'http://localhost:5179'
const COMPANY = process.env.COMPANY_CODE || 'JP01'

const sampleBanks = [
  { bankCode:'0005', name:'三菱UFJ銀行', nameKana:'ミツビシUFJ' },
  { bankCode:'0001', name:'みずほ銀行', nameKana:'ミズホ' },
  { bankCode:'0009', name:'三井住友銀行', nameKana:'ミツイスミトモ' }
]

const sampleBranches = [
  // 三菱UFJ
  { bankCode:'0005', branchCode:'001', branchName:'丸之内支店', branchKana:'マルノウチ' },
  { bankCode:'0005', branchCode:'008', branchName:'新宿中央支店', branchKana:'シンジュクチュウオウ' },
  // みずほ
  { bankCode:'0001', branchCode:'101', branchName:'日本橋支店', branchKana:'ニホンバシ' },
  { bankCode:'0001', branchCode:'114', branchName:'渋谷支店', branchKana:'シブヤ' },
  // 三井住友
  { bankCode:'0009', branchCode:'212', branchName:'大阪本店営業部', branchKana:'オオサカホンテン' },
  { bankCode:'0009', branchCode:'234', branchName:'品川支店', branchKana:'シナガワ' }
]

async function main(){
  const api = axios.create({ baseURL: BACKEND })
  api.defaults.headers.common['x-company-code'] = COMPANY
  for (const b of sampleBanks) {
    await api.post('/objects/bank', { payload: b })
  }
  for (const br of sampleBranches) {
    await api.post('/objects/branch', { payload: br })
  }
  console.log('sample banks/branches imported')
}

main().catch(e => console.error(e?.response?.data || e))


