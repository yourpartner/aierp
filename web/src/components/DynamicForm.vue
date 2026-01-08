<template>
  <div>
    <template v-for="(block, idx) in layout" :key="idx">
      <!-- 网格块 -->
      <el-row v-if="block.type==='grid'" :gutter="12" style="margin-bottom:8px;">
        <template v-for="(col, cidx) in block.cols" :key="cidx">
          <el-col :span="col.span || 6" v-if="isVisible(col)">
            <el-form-item :label="labelOf(col)" :required="isRequired(col)">
              <!-- 按钮型控件：不参与 v-model，仅触发 action -->
              <template v-if="col.widget==='button'">
                <el-button :type="col.props?.type || 'default'" :disabled="isDisabled(col)" @click="emitAction(col)">{{ buttonLabel(col) }}</el-button>
              </template>
              <!-- 纯文本展示控件 -->
              <template v-else-if="col.widget==='text'">
                <span>{{ displayText(col) }}</span>
              </template>
              <template v-else-if="col.widget==='label'">
                <span class="df-label">{{ formatLabelValue(getByPath(props.model, col.field), col, { model: props.model }) }}</span>
              </template>
              <!-- 隐藏字段：不渲染任何可见元素 -->
              <template v-else-if="col.widget==='hidden'">
                <!-- 隐藏字段，不渲染 -->
              </template>
              <template v-else>
                <!-- 数组字段渲染（通用表格编辑） -->
                <template v-if="isArrayField(col)">
                  <div class="array-editor">
                    <div style="display:flex;gap:8px;align-items:center">
                      <el-button v-if="!isAddDisabled(col.field)" size="small" @click="addArrayRow(col.field)">{{ col?.props?.addRowText || dynamicText.value.addRow }}</el-button>
                      <template v-if="arrayToolbar(col.field).length>0">
                        <el-button v-for="btn in arrayToolbar(col.field)" :key="btn.text" size="small" :type="btn.type || 'primary'" :disabled="isToolbarDisabled(btn)" @click="emitArrayToolbar(btn, col.field)">{{ btn.text }}</el-button>
                      </template>
                    </div>
                    <el-table :data="getArray(col.field)" size="small" border style="margin-top:6px">
                      <el-table-column v-for="c in arrayColumns(col.field)" :key="c.field" :label="translateSchemaLabel(c.label, c.field, 'field')" :width="c.width">
                        <template #default="{ row }">
                          <template v-if="(c.widget||'')==='button'">
                            <el-button size="small" :type="c.props?.type || 'default'" @click="emitArrayAction(col.field, c, row)">{{ c.props?.text || translateSchemaLabel(c.label, c.field, 'button') || dynamicText.value.button }}</el-button>
                          </template>
                          <template v-else-if="(c.widget||'')==='labelButton' || c?.props?.action">
                            <div style="display:flex;align-items:center;gap:6px">
                              <span>{{ row[c.field] ?? '' }}</span>
                              <el-button size="small" :type="c.props?.type || 'default'" @click="emitArrayAction(col.field, c, row)">{{ c.props?.text || dynamicText.value.select }}</el-button>
                            </div>
                          </template>
                          <template v-else-if="(c.widget||'')==='label'">
                            <span class="df-label">{{ formatLabelValue(row[c.field], c, { row, model: props.model }) }}</span>
                          </template>
                          <template v-else-if="(c.widget||'')==='select'">
                            <el-select v-model="row[c.field]" filterable clearable style="width:100%" :disabled="(props as any).readonly || c.props?.disabled" v-bind="selectOnlyProps(c)">
                              <el-option v-for="opt in selectOptionsForArray(c)" :key="opt.value" :label="opt.label" :value="opt.value" />
                            </el-select>
                          </template>
                          <template v-else>
                            <component :is="c.widget ? widgetByName(c.widget) : (c.inputType==='date' ? 'el-date-picker' : (c.inputType==='number' ? 'el-input' : autoWidget(c.field)))"
                                       v-model="row[c.field]"
                                       :type="c.inputType==='number' ? 'number' : (c.inputType || 'text')"
                                       :value-format="c.valueFormat || (c.inputType==='date' ? 'YYYY-MM-DD' : undefined)"
                                       :disabled="(props as any).readonly || c.props?.disabled"
                                       v-bind="{ ...(c.props || {}), disabled: ((props as any).readonly || c.props?.disabled) }"
                                       style="width:100%" />
                          </template>
                        </template>
                      </el-table-column>
                      <el-table-column :label="dynamicText.value.actions" width="80">
                        <template #default="{ $index }">
                          <el-button size="small" type="danger" :disabled="(props as any).readonly" @click="removeArrayRow(col.field, $index)">{{ col?.removeText || dynamicText.value.removeRow || '削除' }}</el-button>
                        </template>
                      </el-table-column>
                    </el-table>
                  </div>
                </template>
                <template v-else>
                  <component
                    :is="widgetOf(col)"
                    :model-value="getByPath(props.model, col.field)"
                    @update:model-value="v => setByPath(props.model, col.field, filterValue(col, v))"
                    :root-model="props.model"
                    :set-path="(p:any,v:any)=>setByPath(props.model,p,v)"
                    v-bind="{ ...(col.props || {}), disabled: isDisabled(col) }"
                    style="width:100%"
                  >
                    <template v-if="col.widget==='select'">
                      <el-option v-for="opt in selectOptions(col)" :key="opt.value" :label="opt.label" :value="opt.value" />
                    </template>
                  </component>
                </template>
              </template>
            </el-form-item>
          </el-col>
        </template>
      </el-row>

      <template v-else-if="block.type==='table'">
        <div class="array-editor">
          <div style="display:flex;gap:8px;align-items:center">
            <el-button v-if="!isAddDisabled(block.field)" size="small" @click="addArrayRow(block.field)">{{ block?.props?.addRowText || dynamicText.value.addRow }}</el-button>
            <template v-if="arrayToolbar(block.field).length>0">
              <el-button
                v-for="btn in arrayToolbar(block.field)"
                :key="btn.text"
                size="small"
                :type="btn.type || 'primary'"
                :disabled="isToolbarDisabled(btn)"
                @click="emitArrayToolbar(btn, block.field)"
              >
                {{ btn.text }}
              </el-button>
            </template>
          </div>
          <el-table :data="getArray(block.field)" size="small" border style="margin-top:6px">
            <el-table-column
              v-for="c in arrayColumns(block.field)"
              :key="c.field"
              :label="translateSchemaLabel(c.label, c.field, 'field')"
              :width="c.width"
            >
              <template #default="{ row }">
                <template v-if="(c.widget||'')==='button'">
                  <el-button size="small" :type="c.props?.type || 'default'" @click="emitArrayAction(block.field, c, row)">
                    {{ c.props?.text || translateSchemaLabel(c.label, c.field, 'button') || dynamicText.value.button }}
                  </el-button>
                </template>
                <template v-else-if="(c.widget||'')==='labelButton' || c?.props?.action">
                  <div style="display:flex;align-items:center;gap:6px">
                    <span>{{ row[c.field] ?? '' }}</span>
                    <el-button size="small" :type="c.props?.type || 'default'" @click="emitArrayAction(block.field, c, row)">{{ c.props?.text || dynamicText.value.select }}</el-button>
                  </div>
                </template>
                <template v-else-if="(c.widget||'')==='label'">
                  <span class="df-label">{{ formatLabelValue(row[c.field], c, { row, model: props.model }) }}</span>
                </template>
                <template v-else-if="(c.widget||'')==='select'">
                  <el-select v-model="row[c.field]" filterable clearable style="width:100%" :disabled="(props as any).readonly || c.props?.disabled" v-bind="selectOnlyProps(c)">
                    <el-option v-for="opt in selectOptionsForArray(c)" :key="opt.value" :label="opt.label" :value="opt.value" />
                  </el-select>
                </template>
                <template v-else>
                  <component
                    :is="c.widget ? widgetByName(c.widget) : (c.inputType==='date' ? 'el-date-picker' : (c.inputType==='number' ? 'el-input' : autoWidget(c.field)))"
                    v-model="row[c.field]"
                    :type="c.inputType==='number' ? 'number' : (c.inputType || 'text')"
                    :value-format="c.valueFormat || (c.inputType==='date' ? 'YYYY-MM-DD' : undefined)"
                    :disabled="(props as any).readonly || c.props?.disabled"
                    v-bind="{ ...(c.props || {}), disabled: ((props as any).readonly || c.props?.disabled) }"
                    style="width:100%"
                  />
                </template>
              </template>
            </el-table-column>
            <el-table-column :label="dynamicText.value.actions" width="80">
              <template #default="{ $index }">
                <el-button size="small" type="danger" :disabled="(props as any).readonly" @click="removeArrayRow(block.field, $index)">{{ block?.removeText || dynamicText.value.removeRow || '削除' }}</el-button>
              </template>
            </el-table-column>
          </el-table>
        </div>
      </template>

      <!-- 明细行编辑（lines 类型，与 table 类似但专用于订单明细） -->
      <template v-else-if="block.type==='lines'">
        <div class="lines-editor">
          <div class="lines-header">
            <span class="lines-title">{{ translateSchemaLabel(block.label, block.field, 'field') || '明細' }}</span>
            <el-button size="small" type="primary" :disabled="(props as any).readonly" @click="addArrayRow(block.field)">{{ block.addButtonText || dynamicText.value.addRow }}</el-button>
          </div>
          <el-table :data="getArray(block.field)" size="small" border style="margin-top:8px">
            <el-table-column
              v-for="c in (block.columns || [])"
              :key="c.field"
              :label="translateSchemaLabel(c.label, c.field, 'field')"
              :width="c.width"
              :min-width="c.minWidth || 80"
            >
              <template #default="{ row, $index }">
                <template v-if="c.widget==='select'">
                  <el-select v-model="row[c.field]" filterable clearable style="width:100%" :disabled="(props as any).readonly || c.props?.disabled" v-bind="selectOnlyProps(c)">
                    <el-option v-for="opt in (c.props?.options || [])" :key="opt.value" :label="opt.label" :value="opt.value" />
                  </el-select>
                </template>
                <template v-else-if="c.widget==='number'">
                  <el-input-number
                    v-model="row[c.field]"
                    :disabled="(props as any).readonly || c.props?.disabled"
                    :min="c.props?.min"
                    :max="c.props?.max"
                    :precision="c.props?.precision || 2"
                    :controls="false"
                    style="width:100%"
                  />
                </template>
                <template v-else>
                  <el-input
                    v-model="row[c.field]"
                    :disabled="(props as any).readonly || c.props?.disabled"
                    :placeholder="c.props?.placeholder"
                    style="width:100%"
                  />
                </template>
              </template>
            </el-table-column>
            <el-table-column label="操作" width="70" fixed="right">
              <template #default="{ $index }">
                <el-button size="small" type="danger" text :disabled="(props as any).readonly" @click="removeArrayRow(block.field, $index)">削除</el-button>
              </template>
            </el-table-column>
          </el-table>
        </div>
      </template>

      <!-- 分区块：带标题的分组，内部再放 layout（grid 等） -->
      <template v-else-if="block.type==='section'">
        <el-divider content-position="left">{{ translateSectionTitle(block.title) }}</el-divider>
        <template v-for="(sub, sidx) in (block.layout || [])" :key="'s'+idx+'-'+sidx">
          <el-row v-if="sub.type==='grid'" :gutter="12" style="margin-bottom:8px;">
            <template v-for="(col, cidx) in sub.cols" :key="cidx">
              <el-col :span="col.span || 6" v-if="isVisible(col)">
                <el-form-item :label="labelOf(col)" :required="isRequired(col)">
                  <template v-if="col.widget==='button'">
                    <el-button :type="col.props?.type || 'default'" :disabled="isDisabled(col)" @click="emitAction(col)">{{ buttonLabel(col) }}</el-button>
                  </template>
                  <template v-else-if="col.widget==='text'">
                    <span>{{ displayText(col) }}</span>
                  </template>
                  <template v-else-if="col.widget==='label'">
                    <span class="df-label">{{ formatLabelValue(getByPath(props.model, col.field), col, { model: props.model }) }}</span>
                  </template>
                  <template v-else>
                    <component
                      :is="widgetOf(col)"
                      :model-value="getByPath(props.model, col.field)"
                      @update:model-value="v => setByPath(props.model, col.field, filterValue(col, v))"
                      :root-model="props.model"
                      :set-path="(p:any,v:any)=>setByPath(props.model,p,v)"
                        v-bind="{ ...(col.props || {}), disabled: isDisabled(col) }"
                      style="width:100%"
                    >
                      <template v-if="col.widget==='select'">
                        <el-option v-for="opt in selectOptions(col)" :key="opt.value" :label="opt.label" :value="opt.value" />
                      </template>
                    </component>
                  </template>
                </el-form-item>
              </el-col>
            </template>
          </el-row>
          <template v-else-if="sub.type==='table'">
            <div class="array-editor">
              <div style="display:flex;gap:8px;align-items:center">
                <el-button v-if="!isAddDisabled(sub.field)" size="small" @click="addArrayRow(sub.field)">{{ sub?.props?.addRowText || dynamicText.value.addRow }}</el-button>
                <template v-if="arrayToolbar(sub.field).length>0">
                  <el-button
                    v-for="btn in arrayToolbar(sub.field)"
                    :key="btn.text"
                    size="small"
                    :type="btn.type || 'primary'"
                    :disabled="isToolbarDisabled(btn)"
                    @click="emitArrayToolbar(btn, sub.field)"
                  >
                    {{ btn.text }}
                  </el-button>
                </template>
              </div>
              <el-table :data="getArray(sub.field)" size="small" border style="margin-top:6px;">
                <el-table-column
                  v-for="c in arrayColumns(sub.field)"
                  :key="c.field"
                  :label="translateSchemaLabel(c.label, c.field, 'field')"
                  :width="c.width"
                >
                  <template #default="{ row }">
                    <template v-if="(c.widget||'')==='button'">
                      <el-button size="small" :type="c.props?.type || 'default'" @click="emitArrayAction(sub.field, c, row)">
                        {{ c.props?.text || translateSchemaLabel(c.label, c.field, 'button') || dynamicText.value.button }}
                      </el-button>
                    </template>
                    <template v-else-if="(c.widget||'')==='labelButton' || c?.props?.action">
                      <div style="display:flex;align-items:center;gap:6px">
                        <span>{{ row[c.field] ?? '' }}</span>
                        <el-button size="small" :type="c.props?.type || 'default'" @click="emitArrayAction(sub.field, c, row)">{{ c.props?.text || dynamicText.value.select }}</el-button>
                      </div>
                    </template>
                    <template v-else-if="(c.widget||'')==='select'">
                      <el-select v-model="row[c.field]" filterable clearable style="width:100%" :disabled="(props as any).readonly || c.props?.disabled" v-bind="selectOnlyProps(c)">
                        <el-option v-for="opt in selectOptionsForArray(c)" :key="opt.value" :label="opt.label" :value="opt.value" />
                      </el-select>
                    </template>
                    <template v-else>
                      <component
                        :is="c.widget ? widgetByName(c.widget) : (c.inputType==='date' ? 'el-date-picker' : (c.inputType==='number' ? 'el-input' : autoWidget(c.field)))"
                        v-model="row[c.field]"
                        :type="c.inputType==='number' ? 'number' : (c.inputType || 'text')"
                        :value-format="c.valueFormat || (c.inputType==='date' ? 'YYYY-MM-DD' : undefined)"
                        :disabled="(props as any).readonly || c.props?.disabled"
                        v-bind="c.props || {}"
                        style="width:100%"
                      />
                    </template>
                  </template>
                </el-table-column>
                <el-table-column :label="dynamicText.value.actions" width="80">
                  <template #default="{ $index }">
                    <el-button size="small" type="danger" :disabled="(props as any).readonly" @click="removeArrayRow(sub.field, $index)">{{ sub?.removeText || dynamicText.value.removeRow || '削除' }}</el-button>
                  </template>
                </el-table-column>
              </el-table>
            </div>
          </template>
        </template>
      </template>

      <!-- 标签页块：tabs -> items[].children 为字段列 -->
      <template v-else-if="block.type==='tabs'">
        <el-tabs>
          <el-tab-pane v-for="(tab, tidx) in (block.items||[])" :key="'t'+tidx" :label="translateTabTitle(tab.title) || ('Tab '+(tidx+1))">
            <el-row :gutter="12" style="margin-bottom:8px;">
              <template v-for="(col, cidx) in (tab.children||[])" :key="'tc'+tidx+'-'+cidx">
                <el-col :span="col.span || 6" v-if="isVisible(col)">
                  <el-form-item :label="labelOf(col)" :required="isRequired(col)">
                    <template v-if="col.widget==='button'">
                      <el-button :type="col.props?.type || 'default'" :disabled="isDisabled(col)" @click="emitAction(col)">{{ buttonLabel(col) }}</el-button>
                    </template>
                  <template v-else-if="col.widget==='text'">
                    <span>{{ displayText(col) }}</span>
                  </template>
                  <template v-else-if="col.widget==='label'">
                    <span class="df-label">{{ formatLabelValue(getByPath(props.model, col.field), col, { model: props.model }) }}</span>
                    </template>
                    <template v-else>
                      <template v-if="isArrayField(col)">
                        <div class="array-editor">
                          <div style="display:flex;gap:8px;align-items:center">
                            <el-button v-if="!isAddDisabled(col.field)" size="small" @click="addArrayRow(col.field)">{{ col?.props?.addRowText || dynamicText.value.addRow }}</el-button>
                            <template v-if="arrayToolbar(col.field).length>0">
                              <el-button v-for="btn in arrayToolbar(col.field)" :key="btn.text" size="small" :type="btn.type || 'primary'" :disabled="isToolbarDisabled(btn)" @click="emitArrayToolbar(btn, col.field)">{{ btn.text }}</el-button>
                            </template>
                          </div>
                          <el-table :data="getArray(col.field)" size="small" border style="margin-top:6px">
                            <el-table-column v-for="c in arrayColumns(col.field)" :key="c.field" :label="translateSchemaLabel(c.label, c.field, 'field')" :width="c.width">
                              <template #default="{ row }">
                                <template v-if="(c.widget||'')==='button'">
                                  <el-button size="small" :type="c.props?.type || 'default'" @click="emitArrayAction(col.field, c, row)">{{ c.props?.text || translateSchemaLabel(c.label, c.field, 'button') || dynamicText.value.button }}</el-button>
                                </template>
                                <template v-else-if="(c.widget||'')==='labelButton' || c?.props?.action">
                                  <div style="display:flex;align-items:center;gap:6px">
                                    <span>{{ row[c.field] ?? '' }}</span>
                                    <el-button size="small" :type="c.props?.type || 'default'" @click="emitArrayAction(col.field, c, row)">{{ c.props?.text || dynamicText.value.select }}</el-button>
                                  </div>
                                </template>
                                <template v-else-if="(c.widget||'')==='select'">
                                  <el-select v-model="row[c.field]" filterable clearable style="width:100%" :disabled="(props as any).readonly || c.props?.disabled" v-bind="selectOnlyProps(c)">
                                    <el-option v-for="opt in selectOptionsForArray(c)" :key="opt.value" :label="opt.label" :value="opt.value" />
                                  </el-select>
                                </template>
                                <template v-else>
                                  <component :is="c.widget ? widgetByName(c.widget) : (c.inputType==='date' ? 'el-date-picker' : (c.inputType==='number' ? 'el-input' : autoWidget(c.field)))"
                                             v-model="row[c.field]"
                                             :type="c.inputType==='number' ? 'number' : (c.inputType || 'text')"
                                             :value-format="c.valueFormat || (c.inputType==='date' ? 'YYYY-MM-DD' : undefined)"
                                             :disabled="(props as any).readonly || c.props?.disabled"
                                             v-bind="{ ...(c.props || {}), disabled: ((props as any).readonly || c.props?.disabled) }"
                                             style="width:100%" />
                                </template>
                              </template>
                            </el-table-column>
                            <el-table-column :label="dynamicText.value.actions" width="80">
                              <template #default="{ $index }">
                                <el-button size="small" type="danger" :disabled="(props as any).readonly" @click="removeArrayRow(col.field, $index)">{{ col?.removeText || dynamicText.value.removeRow || '削除' }}</el-button>
                              </template>
                            </el-table-column>
                          </el-table>
                        </div>
                      </template>
                      <template v-else>
                        <component
                          :is="widgetOf(col)"
                          :model-value="getByPath(props.model, col.field)"
                          @update:model-value="v => setByPath(props.model, col.field, filterValue(col, v))"
                          :root-model="props.model"
                          :set-path="(p:any,v:any)=>setByPath(props.model,p,v)"
                          v-bind="{ ...(col.props || {}), disabled: isDisabled(col) }"
                          style="width:100%"
                        >
                          <template v-if="col.widget==='select'">
                            <el-option v-for="opt in selectOptions(col)" :key="opt.value" :label="opt.label" :value="opt.value" />
                          </template>
                        </component>
                      </template>
                    </template>
                  </el-form-item>
                </el-col>
              </template>
            </el-row>
          </el-tab-pane>
        </el-tabs>
      </template>
    </template>
  </div>
  
