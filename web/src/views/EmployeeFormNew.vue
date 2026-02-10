<template>
  <div class="emp-form-wrap">
    <el-card class="emp-form-card">
      <template #header>
        <div class="page-header detail-header">
          <div class="page-header-left">
            <div class="page-header-title">{{ isReadonly ? '社員照会' : (empId ? '社員編集' : '社員登録') }}</div>
            <div class="page-header-meta" v-if="!loading && (model.nameKanji || model.code)">
              <el-tag v-if="model.code" size="small" type="info">{{ model.code }}</el-tag>
              <span class="page-header-name">{{ model.nameKanji }}</span>
              <span class="page-header-kana" v-if="model.nameKana">（{{ model.nameKana }}）</span>
              <el-tag v-if="employmentStatus" :type="employmentStatus === 'active' ? 'success' : 'warning'" size="small">
                {{ employmentStatus === 'active' ? '在籍' : '退職済' }}
              </el-tag>
            </div>
          </div>
          <div class="detail-header-actions">
            <el-button v-if="isReadonly" type="primary" @click="emit('switchToEdit')">
              <el-icon><Edit /></el-icon>
              編集モードに切替
            </el-button>
            <el-button v-else type="primary" @click="handleSave" :loading="saving">保存</el-button>
          </div>
        </div>
      </template>

      <div v-if="loading" class="emp-loading">
        <el-skeleton :rows="8" animated />
      </div>

      <div v-else-if="error" class="emp-error">
        <el-result icon="error" title="読み込みエラー" :sub-title="error">
          <template #extra>
            <el-button @click="reload">再読み込み</el-button>
          </template>
        </el-result>
      </div>

      <div v-else :class="['emp-content', { 'emp-content--readonly': isReadonly }]">
      <!-- 左侧主要信息 -->
      <div class="emp-main">
        <!-- 基本情報カード -->
        <div class="emp-card">
          <div class="emp-card__header">
            <el-icon><User /></el-icon>
            <span>基本情報</span>
          </div>
          <div class="emp-card__body">
            <!-- 第1行：姓名 -->
            <div class="emp-row">
              <div class="emp-field emp-field--required" style="flex:0 0 200px">
                <label>氏名（カナ）</label>
                <el-input v-model="model.nameKana" placeholder="ヤマダ タロウ" />
              </div>
              <div class="emp-field emp-field--required" style="flex:0 0 160px">
                <label>氏名（漢字）</label>
                <el-input v-model="model.nameKanji" placeholder="山田 太郎" />
              </div>
              <div class="emp-field emp-field--required" style="flex:0 0 80px">
                <label>性別</label>
                <el-select v-model="model.gender" placeholder="選択" style="width:100%">
                  <el-option label="男性" value="M" />
                  <el-option label="女性" value="F" />
                </el-select>
              </div>
              <div class="emp-field emp-field--required" style="flex:0 0 150px">
                <label>生年月日</label>
                <el-date-picker v-model="model.birthDate" type="date" placeholder="選択" value-format="YYYY-MM-DD" style="width:100%" />
              </div>
            </div>
            <!-- 第2行：国籍、社员代码等 -->
            <div class="emp-row">
              <div class="emp-field emp-field--required" style="flex:0 0 120px">
                <label>国籍</label>
                <el-select v-model="model.nationality" placeholder="選択" filterable style="width:100%">
                  <el-option label="日本" value="JP" />
                  <el-option label="中国" value="CN" />
                  <el-option label="韓国" value="KR" />
                  <el-option label="台湾" value="TW" />
                  <el-option label="ベトナム" value="VN" />
                  <el-option label="フィリピン" value="PH" />
                  <el-option label="アメリカ" value="US" />
                  <el-option label="その他" value="OTHER" />
                </el-select>
              </div>
              <div class="emp-field" style="flex:0 0 150px" v-if="model.nationality && model.nationality !== 'JP'">
                <label>来日日</label>
                <el-date-picker v-model="model.arriveJPDate" type="date" placeholder="選択" value-format="YYYY-MM-DD" style="width:100%" />
              </div>
              <div class="emp-field" style="flex:0 0 120px" v-if="empId">
                <label>社員コード</label>
                <el-input v-model="model.code" disabled />
              </div>
              <div class="emp-field" style="flex:0 0 180px">
                <label>マイナンバー</label>
                <el-input v-model="model.myNumber" placeholder="12桁数字" maxlength="12" />
              </div>
              <div class="emp-field" style="flex:0 0 200px" v-if="isContractorEmployee">
                <label>インボイス登録番号</label>
                <el-input v-model="model.taxNo" placeholder="T0000000000000">
                  <template #prefix>
                    <el-tooltip content="個人事業主のみ必要" placement="top">
                      <el-icon style="color:#e6a23c"><Warning /></el-icon>
                    </el-tooltip>
                  </template>
                </el-input>
              </div>
            </div>
          </div>
        </div>

        <!-- 連絡先カード -->
        <div class="emp-card">
          <div class="emp-card__header">
            <el-icon><Phone /></el-icon>
            <span>連絡先</span>
          </div>
          <div class="emp-card__body">
            <div class="emp-row">
              <div class="emp-field" style="flex:0 0 150px">
                <label>電話番号</label>
                <el-input v-model="model.contact.phone" placeholder="090-1234-5678" />
              </div>
              <div class="emp-field" style="flex:0 0 240px">
                <label>メールアドレス</label>
                <el-input v-model="model.contact.email" placeholder="example@mail.com" />
              </div>
              <div class="emp-field" style="flex:0 0 110px">
                <label>郵便番号</label>
                <el-input v-model="model.contact.postalCode" placeholder="100-0001" />
              </div>
              <div class="emp-field" style="flex:1;min-width:200px">
                <label>住所</label>
                <el-input v-model="model.contact.address" placeholder="住所を入力" />
              </div>
            </div>
          </div>
        </div>

        <!-- 雇用契約カード -->
        <div class="emp-card">
          <div class="emp-card__header">
            <el-icon><Document /></el-icon>
            <span>雇用契約</span>
            <el-button size="small" text type="primary" @click="addContract" class="emp-card__add">
              <el-icon><Plus /></el-icon>追加
            </el-button>
          </div>
          <div class="emp-card__body">
            <div v-if="!model.contracts?.length" class="emp-empty">
              契約情報がありません
            </div>
            <div v-else class="emp-list">
              <div 
                v-for="(item, idx) in model.contracts" 
                :key="idx" 
                class="emp-list__item emp-list__item--inline"
                :class="{ 'emp-list__item--active': isContractActive(item) }"
              >
                <div class="emp-mini-field" style="flex:0 0 140px">
                  <label>
                    雇用区分
                    <el-button 
                      v-if="idx === 0" 
                      size="small" 
                      text 
                      type="info" 
                      class="field-settings-btn"
                      @click="openEmploymentTypeManager"
                    >
                      <el-icon><Setting /></el-icon>
                    </el-button>
                  </label>
                  <el-select 
                    v-model="item.employmentTypeCode" 
                    filterable 
                    allow-create 
                    placeholder="選択"
                    size="small"
                    style="width:100%"
                  >
                    <el-option 
                      v-for="opt in employmentTypeOptions" 
                      :key="opt.value" 
                      :label="opt.label" 
                      :value="opt.value" 
                    />
                  </el-select>
                </div>
                <div class="emp-mini-field" style="flex:0 0 130px">
                  <label>開始日</label>
                  <el-date-picker v-model="item.periodFrom" type="date" size="small" placeholder="選択" value-format="YYYY-MM-DD" style="width:100%" />
                </div>
                <div class="emp-mini-field" style="flex:0 0 130px">
                  <label>終了日</label>
                  <el-date-picker v-model="item.periodTo" type="date" size="small" placeholder="選択" value-format="YYYY-MM-DD" style="width:100%" />
                </div>
                <div class="emp-mini-field" style="flex:1;min-width:120px">
                  <label>備考</label>
                  <el-input v-model="item.note" size="small" placeholder="備考" />
                </div>
                <el-button size="small" text type="danger" @click="removeContract(idx)" style="margin-top:18px">
                  <el-icon><Delete /></el-icon>
                </el-button>
              </div>
            </div>
          </div>
        </div>

        <!-- 給与情報カード -->
        <div class="emp-card">
          <div class="emp-card__header">
            <el-icon><Money /></el-icon>
            <span>給与情報</span>
            <el-button size="small" text type="primary" @click="addSalary" class="emp-card__add">
              <el-icon><Plus /></el-icon>追加
            </el-button>
          </div>
          <div class="emp-card__body">
            <div v-if="!model.salaries?.length" class="emp-empty">
              給与情報がありません
            </div>
            <div v-else class="emp-list">
              <div 
                v-for="(item, idx) in model.salaries" 
                :key="idx" 
                class="emp-list__item emp-list__item--inline"
                :class="{ 'emp-list__item--active': isSalaryActive(item) }"
              >
                <div class="emp-mini-field" style="flex:0 0 130px">
                  <label>開始日</label>
                  <el-date-picker v-model="item.startDate" type="date" size="small" placeholder="選択" value-format="YYYY-MM-DD" style="width:100%" />
                </div>
                <div class="emp-mini-field" style="flex:1;min-width:300px">
                  <label>給与説明</label>
                  <el-input 
                    v-model="item.description" 
                    type="textarea" 
                    :rows="2" 
                    size="small" 
                    placeholder="例：月給30万円、交通費月1万円支給" 
                  />
                </div>
                <el-button size="small" text type="danger" @click="removeSalary(idx)" style="margin-top:18px; align-self:flex-start">
                  <el-icon><Delete /></el-icon>
                </el-button>
              </div>
            </div>
          </div>
        </div>

        <!-- 所属部門カード -->
        <div class="emp-card">
          <div class="emp-card__header">
            <el-icon><OfficeBuilding /></el-icon>
            <span>所属部門</span>
            <el-button size="small" text type="primary" @click="addDepartment" class="emp-card__add">
              <el-icon><Plus /></el-icon>追加
            </el-button>
          </div>
          <div class="emp-card__body">
            <div v-if="!model.departments?.length" class="emp-empty">
              部門情報がありません
            </div>
            <div v-else class="emp-list">
              <div 
                v-for="(item, idx) in model.departments" 
                :key="idx" 
                class="emp-list__item emp-list__item--inline"
              >
                <div class="emp-mini-field" style="flex:1;min-width:200px">
                  <label>部門</label>
                  <el-select 
                    v-model="item.departmentId" 
                    filterable 
                    placeholder="部門を選択"
                    size="small"
                    style="width:100%"
                  >
                    <el-option 
                      v-for="opt in departmentOptions" 
                      :key="opt.value" 
                      :label="opt.label" 
                      :value="opt.value" 
                    />
                  </el-select>
                </div>
                <div class="emp-mini-field" style="flex:0 0 140px">
                  <label>
                    役職
                    <el-button 
                      v-if="idx === 0" 
                      size="small" 
                      text 
                      type="info" 
                      class="field-settings-btn"
                      @click="openPositionManager"
                    >
                      <el-icon><Setting /></el-icon>
                    </el-button>
                  </label>
                  <el-select 
                    v-model="item.position" 
                    filterable 
                    allow-create 
                    default-first-option
                    placeholder="選択/入力"
                    size="small"
                    style="width:100%"
                    @change="(val) => onPositionChange(val, item)"
                  >
                    <el-option 
                      v-for="opt in positionOptions" 
                      :key="opt.value" 
                      :label="opt.label" 
                      :value="opt.value" 
                    />
                  </el-select>
                </div>
                <div class="emp-mini-field" style="flex:0 0 130px">
                  <label>開始日</label>
                  <el-date-picker v-model="item.fromDate" type="date" size="small" placeholder="選択" value-format="YYYY-MM-DD" style="width:100%" />
                </div>
                <div class="emp-mini-field" style="flex:0 0 130px">
                  <label>終了日</label>
                  <el-date-picker v-model="item.toDate" type="date" size="small" placeholder="選択" value-format="YYYY-MM-DD" style="width:100%" />
                </div>
                <el-button size="small" text type="danger" @click="removeDepartment(idx)" style="margin-top:18px">
                  <el-icon><Delete /></el-icon>
                </el-button>
              </div>
            </div>
          </div>
        </div>

        <!-- 扶養親族カード -->
        <div class="emp-card emp-card--compact">
          <div class="emp-card__header">
            <el-icon><UserFilled /></el-icon>
            <span>扶養親族</span>
            <el-button size="small" text type="primary" @click="addDependent" class="emp-card__add">
              <el-icon><Plus /></el-icon>追加
            </el-button>
          </div>
          <div class="emp-card__body">
            <div v-if="!model.dependents?.length" class="emp-empty">
              扶養親族がありません
            </div>
            <div v-else class="emp-bank-list">
              <div 
                v-for="(item, idx) in model.dependents" 
                :key="idx" 
                class="emp-bank-item"
              >
                <div class="emp-bank-item__header">
                  <span class="emp-bank-item__title">扶養親族 {{ idx + 1 }}</span>
                  <el-button size="small" text type="danger" @click="removeDependent(idx)">
                    <el-icon><Delete /></el-icon>
                  </el-button>
                </div>
                <div class="emp-row-compact">
                  <div class="emp-field emp-field--stacked" style="flex:0 0 150px">
                    <label>氏名（カナ）</label>
                    <el-input v-model="item.nameKana" size="small" placeholder="ヤマダ ハナコ" />
                  </div>
                  <div class="emp-field emp-field--stacked" style="flex:0 0 150px">
                    <label>氏名（漢字）</label>
                    <el-input v-model="item.nameKanji" size="small" placeholder="山田 花子" />
                  </div>
                  <div class="emp-field emp-field--stacked" style="flex:0 0 130px">
                    <label>生年月日</label>
                    <el-date-picker v-model="item.birthDate" type="date" size="small" placeholder="選択" value-format="YYYY-MM-DD" style="width:100%" />
                  </div>
                  <div class="emp-field emp-field--stacked" style="flex:0 0 80px">
                    <label>性別</label>
                    <el-select v-model="item.gender" size="small" style="width:100%">
                      <el-option label="男" value="M" />
                      <el-option label="女" value="F" />
                    </el-select>
                  </div>
                  <div class="emp-field emp-field--stacked" style="flex:1">
                    <label>関係</label>
                    <el-select v-model="item.relation" size="small" filterable allow-create style="width:100%">
                      <el-option label="配偶者" value="配偶者" />
                      <el-option label="子" value="子" />
                      <el-option label="父" value="父" />
                      <el-option label="母" value="母" />
                      <el-option label="祖父" value="祖父" />
                      <el-option label="祖母" value="祖母" />
                      <el-option label="兄弟姉妹" value="兄弟姉妹" />
                      <el-option label="夫の父" value="夫の父" />
                      <el-option label="夫の母" value="夫の母" />
                      <el-option label="妻の父" value="妻の父" />
                      <el-option label="妻の母" value="妻の母" />
                      <el-option label="その他" value="その他" />
                    </el-select>
                  </div>
                </div>
                <div class="emp-row-compact">
                  <div class="emp-field emp-field--stacked" style="flex:0 0 70px">
                    <label>同居</label>
                    <el-select v-model="item.cohabiting" size="small" style="width:100%">
                      <el-option label="あり" :value="true" />
                      <el-option label="なし" :value="false" />
                    </el-select>
                  </div>
                  <div class="emp-field emp-field--stacked" style="flex:1">
                    <label>住所</label>
                    <el-input v-model="item.address" size="small" placeholder="住所" />
                  </div>
                </div>
              </div>
            </div>
          </div>
        </div>
      </div>

      <!-- 右侧辅助信息 -->
      <div class="emp-side">
        <!-- 社会保険カード -->
        <div class="emp-card emp-card--compact">
          <div class="emp-card__header">
            <el-icon><FirstAidKit /></el-icon>
            <span>社会保険</span>
          </div>
          <div class="emp-card__body">
            <div class="emp-row-compact">
              <div class="emp-field emp-field--stacked" style="flex:1">
                <label>雇用保険番号</label>
                <el-input v-model="model.insurance.hireInsuranceNo" placeholder="51000000000" size="small" />
              </div>
              <div class="emp-field emp-field--stacked" style="flex:1">
                <label>健康保険番号</label>
                <el-input v-model="model.insurance.healthNo" placeholder="健保番号" size="small" />
              </div>
            </div>
            <div class="emp-row-compact">
              <div class="emp-field emp-field--stacked" style="flex:1">
                <label>厚生年金番号</label>
                <el-input v-model="model.insurance.endowNo" placeholder="年金番号" size="small" />
              </div>
              <div class="emp-field emp-field--stacked" style="flex:1">
                <label>年金基礎番号</label>
                <el-input v-model="model.insurance.endowBaseNo" placeholder="基礎年金番号" size="small" />
              </div>
            </div>
            <div class="emp-row-compact">
              <div class="emp-field emp-field--stacked" style="flex:1">
                <label>加入日</label>
                <el-date-picker v-model="model.insurance.joinDate" type="date" size="small" placeholder="選択" value-format="YYYY-MM-DD" style="width:100%" />
              </div>
              <div class="emp-field emp-field--stacked" style="flex:1">
                <label>喪失日</label>
                <el-date-picker v-model="model.insurance.quitDate" type="date" size="small" placeholder="選択" value-format="YYYY-MM-DD" style="width:100%" />
              </div>
            </div>
          </div>
        </div>

        <!-- 銀行口座カード -->
        <div class="emp-card emp-card--compact">
          <div class="emp-card__header">
            <el-icon><CreditCard /></el-icon>
            <span>銀行口座</span>
            <el-button size="small" text type="primary" @click="addBankAccount" class="emp-card__add">
              <el-icon><Plus /></el-icon>追加
            </el-button>
          </div>
          <div class="emp-card__body">
            <div v-if="!model.bankAccounts?.length" class="emp-empty">
              口座情報がありません
            </div>
            <div v-else class="emp-bank-list">
              <div 
                v-for="(item, idx) in model.bankAccounts" 
                :key="idx" 
                class="emp-bank-item"
              >
                <div class="emp-bank-item__header">
                  <span class="emp-bank-item__title">口座 {{ idx + 1 }}</span>
                  <el-button size="small" text type="danger" @click="removeBankAccount(idx)">
                    <el-icon><Delete /></el-icon>
                  </el-button>
                </div>
                <div class="emp-row-compact">
                  <div class="emp-field emp-field--stacked" style="flex:1">
                    <label>銀行</label>
                    <div class="emp-input-with-btn">
                      <el-input v-model="item.bank" size="small" placeholder="銀行名" readonly />
                      <el-button size="small" @click="openBankPicker(item)">選択</el-button>
                    </div>
                  </div>
                  <div class="emp-field emp-field--stacked" style="flex:1">
                    <label>支店</label>
                    <div class="emp-input-with-btn">
                      <el-input v-model="item.branch" size="small" placeholder="支店名" readonly />
                      <el-button size="small" @click="openBranchPicker(item)">選択</el-button>
                    </div>
                  </div>
                </div>
                <div class="emp-row-compact">
                  <div class="emp-field emp-field--stacked" style="flex:0 0 90px">
                    <label>種別</label>
                    <el-select v-model="item.accountType" size="small" style="width:100%">
                      <el-option label="普通" value="ordinary" />
                      <el-option label="当座" value="checking" />
                    </el-select>
                  </div>
                  <div class="emp-field emp-field--stacked" style="flex:1">
                    <label>口座番号</label>
                    <el-input v-model="item.accountNo" size="small" placeholder="1234567" />
                  </div>
                </div>
                <div class="emp-row-compact">
                  <div class="emp-field emp-field--stacked" style="flex:1">
                    <label>口座名義（カナ）</label>
                    <el-input v-model="item.holder" size="small" placeholder="ヤマダ タロウ" />
                  </div>
                </div>
              </div>
            </div>
          </div>
        </div>

        <!-- 緊急連絡先カード -->
        <div class="emp-card emp-card--compact">
          <div class="emp-card__header">
            <el-icon><Warning /></el-icon>
            <span>緊急連絡先</span>
            <el-button size="small" text type="primary" @click="addEmergency" class="emp-card__add">
              <el-icon><Plus /></el-icon>追加
            </el-button>
          </div>
          <div class="emp-card__body">
            <div v-if="!model.emergencies?.length" class="emp-empty">
              緊急連絡先がありません
            </div>
            <div v-else class="emp-bank-list">
              <div 
                v-for="(item, idx) in model.emergencies" 
                :key="idx" 
                class="emp-bank-item"
              >
                <div class="emp-bank-item__header">
                  <span class="emp-bank-item__title">連絡先 {{ idx + 1 }}</span>
                  <el-button size="small" text type="danger" @click="removeEmergency(idx)">
                    <el-icon><Delete /></el-icon>
                  </el-button>
                </div>
                <div class="emp-row-compact">
                  <div class="emp-field emp-field--stacked" style="flex:1">
                    <label>氏名</label>
                    <el-input v-model="item.nameKanji" size="small" placeholder="山田 花子" />
                  </div>
                  <div class="emp-field emp-field--stacked" style="flex:0 0 100px">
                    <label>続柄</label>
                    <el-select v-model="item.relation" size="small" style="width:100%">
                      <el-option label="両親" value="parent" />
                      <el-option label="配偶者" value="spouse" />
                      <el-option label="子ども" value="child" />
                      <el-option label="友人" value="friend" />
                      <el-option label="その他" value="other" />
                    </el-select>
                  </div>
                </div>
                <div class="emp-row-compact">
                  <div class="emp-field emp-field--stacked" style="flex:1">
                    <label>電話番号</label>
                    <el-input v-model="item.phone" size="small" placeholder="090-1234-5678" />
                  </div>
                </div>
              </div>
            </div>
          </div>
        </div>

        <!-- 添付書類カード -->
        <div class="emp-card">
          <div class="emp-card__header">
            <el-icon><Folder /></el-icon>
            <span>添付書類</span>
            <el-button size="small" text type="primary" @click="triggerUpload" class="emp-card__add" :disabled="!empId">
              <el-icon><Upload /></el-icon>
              アップロード
            </el-button>
          </div>
          <div class="emp-card__body">
            <div v-if="!model.attachments?.length" class="emp-empty">
              添付ファイルがありません
            </div>
            <div v-else class="emp-attach-grid">
              <div 
                v-for="(item, idx) in model.attachments" 
                :key="idx" 
                class="emp-attach-item"
              >
                <el-icon><Document /></el-icon>
                <span class="emp-attach-item__name" @click="openAttachment(item)">{{ item.fileName }}</span>
                <el-button size="small" text type="danger" @click="removeAttachment(idx)">
                  <el-icon><Delete /></el-icon>
                </el-button>
              </div>
            </div>
          </div>
        </div>

        </div>
      </div>
    </el-card>

    <!-- 银行选择弹窗 -->
    <el-dialog v-model="showBankPicker" title="銀行を選択" width="720px" append-to-body destroy-on-close>
      <BankBranchPicker mode="bank" @select="onPickBank" @cancel="showBankPicker=false" />
    </el-dialog>
    <el-dialog v-model="showBranchPicker" title="支店を選択" width="720px" append-to-body destroy-on-close>
      <BankBranchPicker mode="branch" :bank-code="currentBankItem?.bankCode" @select="onPickBranch" @cancel="showBranchPicker=false" />
    </el-dialog>

    <!-- 雇用区分管理弹窗 -->
    <el-dialog v-model="showEmploymentTypeManager" title="雇用区分の管理" width="500px" append-to-body destroy-on-close>
      <div class="master-manager">
        <div v-if="!employmentTypeOptions.length" class="master-empty">
          雇用区分がありません
        </div>
        <div v-else class="master-list">
          <div v-for="(item, idx) in employmentTypeOptions" :key="item.value" class="master-item">
            <div class="master-item__info">
              <span class="master-item__name">{{ item.label }}</span>
              <el-tag v-if="item.isContractor" size="small" type="warning">個人事業主</el-tag>
            </div>
            <div class="master-item__actions">
              <el-button size="small" text type="primary" @click="editEmploymentType(item)">
                <el-icon><Edit /></el-icon>
              </el-button>
              <el-button size="small" text type="danger" @click="deleteEmploymentType(item)">
                <el-icon><Delete /></el-icon>
              </el-button>
            </div>
          </div>
        </div>
        <div class="master-hint">
          <el-icon><InfoFilled /></el-icon>
          新規追加は契約の雇用区分欄に直接入力してください
        </div>
      </div>
    </el-dialog>

    <!-- 雇用区分編集弹窗 -->
    <el-dialog v-model="showEmploymentTypeEdit" title="雇用区分の編集" width="400px" append-to-body destroy-on-close>
      <el-form label-width="100px" label-position="left">
        <el-form-item label="名称">
          <el-input v-model="editingEmploymentType.name" placeholder="名称" />
        </el-form-item>
        <el-form-item label="個人事業主">
          <el-switch v-model="editingEmploymentType.isContractor" />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="showEmploymentTypeEdit = false">キャンセル</el-button>
        <el-button type="primary" @click="saveEmploymentType">保存</el-button>
      </template>
    </el-dialog>

    <!-- 役職管理弹窗 -->
    <el-dialog v-model="showPositionManager" title="役職の管理" width="450px" append-to-body destroy-on-close>
      <div class="master-manager">
        <div v-if="!positionOptions.length" class="master-empty">
          役職がありません
        </div>
        <div v-else class="master-list">
          <div v-for="(item, idx) in positionOptions" :key="item.value" class="master-item">
            <div class="master-item__info">
              <span class="master-item__name">{{ item.label }}</span>
            </div>
            <div class="master-item__actions">
              <el-button size="small" text type="primary" @click="editPosition(item)">
                <el-icon><Edit /></el-icon>
              </el-button>
              <el-button size="small" text type="danger" @click="deletePosition(item)">
                <el-icon><Delete /></el-icon>
              </el-button>
            </div>
          </div>
        </div>
        <div class="master-hint">
          <el-icon><InfoFilled /></el-icon>
          新規追加は所属部門の役職欄に直接入力してください
        </div>
      </div>
    </el-dialog>

    <!-- 役職編集弹窗 -->
    <el-dialog v-model="showPositionEdit" title="役職の編集" width="350px" append-to-body destroy-on-close>
      <el-form label-width="80px" label-position="left">
        <el-form-item label="名称">
          <el-input v-model="editingPosition.name" placeholder="役職名" />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="showPositionEdit = false">キャンセル</el-button>
        <el-button type="primary" @click="savePosition">保存</el-button>
      </template>
    </el-dialog>

    <!-- 隐藏文件输入 -->
    <input type="file" ref="fileInput" style="display:none" @change="onFileChosen" />
  </div>
