// 导入 zengin-code 开源数据到 banks/branches
// 运行：node web/scripts/import-zengin.js
import axios from 'axios'
import { createRequire } from 'module'
const require = createRequire(import.meta.url)

const BACKEND = process.env.BACKEND_BASE || 'http://localhost:5179'
const COMPANY = process.env.COMPANY_CODE || 'JP01'

function fetchZenginLocal(){
  let banks, branches
  try { banks = require('zengin-code/dist/banks.json') } catch {}
  try { branches = require('zengin-code/dist/branches.json') } catch {}
  if (!banks) banks = require('zengin-code/banks.json')
  if (!branches) branches = require('zengin-code/branches.json')
  return { banks, branches }
}

async function main(){
  const api = axios.create({ baseURL: BACKEND })
  api.defaults.headers.common['x-company-code'] = COMPANY
  const { banks, branches } = fetchZenginLocal()
  console.log('banks:', Object.keys(banks).length, 'branches:', Object.keys(branches).length)

  // 写入银行
  for (const code of Object.keys(banks)){
    const b = banks[code]
    const payload = { bankCode: code, name: b.name, nameKana: b.kana }
    await api.post('/objects/bank', { payload })
  }
  console.log('banks imported')

  // 写入支店（量较大，按银行批次）
  for (const bankCode of Object.keys(branches)){
    const map = branches[bankCode]
    for (const brCode of Object.keys(map)){
      const br = map[brCode]
      const payload = { bankCode, branchCode: brCode, branchName: br.name, branchKana: br.kana }
      await api.post('/objects/branch', { payload })
    }
  }
  console.log('branches imported')
}

main().catch(e => console.error(e?.response?.data || e))