</template>

<script setup lang="ts">
import { computed, reactive } from 'vue'
import jaconv from 'jaconv'
import api from '../api'
import { useI18n } from '../i18n'

const props = defineProps<{
  ui: any
  model: any
  schema?: any
  arrayFields?: string[]
  columnsOverride?: Record<string, any[]>
  disableArrayAdd?: string[] | Record<string, boolean>
  readonly?: boolean
}>()
const emit = defineEmits<{ (e:'action', name:string, payload?:any): void }>()
const { section, text } = useI18n()
const dynamicText = section({ addRow: 'Add Row', removeRow: 'Remove', select: 'Select', upload: 'Upload', actions: 'Actions', button: 'Button', value: 'Value' }, (msg) => msg.dynamicForm)
const schemaLabels = computed(() => text.value?.schemaLabels ?? {})

const layout = computed(() => props.ui?.form?.layout || [])
const remoteOptions: Record<string, any[]> = reactive({})
const loadingUrl: Record<string, boolean> = reactive({})

function normalizeKey(input: string) {
  return input.toLowerCase().replace(/[^a-z0-9]/g, '')
}

function translateSchemaLabel(label?: string, field?: string, scope?: 'field' | 'button' | 'section' | 'tab' | 'option') {
  const map = schemaLabels.value || {}
  const candidates: string[] = []
  if (field) {
    candidates.push(field)
    const normalizedField = normalizeKey(field)
    candidates.push(normalizedField)
    const last = field.split('.').pop()
    if (last && last !== field) {
      candidates.push(last)
      candidates.push(normalizeKey(last))
    }
  }
  if (label) {
    candidates.push(label)
    const normalizedLabel = normalizeKey(label)
    candidates.push(normalizedLabel)
    candidates.push(`__label__${normalizedLabel}`)
  }
  if (scope && label) {
    const normalizedLabel = normalizeKey(label)
    candidates.push(`${scope}:${label}`)
    candidates.push(`${scope}:${normalizedLabel}`)
  }
  if (scope && field) {
    const normalizedField = normalizeKey(field)
    candidates.push(`${scope}:${field}`)
    candidates.push(`${scope}:${normalizedField}`)
  }
  for (const key of candidates) {
    if (key && map[key] !== undefined) {
      return map[key]
    }
  }
  return label ?? (field ?? '')
}

