// WhyLine's own resources: monitoring, registry, managed identity, Container Apps environment and the app.
param location string
param tags object
param token string
param foundryProjectEndpoint string
param reasoningModel string
param fastModel string
param webExists bool

var placeholderImage = 'mcr.microsoft.com/azuredocs/containerapps-helloworld:latest'
var acrPullRoleId = '7f951dda-4ed3-4680-a7ca-43fe172d538d'

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: 'log-whyline-${token}'
  location: location
  tags: tags
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

// Traces and metrics from OpenTelemetry. Connect this resource to your Foundry project (Tracing) to see them there.
resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'appi-whyline-${token}'
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logs.id
  }
}

resource identity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-whyline-${token}'
  location: location
  tags: tags
}

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: 'crwhyline${token}'
  location: location
  tags: tags
  sku: { name: 'Basic' }
  properties: { adminUserEnabled: false }
}

resource acrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(registry.id, identity.id, acrPullRoleId)
  scope: registry
  properties: {
    principalId: identity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPullRoleId)
  }
}

resource environment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: 'cae-whyline-${token}'
  location: location
  tags: tags
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logs.properties.customerId
        sharedKey: logs.listKeys().primarySharedKey
      }
    }
  }
}

resource existingWeb 'Microsoft.App/containerApps@2024-03-01' existing = if (webExists) {
  name: 'ca-whyline-${token}'
}

// One replica, always on: the live match runs in memory and Blazor Server keeps a SignalR circuit per viewer.
resource web 'Microsoft.App/containerApps@2024-03-01' = {
  name: 'ca-whyline-${token}'
  location: location
  tags: union(tags, { 'azd-service-name': 'web' })
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${identity.id}': {} }
  }
  dependsOn: [acrPull]
  properties: {
    managedEnvironmentId: environment.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
        allowInsecure: false
        stickySessions: { affinity: 'sticky' }
      }
      registries: [
        {
          server: registry.properties.loginServer
          identity: identity.id
        }
      ]
    }
    template: {
      containers: [
        {
          name: 'web'
          image: webExists ? existingWeb!.properties.template.containers[0].image : placeholderImage
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          env: [
            { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
            { name: 'Agents__Mode', value: 'Foundry' }
            { name: 'Agents__Orchestration', value: 'Workflow' }
            { name: 'Agents__Foundry__Auth', value: 'EntraId' }
            { name: 'Agents__Foundry__ProjectEndpoint', value: foundryProjectEndpoint }
            { name: 'Agents__Foundry__ReasoningModel', value: reasoningModel }
            { name: 'Agents__Foundry__FastModel', value: fastModel }
            { name: 'AZURE_CLIENT_ID', value: identity.properties.clientId }
            { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.properties.ConnectionString }
          ]
          probes: [
            {
              type: 'Liveness'
              httpGet: { path: '/health', port: 8080 }
              initialDelaySeconds: 10
              periodSeconds: 30
            }
          ]
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: 1
      }
    }
  }
}

output identityPrincipalId string = identity.properties.principalId
output registryLoginServer string = registry.properties.loginServer
output registryName string = registry.name
output webName string = web.name
output webUrl string = 'https://${web.properties.configuration.ingress.fqdn}'
output appInsightsId string = appInsights.id
output appInsightsConnectionString string = appInsights.properties.ConnectionString
