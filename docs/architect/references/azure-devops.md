# Azure DevOps reference

Base URL: `https://dev.azure.com/{organization}/{project}/_apis`. Use `api-version=7.1`.

## Authentication

### Azure CLI

```bash
az login                       # once, interactive
az extension add --name azure-devops   # for `az boards` / `az repos` commands
az account get-access-token \
  --resource 499b84ac-1321-427f-aa17-267ca6975798 \
  --query "{token:accessToken, expiresOn:expiresOn}" -o json
```

In .NET, use `Azure.Identity` instead of shelling out:

```csharp
var cred = new AzureCliCredential();
var token = await cred.GetTokenAsync(
    new TokenRequestContext(["499b84ac-1321-427f-aa17-267ca6975798/.default"]), ct);
// cache token.Token until token.ExpiresOn
```

`499b84ac-1321-427f-aa17-267ca6975798` is the Azure DevOps resource ID. Send the token as
`Authorization: Bearer <token>`, and refresh it before `expiresOn`.

### Personal Access Token

Send the PAT as `Authorization: Basic base64(":" + PAT)` (the username is empty).
Minimum scopes: **Work Items (Read & Write)** and **Code (Read & Write)**.

## Query tagged work items (WIQL)

```http
POST /{project}/_apis/wit/wiql?api-version=7.1
Content-Type: application/json

{ "query": "SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = @project AND [System.Tags] CONTAINS 'ai-workflow' AND [System.Tags] NOT CONTAINS 'ai-in-progress' AND [System.State] IN ('New','Active') ORDER BY [System.ChangedDate] ASC" }
```

CLI equivalent: `az boards query --wiql "<query>"`.

## Read work items

```http
GET /_apis/wit/workitems?ids=1,2,3&$expand=all&api-version=7.1
GET /{project}/_apis/wit/workItems/{id}/comments?api-version=7.1-preview.4
```

Useful fields: `System.Title`, `System.Description`, `Microsoft.VSTS.Common.AcceptanceCriteria`,
`Microsoft.VSTS.TCM.ReproSteps`, `System.Tags` (a `; `-separated string), `System.AreaPath`, `System.State`.

## Claim / update (JSON Patch)

```http
PATCH /_apis/wit/workitems/{id}?api-version=7.1
Content-Type: application/json-patch+json

[
  { "op": "test", "path": "/rev", "value": 7 },
  { "op": "add",  "path": "/fields/System.Tags", "value": "ai-workflow; ai-in-progress" }
]
```

The `test` on `/rev` gives optimistic concurrency, so two daemons cannot claim the same item.

## Comment on a work item

```http
POST /{project}/_apis/wit/workItems/{id}/comments?api-version=7.1-preview.4
{ "text": "agentd picked this up — Discord thread: <link>" }
```

## Create a Pull Request linked to the work item

```http
POST /{project}/_apis/git/repositories/{repositoryId}/pullrequests?api-version=7.1
{
  "sourceRefName": "refs/heads/ai/1234-fix-login",
  "targetRefName": "refs/heads/main",
  "title": "Fix login redirect (WI-1234)",
  "description": "...",
  "workItemRefs": [ { "id": "1234" } ]
}
```

CLI equivalent:
`az repos pr create --source-branch ai/1234-fix-login --target-branch main --title ... --description ... --work-items 1234`

## Links

- WIQL: https://learn.microsoft.com/rest/api/azure/devops/wit/wiql
- Work items: https://learn.microsoft.com/rest/api/azure/devops/wit/work-items
- Pull requests: https://learn.microsoft.com/rest/api/azure/devops/git/pull-requests
- PAT: https://learn.microsoft.com/azure/devops/organizations/accounts/use-personal-access-tokens-to-authenticate