function translateSectionTitle(title?: string) {
  if (!title) return ''
  return translateSchemaLabel(title, undefined, 'section')
}

function translateTabTitle(title?: string) {
  if (!title) return ''
  return translateSchemaLabel(title, undefined, 'tab')
}

function ensureOptions(url:string){
  if (remoteOptions[url] || loadingUrl[url]) return
  loadingUrl[url] = true
  api.get(url).then(r=>{
    const data:any = r?.data
    const arr:any[] = Array.isArray(data) ? data : (Array.isArray(data?.data) ? data.data : [])
    remoteOptions[url] = arr
  }).catch(()=>{ remoteOptions[url] = [] }).finally(()=>{ loadingUrl[url] = false })
}

function getFieldFromRow(row:any, key:string){
  if (!row) return undefined
  // 支持点路径：如 payload.code
  const parts = String(key||'').split('.')
  let cur:any = row
  for (const k of parts){
    if (cur == null) return undefined
    cur = cur[k]
  }
  if (cur !== undefined) return cur
  // 兼容顶层/载荷直取
  if (Object.prototype.hasOwnProperty.call(row, key)) return (row as any)[key]
  if (row?.payload && Object.prototype.hasOwnProperty.call(row.payload, key)) return (row.payload as any)[key]
  return undefined
}

