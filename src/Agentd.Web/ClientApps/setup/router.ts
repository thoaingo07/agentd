import { createRouter, createWebHistory } from 'vue-router'
import DatabaseStep from './steps/DatabaseStep.vue'
import AzureDevOpsStep from './steps/AzureDevOpsStep.vue'
import GitKeyStep from './steps/GitKeyStep.vue'
import ClaudeStep from './steps/ClaudeStep.vue'
import ChatStep from './steps/ChatStep.vue'

/** The wizard's steps, in order (docs/architect/deployment.md §5a). Later steps join as their APIs land. */
export const steps = [
  { path: '/database', name: 'database', title: 'Database', component: DatabaseStep },
  { path: '/azure-devops', name: 'azure-devops', title: 'Azure DevOps', component: AzureDevOpsStep },
  { path: '/git', name: 'git', title: 'Git access', component: GitKeyStep },
  { path: '/claude', name: 'claude', title: 'Claude', component: ClaudeStep },
  { path: '/chat', name: 'chat', title: 'Chat (optional)', component: ChatStep },
] as const

export const router = createRouter({
  history: createWebHistory('/setup/wizard/'),
  routes: [
    { path: '/', redirect: steps[0].path },
    ...steps.map((s) => ({ path: s.path, name: s.name, component: s.component, meta: { title: s.title } })),
    { path: '/:rest(.*)*', redirect: steps[0].path },
  ],
})