</template>

<script setup lang="ts">
import { ref, reactive, computed, onMounted, watch } from 'vue'
import { useRoute } from 'vue-router'
import { ElMessage, ElMessageBox } from 'element-plus'
import { 
  User, Phone, Document, OfficeBuilding, FirstAidKit, 
  CreditCard, Warning, Folder, Plus, Delete, Upload, Check,
  Setting, Edit, InfoFilled, Money, UserFilled
} from '@element-plus/icons-vue'
import api from '../api'
import BankBranchPicker from '../components/BankBranchPicker.vue'

const props = defineProps<{ 
  empId?: string
  bare?: boolean
  readonly?: boolean
}>()

const emit = defineEmits<{
  (e: 'saved', id?: string): void
  (e: 'switchToEdit'): void
}>()

const route = useRoute()
const id = computed(() => props.empId || (route.params as any).id)
const empId = computed(() => id.value)
const bare = computed(() => props.bare === true)
const isReadonly = computed(() => props.readonly === true)

const loading = ref(false)
const saving = ref(false)
const error = ref('')

// 表单数据模型
const model = reactive({
  code: '',
  nameKanji: '',
  nameKana: '',
  gender: '',
  birthDate: '',
  nationality: 'JP',
  arriveJPDate: '',
  startWorkDate: '',
  myNumber: '',
  taxNo: '',
  contact: {
    phone: '',
    email: '',
    postalCode: '',
    address: ''
  },
  insurance: {
    hireInsuranceNo: '',
    endowNo: '',
    healthNo: '',
    endowBaseNo: '',
    joinDate: '',
    quitDate: ''
  },
  contracts: [] as any[],
  salaries: [] as any[],
  departments: [] as any[],
  bankAccounts: [] as any[],
  emergencies: [] as any[],
  attachments: [] as any[],
  dependents: [] as any[],
  primaryDepartmentName: ''
})

