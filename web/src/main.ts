import './style.css'

import { createApp } from 'vue'
import App from './App.vue'
import router from './router'
import ElementPlus from 'element-plus'
import 'element-plus/dist/index.css'
import { installI18n } from './i18n'

const app = createApp(App)

app.use(router)
app.use(ElementPlus, { size: 'small' })
installI18n(app)

app.mount('#app')
