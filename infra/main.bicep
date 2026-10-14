// WhyLine on Azure: Container Apps + Container Registry + Application Insights, signing in to your existing Microsoft
// Foundry project with a managed identity (no API key in the cloud). Deploy with `azd up`; see docs/deploy-azure.md.
targetScope = 'subscription'

@minLength(1)
@maxLength(64)
@description('azd environment name; used to name the resource group and resources.')
param environmentName string

@minLength(1)
@description('Region for the WhyLine resources (Container Apps, registry, monitoring).')
param location string

@description('Your user object id (azd sets AZURE_PRINCIPAL_ID). Gets data access to the Foundry project so you can run locally with az login.')
param principalId string = ''

@description('Foundry project endpoint, e.g. https://my-resource.services.ai.azure.com/api/projects/my-project')
param foundryProjectEndpoint string

@description('Resource group that contains the Foundry resource (it can differ from the WhyLine resource group).')
param foundryResourceGroup string

@description('Model deployment for the Analyst and Recap agents.')
param reasoningModel string = 'gpt-5.5'

@description('Model deployment for the Narrator and Localizer agents.')
param fastModel string = 'gpt-5.4-mini'

@description('Set by azd once the app exists, so re-provisioning keeps the deployed image.')
param webExists bool = false

var tags = { 'azd-env-name': environmentName, app: 'whyline' }
var token = toLower(uniqueString(subscription().id, environmentName, location))

// https://<foundry-resource>.services.ai.azure.com/api/projects/<project>
var endpoint = endsWith(trim(foundryProjectEndpoint), '/') ? substring(trim(foundryProjectEndpoint), 0, max(length(trim(foundryProjectEndpoint)) - 1, 0)) : trim(foundryProjectEndpoint)
var foundryHost = split(split(endpoint, '//')[1], '/')[0]
var foundryAccountName = split(foundryHost, '.')[0]
var foundryProjectName = last(split(endpoint, '/'))

resource rg 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: 'rg-${environmentName}'
  location: location
  tags: tags
}

module app 'modules/app.bicep' = {
  name: 'whyline-app'
  scope: rg
  params: {
    location: location
    tags: tags
    token: token
    foundryProjectEndpoint: endpoint
    reasoningModel: reasoningModel
    fastModel: fastModel
    webExists: webExists
  }
}

module foundryAccess 'modules/foundry-access.bicep' = {
  name: 'whyline-foundry-access'
  scope: resourceGroup(foundryResourceGroup)
  params: {
    foundryAccountName: foundryAccountName
    foundryProjectName: foundryProjectName
    appPrincipalId: app.outputs.identityPrincipalId
    userPrincipalId: principalId
    appInsightsId: app.outputs.appInsightsId
    appInsightsConnectionString: app.outputs.appInsightsConnectionString
  }
}

output AZURE_RESOURCE_GROUP string = rg.name
output AZURE_CONTAINER_REGISTRY_ENDPOINT string = app.outputs.registryLoginServer
output AZURE_CONTAINER_REGISTRY_NAME string = app.outputs.registryName
output SERVICE_WEB_NAME string = app.outputs.webName
output WHYLINE_URL string = app.outputs.webUrl
output FOUNDRY_ACCOUNT_NAME string = foundryAccountName
output FOUNDRY_PROJECT_NAME string = foundryProjectName
