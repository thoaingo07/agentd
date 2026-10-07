# Brainstorming ideas

`!idea` is for when the requirements aren't clear yet. You talk the idea through with an agent that reads the real
code, and when you're ready it drafts **User Stories and Tasks** that agentd can create in Azure DevOps.

## Start

```text
!idea a pipeline that deploys all services to staging when we merge to develop
!idea --repo portal --model opus --effort high dark mode for the admin pages
```

- **The thread:** agentd opens **💡 Idea: <first words>** in the channel.
- **The agent:** works on a **read-only** copy of the repository's base branch. It can read and search the code but
  never edit, build or commit.
- **The repository:** `--repo` picks it. With a single registered repository it's implied; with several, `--repo` is
  required.
- **The model:** `--model` and `--effort` pick it for this idea. `!model` / `!effort` in the thread change it later.

## Talk it through

Every message in the thread continues the same conversation. Ask questions, push back, add constraints, point at
files. The agent explores options and trade-offs, points at the code that would change, and sizes the work.

If you send several messages while it's thinking, they're answered together in its next reply ("💭 Still thinking…").
At most two ideas think at the same time.

## Drafts

When it's time, the agent drafts the work items as stories with their tasks. Ask for drafts whenever you like ("draft
the work items now"). The draft looks like this:

```text
🗂 Proposed work items
1. User Story: Deploy to staging on merge · 5 pts
      2. Task: Pipeline for the API · 3 h
      3. Task: Pipeline for the web app · 2 h
1 create them in Azure DevOps · 2 create and start · 3 change · 4 discard. Or just tell me what to change.
```

| Answer | What happens |
|---|---|
| **1** create | the items are created in Azure DevOps (tasks linked to their story), and the links are posted |
| **2** create and start | the same, plus the tags `ai-workflow` and `repo:<name>` on the stories, so agentd picks them up as jobs (with plan approval) |
| **3** change, or any other text | the agent revises and drafts again |
| **4** discard | nothing is created |

After creating or discarding, agentd asks whether to delete the thread. The idea and its whole conversation stay in
agentd, and the **Ideas** page in the web UI shows them, linked from the work items they created.