// 下拉选项
const departmentOptions = ref<{label:string, value:string}[]>([])
const employmentTypeOptions = ref<{label:string, value:string, isContractor?:boolean}[]>([])
const positionOptions = ref<{label:string, value:string}[]>([])

// 雇用区分管理
const showEmploymentTypeManager = ref(false)
const showEmploymentTypeEdit = ref(false)
const editingEmploymentType = ref<{ id: string, name: string, isContractor: boolean }>({ id: '', name: '', isContractor: false })
const employmentTypeIdMap = ref<Record<string, string>>({}) // value -> id mapping

// 役職管理
const showPositionManager = ref(false)
const showPositionEdit = ref(false)
const editingPosition = ref<{ id: string, name: string }>({ id: '', name: '' })
const positionIdMap = ref<Record<string, string>>({}) // value -> id mapping

// 银行选择
const showBankPicker = ref(false)
const showBranchPicker = ref(false)
const currentBankItem = ref<any>(null)

const fileInput = ref<HTMLInputElement|null>(null)

// 计算属性
const avatarText = computed(() => {
  const name = model.nameKanji || model.nameKana || ''
  return name.charAt(0) || '?'
})

const employmentStatus = computed(() => {
  if (!model.contracts?.length) return null
  const now = new Date().toISOString().slice(0, 10)
  const active = model.contracts.some(c => {
    const from = c.periodFrom || ''
    const to = c.periodTo || '9999-12-31'
    return from <= now && to >= now
  })
  return active ? 'active' : 'resigned'
})

