<template>
  <div class="assets-list">
    <el-card class="assets-card">
      <template #header>
        <div class="assets-header">
          <div class="assets-header__left">
            <el-icon class="assets-header__icon"><Box /></el-icon>
            <span class="assets-header__title">固定資産</span>
            <el-tag size="small" type="info" class="assets-header__count">{{ rows.length }}件</el-tag>
          </div>
          <div class="assets-header__right">
            <el-tooltip content="新規購入資産を会計仕訳と同時に登録" placement="bottom">
              <el-button type="success" @click="openAcquisitionDialog">
                <el-icon><Coin /></el-icon>
                <span>資産取得</span>
              </el-button>
            </el-tooltip>
            <el-tooltip content="既存システムからの移行データを登録" placement="bottom">
              <el-button type="primary" @click="openCreateDialog">
                <el-icon><Plus /></el-icon>
                <span>過去資産導入</span>
              </el-button>
            </el-tooltip>
          </div>
        </div>
      </template>

      <!-- 検索フィルター -->
      <div class="assets-filters">
        <el-select v-model="filters.assetClassId" placeholder="資産クラス" clearable class="assets-filters__class" @change="load">
          <el-option v-for="ac in assetClasses" :key="ac.id" :label="ac.class_name" :value="ac.id" />
        </el-select>
        <el-input v-model="filters.assetNo" placeholder="資産番号" clearable class="assets-filters__no" @keyup.enter="load" />
        <el-input v-model="filters.assetName" placeholder="資産名称" clearable class="assets-filters__name" @keyup.enter="load">
          <template #prefix>
            <el-icon><Search /></el-icon>
          </template>
        </el-input>
        <el-button type="primary" plain @click="load">
          <el-icon><Search /></el-icon>
          検索
        </el-button>
      </div>

      <el-table :data="rows" border stripe highlight-current-row class="assets-table" v-loading="loading">
        <el-table-column label="資産番号" prop="asset_no" width="100" />
        <el-table-column label="資産クラス" min-width="140">
          <template #default="{ row }">
            {{ row.asset_class_name || getAssetClassName(row.asset_class_id) }}
          </template>
        </el-table-column>
        <el-table-column label="部門" min-width="120">
          <template #default="{ row }">
            {{ getDepartmentName(row.department_id) }}
          </template>
        </el-table-column>
        <el-table-column label="資産名称" prop="asset_name" min-width="200" />
        <el-table-column label="資本化日付" width="120">
          <template #default="{ row }">
            {{ row.capitalization_date }}
          </template>
        </el-table-column>
        <el-table-column label="償却開始日" width="120">
          <template #default="{ row }">
            {{ row.depreciation_start_date }}
          </template>
        </el-table-column>
        <el-table-column label="耐用年数" width="100">
          <template #default="{ row }">
            {{ row.useful_life }}
          </template>
        </el-table-column>
        <el-table-column label="償却方法" width="100">
          <template #default="{ row }">
            {{ row.depreciation_method === 'DECLINING_BALANCE' ? '定率法' : '定額法' }}
          </template>
        </el-table-column>
        <el-table-column label="取得価額" width="120" align="right">
          <template #default="{ row }">
            {{ formatNumber(row.acquisition_cost) }}
          </template>
        </el-table-column>
        <el-table-column label="帳簿価額" width="120" align="right">
          <template #default="{ row }">
            {{ formatNumber(row.book_value) }}
          </template>
        </el-table-column>
        <el-table-column label="備考" min-width="180">
          <template #default="{ row }">
            {{ row.payload?.remarks || '' }}
          </template>
        </el-table-column>
        <el-table-column label="アクション" width="180" fixed="right">
          <template #default="{ row }">
            <el-tooltip content="詳細" placement="top">
              <el-button size="small" type="info" circle @click="openViewDialog(row)">
                <el-icon><View /></el-icon>
              </el-button>
            </el-tooltip>
            <el-tooltip content="資産取得" placement="top">
              <el-button size="small" type="success" circle @click="openAcquisitionForAsset(row)" :disabled="hasAcquisitionTransaction(row)">
                <el-icon><Coin /></el-icon>
              </el-button>
            </el-tooltip>
            <el-tooltip content="編集" placement="top">
              <el-button size="small" type="primary" circle @click="openEditDialog(row)">
                <el-icon><Edit /></el-icon>
              </el-button>
            </el-tooltip>
            <el-tooltip content="削除" placement="top">
              <el-button size="small" type="danger" circle @click="confirmDelete(row)">
                <el-icon><Delete /></el-icon>
              </el-button>
            </el-tooltip>
          </template>
        </el-table-column>
      </el-table>
      <!-- ページネーション -->
      <div class="assets-pagination" v-if="rows.length > 0">
        <span class="assets-pagination__info">全 {{ rows.length }} 件の資産</span>
      </div>
    </el-card>

    <!-- 新建/编辑弹窗 -->
    <el-dialog v-model="showDialog" :title="dialogTitle" width="860px" destroy-on-close top="5vh">
      <div class="dialog-content asset-dialog">
        <h4 class="section-title">資産マスタ</h4>
        
        <!-- 新規登録時说明 -->
        <el-alert v-if="!isEdit" type="info" :closable="false" style="margin-bottom: 16px;">
          初期データ導入用です。既存システムから移行する資産データを登録できます。<br/>
          新規購入資産は「資産取得」ボタンから会計仕訳と同時に登録してください。
        </el-alert>
        
        <el-form :model="form" label-width="100px" class="asset-form">
          <el-row :gutter="20">
            <el-col :span="8" v-if="isEdit">
              <el-form-item label="資産番号">
                <el-input v-model="form.assetNo" disabled />
              </el-form-item>
            </el-col>
            <el-col :span="isEdit ? 16 : 24">
              <el-form-item label="資産名称" required>
                <el-input v-model="form.assetName" placeholder="資産名称を入力" />
              </el-form-item>
            </el-col>
          </el-row>
          <el-row :gutter="20">
            <el-col :span="8">
              <el-form-item label="資産クラス" required>
                <el-select v-model="form.assetClassId" placeholder="選択" style="width: 100%" :disabled="isEdit" @change="onFormClassChange">
                  <el-option v-for="ac in assetClasses" :key="ac.id" :label="ac.class_name" :value="ac.id" />
                </el-select>
              </el-form-item>
            </el-col>
            <el-col :span="8">
              <el-form-item label="償却方法" required>
                <el-select v-model="form.depreciationMethod" style="width: 100%">
                  <el-option label="定額法" value="STRAIGHT_LINE" />
                  <el-option label="定率法" value="DECLINING_BALANCE" />
                </el-select>
              </el-form-item>
            </el-col>
            <el-col :span="8">
              <el-form-item label="耐用年数" required class="useful-life-item">
                <div class="useful-life-input">
                  <el-input-number v-model="form.usefulLife" :min="1" :max="100" :controls="true" />
                  <span class="unit">年</span>
                </div>
              </el-form-item>
            </el-col>
          </el-row>
          
          <!-- 金额和日期字段：新規時可编辑，編集時显示但禁用 -->
          <el-row :gutter="20">
            <el-col :span="8">
              <el-form-item label="取得価額" :required="!isEdit">
                <el-input :model-value="formatNumberInput(form.acquisitionCost)" @input="v => form.acquisitionCost = parseNumberInput(v)" :disabled="isEdit" placeholder="取得価額" />
              </el-form-item>
            </el-col>
            <el-col :span="8">
              <el-form-item label="帳簿価額" :required="!isEdit">
                <el-input :model-value="formatNumberInput(form.bookValue)" @input="v => form.bookValue = parseNumberInput(v)" :disabled="isEdit" placeholder="帳簿価額" />
              </el-form-item>
            </el-col>
            <el-col :span="8">
              <el-form-item label="資本化日付" :required="!isEdit">
                <el-date-picker v-model="form.capitalizationDate" type="date" value-format="YYYY-MM-DD" style="width: 100%" :disabled="isEdit" placeholder="資本化日付" @change="onCapitalizationDateChange" />
              </el-form-item>
            </el-col>
          </el-row>
          <el-row :gutter="20">
            <el-col :span="8">
              <el-form-item label="償却開始日" :required="!isEdit">
                <el-date-picker v-model="form.depreciationStartDate" type="date" value-format="YYYY-MM-DD" style="width: 100%" :disabled="isEdit" placeholder="償却開始日" />
              </el-form-item>
            </el-col>
            <el-col :span="8">
              <el-form-item label="部門" required>
                <el-select v-model="form.departmentId" placeholder="部門を選択" style="width: 100%" filterable>
                  <el-option v-for="opt in departmentOptions" :key="opt.value" :label="opt.label" :value="opt.value" />
                </el-select>
              </el-form-item>
            </el-col>
            <el-col :span="8">
              <el-form-item label="備考">
                <el-input v-model="form.remarks" placeholder="備考" />
              </el-form-item>
            </el-col>
          </el-row>
        </el-form>

        <!-- 交易明细（仅在编辑模式显示） -->
        <template v-if="isEdit && (transactions.length > 0 || pendingTransactions.length > 0)">
          <h4 class="section-title" style="margin-top: 20px;">資産トランザクション</h4>
          <el-table :data="allTransactions" size="small" stripe :row-class-name="getRowClassName" class="tx-table">
            <el-table-column label="状態" width="70">
              <template #default="{ row }">
                <el-tag v-if="row.isPending" type="info" size="small">予定</el-tag>
                <el-tag v-else type="success" size="small">記帳済</el-tag>
              </template>
            </el-table-column>
            <el-table-column label="タイプ" width="70">
              <template #default="{ row }">
                {{ formatTransactionType(row.transaction_type || row.transactionType) }}
              </template>
            </el-table-column>
            <el-table-column label="転記日付" width="100">
              <template #default="{ row }">
                {{ row.posting_date || row.postingDate }}
              </template>
            </el-table-column>
            <el-table-column label="金額" width="110" align="right">
              <template #default="{ row }">
                {{ formatNumber(row.amount) }}
              </template>
            </el-table-column>
            <el-table-column label="伝票番号" width="130">
              <template #default="{ row }">
                <el-link v-if="getVoucherNo(row)" type="primary" @click="openVoucherDialogByRow(row)">
                  {{ getVoucherNo(row) }}
                </el-link>
                <span v-else-if="row.isPending" class="pending-text">-</span>
              </template>
            </el-table-column>
            <el-table-column label="備考" min-width="140">
              <template #default="{ row }">
                {{ row.payload?.note || row.note || '' }}
              </template>
            </el-table-column>
            <el-table-column label="操作" width="60" align="center">
              <template #default="{ row }">
                <el-button 
                  v-if="!row.isPending && row.transaction_type === 'ACQUISITION'" 
                  size="small" 
                  type="danger" 
                  circle 
                  @click="confirmDeleteTransaction(row)"
                >
                  <el-icon><Delete /></el-icon>
                </el-button>
              </template>
            </el-table-column>
          </el-table>
        </template>
      </div>
      <template #footer>
        <el-button @click="showDialog = false">キャンセル</el-button>
        <el-tooltip v-if="isEdit" content="資産除却（会計仕訳を作成し、帳簿価額を0にします）" placement="top">
          <el-button type="danger" @click="openDisposalDialog" :disabled="isDisposed" :loading="disposalSaving">除却</el-button>
        </el-tooltip>
        <el-button type="primary" @click="saveForm" :loading="saving">保存</el-button>
      </template>
    </el-dialog>

    <!-- 凭证查看弹窗 -->
    <el-dialog v-model="showVoucherDialog" width="auto" append-to-body destroy-on-close class="voucher-detail-dialog">
      <template #header></template>
      <div class="voucher-dialog-card-wrap">
        <VouchersList 
          v-if="showVoucherDialog" 
          ref="voucherDetailRef" 
          class="voucher-detail-embed" 
          :allow-edit="false"
          :initial-voucher-id="selectedVoucherId || undefined"
          :initial-voucher-no="selectedVoucherNo || undefined"
        />
      </div>
    </el-dialog>

    <!-- 资产取得对话框 -->
    <el-dialog v-model="showAcquisitionDialog" :title="isNewAssetAcquisition ? '資産取得（新規）' : '資産取得（資本化）'" width="1050px" destroy-on-close top="5vh" class="acquisition-dialog">
      <div class="acquisition-content">
        <el-alert type="info" :closable="false" class="acquisition-alert">
          {{ isNewAssetAcquisition ? '資産取得を行うと、資産マスタと取得仕訳が同時に作成されます。' : '既存資産の資本化を行います。取得仕訳のみ作成されます。' }}
        </el-alert>
        
        <!-- 资产信息区域 -->
        <div class="acquisition-section">
          <h4 class="section-title">資産情報</h4>
          <el-form :model="acquisitionForm" label-width="70px" class="acquisition-form" label-position="left">
            <el-row :gutter="16">
              <el-col :span="5" v-if="!isNewAssetAcquisition">
                <el-form-item label="資産番号">
                  <el-input v-model="acquisitionForm.assetNo" disabled />
                </el-form-item>
              </el-col>
              <el-col :span="isNewAssetAcquisition ? 5 : 5">
                <el-form-item label="クラス" :required="isNewAssetAcquisition">
                  <el-select v-model="acquisitionForm.assetClassId" placeholder="選択" style="width: 100%" @change="onAcquisitionClassChange" :disabled="!isNewAssetAcquisition">
                    <el-option v-for="ac in assetClasses" :key="ac.id" :label="ac.class_name" :value="ac.id" />
                  </el-select>
                </el-form-item>
              </el-col>
              <el-col :span="isNewAssetAcquisition ? 9 : 5">
                <el-form-item label="資産名称" :required="isNewAssetAcquisition">
                  <el-input v-model="acquisitionForm.assetName" placeholder="資産名称を入力" :disabled="!isNewAssetAcquisition" />
                </el-form-item>
              </el-col>
              <el-col :span="5">
                <el-form-item label="償却方法" :required="isNewAssetAcquisition">
                  <el-select v-model="acquisitionForm.depreciationMethod" style="width: 100%" :disabled="!isNewAssetAcquisition">
                    <el-option label="定額法" value="STRAIGHT_LINE" />
                    <el-option label="定率法" value="DECLINING_BALANCE" />
                  </el-select>
                </el-form-item>
              </el-col>
              <el-col :span="5">
                <el-form-item label="耐用年数" :required="isNewAssetAcquisition" class="useful-life-item nowrap-label">
                  <div class="useful-life-input">
                    <el-input v-model.number="acquisitionForm.usefulLife" type="number" :min="1" :max="100" :disabled="!isNewAssetAcquisition" style="width: 80px" />
                    <span class="unit">年</span>
                  </div>
                </el-form-item>
              </el-col>
            </el-row>
            <el-row :gutter="16">
              <el-col :span="5">
                <el-form-item label="取得日" required>
                  <el-date-picker v-model="acquisitionForm.acquisitionDate" type="date" value-format="YYYY-MM-DD" style="width: 100%" />
                </el-form-item>
              </el-col>
              <el-col :span="5">
                <el-form-item label="部門" required>
                  <el-select v-model="acquisitionForm.departmentId" placeholder="部門を選択" style="width: 100%" filterable :disabled="!isNewAssetAcquisition">
                    <el-option v-for="opt in departmentOptions" :key="opt.value" :label="opt.label" :value="opt.value" />
                  </el-select>
                </el-form-item>
              </el-col>
              <el-col :span="14">
                <el-form-item label="備考">
                  <el-input v-model="acquisitionForm.remarks" placeholder="備考" :disabled="!isNewAssetAcquisition" />
                </el-form-item>
              </el-col>
            </el-row>
          </el-form>
        </div>

        <!-- 取得仕訳区域 -->
        <div class="acquisition-section voucher-section">
          <h4 class="section-title">取得仕訳</h4>
          <el-table :data="acquisitionForm.voucherLines" border size="small" style="width: 100%">
            <el-table-column label="#" width="36" type="index" />
            <el-table-column label="勘定科目" width="160">
              <template #default="{ row }">
                <el-select v-model="row.accountCode" filterable remote reserve-keyword :remote-method="searchAccounts" :loading="loadingAccounts" style="width: 100%" placeholder="科目" size="small">
                  <el-option v-for="opt in accountOptions" :key="opt.value" :label="opt.label" :value="opt.value" />
                </el-select>
              </template>
            </el-table-column>
            <el-table-column label="借貸" width="80">
              <template #default="{ row }">
                <el-select v-model="row.drcr" style="width: 100%" size="small">
                  <el-option label="借方" value="DR" />
                  <el-option label="貸方" value="CR" />
                </el-select>
              </template>
            </el-table-column>
            <el-table-column label="金額" width="120">
              <template #default="{ row }">
                <el-input 
                  type="text" 
                  inputmode="decimal"
                  :model-value="formatAmountInput(row.amount)"
                  @input="onAmountInput(row, $event)"
                  placeholder="金額"
                  size="small"
                />
              </template>
            </el-table-column>
            <el-table-column label="税率" width="70" v-if="hasAnyTaxField">
              <template #default="{ row }">
                <el-input-number v-if="shouldShowTax(row)" v-model="row.taxRate" :min="0" :max="100" :step="1" :precision="0" size="small" :controls="false" style="width: 100%" />
              </template>
            </el-table-column>
            <el-table-column label="仕入先" width="140">
              <template #default="{ row }">
                <el-select v-if="isVisible(row, 'vendorId')" v-model="row.vendorId" :class="{ req: isRequired(row, 'vendorId') }" filterable remote reserve-keyword :remote-method="searchVendors" :loading="loadingVendors" clearable style="width: 100%" placeholder="仕入先" size="small">
                  <el-option v-for="v in vendorOptions" :key="v.value" :label="v.label" :value="v.value" />
                </el-select>
              </template>
            </el-table-column>
            <el-table-column label="固定資産" width="130">
              <template #default="{ row, $index: idx }">
                <!-- 新建资产模式：第一行（借方资产科目行）显示只读文本框，与资产名称同步 -->
                <el-input 
                  v-if="isNewAssetAcquisition && idx === 0 && row.drcr === 'DR'" 
                  :model-value="acquisitionForm.assetName || '（新規）'" 
                  disabled 
                  size="small"
                  placeholder="（新規）"
                />
                <!-- 已有资产模式：显示下拉选择框 -->
                <el-select v-else-if="isAssetAccount(row)" v-model="row.assetId" filterable remote reserve-keyword :remote-method="searchAssets" :loading="loadingAssets" clearable style="width: 100%" placeholder="資産" size="small">
                  <el-option v-for="a in assetOptions" :key="a.value" :label="a.label" :value="a.value" />
                </el-select>
              </template>
            </el-table-column>
            <el-table-column label="支払日" width="120">
              <template #default="{ row }">
                <el-date-picker v-if="isVisible(row, 'paymentDate')" v-model="row.paymentDate" type="date" value-format="YYYY-MM-DD" style="width: 100%" size="small" />
              </template>
            </el-table-column>
            <el-table-column label="摘要" min-width="120">
              <template #default="{ row }">
                <el-input v-model="row.note" placeholder="摘要" size="small" />
              </template>
            </el-table-column>
            <el-table-column label="" width="60" fixed="right">
              <template #default="{ $index }">
                <el-button size="small" type="danger" text @click="removeAcquisitionLine($index)" :disabled="acquisitionForm.voucherLines.length <= 2">削除</el-button>
              </template>
            </el-table-column>
          </el-table>

          <div class="line-actions">
            <el-button size="small" type="primary" @click="addAcquisitionLine">行追加</el-button>
          </div>

          <div class="voucher-totals" :class="{ warn: sumDebit !== sumCredit }">
            <span>借方合計: {{ formatNumber(sumDebit) }}</span>
            <span>貸方合計: {{ formatNumber(sumCredit) }}</span>
            <span v-if="sumDebit !== sumCredit" class="imbalance">（借貸不一致）</span>
          </div>
        </div>
      </div>
      <template #footer>
        <el-button @click="showAcquisitionDialog = false">キャンセル</el-button>
        <el-button type="primary" @click="submitAcquisition" :loading="acquisitionSaving" :disabled="sumDebit !== sumCredit || sumDebit === 0">取得実行</el-button>
      </template>
    </el-dialog>

    <!-- 资产除却对话框（方案3：科目/分录由系统自动决定，仅确认） -->
    <el-dialog v-model="showDisposalDialog" title="資産除却" width="860px" destroy-on-close top="5vh">
      <div class="dialog-content asset-dialog">
        <el-alert type="warning" :closable="false" style="margin-bottom: 16px;">
          除却を実行すると、会計仕訳が自動作成され、帳簿価額が0になります。（科目は資産クラス設定から自動決定）
        </el-alert>

        <el-form :model="disposalForm" label-width="80px">
          <el-row :gutter="16">
            <el-col :span="8">
              <el-form-item label="除却日" required>
                <el-date-picker v-model="disposalForm.disposalDate" type="date" value-format="YYYY-MM-DD" style="width: 100%" />
              </el-form-item>
            </el-col>
            <el-col :span="16">
              <el-form-item label="摘要">
                <el-input v-model="disposalForm.note" placeholder="摘要（任意）" />
              </el-form-item>
            </el-col>
          </el-row>
        </el-form>

        <h4 class="section-title" style="margin-top: 12px;">自動仕訳プレビュー</h4>
        <el-table :data="disposalPreviewLines" size="small" stripe style="width: 100%">
          <el-table-column label="借貸" width="80">
            <template #default="{ row }">{{ row.drcr === 'DR' ? '借方' : '貸方' }}</template>
          </el-table-column>
          <el-table-column label="勘定科目" min-width="160" prop="accountCode" />
          <el-table-column label="金額" width="120" align="right">
            <template #default="{ row }">{{ formatNumber(row.amount) }}</template>
          </el-table-column>
          <el-table-column label="摘要" min-width="180" prop="note" />
        </el-table>
      </div>
      <template #footer>
        <el-button @click="showDisposalDialog = false">キャンセル</el-button>
        <el-button type="primary" @click="submitDisposal" :loading="disposalSaving" :disabled="!disposalForm.disposalDate || disposalPreviewLines.length === 0">除却実行</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { ref, onMounted, reactive, computed, nextTick } from 'vue'
