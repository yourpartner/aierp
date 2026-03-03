<template>
  <div class="withholding-page">
    <el-card class="toolbar-card">
      <div class="toolbar">
        <div class="toolbar-left">
          <el-icon><Document /></el-icon>
          <span class="title">源泉徴収票作成（モック）</span>
          <el-tag type="info" size="small">デモ表示</el-tag>
        </div>
        <div class="toolbar-right">
          <el-select v-model="selectedYear" style="width: 120px">
            <el-option v-for="y in years" :key="y" :label="`${y}年`" :value="y" />
          </el-select>
          <el-select v-model="selectedEmployee" style="width: 220px">
            <el-option
              v-for="e in employees"
              :key="e.id"
              :label="`${e.name} (${e.employeeCode})`"
              :value="e.id"
            />
          </el-select>
          <el-button type="primary" disabled>
            <el-icon><Edit /></el-icon>
            作成（モック）
          </el-button>
          <el-button disabled>
            <el-icon><Download /></el-icon>
            PDF出力（モック）
          </el-button>
        </div>
      </div>
    </el-card>

    <el-card class="preview-card">
      <template #header>
        <div class="preview-header">
          <span>プレビュー</span>
          <el-tag size="small" type="warning">固定サンプルデータ</el-tag>
        </div>
      </template>

      <div class="slip-paper">
        <div class="paper-title">給与所得の源泉徴収票</div>

        <div class="paper-grid">
          <div class="box">
            <div class="box-label">支払を受ける者</div>
            <table class="slip-table">
              <tr>
                <th>氏名</th>
                <td>{{ slip.employee.name }}</td>
                <th>区分</th>
                <td>{{ slip.employee.category }}</td>
              </tr>
              <tr>
                <th>住所</th>
                <td colspan="3">{{ slip.employee.address }}</td>
              </tr>
              <tr>
                <th>個人番号</th>
                <td>{{ slip.employee.myNumberMasked }}</td>
                <th>受給者番号</th>
                <td>{{ slip.employee.employeeCode }}</td>
              </tr>
            </table>
          </div>

          <div class="box">
            <div class="box-label">支払者</div>
            <table class="slip-table">
              <tr>
                <th>名称</th>
                <td>{{ slip.company.name }}</td>
              </tr>
              <tr>
                <th>所在地</th>
                <td>{{ slip.company.address }}</td>
              </tr>
              <tr>
                <th>法人番号</th>
                <td>{{ slip.company.corporateNumberMasked }}</td>
              </tr>
            </table>
          </div>

          <div class="box box-wide">
            <div class="box-label">支払金額等</div>
            <table class="slip-table amount-table">
              <tr>
                <th>支払金額</th>
                <th>給与所得控除後の金額</th>
                <th>所得控除の額の合計額</th>
                <th>源泉徴収税額</th>
              </tr>
              <tr>
                <td>{{ yen(slip.amounts.paymentAmount) }}</td>
                <td>{{ yen(slip.amounts.salaryIncomeAfterDeduction) }}</td>
                <td>{{ yen(slip.amounts.totalIncomeDeductions) }}</td>
                <td>{{ yen(slip.amounts.withholdingTaxAmount) }}</td>
              </tr>
            </table>
          </div>

          <div class="box box-wide">
            <div class="box-label">控除等の内訳（主な項目）</div>
            <table class="slip-table">
              <tr>
                <th>社会保険料等の金額</th>
                <td>{{ yen(slip.deductions.socialInsurance) }}</td>
                <th>生命保険料の控除額</th>
                <td>{{ yen(slip.deductions.lifeInsurance) }}</td>
              </tr>
              <tr>
                <th>地震保険料の控除額</th>
                <td>{{ yen(slip.deductions.earthquakeInsurance) }}</td>
                <th>配偶者（特別）控除額</th>
                <td>{{ yen(slip.deductions.spouseDeduction) }}</td>
              </tr>
              <tr>
                <th>扶養控除の額の合計額</th>
                <td>{{ yen(slip.deductions.dependentDeduction) }}</td>
                <th>住宅借入金等特別控除の額</th>
                <td>{{ yen(slip.deductions.housingLoanDeduction) }}</td>
              </tr>
            </table>
          </div>

          <div class="box">
            <div class="box-label">扶養親族情報（サンプル）</div>
            <table class="slip-table">
              <tr>
                <th>配偶者の有無</th>
                <td>{{ slip.family.spouse }}</td>
              </tr>
              <tr>
                <th>扶養親族数</th>
                <td>{{ slip.family.dependentCount }}人</td>
              </tr>
              <tr>
                <th>16歳未満扶養親族</th>
                <td>{{ slip.family.under16Count }}人</td>
              </tr>
            </table>
          </div>

          <div class="box">
            <div class="box-label">作成情報</div>
            <table class="slip-table">
              <tr>
                <th>対象年</th>
                <td>{{ selectedYear }}年分</td>
              </tr>
              <tr>
                <th>作成日</th>
                <td>{{ slip.meta.createdAt }}</td>
              </tr>
              <tr>
                <th>作成者</th>
                <td>{{ slip.meta.createdBy }}</td>
              </tr>
            </table>
          </div>
        </div>
      </div>
    </el-card>
  </div>
</template>

<script setup lang="ts">
import { computed, ref } from 'vue'
import { Document, Download, Edit } from '@element-plus/icons-vue'

const years = [2025, 2024, 2023]
const selectedYear = ref(2025)
const selectedEmployee = ref('emp-001')

