import { createRouter, createWebHistory } from 'vue-router'
import DashboardView from './views/DashboardView.vue'

export const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/', name: 'dashboard', component: DashboardView },
    { path: '/jobs/:id(\\d+)', name: 'job', component: () => import('./views/SessionView.vue'), props: (r) => ({ id: Number(r.params.id) }) },
    { path: '/workitems/:id(\\d+)', name: 'work-item', component: () => import('./views/WorkItemView.vue'), props: (r) => ({ id: Number(r.params.id) }) },
    { path: '/history', name: 'history', component: () => import('./views/HistoryView.vue') },
    { path: '/ideas', name: 'ideas', component: () => import('./views/IdeasView.vue') },
    { path: '/ideas/:id(\\d+)', name: 'idea', component: () => import('./views/IdeaView.vue'), props: (r) => ({ id: Number(r.params.id) }) },
    { path: '/settings', name: 'settings', component: () => import('./views/SettingsView.vue') },
    { path: '/settings/:area', name: 'settings-area', component: () => import('./views/SettingsAreaView.vue'), props: true },
    { path: '/:pathMatch(.*)*', name: 'not-found', component: () => import('./views/NotFoundView.vue') },
  ],
})