import api from '../api'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Edit, Delete, View, Plus, Minus, Coin, Box, Search } from '@element-plus/icons-vue'
import VouchersList from './VouchersList.vue'

const rows = ref<any[]>([])
const assetClasses = ref<any[]>([])
const loading = ref(false)
const showDialog = ref(false)
const isEdit = ref(false)
const editId = ref<string | null>(null)
const saving = ref(false)

// 弹窗标题
const dialogTitle = computed(() => {
  if (isEdit.value) return '資産編集'
  return '過去資産導入'
})
const transactions = ref<any[]>([])
const pendingTransactions = ref<any[]>([])
const showVoucherDialog = ref(false)
const voucherDetailRef = ref<InstanceType<typeof VouchersList> | null>(null)
const selectedVoucherId = ref<string>('')
const selectedVoucherNo = ref<string>('')

// 资产取得相关
const showAcquisitionDialog = ref(false)
const acquisitionSaving = ref(false)
const accountOptions = ref<{ label: string; value: string }[]>([])
const vendorOptions = ref<{ label: string; value: string }[]>([])
const assetOptions = ref<{ label: string; value: string }[]>([])
const loadingAccounts = ref(false)
const loadingVendors = ref(false)
const loadingAssets = ref(false)
const existingAssetId = ref<string | null>(null) // 针对已有资产的取得
const isNewAssetAcquisition = ref(true) // true=新建资产+取得, false=已有资产取得

