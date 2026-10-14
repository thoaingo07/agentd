import type { Component } from 'vue'
import DatabaseStep from '../shared/setup/steps/DatabaseStep.vue'
import AzureDevOpsStep from '../shared/setup/steps/AzureDevOpsStep.vue'
import GitKeyStep from '../shared/setup/steps/GitKeyStep.vue'
import ClaudeStep from '../shared/setup/steps/ClaudeStep.vue'
import ModelsStep from '../shared/setup/steps/ModelsStep.vue'
import ChatStep from '../shared/setup/steps/ChatStep.vue'
import RepositoriesStep from '../shared/setup/steps/RepositoriesStep.vue'
import ReviewStep from '../shared/setup/steps/ReviewStep.vue'
import ArtifactStorageStep from '../shared/setup/steps/ArtifactStorageStep.vue'
import FoldersStep from '../shared/setup/steps/FoldersStep.vue'
import MonitoringSettings from './components/MonitoringSettings.vue'

/** Settings pages (Admins): the setup wizard's forms, Health (its review, live), and Monitoring (not in the wizard). */
export const settingsAreas: { name: string; title: string; component: Component }[] = [
  { name: 'health', title: 'Health', component: ReviewStep },
  { name: 'database', title: 'Database', component: DatabaseStep },
  { name: 'azure-devops', title: 'Azure DevOps', component: AzureDevOpsStep },
  { name: 'git', title: 'Git access', component: GitKeyStep },
  { name: 'claude', title: 'Claude', component: ClaudeStep },
  { name: 'models', title: 'Models', component: ModelsStep },
  { name: 'chat', title: 'Chat', component: ChatStep },
  { name: 'repositories', title: 'Repositories', component: RepositoriesStep },
  { name: 'artifacts', title: 'Artifacts', component: ArtifactStorageStep },
  { name: 'folders', title: 'Folders', component: FoldersStep },
  { name: 'monitoring', title: 'Monitoring', component: MonitoringSettings },
]
