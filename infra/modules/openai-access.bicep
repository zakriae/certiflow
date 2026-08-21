targetScope = 'resourceGroup'

@description('Principal id of the worker identity, the only thing allowed to spend tokens.')
param workerPrincipalId string

@description('Name of the pre-existing Azure OpenAI account in THIS resource group.')
param openAiAccountName string

/*
  Deployed into the Azure OpenAI account's own resource group, which is not the one the environment
  lives in.

  That separation is the point. The OpenAI account was created by hand, holds a model deployment
  that costs time and quota to recreate, and bills per token rather than per hour - so it should
  outlive any environment. Keeping it in the environment's resource group would mean
  `az group delete` takes it with them, and the teardown script's promise not to would be a comment
  rather than a fact.

  Only the worker gets this role. Guardrail G1 and §13.4 both reduce to "the thing that spends money
  should be the only thing that can", and this is where that stops being a policy and becomes a
  permission.
*/
resource account 'Microsoft.CognitiveServices/accounts@2024-10-01' existing = {
  name: openAiAccountName
}

resource openAiUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(account.id, workerPrincipalId, 'CognitiveServicesOpenAIUser')
  scope: account
  properties: {
    principalId: workerPrincipalId
    roleDefinitionId: subscriptionResourceId(
      'Microsoft.Authorization/roleDefinitions', '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')
    principalType: 'ServicePrincipal'
  }
}

output endpoint string = account.properties.endpoint
