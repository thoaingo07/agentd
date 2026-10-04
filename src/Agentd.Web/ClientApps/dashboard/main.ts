import { createApp } from 'vue'
import { createPinia } from 'pinia'
import App from './App.vue'
import { router } from './router'
import { use } from '../shared/api/http'
import { useConnectionStore } from './stores/connection'
import { useJobsStore } from './stores/jobs'
import { useSessionStore } from './stores/session'
import { useUiStore } from './stores/ui'
import '../shared/styles/app.css'

const app = createApp(App).use(createPinia()).use(router)

// App interceptors (http.ts already handles the antiforgery token and 401s).
use({
  onError: (err) => {
    if (err.status >= 500) useUiStore().toast('agentd had a problem; try again.', 'error')
  },
})
if (import.meta.env.DEV) {
  use({
    onResponse: (res, req) => {
      console.debug(req.method, req.url, res.status)
      return res
    },
  })
}

app.mount('#app')

void (async () => {
  let from = 0
  try {
    await useSessionStore().load()
    from = await useJobsStore().load()   // stream "all" from the snapshot's position, not from the beginning
  } finally {
    const connection = useConnectionStore()
    await connection.start()
    await connection.subscribe('all', from)
  }
})()