// 资产除却相关
const showDisposalDialog = ref(false)
const disposalSaving = ref(false)
const disposalForm = reactive({
  disposalDate: '',
  note: '',
})

// 科目规则缓存
const codeToRules = new Map<string, any>()

// 根据资产类别（有形/无形）和取得日计算折旧开始日
function calculateDepreciationStartDate(acquisitionDate: string, isTangible: boolean): string {
  if (!acquisitionDate) return ''
  const date = new Date(acquisitionDate)
  if (isTangible) {
    // 有形资产：翌月1日
    date.setMonth(date.getMonth() + 1)
    date.setDate(1)
  } else {
    // 无形资产：当月1日
    date.setDate(1)
  }
  return date.toISOString().slice(0, 10)
}

// 获取当前选择的资产类别是否为有形资产
function isSelectedClassTangible(): boolean {
  if (!acquisitionForm.assetClassId) return true // 默认有形
  const ac = assetClasses.value.find(x => x.id === acquisitionForm.assetClassId)
  if (!ac) return true
  // 检查 payload.isTangible 或 asset_type
  if (ac.payload?.isTangible !== undefined) return ac.payload.isTangible
  return ac.asset_type !== 'INTANGIBLE'
}

const acquisitionForm = reactive({
  assetClassId: '',
  assetName: '',
  assetNo: '',
  departmentId: '',
  depreciationMethod: 'STRAIGHT_LINE',
  usefulLife: 5,
  acquisitionDate: '',
  acquisitionCost: 0,
  remarks: '',
  voucherLines: [
    { drcr: 'DR', accountCode: '', amount: '', taxRate: 10, vendorId: '', assetId: '', paymentDate: '', note: '' },
    { drcr: 'CR', accountCode: '', amount: '', taxRate: 10, vendorId: '', assetId: '', paymentDate: '', note: '' }
  ] as any[]
})