// 判断契约是否当前有效
function isContractActive(item: any) {
  const now = new Date().toISOString().slice(0, 10)
  const from = item.periodFrom || ''
  const to = item.periodTo || '9999-12-31'
  return from <= now && to >= now
}

// 加载数据
async function reload() {
  loading.value = true
  error.value = ''
  try {
    await Promise.all([
      loadDepartments(),
      loadEmploymentTypes(),
      loadPositionTypes()
    ])
    if (id.value) {
      await loadEmployee()
    }
  } catch (e: any) {
    error.value = e?.response?.data?.error || e?.message || '読み込みに失敗しました'
  } finally {
    loading.value = false
  }
}

async function loadEmployee() {
  const r = await api.get(`/objects/employee/${id.value}`)
  const data = r.data?.payload || r.data || {}
  Object.assign(model, {
    code: data.code || '',
    nameKanji: data.nameKanji || '',
    nameKana: data.nameKana || '',
    gender: data.gender || '',
    birthDate: data.birthDate || '',
    nationality: data.nationality || 'JP',
    arriveJPDate: data.arriveJPDate || '',
    startWorkDate: data.startWorkDate || '',
    myNumber: data.myNumber || '',
    taxNo: data.taxNo || '',
    contact: {
      phone: data.contact?.phone || '',
      email: data.contact?.email || '',
      postalCode: data.contact?.postalCode || '',
      address: data.contact?.address || ''
    },
    insurance: {
      hireInsuranceNo: data.insurance?.hireInsuranceNo || '',
      endowNo: data.insurance?.endowNo || '',
      healthNo: data.insurance?.healthNo || '',
      endowBaseNo: data.insurance?.endowBaseNo || '',
      joinDate: data.insurance?.joinDate || '',
      quitDate: data.insurance?.quitDate || ''
    },
    contracts: Array.isArray(data.contracts) ? data.contracts : [],
    salaries: Array.isArray(data.salaries) ? data.salaries : [],
    departments: Array.isArray(data.departments) ? data.departments : [],
    bankAccounts: Array.isArray(data.bankAccounts) ? data.bankAccounts : [],
    emergencies: Array.isArray(data.emergencies) ? data.emergencies : [],
    attachments: Array.isArray(data.attachments) ? data.attachments : [],
    dependents: Array.isArray(data.dependents) ? data.dependents : [],
    primaryDepartmentName: data.primaryDepartmentName || ''
  })
  
  // 解析银行账户的银行名称和支店名称
  await resolveBankNames()
}