function applyFilter(rows:any[], col:any){
  const fb = col?.props?.filterBy
  if (!fb || !fb.field || !fb.equalsField) return rows
  const eq = getByPath(props.model, fb.equalsField)
  return rows.filter(r => getFieldFromRow(r, fb.field) === eq)
}

function toOption(row:any, col:any){
  const valKey = col?.props?.optionValue
  const lblTpl = col?.props?.optionLabel
  const value = valKey ? getFieldFromRow(row, valKey) : (row?.value ?? row?.code ?? row?.id)
  let label:string
  if (typeof lblTpl === 'string'){
    label = lblTpl.replace(/\{([^}]+)\}/g, (_:any, p:string) => {
      const k = (p||'').trim()
      const v = getFieldFromRow(row, k)
      return v===undefined||v===null ? '' : String(v)
    })
  } else {
    label = String(getFieldFromRow(row, 'name') ?? value ?? '')
  }
  return { label, value }
}
  
  function arrayToolbar(path:string){
    const cfg = findUiColumnConfig(path)
    const tb = (cfg && Array.isArray(cfg.toolbar)) ? cfg.toolbar : []
    if (tb.length>0) return tb
    const last = String(path||'').split('.').pop()
    if (last === 'attachments') return [{ type:'primary', text: dynamicText.value.upload, action:'__upload' }]
    return []
  }
  function isToolbarDisabled(btn:any){
    const rule = btn?.disabledWhen
    if (!rule) return false
    const cur = getByPath(props.model, rule.field)
    if (Object.prototype.hasOwnProperty.call(rule,'equals')) return cur === rule.equals
    if (rule.empty === true) return cur===undefined || cur===null || cur===''
    return false
  }
  function emitArrayToolbar(btn:any, path?:string){
    const name = btn?.action
    if (!name) return
    emit('action', name, { model: props.model, arrayPath: path })
  }