// 合并已记账和未记账的交易，按日期排序
const allTransactions = computed(() => {
  const posted = transactions.value.map(tx => ({ ...tx, isPending: false }))
  const pending = pendingTransactions.value.map(tx => ({ ...tx, isPending: true }))
  return [...posted, ...pending].sort((a, b) => {
    const dateA = a.posting_date || a.postingDate || ''
    const dateB = b.posting_date || b.postingDate || ''
    return dateA.localeCompare(dateB)
  })
})

function getRowClassName({ row }: { row: any }) {
  return row.isPending ? 'pending-row' : ''
}

const sumDebit = computed(() => {
  return acquisitionForm.voucherLines
    .filter(l => l.drcr === 'DR')
    .reduce((acc, l) => acc + (Number(l.amount) || 0), 0)
})

const sumCredit = computed(() => {
  return acquisitionForm.voucherLines
    .filter(l => l.drcr === 'CR')
    .reduce((acc, l) => acc + (Number(l.amount) || 0), 0)
})

// 是否有任何行需要显示税率字段
const hasAnyTaxField = computed(() => {
  return acquisitionForm.voucherLines.some(line => shouldShowTax(line))
})

const disposalPreviewLines = computed(() => {
  const classId = form.assetClassId
  const ac = assetClasses.value.find(x => x.id === classId)
  const acquisitionAccount = ac?.payload?.acquisitionAccount || ac?.acquisition_account || ''
  const accumulatedDepAccount = ac?.payload?.accumulatedDepreciationAccount || ac?.accumulated_depreciation_account || ''
  const disposalAccount = ac?.payload?.disposalAccount || ac?.disposal_account || ''

  const acqCost = Number(String(form.acquisitionCost || '').replace(/,/g, '')) || 0
  const bookValue = Number(String(form.bookValue || '').replace(/,/g, '')) || 0
  const accDep = Math.max(acqCost - bookValue, 0)
  const note = (disposalForm.note || `資産除却「${form.assetNo} ${form.assetName}」`).trim()

  const lines: any[] = []
  if (accumulatedDepAccount && accDep > 0) {
    lines.push({ drcr: 'DR', accountCode: accumulatedDepAccount, amount: accDep, note })
  }
  if (disposalAccount && bookValue > 0) {
    lines.push({ drcr: 'DR', accountCode: disposalAccount, amount: bookValue, note })
  }
  if (acquisitionAccount && acqCost > 0) {
    lines.push({ drcr: 'CR', accountCode: acquisitionAccount, amount: acqCost, note })
  }
  return lines
})

const isDisposed = computed(() => {
  // 1) 明细交易中已有除却
  const hasTx = transactions.value.some(t => (t.transaction_type || t.transactionType) === 'DISPOSAL')
  if (hasTx) return true
  // 2) 帐簿価額为 0（或更小）
  const bv = Number(String(form.bookValue || '').replace(/,/g, '')) || 0
  return bv <= 0
})

const filters = reactive({
  assetClassId: '',
  assetNo: '',
  assetName: ''
})

