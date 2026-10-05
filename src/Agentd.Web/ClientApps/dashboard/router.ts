import { createRouter, createWebHistory } from 'vue-router'
import DashboardView from './views/DashboardView.vue'

export const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/', name: 'dashboard', component: DashboardView },
    { path: '/jobs/:id(\\d+)', name: 'job', component: () => import('./views/JobView.vue'), props: (r) => ({ id: Number(r.params.id) }) },
    { path: '/history', name: 'history', component: () => import('./views/PlaceholderView.vue'), props: { title: 'History', task: 'T3.11' } },
    { path: '/settings', name: 'settings', component: () => import('./views/SettingsView.vue') },
    { path: '/:pathMatch(.*)*', name: 'not-found', component: () => import('./views/NotFoundView.vue') },
  ],
})