function getByPath(obj: any, path: string) {
  return path.split('.').reduce((o, k) => (o ? o[k] : undefined), obj)
}
function setByPath(obj: any, path: string, val: any) {
  const parts = path.split('.')
  const last = parts.pop() as string
  const target = parts.reduce((o, k) => (o[k] ??= {}), obj)
  target[last] = val
}

function valueProxy(path: string) {
  return {
    get() { return getByPath(props.model, path) },
    set(v: any) { setByPath(props.model, path, v) }
  }
}

function widgetOf(col: any) {
  const w = col.widget || 'input'
  switch (w) {
    case 'input': return 'el-input'
    case 'textarea': return 'el-input'
    case 'select': return 'el-select'
    case 'el-select-v2': return 'el-select-v2'
    case 'label': return 'div'
    case 'tree-select':
    case 'el-tree-select': return 'el-tree-select'
    case 'switch': return 'el-switch'
    case 'date': return 'el-date-picker'
    case 'button': return 'el-button'
    case 'text': return 'span'
    default: return 'el-input'
  }
}

function widgetByName(name:string){
  switch (name) {
    case 'el-input': return 'el-input'
    case 'el-select': return 'el-select'
    case 'label': return 'div'
    case 'el-tree-select': return 'el-tree-select'
    case 'el-switch': return 'el-switch'
    case 'el-date-picker': return 'el-date-picker'
    default: return name
  }
}