const form = reactive({
  assetNo: '',
  assetName: '',
  assetClassId: '',
  departmentId: '',
  depreciationMethod: 'STRAIGHT_LINE',
  usefulLife: 5,
  acquisitionCost: '',
  bookValue: '',
  capitalizationDate: '',
  depreciationStartDate: '',
  remarks: ''
})

// 部门选项
const departmentOptions = ref<{ label: string; value: string }[]>([])

function resetForm() {
  form.assetNo = ''
  form.assetName = ''
  form.assetClassId = ''
  form.departmentId = ''
  form.depreciationMethod = 'STRAIGHT_LINE'
  form.usefulLife = 5
  form.acquisitionCost = ''
  form.bookValue = ''
  form.capitalizationDate = ''
  form.depreciationStartDate = ''
  form.remarks = ''
  editId.value = null
  transactions.value = []
  pendingTransactions.value = []
}

// 加载部门列表
async function loadDepartments() {
  try {
    const res = await api.post('/objects/department/search', {
      page: 1,
      pageSize: 200,
      where: [],
      orderBy: [{ field: 'level', dir: 'ASC' }, { field: 'order', dir: 'ASC' }, { field: 'department_code', dir: 'ASC' }]
    })
    const depts = res.data?.data || []
    departmentOptions.value = depts.map((d: any) => ({
      label: `${d.name || d.payload?.name || ''} (${d.department_code || ''})`,
      value: d.id
    }))
  } catch (e) {
    console.error('Failed to load departments:', e)
  }
}

function formatNumber(value: any): string {
  if (value == null || value === '') return ''
  const num = Number(value)
  if (isNaN(num)) return String(value)
  return num.toLocaleString('ja-JP')
}

// 格式化输入框中的金额（带千分符）
function formatNumberInput(value: any): string {
  if (value == null || value === '') return ''
  const num = Number(String(value).replace(/,/g, ''))
  if (isNaN(num)) return String(value)
  return num.toLocaleString('ja-JP')
}

// 解析输入框中的金额（去除千分符）
function parseNumberInput(value: string): string {
  return value.replace(/,/g, '')
}

// 金额输入格式化（千分位显示）
function formatAmountInput(value: any): string {
  if (value == null || value === '') return ''
  const num = Number(String(value).replace(/,/g, ''))
  if (isNaN(num)) return String(value)
  return num.toLocaleString('ja-JP')
}

// 金额输入处理
function onAmountInput(row: any, val: string) {
  // 移除非数字字符（保留负号和小数点）
  const cleaned = String(val).replace(/[^\d.-]/g, '')
  row.amount = cleaned
}

// 科目规则相关方法
function getRules(code: string) {
  return codeToRules.get(code) || null
}

function getTaxType(code: string) {
  const rules = getRules(code)
  return rules?.taxType || 'NON_TAX'
}

function needsTax(row: any) {
  const type = row?.taxType || getTaxType(row?.accountCode || '')
  return type === 'INPUT_TAX' || type === 'OUTPUT_TAX'
}

function shouldShowTax(row: any) {
  return needsTax(row)
}

function isVisible(row: any, field: string) {
  const r = getRules(row.accountCode)
  if (!r || !r.fieldRules) return true
  const state = r.fieldRules[field]
  if (state === 'hidden') return false
  return true
}

function isRequired(row: any, field: string) {
  const r = getRules(row.accountCode)
  if (!r || !r.fieldRules) return false
  return r.fieldRules[field] === 'required'
}

function isAssetAccount(row: any) {
  const r = getRules(row.accountCode)
  // 如果科目有 assetId 字段规则，则显示固定资产选择
  if (r?.fieldRules?.assetId && r.fieldRules.assetId !== 'hidden') return true
  return false
}

function formatTransactionType(type: string): string {
  switch (type) {
    case 'ACQUISITION': return '取得'
    case 'DEPRECIATION': return '償却'
    case 'DISPOSAL': return '除却'
    default: return type || ''
  }
}

function getAssetClassName(classId: string): string {
  const ac = assetClasses.value.find(x => x.id === classId)
  return ac?.class_name || classId || ''
}

function getDepartmentName(deptId: string): string {
  if (!deptId) return ''
  const dept = departmentOptions.value.find(x => x.value === deptId)
  return dept?.label || ''
}

// 从交易行获取凭证ID（兼容生成列和payload两种格式）
function getVoucherId(row: any): string {
  return row?.voucher_id || row?.payload?.voucherId || ''
}

// 从交易行获取凭证号（兼容生成列和payload两种格式）
function getVoucherNo(row: any): string {
  return row?.voucher_no || row?.payload?.voucherNo || ''
}

async function loadAssetClasses() {
  try {
    const resp = await api.get('/fixed-assets/classes')
    assetClasses.value = Array.isArray(resp.data) ? resp.data : []
  } catch (e) {
    console.error('Failed to load asset classes', e)
  }
}

async function load() {
  loading.value = true
  try {
    let url = '/fixed-assets/assets'
    const params = new URLSearchParams()
    if (filters.assetClassId) params.append('assetClassId', filters.assetClassId)
    if (filters.assetNo) params.append('assetNo', filters.assetNo)
    if (filters.assetName) params.append('assetName', filters.assetName)
    if (params.toString()) url += '?' + params.toString()
    
    const resp = await api.get(url)
    rows.value = Array.isArray(resp.data) ? resp.data : []
  } catch (e) {
    console.error('Failed to load assets', e)
    ElMessage.error('固定資産の読み込みに失敗しました')
  } finally {
    loading.value = false
  }
}

function openCreateDialog() {
  resetForm()
  isEdit.value = false
  showDialog.value = true
}

function openViewDialog(row: any) {
  openEditDialog(row)
}

async function openEditDialog(row: any) {
  resetForm()
  isEdit.value = true
  editId.value = row.id

  // 加载详细信息（包含交易明细）
  try {
    const resp = await api.get(`/fixed-assets/assets/${row.id}`)
    const data = resp.data
    const payload = data.payload || {}
    
    form.assetNo = payload.assetNo || data.asset_no || ''
    form.assetName = payload.assetName || data.asset_name || ''
    form.assetClassId = payload.assetClassId || data.asset_class_id || ''
    form.departmentId = payload.departmentId || data.department_id || ''
    form.depreciationMethod = payload.depreciationMethod || data.depreciation_method || 'STRAIGHT_LINE'
    form.usefulLife = payload.usefulLife || data.useful_life || 5
    form.acquisitionCost = payload.acquisitionCost || data.acquisition_cost || ''
    form.bookValue = payload.bookValue || data.book_value || ''
    form.capitalizationDate = payload.capitalizationDate || data.capitalization_date || ''
    form.depreciationStartDate = payload.depreciationStartDate || data.depreciation_start_date || ''
    form.remarks = payload.remarks || ''
    
    transactions.value = data.transactions || []
    pendingTransactions.value = data.pendingTransactions || []
    console.log('Loaded transactions:', JSON.stringify(transactions.value, null, 2))
  } catch (e) {
    console.error('Failed to load asset detail', e)
    ElMessage.error('資産詳細の読み込みに失敗しました')
    return
  }

  showDialog.value = true
}