const employees = [
  { id: 'emp-001', employeeCode: 'E0001', name: '山田 太郎' },
  { id: 'emp-002', employeeCode: 'E0002', name: '佐藤 花子' },
  { id: 'emp-003', employeeCode: 'E0003', name: '鈴木 一郎' },
]

const sampleByEmployee: Record<string, any> = {
  'emp-001': {
    employee: {
      name: '山田 太郎',
      category: '甲欄',
      address: '東京都千代田区丸の内1-1-1',
      myNumberMasked: '***-****-1234',
      employeeCode: 'E0001',
    },
    company: {
      name: '株式会社サンプルテック',
      address: '東京都港区芝公園2-2-2',
      corporateNumberMasked: '***-****-5678',
    },
    amounts: {
      paymentAmount: 6840000,
      salaryIncomeAfterDeduction: 5040000,
      totalIncomeDeductions: 1824000,
      withholdingTaxAmount: 153200,
    },
    deductions: {
      socialInsurance: 968000,
      lifeInsurance: 80000,
      earthquakeInsurance: 30000,
      spouseDeduction: 380000,
      dependentDeduction: 330000,
      housingLoanDeduction: 120000,
    },
    family: {
      spouse: '有',
      dependentCount: 1,
      under16Count: 0,
    },
    meta: {
      createdAt: '2026-01-15',
      createdBy: '給与担当A',
    },
  },
  'emp-002': {
    employee: {
      name: '佐藤 花子',
      category: '甲欄',
      address: '神奈川県横浜市中区本町3-3-3',
      myNumberMasked: '***-****-2233',
      employeeCode: 'E0002',
    },
    company: {
      name: '株式会社サンプルテック',
      address: '東京都港区芝公園2-2-2',
      corporateNumberMasked: '***-****-5678',
    },
    amounts: {
      paymentAmount: 5980000,
      salaryIncomeAfterDeduction: 4380000,
      totalIncomeDeductions: 1640000,
      withholdingTaxAmount: 121000,
    },
    deductions: {
      socialInsurance: 892000,
      lifeInsurance: 70000,
      earthquakeInsurance: 12000,
      spouseDeduction: 0,
      dependentDeduction: 0,
      housingLoanDeduction: 0,
    },
    family: {
      spouse: '無',
      dependentCount: 0,
      under16Count: 0,
    },
    meta: {
      createdAt: '2026-01-15',
      createdBy: '給与担当A',
    },
  },
  'emp-003': {
    employee: {
      name: '鈴木 一郎',
      category: '乙欄',
      address: '埼玉県さいたま市大宮区桜木町4-4-4',
      myNumberMasked: '***-****-3344',
      employeeCode: 'E0003',
    },
    company: {
      name: '株式会社サンプルテック',
      address: '東京都港区芝公園2-2-2',
      corporateNumberMasked: '***-****-5678',
    },
    amounts: {
      paymentAmount: 3720000,
      salaryIncomeAfterDeduction: 2760000,
      totalIncomeDeductions: 1030000,
      withholdingTaxAmount: 90200,
    },
    deductions: {
      socialInsurance: 612000,
      lifeInsurance: 28000,
      earthquakeInsurance: 0,
      spouseDeduction: 0,
      dependentDeduction: 0,
      housingLoanDeduction: 0,
    },
    family: {
      spouse: '無',
      dependentCount: 0,
      under16Count: 0,
    },
    meta: {
      createdAt: '2026-01-15',
      createdBy: '給与担当A',
    },
  },
}

const slip = computed(() => sampleByEmployee[selectedEmployee.value] ?? sampleByEmployee['emp-001'])

function yen(v: number): string {
  return Number(v || 0).toLocaleString('ja-JP')
}
</script>

<style scoped>
.withholding-page { padding: 16px; background: #f5f7fa; min-height: 100%; }
.toolbar-card { margin-bottom: 12px; }
.toolbar { display: flex; justify-content: space-between; align-items: center; gap: 12px; flex-wrap: wrap; }
.toolbar-left { display: flex; align-items: center; gap: 8px; }
.title { font-size: 18px; font-weight: 600; color: #303133; }
.toolbar-right { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; }

.preview-header { display: flex; align-items: center; gap: 8px; }
.slip-paper {
  margin: 0 auto;
  max-width: 1100px;
  background: #fff;
  border: 1px solid #dcdfe6;
  padding: 18px;
  box-shadow: 0 1px 3px rgba(0,0,0,0.05);
}
.paper-title {
  text-align: center;
  font-size: 22px;
  font-weight: 700;
  margin-bottom: 14px;
  letter-spacing: 2px;
}
.paper-grid {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 10px;
}
.box { border: 1px solid #333; }
.box-wide { grid-column: 1 / -1; }
.box-label {
  background: #f2f2f2;
  border-bottom: 1px solid #333;
  padding: 6px 8px;
  font-size: 12px;
  font-weight: 700;
}
.slip-table {
  width: 100%;
  border-collapse: collapse;
  font-size: 12px;
}
.slip-table th, .slip-table td {
  border: 1px solid #555;
  padding: 6px 8px;
  vertical-align: middle;
}
.slip-table th {
  width: 28%;
  background: #fafafa;
  font-weight: 600;
  text-align: left;
}
.amount-table th, .amount-table td { text-align: right; }
.amount-table th:first-child, .amount-table td:first-child { text-align: right; }

@media (max-width: 900px) {
  .paper-grid { grid-template-columns: 1fr; }
  .box-wide { grid-column: auto; }
}
</style>