function selectOptions(col: any) {
  const url = col?.props?.optionsUrl
  if (typeof url === 'string' && url.length>0){
    ensureOptions(url)
    const rows = applyFilter(remoteOptions[url] || [], col)
    return rows.map(r=> toOption(r, col))
  }
  let src:any = (col.options ?? col.props?.options ?? [])
  if (src && typeof src === 'object' && Array.isArray((src as any).value)) src = (src as any).value
  const opts = Array.isArray(src) ? src : []
  return opts.map((x: any) => {
    if (typeof x === 'string') {
      return { label: translateSchemaLabel(x, undefined, 'option'), value: x }
    }
    if (x && typeof x === 'object') {
      const next = { ...x }
      if (next.label !== undefined) {
        next.label = translateSchemaLabel(String(next.label), undefined, 'option')
      }
      return next
    }
    return x
  })
}

function selectOptionsForArray(col:any){
  const url = col?.props?.optionsUrl
  if (typeof url === 'string' && url.length>0){
    ensureOptions(url)
    const rows = applyFilter(remoteOptions[url] || [], col)
    return rows.map(r=> toOption(r, col))
  }
  let src:any = (col.options ?? col.props?.options ?? [])
  if (src && typeof src === 'object' && Array.isArray((src as any).value)) src = (src as any).value
  const opts = Array.isArray(src) ? src : []
  return opts.map((x:any)=> {
    if (typeof x === 'string') return ({label: translateSchemaLabel(x, undefined, 'option'), value:x})
    if (x && typeof x === 'object'){
      const next = { ...x }
      if (next.label !== undefined) next.label = translateSchemaLabel(String(next.label), undefined, 'option')
      return next
    }
    return x
  })
}