// 根据bankCode和branchCode查询银行名称和支店名称
async function resolveBankNames() {
  if (!model.bankAccounts?.length) return
  
  // 收集所有需要查询的银行代码和支店代码
  const bankCodes = [...new Set(model.bankAccounts.map(b => b.bankCode).filter(Boolean))]
  const branchKeys = [...new Set(model.bankAccounts.map(b => b.bankCode && b.branchCode ? `${b.bankCode}-${b.branchCode}` : '').filter(Boolean))]
  
  // 批量查询银行
  const bankMap: Record<string, string> = {}
  if (bankCodes.length) {
    const bankRes = await api.post('/objects/bank/search', {
      page: 1,
      pageSize: 100,
      where: [{ json: 'payload.bankCode', op: 'in', value: bankCodes }],
      orderBy: []
    })
    for (const b of bankRes.data?.data || []) {
      bankMap[b.payload?.bankCode] = b.payload?.name || ''
    }
  }
  
  // 批量查询支店
  const branchMap: Record<string, string> = {}
  if (branchKeys.length) {
    // 需要按银行分组查询支店
    for (const bankCode of bankCodes) {
      const branchCodes = model.bankAccounts
        .filter(b => b.bankCode === bankCode && b.branchCode)
        .map(b => b.branchCode)
      if (branchCodes.length) {
        const branchRes = await api.post('/objects/branch/search', {
          page: 1,
          pageSize: 100,
          where: [
            { json: 'payload.bankCode', op: 'eq', value: bankCode },
            { json: 'payload.branchCode', op: 'in', value: branchCodes }
          ],
          orderBy: []
        })
        for (const br of branchRes.data?.data || []) {
          branchMap[`${br.payload?.bankCode}-${br.payload?.branchCode}`] = br.payload?.branchName || ''
        }
      }
    }
  }
  
  // 填充银行名称和支店名称
  for (const acc of model.bankAccounts) {
    if (acc.bankCode && !acc.bank) {
      acc.bank = bankMap[acc.bankCode] || ''
    }
    if (acc.bankCode && acc.branchCode && !acc.branch) {
      acc.branch = branchMap[`${acc.bankCode}-${acc.branchCode}`] || ''
    }
  }
}

async function loadDepartments() {
  let page = 1
  const pageSize = 500
  let all: any[] = []
  while (true) {
    const r = await api.post('/objects/department/search', { 
      page, 
      pageSize, 
      where: [], 
      // 按path排序以保证父部门在子部门之前，子部门紧跟父部门
      orderBy: [{ field: 'path', dir: 'ASC' }] 
    })
    const list = r.data?.data || []
    all = all.concat(list)
    if (list.length < pageSize) break
    page++
  }
  departmentOptions.value = all.map(x => {
    const name = x.name || x.payload?.name || ''
    const code = x.department_code || x.payload?.code || ''
    const level = typeof x.level === 'number' ? x.level : (typeof x.payload?.level === 'number' ? x.payload.level : 0)
    // level从1开始，顶级部门(level=1)不缩进，子部门(level>1)缩进level-1次
    const indent = level > 1 ? '　'.repeat(level - 1) : ''
    return { label: `${indent}${name} (${code})`, value: x.id }
  })
}

