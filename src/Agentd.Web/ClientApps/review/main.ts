// `agentd review`'s page on a developer's laptop (docs/architect/review-sessions.md §5): the same review page as the web
// UI, served by the CLI on 127.0.0.1 for one local session, plus Send (to .agentd/review.md for their agent).
import { createApp } from 'vue'
import { createPinia } from 'pinia'
import { createRouter, createWebHistory } from 'vue-router'
import App from './App.vue'
import ReviewView from '../dashboard/views/ReviewView.vue'
import '../shared/styles/app.css'

const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/reviews/:id(\\d+)', name: 'review', component: ReviewView, props: (r) => ({ id: Number(r.params.id) }) },
    { path: '/:rest(.*)*', name: 'reviews', redirect: '/reviews/1' },
  ],
})

createApp(App).use(createPinia()).use(router).mount('#app')