function selectOnlyProps(c:any){
  const p = Object.assign({}, c.props || {})
  // 移除 input/datepicker 遗留属性，避免干扰 el-select
  delete (p as any).type
  delete (p as any).valueFormat
  delete (p as any)['value-format']
  delete (p as any)['valueformat']
  return p
}

function isRequired(col:any){
  if (!col) return false
  if (col.required === true) return true
  if (col.props && col.props.required === true) return true
  return false
}

function isVisible(col: any) {
  // hidden widget 不显示
  if (col?.widget === 'hidden') return false
  const cond = col.visibleWhen
  if (!cond) return true
  const cur = getByPath(props.model, cond.field)
  if (Array.isArray((cond as any).in)) return (cond as any).in.includes(cur)
  return cur === cond.equals
}

// 是否禁用：支持 props.disabled 或 disabledWhen 规则
function isDisabled(col:any){
  if ((props as any).readonly === true){
    const w = (col?.widget || '').toString()
    if (w && (w === 'text' || w === 'label' || w === 'hidden')) return false
    return true
  }
  if (col?.props?.disabled) return true
  const rule = col?.props?.disabledWhen
  if (!rule) return false
  const cur = getByPath(props.model, rule.field)
  if (Object.prototype.hasOwnProperty.call(rule,'equals')) return cur === rule.equals
  if (rule.empty === true) return cur===undefined || cur===null || cur===''
  return false
}

function emitAction(col:any){
  const name = col?.props?.action
  if (name) emit('action', name, { col, model: props.model })
}

// 表单项标签：按钮/文本不显示 label
function labelOf(col:any){
  if (col?.widget==='button' || col?.widget==='text') return undefined as any
  return translateSchemaLabel(col?.label, col?.field, 'field')
}

function buttonLabel(col:any){
  return translateSchemaLabel(col?.label, col?.field, 'button')
}

// 文本显示值：优先取字段值，可通过 props.format 映射
function displayText(col:any){
  const val = col?.field ? getByPath(props.model, col.field) : ''
  if (col?.props?.prefix || col?.props?.suffix) {
    return `${col.props.prefix||''}${val??''}${col.props.suffix||''}`
  }
  return val
}
function formatLabelValue(value:any, col:any, ctx?:{ row?:any, model?:any }){
  if (typeof col?.props?.formatter === 'function'){
    try{
      return col.props.formatter(value, ctx?.row || ctx?.model || null, col)
    }catch{}
  }
  if (value === null || value === undefined || value === ''){
    return col?.props?.placeholder || ''
  }
  return value
}

// 过滤器：根据列配置的 props.filter 应用输入清洗
function filterValue(col:any, v:any){
  const f = col?.props?.filter
  // 数值字段统一转 number
  if (col?.props?.type === 'number'){
    const n = Number(v)
    return Number.isFinite(n) ? n : 0
  }
  if (!f || typeof v !== 'string') return v
  if (f === 'katakana-half') {
    // 转换为カタカナ（全角），再转为半角片假名，最后过滤非半角片假名与空格
    const kata = jaconv.toKatakana(v)
    const half = jaconv.toHan(kata)
    return half.replace(/[^\uff65-\uff9f\s]/g, '')
  }
  if (f === 'katakana-zen') {
    // 转换为カタカナ（全角），并仅保留全角片假名与空格
    const kata = jaconv.toKatakana(v)
    const zen = jaconv.toZen(kata)
    return zen.replace(/[^\u30A0-\u30FF\u3000\s]/g, '')
  }
  return v
}
// 数组字段通用支持：当字段值是数组，自动以表格渲染，列从第一行推断
function isArrayField(col:any){
  const val = getByPath(props.model, col.field)
  if (Array.isArray(val)) return true
  // 若 schema 声明为数组，也视为数组字段（即使当前值为空）
  const s:any = (props as any).schema
  const top = (col.field || '').split('.')[0]
  const def = s?.properties?.[top]
  if (def?.type === 'array') return true
  // 显式 arrayFields 名单
  const af = (props as any).arrayFields as string[] | undefined
  return Array.isArray(af) && af.includes(top)
}
function getArray(path:string){
  let val = getByPath(props.model, path)
  if (!Array.isArray(val)) {
    // 确保模型中存在数组，便于后续新增一行
    setByPath(props.model, path, [])
    val = getByPath(props.model, path)
  }
  return val
}
function arrayColumns(path:string){
  // 0) 显式覆盖优先（父组件传入）
  const topKey = String(path||'').split('.')[0]
  const override = (props as any).columnsOverride?.[path] || (props as any).columnsOverride?.[topKey]
  if (Array.isArray(override) && override.length>0) return normalizeArrayColumns(override)
  // 1) 优先 ui.columns（需要在对应字段配置 props.columns）
  const colCfg = findUiColumnConfig(path)
  if (colCfg && Array.isArray(colCfg.columns) && colCfg.columns.length>0) return normalizeArrayColumns(colCfg.columns)
  // 2) 其次基于 schema 推断（仅支持顶层字段）
  const colsFromSchema = columnsFromSchema(path)
  if (colsFromSchema.length>0) return normalizeArrayColumns(colsFromSchema)
  // 3) 再次：从第一行对象键推断
  const list = getArray(path)
  const first = list[0] || {}
  const keys = Object.keys(first)
  if (keys.length>0) return normalizeArrayColumns(keys.map(k=>({ field:k, label:k })))
  // 4) 兜底：一个通用列
  return normalizeArrayColumns([{ field:'item', label: dynamicText.value.value }])
}