async function loadEmploymentTypes() {
  try {
    const r = await api.post('/objects/employment_type/search', { 
      page: 1, 
      pageSize: 200, 
      where: [], 
      orderBy: [{ field: 'type_code', dir: 'ASC' }] 
    })
    const list = r.data?.data || []
    const idMap: Record<string, string> = {}
    employmentTypeOptions.value = list.map((x: any) => {
      const name = x.payload?.name || x.name || x.type_code || ''
      idMap[name] = x.id
      return {
        label: name,
        value: name,
        isContractor: x.payload?.isContractor === true
      }
    })
    employmentTypeIdMap.value = idMap
  } catch {}
}

async function loadPositionTypes() {
  try {
    const r = await api.post('/objects/position_type/search', { 
      page: 1, 
      pageSize: 200, 
      where: [{ json: 'isActive', op: 'neq', value: false }], 
      orderBy: [{ field: 'payload->>level', dir: 'ASC' }, { field: 'payload->>name', dir: 'ASC' }] 
    })
    const list = r.data?.data || []
    const idMap: Record<string, string> = {}
    positionOptions.value = list.map((x: any) => {
      const name = x.payload?.name || ''
      idMap[name] = x.id
      return {
        label: name,
        value: name
      }
    })
    positionIdMap.value = idMap
  } catch {}
}

// 当用户输入新的职务时，自动创建职务记录
async function onPositionChange(val: string, item: any) {
  if (!val?.trim()) return
  // 检查是否已存在于选项中
  const exists = positionOptions.value.some(opt => opt.value === val)
  if (!exists) {
    // 创建新的职务记录
    try {
      await api.post('/objects/position_type', { 
        payload: { name: val.trim(), isActive: true } 
      })
      // 重新加载职务列表
      await loadPositionTypes()
    } catch {
      // 忽略错误，可能是重复等
    }
  }
}

// 雇用区分管理
function openEmploymentTypeManager() {
  showEmploymentTypeManager.value = true
}

function editEmploymentType(item: any) {
  const id = employmentTypeIdMap.value[item.value]
  if (!id) return
  editingEmploymentType.value = { id, name: item.label, isContractor: item.isContractor || false }
  showEmploymentTypeEdit.value = true
}

async function saveEmploymentType() {
  if (!editingEmploymentType.value.id || !editingEmploymentType.value.name?.trim()) return
  try {
    await api.put(`/objects/employment_type/${editingEmploymentType.value.id}`, {
      payload: { 
        name: editingEmploymentType.value.name.trim(),
        isContractor: editingEmploymentType.value.isContractor,
        isActive: true
      }
    })
    ElMessage.success('更新しました')
    showEmploymentTypeEdit.value = false
    await loadEmploymentTypes()
  } catch (e: any) {
    ElMessage.error(e?.response?.data?.error || '更新に失敗しました')
  }
}

async function deleteEmploymentType(item: any) {
  const id = employmentTypeIdMap.value[item.value]
  if (!id) return
  
  // 检查是否被使用
  try {
    const r = await api.post('/objects/employee/search', {
      page: 1,
      pageSize: 1,
      where: [{ json: 'contracts[*].employmentTypeCode', op: 'eq', value: item.value }],
      orderBy: []
    })
    if (r.data?.data?.length > 0) {
      ElMessage.error(`「${item.label}」は社員データで使用されているため削除できません`)
      return
    }
  } catch {}
  
  try {
    await ElMessageBox.confirm(
      `「${item.label}」を削除しますか？`,
      '削除確認',
      { confirmButtonText: '削除', cancelButtonText: 'キャンセル', type: 'warning' }
    )
    await api.delete(`/objects/employment_type/${id}`)
    ElMessage.success('削除しました')
    await loadEmploymentTypes()
  } catch {
    // cancelled
  }
}

// 役職管理
function openPositionManager() {
  showPositionManager.value = true
}

function editPosition(item: any) {
  const id = positionIdMap.value[item.value]
  if (!id) return
  editingPosition.value = { id, name: item.label }
  showPositionEdit.value = true
}

async function savePosition() {
  if (!editingPosition.value.id || !editingPosition.value.name?.trim()) return
  try {
    await api.put(`/objects/position_type/${editingPosition.value.id}`, {
      payload: { 
        name: editingPosition.value.name.trim(),
        isActive: true
      }
    })
    ElMessage.success('更新しました')
    showPositionEdit.value = false
    await loadPositionTypes()
  } catch (e: any) {
    ElMessage.error(e?.response?.data?.error || '更新に失敗しました')
  }
}

async function deletePosition(item: any) {
  const id = positionIdMap.value[item.value]
  if (!id) return
  
  // 检查是否被使用
  try {
    const r = await api.post('/objects/employee/search', {
      page: 1,
      pageSize: 1,
      where: [{ json: 'departments[*].position', op: 'eq', value: item.value }],
      orderBy: []
    })
    if (r.data?.data?.length > 0) {
      ElMessage.error(`「${item.label}」は社員データで使用されているため削除できません`)
      return
    }
  } catch {}
  
  try {
    await ElMessageBox.confirm(
      `「${item.label}」を削除しますか？`,
      '削除確認',
      { confirmButtonText: '削除', cancelButtonText: 'キャンセル', type: 'warning' }
    )
    await api.delete(`/objects/position_type/${id}`)
    ElMessage.success('削除しました')
    await loadPositionTypes()
  } catch {
    // cancelled
  }
}

// 判断当前员工是否是个人事业主（根据当前有效契约的雇用区分）
const isContractorEmployee = computed(() => {
  if (!model.contracts?.length) return false
  const now = new Date().toISOString().slice(0, 10)
  // 找到当前有效的契约
  const activeContract = model.contracts.find(c => {
    const from = c.periodFrom || ''
    const to = c.periodTo || '9999-12-31'
    return from <= now && to >= now
  }) || model.contracts[0]
  
  if (!activeContract?.employmentTypeCode) return false
  
  // 检查该雇用区分是否是个人事业主
  const typeOpt = employmentTypeOptions.value.find(
    opt => opt.value === activeContract.employmentTypeCode || opt.label === activeContract.employmentTypeCode
  )
  return typeOpt?.isContractor === true
})

// 数组操作
function addContract() {
  model.contracts.push({
    employmentTypeCode: '',
    periodFrom: '',
    periodTo: '',
    note: ''
  })
}
function removeContract(idx: number) {
  model.contracts.splice(idx, 1)
}

function addSalary() {
  model.salaries.push({
    startDate: new Date().toISOString().slice(0, 10),
    description: ''
  })
}
function removeSalary(idx: number) {
  model.salaries.splice(idx, 1)
}

function isSalaryActive(item: any) {
  if (!item.startDate) return false
  const now = new Date().toISOString().slice(0, 10)
  // 找到所有开始日期小于等于今天的工资记录
  const validSalaries = model.salaries
    .filter(s => s.startDate && s.startDate <= now)
    .sort((a, b) => b.startDate.localeCompare(a.startDate))
  // 当前有效的是最近的一个
  return validSalaries[0]?.startDate === item.startDate
}

function addDepartment() {
  model.departments.push({
    departmentId: '',
    position: '',
    fromDate: '',
    toDate: ''
  })
}
function removeDepartment(idx: number) {
  model.departments.splice(idx, 1)
}

function addBankAccount() {
  model.bankAccounts.push({
    bank: '',
    branch: '',
    bankCode: '',
    branchCode: '',
    accountType: 'ordinary',
    accountNo: '',
    holder: ''
  })
}
function removeBankAccount(idx: number) {
  model.bankAccounts.splice(idx, 1)
}

