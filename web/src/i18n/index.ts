import { App, inject, InjectionKey, readonly, Ref, ref, computed, watch } from 'vue'
import { Lang, messages, Messages } from './messages'

const STORAGE_KEY = 'ui_lang'

function detectDefault(): Lang {
  if (typeof navigator !== 'undefined') {
    const lang = navigator.language?.toLowerCase()
    if (lang?.startsWith('ja')) return 'ja'
    if (lang?.startsWith('zh')) return 'zh'
    if (lang?.startsWith('en')) return 'en'
  }
  return 'ja'
}

function loadLang(): Lang {
  try {
    const saved = window.localStorage.getItem(STORAGE_KEY)
    if (saved === 'ja' || saved === 'en' || saved === 'zh') return saved as Lang
  } catch {}
  return detectDefault()
}

function persistLang(lang: Lang) {
  try {
    window.localStorage.setItem(STORAGE_KEY, lang)
  } catch {}
}

const currentLang = ref<Lang>(loadLang())
const currentMessages = ref<Messages>(messages[currentLang.value])

watch(currentLang, (lang) => {
  currentMessages.value = messages[lang]
})

export const langKey: InjectionKey<Ref<Lang>> = Symbol('i18n.lang')
export const messagesKey: InjectionKey<typeof messages> = Symbol('i18n.messages')
export const currentMessagesKey: InjectionKey<Ref<Messages>> = Symbol('i18n.currentMessages')

export function installI18n(app: App) {
  app.provide(langKey, currentLang)
  app.provide(messagesKey, messages)
  app.provide(currentMessagesKey, currentMessages)
}

export function setLang(lang: Lang) {
  if (currentLang.value === lang) return
  currentLang.value = lang
  persistLang(lang)
}

export function getLang(): Lang {
  return currentLang.value
}

export type MessageKey = [Lang, keyof typeof messages['ja']] extends never ? never : string

export function t(path: string): any {
  const lang = currentLang.value
  const segs = path.split('.')
  let node: any = messages[lang]
  for (const seg of segs) {
    if (node && seg in node) node = node[seg]
    else return path
  }
  return node
}

export function useI18n() {
  const langRef = inject(langKey, currentLang)
  const msgsRef = inject(currentMessagesKey, currentMessages)
  const textComputed = computed(() => msgsRef.value)
  const section = <T extends Record<string, any>>(defaults: T, selector: (msg: Messages) => Partial<T> | undefined) =>
    computed(() => ({ ...defaults, ...(selector(textComputed.value) ?? {}) }))
  return {
    lang: readonly(langRef),
    setLang,
    t,
    messages,
    text: textComputed,
    buttons: computed(() => textComputed.value.buttons),
    common: computed(() => textComputed.value.common),
    section
  }
}