function normalizeArrayColumns(cols:any[]){
  return cols.map(c=>{
    const name = String(c.field||'')
    // 若配置了 options/props.options 但未指定 widget，则强制使用 select
    const hasOptions = (Array.isArray((c.props||{}).options) && ((c.props||{}).options.length>0))
      || (Array.isArray((c as any).options) && ((c as any).options.length>0))
    if (!c.widget && (hasOptions || /^(departmentId|bank|branch|accountType)$/i.test(name))) {
      c.widget = 'select'
    }
    // 不要把日期/数字的 type 传进 select
    if ((c.widget||'')==='select'){
      if (c.inputType) delete c.inputType
      if (c.valueFormat) delete c.valueFormat
    }
    return c
  })
}
function addArrayRow(path:string){
  const list = getArray(path)
  const cols = arrayColumns(path)
  const cfg = findUiColumnConfig(path)
  const obj:any = {}
  for (const c of cols){ 
    obj[c.field] = c.default ?? (c.inputType==='number'?0 : '') 
  }
  // 自动设置行号（lineNo）
  if (cfg?.autoLineNo || cols.some((c:any) => c.field === 'lineNo')) {
    const maxLineNo = list.reduce((max:number, row:any) => Math.max(max, row.lineNo || 0), 0)
    obj.lineNo = maxLineNo + 1
  }
  ;(list as any[]).push(obj)
}
function removeArrayRow(path:string, idx:number){
  const list = getArray(path)
  if (idx>=0 && idx<list.length) list.splice(idx,1)
}
function isAddDisabled(path:string){
  if ((props as any).readonly === true) return true
  const raw = (props as any).disableArrayAdd
  if (!raw) return false
  const key = String(path || '')
  const top = key.split('.')[0]
  if (Array.isArray(raw)) return raw.includes(key) || raw.includes(top)
  if (typeof raw === 'object') return !!(raw as Record<string, boolean>)[key] || !!(raw as Record<string, boolean>)[top]
  return false
}
function emitArrayAction(path:string, col:any, row:any){
  const name = col?.props?.action
  if (!name) return
  emit('action', name, { arrayPath: path, col, row, model: props.model })
}
function autoWidget(field:string){
  // 简单按字段名推断控件
  if (/^(departmentId|bank|branch|accountType)$/i.test(field)) return 'el-select'
  if (/date/i.test(field)) return 'el-date-picker'
  return 'el-input'
}

function findUiColumnConfig(path:string){
  // 在当前 ui 里找到该字段的配置对象，读取 props.columns
  const blocks = layout.value as any[]
  const scan = (arr:any[]):any => {
    for (const b of arr){
      if (b?.type==='grid' && Array.isArray(b.cols)){
        const hit = b.cols.find((c:any)=> c.field===path)
        if (hit && hit.props?.columns) return hit.props
      } else if (b?.type==='section' && Array.isArray(b.layout)){
        const x = scan(b.layout); if (x) return x
      } else if (b?.type==='tabs' && Array.isArray(b.items)){
        for (const t of b.items){ if (Array.isArray(t.children)){ const x= t.children.find((c:any)=>c.field===path); if (x?.props?.columns) return x.props } }
      }
    }
    return null
  }
  return scan(blocks)
}

function columnsFromSchema(path:string){
  let s:any = (props as any).schema
  if (s && typeof s === 'string'){
    try{ s = JSON.parse(s) }catch{ s = null }
  }
  if (!s || !s.properties) return [] as any[]
  const def = s.properties[path]
  const propsMap = def?.items?.properties
  if (!propsMap) return [] as any[]
  return Object.keys(propsMap).map(k=>({ field:k, label:k }))
}
</script>

<style scoped>
.lines-editor {
  margin: 16px 0;
  padding: 12px;
  background: #fafafa;
  border-radius: 6px;
  border: 1px solid #ebeef5;
}

.lines-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 8px;
}

.lines-title {
  font-weight: 600;
  font-size: 14px;
  color: #303133;
}

.lines-editor :deep(.el-table) {
  background: white;
}

.lines-editor :deep(.el-input-number) {
  width: 100%;
}

.lines-editor :deep(.el-input-number .el-input__wrapper) {
  padding-left: 8px;
  padding-right: 8px;
}

.df-label {
  display: inline-block;
  padding: 4px 6px;
  color: #606266;
  background-color: #f5f7fa;
  border-radius: 4px;
  word-break: break-all;
}
</style>


