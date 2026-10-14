// Runs in the Foundry resource's resource group. Lets the WhyLine app (and you, for local runs with az login) call the
// Foundry project with Microsoft Entra ID, and connects WhyLine's Application Insights to the project's Tracing view.
param foundryAccountName string
param foundryProjectName string
param appPrincipalId string
param userPrincipalId string = ''
param appInsightsId string
@secure()
param appInsightsConnectionString string

@description('Connect Application Insights to the Foundry project so traces appear under Foundry → Tracing.')
param connectTracing bool = true

var azureAiUserRoleId = '53ca6127-db72-4b80-b1b0-d745d6d5456d'        // Azure AI User: data actions on the Foundry resource
var openAiUserRoleId = '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd'         // Cognitive Services OpenAI User

resource account 'Microsoft.CognitiveServices/accounts@2025-06-01' existing = {
  name: foundryAccountName
}

resource project 'Microsoft.CognitiveServices/accounts/projects@2025-06-01' existing = {
  parent: account
  name: foundryProjectName
}

resource appAiUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(account.id, appPrincipalId, azureAiUserRoleId)
  scope: account
  properties: {
    principalId: appPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', azureAiUserRoleId)
  }
}

resource appOpenAiUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(account.id, appPrincipalId, openAiUserRoleId)
  scope: account
  properties: {
    principalId: appPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', openAiUserRoleId)
  }
}

resource userAiUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(userPrincipalId)) {
  name: guid(account.id, userPrincipalId, azureAiUserRoleId)
  scope: account
  properties: {
    principalId: userPrincipalId
    principalType: 'User'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', azureAiUserRoleId)
  }
}

resource tracing 'Microsoft.CognitiveServices/accounts/projects/connections@2025-06-01' = if (connectTracing) {
  parent: project
  name: 'whyline-appinsights'
  properties: {
    category: 'AppInsights'
    target: appInsightsId
    authType: 'ApiKey'
    isSharedToAll: true
    credentials: { key: appInsightsConnectionString }
    metadata: { ApiType: 'Azure', ResourceId: appInsightsId }
  }
}