function addEmergency() {
  model.emergencies.push({
    nameKanji: '',
    nameKana: '',
    relation: '',
    phone: '',
    address: ''
  })
}
function removeEmergency(idx: number) {
  model.emergencies.splice(idx, 1)
}

function addDependent() {
  model.dependents.push({
    nameKana: '',
    nameKanji: '',
    birthDate: '',
    gender: '',
    cohabiting: true,
    relation: '',
    address: ''
  })
}
function removeDependent(idx: number) {
  model.dependents.splice(idx, 1)
}

function removeAttachment(idx: number) {
  model.attachments.splice(idx, 1)
}

// 银行选择
function openBankPicker(item: any) {
  currentBankItem.value = item
  showBankPicker.value = true
}
function openBranchPicker(item: any) {
  currentBankItem.value = item
  showBranchPicker.value = true
}
function onPickBank(row: any) {
  if (currentBankItem.value) {
    currentBankItem.value.bank = row?.payload?.name || row?.name || ''
    currentBankItem.value.bankCode = row?.bank_code || row?.payload?.bankCode || ''
    currentBankItem.value.branch = ''
    currentBankItem.value.branchCode = ''
  }
  showBankPicker.value = false
}
function onPickBranch(row: any) {
  if (currentBankItem.value) {
    currentBankItem.value.branch = row?.payload?.branchName || row?.payload?.name || ''
    currentBankItem.value.branchCode = row?.branch_code || row?.payload?.branchCode || ''
  }
  showBranchPicker.value = false
}

// 附件上传
function triggerUpload() {
  if (!empId.value) {
    ElMessage.error('先に社員情報を保存してください')
    return
  }
  fileInput.value?.click()
}

async function onFileChosen(e: Event) {
  const input = e.target as HTMLInputElement
  if (!input?.files?.length) return
  if (!empId.value) {
    ElMessage.error('先に社員情報を保存してください')
    return
  }
  const f = input.files[0]
  try {
    const name = f.name || `file_${Date.now()}`
    const r = await api.post(`/employees/${empId.value}/attachments`, f, {
      headers: {
        'Content-Type': f.type || 'application/octet-stream',
        'X-File-Name': encodeURIComponent(name)
      }
    })
    const payload = r.data?.payload || r.data
    if (payload?.attachments) {
      model.attachments = payload.attachments
    }
    ElMessage.success('アップロードしました')
  } catch (e: any) {
    ElMessage.error(e?.response?.data?.error || 'アップロードに失敗しました')
  } finally {
    input.value = ''
  }
}

function openAttachment(item: any) {
  if (item?.url) {
    window.open(item.url, '_blank')
  }
}

function normalizeDependents() {
  const cleaned = (model.dependents || []).map(d => ({
    ...d,
    nameKana: (d.nameKana || '').trim(),
    nameKanji: (d.nameKanji || '').trim(),
    relation: (d.relation || '').trim(),
    address: (d.address || '').trim(),
    birthDate: d.birthDate || '',
    gender: d.gender || '',
    cohabiting: typeof d.cohabiting === 'boolean' ? d.cohabiting : true
  }))
  const isEmpty = (d: any) =>
    !d.nameKana && !d.nameKanji && !d.birthDate && !d.gender && !d.relation && !d.address
  const filtered = cleaned.filter(d => !isEmpty(d))
  const invalidIndexes = filtered
    .map((d, idx) => {
      const hasName = !!(d.nameKana || d.nameKanji)
      const missing = !hasName || !d.birthDate || !d.gender || !d.relation || !d.address
      return missing ? idx + 1 : 0
    })
    .filter(n => n > 0)
  return { filtered, invalidIndexes }
}

// 校验
function validate(): string[] {
  const errs: string[] = []
  if (!model.nameKanji?.trim()) errs.push('氏名（漢字）は必須です')
  if (!model.nameKana?.trim()) errs.push('氏名（カナ）は必須です')
  if (!model.gender) errs.push('性別は必須です')
  if (!model.birthDate) errs.push('生年月日は必須です')
  if (!model.nationality) errs.push('国籍は必須です')
  
  const myNumber = (model.myNumber || '').trim()
  if (myNumber && !/^\d{12}$/.test(myNumber)) {
    errs.push('マイナンバーは12桁の数字で入力してください')
  }
  
  return errs
}

// 保存
async function handleSave() {
  const { filtered, invalidIndexes } = normalizeDependents()
  if (invalidIndexes.length > 0) {
    ElMessage.error(`扶養親族の入力が不足しています（行: ${invalidIndexes.join(', ')}）。すべて入力してから保存してください`)
    return
  }
  if (filtered.length !== model.dependents.length) {
    model.dependents = filtered
  }

  const errs = validate()
  if (errs.length > 0) {
    ElMessage.error(errs[0])
    return
  }
  
  saving.value = true
  try {
    const payload = buildPayload()
    let savedId: string | undefined = empId.value
    if (empId.value) {
      await api.put(`/objects/employee/${empId.value}`, { payload })
    } else {
      const r = await api.post('/objects/employee', { payload })
      savedId = r.data?.id || r.data?.data?.id
    }
    ElMessage.success('保存しました')
    emit('saved', savedId)
  } catch (e: any) {
    ElMessage.error(e?.response?.data?.error || '保存に失敗しました')
  } finally {
    saving.value = false
  }
}

function buildPayload() {
  const toYmd = (v: any) => {
    if (!v || typeof v !== 'string') return v
    const m = v.trim().match(/^(\d{4})[\/.\-](\d{1,2})[\/.\-](\d{1,2})$/)
    if (!m) return v
    return `${m[1]}-${m[2].padStart(2, '0')}-${m[3].padStart(2, '0')}`
  }
  
  return {
    code: model.code || `E${Date.now().toString().slice(-8)}`,
    nameKanji: model.nameKanji,
    nameKana: model.nameKana,
    gender: model.gender,
    birthDate: toYmd(model.birthDate),
    nationality: model.nationality,
    arriveJPDate: toYmd(model.arriveJPDate),
    startWorkDate: toYmd(model.startWorkDate),
    myNumber: model.myNumber,
    taxNo: model.taxNo,
    contact: { ...model.contact },
    insurance: { ...model.insurance },
    contracts: model.contracts.map(c => ({
      ...c,
      periodFrom: toYmd(c.periodFrom),
      periodTo: toYmd(c.periodTo)
    })),
    salaries: model.salaries.map(s => ({
      ...s,
      startDate: toYmd(s.startDate)
    })),
    departments: model.departments.map(d => ({
      ...d,
      fromDate: toYmd(d.fromDate),
      toDate: toYmd(d.toDate)
    })),
    bankAccounts: [...model.bankAccounts],
    emergencies: [...model.emergencies],
    attachments: [...model.attachments],
    dependents: model.dependents.map(d => ({
      ...d,
      nameKana: (d.nameKana || '').trim(),
      nameKanji: (d.nameKanji || '').trim(),
      relation: (d.relation || '').trim(),
      address: (d.address || '').trim(),
      birthDate: toYmd(d.birthDate)
    }))
  }
}

onMounted(reload)
</script>

<style scoped>
/* 外层包装 */
.emp-form-wrap {
  min-width: 1000px;
  max-width: 1400px;
}

