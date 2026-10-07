import { createRouter, createWebHistory } from 'vue-router'
import DatabaseStep from '../shared/setup/steps/DatabaseStep.vue'
import AzureDevOpsStep from '../shared/setup/steps/AzureDevOpsStep.vue'
import GitKeyStep from '../shared/setup/steps/GitKeyStep.vue'
import ClaudeStep from '../shared/setup/steps/ClaudeStep.vue'
import ChatStep from '../shared/setup/steps/ChatStep.vue'
import RepositoriesStep from '../shared/setup/steps/RepositoriesStep.vue'
import ReviewStep from '../shared/setup/steps/ReviewStep.vue'

/** The wizard's steps, in order (docs/architect/deployment.md §5a). */
export const steps = [
  { path: '/database', name: 'database', title: 'Database', component: DatabaseStep },
  { path: '/azure-devops', name: 'azure-devops', title: 'Azure DevOps', component: AzureDevOpsStep },
  { path: '/git', name: 'git', title: 'Git access', component: GitKeyStep },
  { path: '/claude', name: 'claude', title: 'Claude', component: ClaudeStep },
  { path: '/chat', name: 'chat', title: 'Chat (optional)', component: ChatStep },
  { path: '/repositories', name: 'repositories', title: 'Repositories', component: RepositoriesStep },
  { path: '/review', name: 'review', title: 'Review & finish', component: ReviewStep },
] as const

export const router = createRouter({
  history: createWebHistory('/setup/wizard/'),
  routes: [
    { path: '/', redirect: steps[0].path },
    ...steps.map((s) => ({ path: s.path, name: s.name, component: s.component, meta: { title: s.title } })),
    { path: '/:rest(.*)*', redirect: steps[0].path },
  ],
})
