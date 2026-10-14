import { createRouter, createWebHistory } from 'vue-router'
import DashboardView from './views/DashboardView.vue'

export const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/', name: 'dashboard', component: DashboardView },
    { path: '/jobs/:id(\\d+)', name: 'job', component: () => import('./views/JobView.vue'), props: (r) => ({ id: Number(r.params.id) }) },
    { path: '/workitems/:id(\\d+)', name: 'work-item', component: () => import('./views/WorkItemView.vue'), props: (r) => ({ id: Number(r.params.id) }) },
    { path: '/inbox', name: 'inbox', component: () => import('./views/InboxView.vue') },
    { path: '/inbox/:source(job|idea|review|chat|watch)/:id(\\d+)', name: 'inbox-thread', component: () => import('./views/InboxView.vue'), props: (r) => ({ source: String(r.params.source), id: Number(r.params.id) }) },
    { path: '/history', name: 'history', component: () => import('./views/HistoryView.vue') },
    { path: '/chats', name: 'chats', component: () => import('./views/ChatsView.vue') },
    { path: '/chats/:id(\\d+)', name: 'chat', component: () => import('./views/ChatView.vue'), props: (r) => ({ id: Number(r.params.id) }) },
    { path: '/artifacts', name: 'artifacts', component: () => import('./views/ArtifactsView.vue') },
    { path: '/ideas', name: 'ideas', component: () => import('./views/IdeasView.vue') },
    { path: '/ideas/:id(\\d+)', name: 'idea', component: () => import('./views/IdeaView.vue'), props: (r) => ({ id: Number(r.params.id) }) },
    { path: '/reviews', name: 'reviews', component: () => import('./views/ReviewsView.vue') },
    { path: '/reviews/:id(\\d+)', name: 'review', component: () => import('./views/ReviewView.vue'), props: (r) => ({ id: Number(r.params.id) }) },
    {
      // Settings pages share a vertical menu (SettingsLayout).
      path: '/settings',
      component: () => import('./components/SettingsLayout.vue'),
      children: [
        { path: '', name: 'settings', component: () => import('./views/SettingsView.vue') },
        { path: 'log', name: 'system-log', component: () => import('./views/SystemLogView.vue') },
        { path: 'prompts', name: 'prompts', component: () => import('./views/PromptConversationsView.vue') },
        { path: 'prompts/all', name: 'prompts-all', component: () => import('./views/PromptsView.vue') },
        {
          path: 'prompts/:source(job|idea|chat|review|review-session|review-ask|review-fix|follow-up)/:sourceId(\\d+)',
          name: 'prompt-conversation',
          component: () => import('./views/PromptConversationView.vue'),
          props: (r) => ({ source: String(r.params.source), sourceId: Number(r.params.sourceId) }),
        },
        { path: 'prompts/:id(\\d+)', name: 'prompt', component: () => import('./views/PromptView.vue'), props: (r) => ({ id: Number(r.params.id) }) },
        { path: ':area', name: 'settings-area', component: () => import('./views/SettingsAreaView.vue'), props: true },
      ],
    },
    { path: '/:pathMatch(.*)*', name: 'not-found', component: () => import('./views/NotFoundView.vue') },
  ],
})