async function saveForm() {
  if (!form.assetName) {
    ElMessage.warning('資産名称を入力してください')
    return
  }
  if (!form.assetClassId) {
    ElMessage.warning('資産クラスを選択してください')
    return
  }
  if (!form.departmentId) {
    ElMessage.warning('部門を選択してください')
    return
  }
  
  // 新規登録時（初期データ導入）は取得価額、帳簿価額、日付が必須
  if (!isEdit.value) {
    if (!form.acquisitionCost) {
      ElMessage.warning('取得価額を入力してください')
      return
    }
    if (!form.bookValue) {
      ElMessage.warning('帳簿価額を入力してください')
      return
    }
    if (!form.capitalizationDate) {
      ElMessage.warning('資本化日付を入力してください')
      return
    }
    if (!form.depreciationStartDate) {
      ElMessage.warning('償却開始日を入力してください')
      return
    }
  }

  saving.value = true
  try {
    const payload: any = {
      assetName: form.assetName,
      assetClassId: form.assetClassId,
      departmentId: form.departmentId,
      depreciationMethod: form.depreciationMethod,
      usefulLife: form.usefulLife,
      remarks: form.remarks
    }

    if (!isEdit.value) {
      // 新規登録（初期データ導入）: 所有字段
      payload.acquisitionCost = Number(form.acquisitionCost) || 0
      payload.bookValue = Number(form.bookValue) || 0
      payload.capitalizationDate = form.capitalizationDate
      payload.depreciationStartDate = form.depreciationStartDate
    }

    if (isEdit.value && editId.value) {
      // 编辑时只能修改基本信息，财务数据保留原值
      payload.assetNo = form.assetNo
      payload.acquisitionCost = Number(form.acquisitionCost) || 0
      payload.bookValue = Number(form.bookValue) || 0
      payload.capitalizationDate = form.capitalizationDate
      payload.depreciationStartDate = form.depreciationStartDate
      await api.put(`/fixed-assets/assets/${editId.value}`, payload)
      ElMessage.success('更新しました')
    } else {
      await api.post('/fixed-assets/assets', payload)
      ElMessage.success('登録しました')
    }
    showDialog.value = false
    await load()
  } catch (e: any) {
    ElMessage.error(e.response?.data?.error || '保存に失敗しました')
  } finally {
    saving.value = false
  }
}

async function confirmDelete(row: any) {
  try {
    await ElMessageBox.confirm(
      `固定資産「${row.asset_no} ${row.asset_name}」を削除しますか？`,
      '削除確認',
      { confirmButtonText: '削除', cancelButtonText: 'キャンセル', type: 'warning' }
    )
    await api.delete(`/fixed-assets/assets/${row.id}`)
    ElMessage.success('削除しました')
    await load()
  } catch (e: any) {
    if (e !== 'cancel' && e?.response?.data?.error) {
      ElMessage.error(e.response.data.error)
    }
  }
}

async function confirmDeleteTransaction(tx: any) {
  try {
    await ElMessageBox.confirm(
      'このトランザクションを削除しますか？',
      '削除確認',
      { confirmButtonText: '削除', cancelButtonText: 'キャンセル', type: 'warning' }
    )
    await api.delete(`/fixed-assets/transactions/${tx.id}`)
    ElMessage.success('削除しました')
    // 重新加载详情
    if (editId.value) {
      const resp = await api.get(`/fixed-assets/assets/${editId.value}`)
      transactions.value = resp.data.transactions || []
      pendingTransactions.value = resp.data.pendingTransactions || []
    }
  } catch (e: any) {
    if (e !== 'cancel' && e?.response?.data?.error) {
      ElMessage.error(e.response.data.error)
    }
  }
}

// 通过交易行打开凭证弹窗（优先使用voucherId，备用voucherNo）
function openVoucherDialogByRow(row: any) {
  const voucherId = getVoucherId(row)
  const voucherNo = getVoucherNo(row)
  console.log('openVoucherDialogByRow called with:', { voucherId, voucherNo })
  
  if (!voucherId && !voucherNo) {
    console.warn('Both voucherId and voucherNo are empty')
    ElMessage.warning('伝票情報が取得できませんでした')
    return
  }
  
  selectedVoucherId.value = voucherId || ''
  selectedVoucherNo.value = voucherNo || ''
  showVoucherDialog.value = true
  console.log('Opening voucher dialog with:', { selectedVoucherId: selectedVoucherId.value, selectedVoucherNo: selectedVoucherNo.value })
}

// === 资产取得相关方法 ===
async function searchAccounts(query: string) {
  loadingAccounts.value = true
  try {
    const where: any[] = []
    const q = query?.trim()
    if (q) {
      where.push({ json: 'name', op: 'contains', value: q })
      where.push({ field: 'account_code', op: 'contains', value: q })
    }
    const r = await api.post('/objects/account/search', { where, page: 1, pageSize: 50 })
    const rows = Array.isArray(r.data?.data) ? r.data.data : []
    accountOptions.value = rows.map((x: any) => ({
      label: `${x.payload?.name || ''} (${x.account_code})`,
      value: x.account_code
    }))
    // 缓存科目规则
    rows.forEach((x: any) => {
      codeToRules.set(x.account_code, {
        openItem: !!x.payload?.openItem,
        openItemBaseline: x.payload?.openItemBaseline || 'NONE',
        fieldRules: x.payload?.fieldRules || {},
        taxType: x.payload?.taxType || 'NON_TAX'
      })
    })
  } catch (e) {
    console.error('Failed to search accounts', e)
    accountOptions.value = []
  } finally {
    loadingAccounts.value = false
  }
}

async function searchAssets(query: string) {
  loadingAssets.value = true
  try {
    let url = '/fixed-assets/assets'
    if (query?.trim()) {
      url += `?assetName=${encodeURIComponent(query)}`
    }
    const r = await api.get(url)
    const rows = Array.isArray(r.data) ? r.data : []
    assetOptions.value = rows.map((x: any) => ({
      label: `${x.asset_no} ${x.asset_name || ''}`,
      value: x.id
    }))
  } catch (e) {
    console.error('Failed to search assets', e)
    assetOptions.value = []
  } finally {
    loadingAssets.value = false
  }
}

async function searchVendors(query: string) {
  loadingVendors.value = true
  try {
    const base = [{ field: 'flag_vendor', op: 'eq', value: true }]
    const where = query?.trim() ? [...base, { json: 'name', op: 'contains', value: query }] : base
    const r = await api.post('/objects/businesspartner/search', { where, page: 1, pageSize: 50 })
    const rows = Array.isArray(r.data?.data) ? r.data.data : []
    vendorOptions.value = rows.map((x: any) => ({
      label: `${x.payload?.name || ''} (${x.partner_code})`,
      value: x.partner_code
    }))
  } catch (e) {
    console.error('Failed to search vendors', e)
    vendorOptions.value = []
  } finally {
    loadingVendors.value = false
  }
}

function resetAcquisitionForm() {
  acquisitionForm.assetClassId = ''
  acquisitionForm.assetName = ''
  acquisitionForm.assetNo = ''
  acquisitionForm.depreciationMethod = 'STRAIGHT_LINE'
  acquisitionForm.usefulLife = 5
  acquisitionForm.acquisitionDate = ''
  acquisitionForm.acquisitionCost = 0
  acquisitionForm.remarks = ''
  acquisitionForm.voucherLines = [
    { drcr: 'DR', accountCode: '', amount: '', taxRate: 10, vendorId: '', assetId: '', paymentDate: '', note: '' },
    { drcr: 'CR', accountCode: '', amount: '', taxRate: 10, vendorId: '', assetId: '', paymentDate: '', note: '' }
  ]
}

