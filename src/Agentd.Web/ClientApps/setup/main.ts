import { createApp } from 'vue'
import { createPinia } from 'pinia'
import App from './App.vue'
import { router } from './router'
import { setAntiforgeryUrl } from '../shared/api/http'
import '../shared/styles/app.css'

// The wizard runs on the setup session (the one-time link's cookie), so its antiforgery token comes from /api/setup.
setAntiforgeryUrl('/api/setup/antiforgery')

createApp(App).use(createPinia()).use(router).mount('#app')
