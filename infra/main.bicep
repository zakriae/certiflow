targetScope = 'resourceGroup'

/*
  Certiflow's entire runtime, as code.

  NFR-3 is the requirement that shapes this file: the full stack must come back from `main` plus
  Bicep in under 30 minutes, unattended, because the environment is provisioned for recording
  sessions and torn down afterwards (SRS §5.0). That makes "it deploys" insufficient — it has to
  deploy from nothing, repeatably, with no manual step in the middle.

  Two consequences run through everything below:

  1. Nothing here holds a secret. Every service reaches SQL, Storage, Service Bus and Azure OpenAI
     with its own managed identity (NFR-9). There is no connection string with a password in it and
     no key in any app setting, so there is nothing to rotate and nothing to leak in a deployment
     log.

  2. The Azure OpenAI account is *referenced*, never created. It was provisioned by hand, it holds
     a model deployment that costs money to recreate, and a teardown that deletes it would make the
     next deploy a manual job again.
*/

@description('Short environment name, used as a suffix on every resource.')
@minLength(2)
@maxLength(8)
param environmentName string = 'dev'

param location string = resourceGroup().location

@description('Name of the pre-existing Azure OpenAI account. Referenced, never created or deleted.')
param openAiAccountName string

@description('''
Resource group holding the Azure OpenAI account. Deliberately NOT this one: the environment's
resource group is deleted wholesale at teardown, and the OpenAI account must survive that.
''')
param openAiResourceGroup string

@description('The gpt-5-mini deployment inside that account.')
param openAiDeploymentName string = 'gpt-5-mini'

@description('Entra object id that becomes SQL admin. Defaults to the deploying principal.')
param sqlAdminObjectId string

@description('Display name for the SQL Entra admin.')
param sqlAdminLogin string

@description('Container image tag to deploy. The CD workflow passes the commit sha.')
param imageTag string = 'latest'

var suffix = '${environmentName}-${uniqueString(resourceGroup().id)}'
var tags = {
  application: 'certiflow'
  environment: environmentName
  // Read by scripts/teardown.sh. A tag is a poor lock, but it is enough to stop a teardown script
  // deleting a resource group somebody else owns.
  managedBy: 'bicep'
}

module registry 'modules/registry.bicep' = {
  name: 'registry'
  params: {
    name: 'crcertiflow${replace(suffix, '-', '')}'
    location: location
    tags: tags
  }
}

module observability 'modules/observability.bicep' = {
  name: 'observability'
  params: {
    name: 'log-certiflow-${suffix}'
    location: location
    tags: tags
  }
}

module data 'modules/data.bicep' = {
  name: 'data'
  params: {
    suffix: suffix
    location: location
    tags: tags
    sqlAdminObjectId: sqlAdminObjectId
    sqlAdminLogin: sqlAdminLogin
  }
}

module messaging 'modules/messaging.bicep' = {
  name: 'messaging'
  params: {
    name: 'sb-certiflow-${suffix}'
    location: location
    tags: tags
  }
}

module apps 'modules/apps.bicep' = {
  name: 'apps'
  params: {
    suffix: suffix
    location: location
    tags: tags
    imageTag: imageTag
    registryLoginServer: registry.outputs.loginServer
    registryId: registry.outputs.id
    logAnalyticsCustomerId: observability.outputs.customerId
    logAnalyticsKey: observability.outputs.primaryKey
    appInsightsConnectionString: observability.outputs.appInsightsConnectionString
    sqlConnectionString: data.outputs.sqlConnectionString
    storageAccountName: data.outputs.storageAccountName
    storageBlobEndpoint: data.outputs.blobEndpoint
    serviceBusNamespace: messaging.outputs.fullyQualifiedNamespace
    serviceBusId: messaging.outputs.id
    storageAccountId: data.outputs.storageAccountId
    openAiEndpoint: existingOpenAi.properties.endpoint
    openAiDeploymentName: openAiDeploymentName
  }
}

// Referenced across resource groups, never created. See the header and openai-access.bicep.
resource existingOpenAi 'Microsoft.CognitiveServices/accounts@2024-10-01' existing = {
  name: openAiAccountName
  scope: resourceGroup(openAiResourceGroup)
}

// Deployed into the OpenAI account's resource group, because that is where the role assignment has
// to live.
module openAiAccess 'modules/openai-access.bicep' = {
  name: 'openai-access'
  scope: resourceGroup(openAiResourceGroup)
  params: {
    workerPrincipalId: apps.outputs.workerPrincipalId
    openAiAccountName: openAiAccountName
  }
}

output gatewayUrl string = apps.outputs.gatewayUrl
output registryLoginServer string = registry.outputs.loginServer
output sqlServerName string = data.outputs.sqlServerName
output sqlDatabaseName string = data.outputs.sqlDatabaseName
output storageAccountName string = data.outputs.storageAccountName