function resetDisposalForm() {
  disposalForm.disposalDate = ''
  disposalForm.note = ''
}

async function openAcquisitionDialog() {
  resetAcquisitionForm()
  isNewAssetAcquisition.value = true
  existingAssetId.value = null
  // 设置默认日期为今天
  acquisitionForm.acquisitionDate = new Date().toISOString().split('T')[0]
  // 预加载科目和供应商选项
  await Promise.all([searchAccounts(''), searchVendors('')])
  showAcquisitionDialog.value = true
}

// 针对已有资产的取得（资本化）
async function openAcquisitionForAsset(row: any) {
  resetAcquisitionForm()
  isNewAssetAcquisition.value = false
  existingAssetId.value = row.id
  
  // 预填资产信息
  acquisitionForm.assetClassId = row.asset_class_id || row.payload?.assetClassId || ''
  acquisitionForm.assetName = row.asset_name || row.payload?.assetName || ''
  acquisitionForm.assetNo = row.asset_no || row.payload?.assetNo || ''
  acquisitionForm.depreciationMethod = row.depreciation_method || row.payload?.depreciationMethod || 'STRAIGHT_LINE'
  acquisitionForm.usefulLife = row.useful_life || row.payload?.usefulLife || 5
  acquisitionForm.acquisitionDate = row.capitalization_date || row.payload?.capitalizationDate || new Date().toISOString().split('T')[0]
  acquisitionForm.acquisitionCost = Number(row.acquisition_cost || row.payload?.acquisitionCost) || 0
  acquisitionForm.remarks = row.payload?.remarks || ''
  
  // 预填借方金额
  if (acquisitionForm.acquisitionCost > 0) {
    acquisitionForm.voucherLines[0].amount = acquisitionForm.acquisitionCost.toString()
    acquisitionForm.voucherLines[1].amount = acquisitionForm.acquisitionCost.toString()
  }
  
  // 根据资产类别设置借方科目
  const ac = assetClasses.value.find(x => x.id === acquisitionForm.assetClassId)
  const acquisitionAccount = ac?.payload?.acquisitionAccount || ac?.acquisition_account
  if (acquisitionAccount) {
    acquisitionForm.voucherLines[0].accountCode = acquisitionAccount
    // 搜索科目以填充下拉选项
    searchAccounts(acquisitionAccount)
  }
  
  // 预加载科目和供应商选项
  await Promise.all([searchAccounts(''), searchVendors('')])
  showAcquisitionDialog.value = true
}

// 检查资产是否已有取得交易
function hasAcquisitionTransaction(row: any): boolean {
  // 如果有交易记录且第一条是取得交易，则禁用按钮
  // 这里简单判断：如果 book_value 不等于 acquisition_cost，说明已经有折旧或其他交易
  // 更精确的方式是检查 transactions 数组，但需要额外API调用
  // 目前先用一个简单的判断：如果资产已经有取得价额，可能已经取得过
  return false // 暂时允许所有，后续可以通过API检查
}

function onAcquisitionClassChange(classId: string) {
  // 当选择资产类别时，自动设置借方科目（第一行）
  const ac = assetClasses.value.find(x => x.id === classId)
  const acquisitionAccount = ac?.payload?.acquisitionAccount || ac?.acquisition_account
  if (acquisitionAccount && acquisitionForm.voucherLines[0]) {
    acquisitionForm.voucherLines[0].accountCode = acquisitionAccount
    // 确保是借方
    acquisitionForm.voucherLines[0].drcr = 'DR'
    // 搜索科目以填充下拉选项
    searchAccounts(acquisitionAccount)
  }
  // 重新计算折旧开始日（有形/无形资产规则不同）
  updateDepreciationStartDate()
}

// 当取得日变更时，重新计算折旧开始日（用于新規登録弹窗）
function onCapitalizationDateChange() {
  if (form.capitalizationDate && !isEdit.value) {
    const isTangible = isFormClassTangible()
    form.depreciationStartDate = calculateDepreciationStartDate(form.capitalizationDate, isTangible)
  }
}

// 当资产类别变更时（新規登録弹窗），重新计算折旧开始日
function onFormClassChange() {
  if (form.capitalizationDate && !isEdit.value) {
    const isTangible = isFormClassTangible()
    form.depreciationStartDate = calculateDepreciationStartDate(form.capitalizationDate, isTangible)
  }
}

// 获取新規登録表单中选择的资产类别是否为有形资产
function isFormClassTangible(): boolean {
  if (!form.assetClassId) return true // 默认有形
  const ac = assetClasses.value.find(x => x.id === form.assetClassId)
  if (!ac) return true
  if (ac.payload?.isTangible !== undefined) return ac.payload.isTangible
  return ac.asset_type !== 'INTANGIBLE'
}

function addAcquisitionLine() {
  acquisitionForm.voucherLines.push({ drcr: 'CR', accountCode: '', amount: '', taxRate: 10, vendorId: '', assetId: '', paymentDate: '', note: '' })
}

function removeAcquisitionLine(index: number) {
  if (acquisitionForm.voucherLines.length > 2) {
    acquisitionForm.voucherLines.splice(index, 1)
  }
}

async function submitAcquisition() {
  // 验证必填项
  if (isNewAssetAcquisition.value) {
    if (!acquisitionForm.assetClassId) {
      ElMessage.warning('資産クラスを選択してください')
      return
    }
    if (!acquisitionForm.assetName) {
      ElMessage.warning('資産名称を入力してください')
      return
    }
    if (!acquisitionForm.departmentId) {
      ElMessage.warning('部門を選択してください')
      return
    }
  }
  if (!acquisitionForm.acquisitionDate) {
    ElMessage.warning('取得日を選択してください')
    return
  }
  
  // 找到借方行的金额作为取得价额
  const debitLine = acquisitionForm.voucherLines.find(l => l.drcr === 'DR')
  const acquisitionCost = Number(debitLine?.amount) || 0
  if (acquisitionCost <= 0) {
    ElMessage.warning('取得価額を入力してください')
    return
  }

  acquisitionSaving.value = true
  try {
    if (isNewAssetAcquisition.value) {
      // 新资产取得：创建资产主数据 + 凭证
      await api.post('/fixed-assets/acquire', {
        assetClassId: acquisitionForm.assetClassId,
        assetName: acquisitionForm.assetName,
        departmentId: acquisitionForm.departmentId,
        depreciationMethod: acquisitionForm.depreciationMethod,
        usefulLife: acquisitionForm.usefulLife,
        acquisitionDate: acquisitionForm.acquisitionDate,
        acquisitionCost: acquisitionCost,
        remarks: acquisitionForm.remarks,
        voucherLines: acquisitionForm.voucherLines.map(l => ({
          drcr: l.drcr,
          accountCode: l.accountCode,
          amount: Number(l.amount) || 0,
          taxRate: l.taxRate ? Number(l.taxRate) : null,
          vendorId: l.vendorId || null
        }))
      })
    } else {
      // 已有资产取得：只创建凭证和交易记录
      await api.post(`/fixed-assets/assets/${existingAssetId.value}/capitalize`, {
        acquisitionDate: acquisitionForm.acquisitionDate,
        acquisitionCost: acquisitionCost,
        voucherLines: acquisitionForm.voucherLines.map(l => ({
          drcr: l.drcr,
          accountCode: l.accountCode,
          amount: Number(l.amount) || 0,
          taxRate: l.taxRate ? Number(l.taxRate) : null,
          vendorId: l.vendorId || null
        }))
      })
    }
    ElMessage.success('資産取得が完了しました')
    showAcquisitionDialog.value = false
    await load()
  } catch (e: any) {
    console.error('Failed to acquire asset', e)
    ElMessage.error(e.response?.data?.error || '資産取得に失敗しました')
  } finally {
    acquisitionSaving.value = false
  }
}