.emp-form-card {
  border-radius: 8px;
  overflow: visible;
}

/* 页面头部 - 与其他弹窗一致 */
.page-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.page-header-left {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.page-header-title {
  font-size: 18px;
  font-weight: 600;
  color: #303133;
}

.page-header-meta {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}

.page-header-name {
  font-size: 14px;
  font-weight: 500;
  color: #606266;
}

.page-header-kana {
  font-size: 13px;
  color: #909399;
}

.detail-header {
  padding: 0;
}

.detail-header-actions {
  display: flex;
  align-items: center;
  gap: 8px;
}

/* 加载和错误状态 */
.emp-loading, .emp-error {
  padding: 40px;
}

/* 内容区域 */
.emp-content {
  display: grid;
  grid-template-columns: 1fr 420px;
  gap: 20px;
}
@media (max-width: 1200px) {
  .emp-content {
    grid-template-columns: 1fr;
  }
}

.emp-main {
  display: flex;
  flex-direction: column;
  gap: 20px;
}
.emp-side {
  display: flex;
  flex-direction: column;
  gap: 16px;
}

/* 卡片样式 */
.emp-card {
  background: white;
  border-radius: 12px;
  box-shadow: 0 2px 12px rgba(0, 0, 0, 0.06);
  overflow: hidden;
}
.emp-card--compact .emp-card__body {
  padding: 16px 20px;
}
.emp-card__header {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 14px 20px;
  background: linear-gradient(90deg, #f8f9fc 0%, #ffffff 100%);
  border-bottom: 1px solid #ebeef5;
  font-weight: 600;
  font-size: 14px;
  color: #303133;
}
.emp-card__header .el-icon {
  color: #667eea;
}
.emp-card__add {
  margin-left: auto;
}
.emp-card__body {
  padding: 20px;
}

/* 表单行 */
.emp-row {
  display: flex;
  gap: 16px;
  margin-bottom: 16px;
  flex-wrap: wrap;
}
.emp-row:last-child {
  margin-bottom: 0;
}

/* 紧凑表单行（用于右侧栏） */
.emp-row-compact {
  display: flex;
  gap: 8px;
  margin-bottom: 8px;
}
.emp-row-compact:last-child {
  margin-bottom: 0;
}

/* 字段样式 */
.emp-field {
  flex: 1;
  min-width: 0;
}
.emp-field label {
  display: block;
  font-size: 13px;
  color: #606266;
  margin-bottom: 6px;
  font-weight: 500;
}
.emp-field--required label::after {
  content: ' *';
  color: #f56c6c;
}
.emp-field--stacked {
  margin-bottom: 12px;
}
.emp-field--stacked:last-child {
  margin-bottom: 0;
}

/* 迷你字段（用于数组项内） */
.emp-mini-field {
  flex: 1;
  min-width: 0;
}
.emp-mini-field label {
  display: flex;
  align-items: center;
  font-size: 12px;
  color: #909399;
  margin-bottom: 4px;
  height: 18px;
}

/* 列表项 */
.emp-list {
  display: flex;
  flex-direction: column;
  gap: 12px;
}
.emp-list__item {
  display: flex;
  gap: 12px;
  padding: 12px 16px;
  background: #f8f9fc;
  border-radius: 8px;
  border: 1px solid #ebeef5;
  transition: all 0.2s;
}
.emp-list__item:hover {
  border-color: #c0c4cc;
}
.emp-list__item--inline {
  align-items: flex-start;
}
.emp-list__item--active {
  border-color: #67c23a;
  background: #f0f9eb;
}
.emp-list__main {
  flex: 1;
  min-width: 0;
}
.emp-list__row {
  display: flex;
  gap: 12px;
  margin-bottom: 8px;
}
.emp-list__row:last-child {
  margin-bottom: 0;
}

/* 银行账户列表 */
.emp-bank-list {
  display: flex;
  flex-direction: column;
  gap: 12px;
}
.emp-bank-item {
  padding: 12px;
  background: #f8f9fc;
  border-radius: 8px;
  border: 1px solid #ebeef5;
}
.emp-bank-item__header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 10px;
}
.emp-bank-item__title {
  font-size: 12px;
  font-weight: 600;
  color: #606266;
}

/* 带按钮的输入框 */
.emp-input-with-btn {
  display: flex;
  gap: 6px;
}
.emp-input-with-btn .el-input {
  flex: 1;
}

/* 附件网格（左侧主区域） */
.emp-attach-grid {
  display: flex;
  flex-wrap: wrap;
  gap: 10px;
}
.emp-attach-item {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 10px 14px;
  background: #f5f7fa;
  border-radius: 8px;
  font-size: 13px;
  border: 1px solid #ebeef5;
  transition: all 0.2s;
}
.emp-attach-item:hover {
  border-color: #c0c4cc;
  background: #f0f2f5;
}
.emp-attach-item .el-icon {
  color: #909399;
  font-size: 16px;
}
.emp-attach-item__name {
  max-width: 200px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  cursor: pointer;
  color: #409eff;
}
.emp-attach-item__name:hover {
  text-decoration: underline;
}

/* 空状态 */
.emp-empty {
  text-align: center;
  color: #909399;
  font-size: 13px;
  padding: 20px 0;
}

/* 字段设置按钮 */
.field-settings-btn {
  padding: 0;
  margin-left: 2px;
  height: auto;
  line-height: 1;
  vertical-align: baseline;
}
.field-settings-btn .el-icon {
  font-size: 11px;
}

/* 主数据管理弹窗 */
.master-manager {
  min-height: 100px;
}
.master-empty {
  text-align: center;
  color: #909399;
  padding: 30px 0;
}
.master-list {
  max-height: 300px;
  overflow-y: auto;
}
.master-item {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding: 10px 12px;
  border-bottom: 1px solid #ebeef5;
}
.master-item:last-child {
  border-bottom: none;
}
.master-item:hover {
  background: #f5f7fa;
}
.master-item__info {
  display: flex;
  align-items: center;
  gap: 8px;
}
.master-item__name {
  font-size: 14px;
  color: #303133;
}
.master-item__actions {
  display: flex;
  gap: 4px;
}
.master-hint {
  display: flex;
  align-items: center;
  gap: 6px;
  margin-top: 16px;
  padding: 10px 12px;
  background: #f4f4f5;
  border-radius: 6px;
  font-size: 12px;
  color: #909399;
}
.master-hint .el-icon {
  font-size: 14px;
}

/* 只读模式样式 */
.emp-content--readonly {
  pointer-events: none;
}
.emp-content--readonly :deep(.el-input__wrapper),
.emp-content--readonly :deep(.el-textarea__inner),
.emp-content--readonly :deep(.el-select .el-input__wrapper),
.emp-content--readonly :deep(.el-date-editor .el-input__wrapper) {
  background-color: #f5f7fa;
  cursor: default;
}
.emp-content--readonly :deep(.el-button),
.emp-content--readonly :deep(.el-checkbox),
.emp-content--readonly :deep(.el-radio) {
  pointer-events: none;
  opacity: 0.8;
}
.emp-content--readonly :deep(.contract-remove-btn),
.emp-content--readonly :deep(.dept-remove-btn),
.emp-content--readonly :deep(.add-contract-btn),
.emp-content--readonly :deep(.add-dept-btn) {
  display: none;
}
</style>