async function openDisposalDialog() {
  if (!isEdit.value || !editId.value) return
  if (isDisposed.value) {
    ElMessage.warning('この資産は除却済みです')
    return
  }

  resetDisposalForm()
  disposalForm.disposalDate = new Date().toISOString().split('T')[0]
  disposalForm.note = `資産除却「${form.assetNo} ${form.assetName}」`

  showDisposalDialog.value = true
}

async function submitDisposal() {
  if (!editId.value) return
  if (!disposalForm.disposalDate) {
    ElMessage.warning('除却日を選択してください')
    return
  }
  if (disposalPreviewLines.value.length === 0) {
    ElMessage.warning('資産クラスの科目設定が不足しています（プレビューが生成できません）')
    return
  }

  disposalSaving.value = true
  try {
    await api.post(`/fixed-assets/assets/${editId.value}/dispose`, {
      disposalDate: disposalForm.disposalDate,
      note: disposalForm.note,
    })
    ElMessage.success('除却が完了しました')
    showDisposalDialog.value = false
    // 重新加载详情（更新交易与帐簿価額）
    const resp = await api.get(`/fixed-assets/assets/${editId.value}`)
    const data = resp.data
    const payload = data.payload || {}
    form.acquisitionCost = payload.acquisitionCost || data.acquisition_cost || ''
    form.bookValue = payload.bookValue || data.book_value || ''
    transactions.value = data.transactions || []
    pendingTransactions.value = data.pendingTransactions || []
    await load()
  } catch (e: any) {
    console.error('Failed to dispose asset', e)
    ElMessage.error(e.response?.data?.error || '除却に失敗しました')
  } finally {
    disposalSaving.value = false
  }
}

onMounted(async () => {
  await Promise.all([
    loadAssetClasses(),
    loadDepartments()
  ])
  await load()
})
</script>

<style scoped>
.assets-list {
  padding: 16px;
}

.assets-card {
  border-radius: 12px;
  overflow: hidden;
}

.assets-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
}

.assets-header__left {
  display: flex;
  align-items: center;
  gap: 10px;
}

.assets-header__icon {
  font-size: 22px;
  color: #e6a23c;
}

.assets-header__title {
  font-size: 18px;
  font-weight: 600;
  color: #303133;
}

.assets-header__count {
  font-weight: 500;
}

.assets-header__right {
  display: flex;
  gap: 8px;
}

/* フィルター */
.assets-filters {
  display: flex;
  flex-wrap: wrap;
  gap: 12px;
  align-items: center;
  margin-bottom: 16px;
  padding: 16px;
  background: #f8f9fc;
  border-radius: 8px;
}

.assets-filters__class {
  width: 160px;
}

.assets-filters__no {
  width: 120px;
}

.assets-filters__name {
  width: 180px;
}

/* テーブル */
.assets-table {
  border-radius: 8px;
  overflow: hidden;
}

.assets-table :deep(.el-table__header th) {
  background: #f5f7fa;
  font-weight: 600;
  color: #303133;
}

/* ページネーション */
.assets-pagination {
  display: flex;
  justify-content: flex-start;
  align-items: center;
  margin-top: 16px;
  padding-top: 16px;
  border-top: 1px solid #ebeef5;
}

.assets-pagination__info {
  font-size: 13px;
  color: #909399;
}
.dialog-content {
  max-height: 70vh;
  overflow-y: auto;
}
.section-title {
  font-size: 14px;
  font-weight: 600;
  color: #409eff;
  margin: 0 0 12px 0;
  padding-bottom: 8px;
  border-bottom: 1px solid #ebeef5;
}
.req :deep(.el-input__wrapper),
.req :deep(.el-select__wrapper) {
  border-color: #f56c6c;
}
.voucher-dialog-card-wrap {
  padding: 0 16px 16px;
}
.voucher-detail-embed {
  min-width: 800px;
  max-width: 1200px;
}
.pending-row {
  background-color: #fafafa !important;
  color: #909399;
}
.pending-row td {
  color: #909399 !important;
}
.pending-text {
  color: #c0c4cc;
}

/* 资产弹窗样式 */
.asset-dialog {
  padding: 0 8px;
}
.asset-dialog .section-title {
  margin: 0 0 12px 0;
  padding-bottom: 8px;
  border-bottom: 1px solid #ebeef5;
}
.asset-form {
  padding-right: 8px;
}
.asset-form .el-form-item {
  margin-bottom: 14px;
}
.useful-life-item :deep(.el-form-item__content) {
  flex-wrap: nowrap;
}
.useful-life-input {
  display: flex;
  align-items: center;
  gap: 6px;
}
.useful-life-input .el-input-number {
  width: 100px;
}
.useful-life-input .unit {
  white-space: nowrap;
  color: #606266;
}
.tx-table {
  margin-top: 8px;
}

/* 资产取得弹窗样式 */
:deep(.acquisition-dialog .el-dialog__body) {
  padding: 16px 20px;
  max-height: calc(90vh - 140px);
  overflow: auto;
}
.acquisition-content {
  padding: 0;
}
.acquisition-alert {
  margin-bottom: 16px;
}
.acquisition-section {
  margin-bottom: 20px;
}
.acquisition-section .section-title {
  margin: 0 0 12px 0;
  padding-bottom: 8px;
  border-bottom: 1px solid #ebeef5;
  color: #409EFF;
  font-size: 15px;
}
.acquisition-form {
  padding: 0;
}
.acquisition-form .el-form-item {
  margin-bottom: 12px;
}
.acquisition-form .nowrap-label :deep(.el-form-item__label) {
  white-space: nowrap;
}
.acquisition-form :deep(.el-form-item__label) {
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}
.voucher-section {
  background: #fafafa;
  padding: 16px;
  border-radius: 8px;
  border: 1px solid #ebeef5;
}
.voucher-section .section-title {
  margin-top: 0;
}
.line-actions {
  margin: 12px 0;
  display: flex;
  justify-content: flex-end;
}
.voucher-totals {
  display: flex;
  gap: 24px;
  align-items: center;
  font-weight: 600;
  font-size: 14px;
  padding: 12px 0 0;
  border-top: 1px solid #ebeef5;
}
.voucher-totals.warn {
  color: #d93025;
}
.voucher-totals .imbalance {
  color: #f56c6c;
  font-weight: normal;
}
</style>

