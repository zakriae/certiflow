param suffix string
param location string
param tags object
param imageTag string
param registryLoginServer string
param registryId string
param logAnalyticsCustomerId string
@secure()
param logAnalyticsKey string
param appInsightsConnectionString string
param sqlConnectionString string
param storageAccountName string
param storageBlobEndpoint string
param storageAccountId string
param serviceBusNamespace string
param serviceBusId string
param openAiEndpoint string
param openAiDeploymentName string

/*
  Eight containers: the gateway, six APIs and the extraction worker.

  Each gets its own user-assigned managed identity rather than one shared identity for the lot. It
  costs nothing and it means the audit service cannot write to blob storage, the worker cannot read
  the review queue, and a compromised container is limited to what that one service legitimately
  does. A single shared identity would give every container the union of every permission - which
  is the same as giving them all the maximum.
*/

var services = [
  { name: 'gateway',      image: 'gateway',      external: true,  storage: false, openAi: false }
  { name: 'registry',     image: 'registry',     external: false, storage: false, openAi: false }
  { name: 'intake',       image: 'intake',       external: false, storage: true,  openAi: false }
  { name: 'verification', image: 'verification', external: false, storage: false, openAi: false }
  { name: 'compliance',   image: 'compliance',   external: false, storage: false, openAi: false }
  { name: 'audit',        image: 'audit',        external: false, storage: false, openAi: false }
  { name: 'reporting',    image: 'reporting',    external: false, storage: true,  openAi: false }
  { name: 'worker',       image: 'worker',       external: false, storage: true,  openAi: true  }
]

var roles = {
  acrPull: '7f951dda-4ed3-4680-a7ca-43fe172d538d'
  storageBlobDataContributor: 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
  // Delegator, so a service can mint a *user-delegation* SAS. Without it the API can only produce
  // a key-signed SAS, and shared-key access is disabled on the account by design.
  storageBlobDelegator: 'db58b8e5-c6ad-4a2a-8342-4190687cbf4a'
  serviceBusDataOwner: '090c5cfd-751d-490a-894a-3ce6f1109419'
  cognitiveServicesOpenAiUser: '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd'
}

resource identities 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = [for service in services: {
  name: 'id-certiflow-${service.name}-${suffix}'
  location: location
  tags: tags
}]

resource environment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: 'cae-certiflow-${suffix}'
  location: location
  tags: tags
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalyticsCustomerId
        sharedKey: logAnalyticsKey
      }
    }
  }
}

resource acrPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for (service, i) in services: {
  name: guid(registryId, identities[i].id, roles.acrPull)
  scope: resourceGroup()
  properties: {
    principalId: identities[i].properties.principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.acrPull)
    principalType: 'ServicePrincipal'
  }
}]

// Every service talks to the bus. MassTransit creates its own topics and subscriptions at startup,
// which needs Data Owner rather than Sender/Receiver - the topology is code, not deployed
// separately, and that is the price of it.
resource serviceBusAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for (service, i) in services: {
  name: guid(serviceBusId, identities[i].id, roles.serviceBusDataOwner)
  scope: resourceGroup()
  properties: {
    principalId: identities[i].properties.principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.serviceBusDataOwner)
    principalType: 'ServicePrincipal'
  }
}]

resource blobAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for (service, i) in services: if (service.storage) {
  name: guid(storageAccountId, identities[i].id, roles.storageBlobDataContributor)
  scope: resourceGroup()
  properties: {
    principalId: identities[i].properties.principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.storageBlobDataContributor)
    principalType: 'ServicePrincipal'
  }
}]

resource delegatorAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = [for (service, i) in services: if (service.storage) {
  name: guid(storageAccountId, identities[i].id, roles.storageBlobDelegator)
  scope: resourceGroup()
  properties: {
    principalId: identities[i].properties.principalId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.storageBlobDelegator)
    principalType: 'ServicePrincipal'
  }
}]

resource apps 'Microsoft.App/containerApps@2024-03-01' = [for (service, i) in services: {
  name: 'ca-certiflow-${service.name}-${suffix}'
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${identities[i].id}': {} }
  }
  properties: {
    managedEnvironmentId: environment.id
    configuration: {
      ingress: service.name == 'worker' ? null : {
        // Only the gateway is reachable from the internet. Everything else is internal, which is
        // what makes the gateway a front door rather than a suggestion - and why each service also
        // validates the token itself (ADR-0007), because internal is not the same as safe.
        external: service.external
        targetPort: 8080
        transport: 'auto'
        allowInsecure: false
      }
      registries: [
        {
          server: registryLoginServer
          identity: identities[i].id
        }
      ]
      activeRevisionsMode: 'Single'
    }
    template: {
      containers: [
        {
          name: service.name
          image: '${registryLoginServer}/certiflow-${service.image}:${imageTag}'
          resources: {
            cpu: json('0.5')
            memory: '1Gi'
          }
          env: concat([
            { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
            { name: 'DOTNET_ENVIRONMENT', value: 'Production' }
            // How DefaultAzureCredential knows which identity to use when a container has one
            // assigned. Without it the credential tries them in order and fails confusingly.
            { name: 'AZURE_CLIENT_ID', value: identities[i].properties.clientId }
            { name: 'ConnectionStrings__ServiceBus', value: serviceBusNamespace }
            { name: 'ConnectionStrings__RegistryDatabase', value: sqlConnectionString }
            { name: 'ConnectionStrings__IntakeDatabase', value: sqlConnectionString }
            { name: 'ConnectionStrings__IntelligenceDatabase', value: sqlConnectionString }
            { name: 'ConnectionStrings__VerificationDatabase', value: sqlConnectionString }
            { name: 'ConnectionStrings__ComplianceDatabase', value: sqlConnectionString }
            { name: 'ConnectionStrings__AuditDatabase', value: sqlConnectionString }
            { name: 'ConnectionStrings__ReportingDatabase', value: sqlConnectionString }
            { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsightsConnectionString }
            { name: 'Auth__Authority', value: 'https://ca-certiflow-gateway-${suffix}.${environment.properties.defaultDomain}' }
            { name: 'Auth__Audience', value: 'certiflow-api' }
            { name: 'Auth__RequireHttpsMetadata', value: 'true' }
          ], service.storage ? [
            { name: 'Storage__ServiceUri', value: storageBlobEndpoint }
            { name: 'Storage__AccountName', value: storageAccountName }
          ] : [], service.openAi ? [
            { name: 'AzureOpenAI__Endpoint', value: openAiEndpoint }
            { name: 'AzureOpenAI__Deployment', value: openAiDeploymentName }
          ] : [], service.name == 'gateway' ? [
            { name: 'Auth__Issuer', value: 'https://ca-certiflow-gateway-${suffix}.${environment.properties.defaultDomain}' }
            { name: 'Services__Registry', value: 'https://ca-certiflow-registry-${suffix}.internal.${environment.properties.defaultDomain}' }
          ] : [], service.name == 'reporting' ? [
            { name: 'Services__Compliance', value: 'https://ca-certiflow-compliance-${suffix}.internal.${environment.properties.defaultDomain}' }
            { name: 'Services__Registry', value: 'https://ca-certiflow-registry-${suffix}.internal.${environment.properties.defaultDomain}' }
          ] : [])
          probes: service.name == 'worker' ? [] : [
            {
              type: 'Readiness'
              httpGet: { path: '/health', port: 8080 }
              initialDelaySeconds: 5
              periodSeconds: 10
            }
          ]
        }
      ]
      scale: {
        // Zero. The environment exists for a recording session, and paying for a warm replica
        // around the clock to avoid a cold start is the wrong trade (SRS §20 R8 - warm them by
        // hitting them once before recording instead).
        minReplicas: 0
        maxReplicas: 2
      }
    }
  }
  dependsOn: [acrPull, serviceBusAccess]
}]

output gatewayUrl string = 'https://${apps[0].properties.configuration.ingress.fqdn}'
// The worker's principal, so the OpenAI role assignment can be made in the account's own resource
// group - which is deliberately not this one. See modules/openai-access.bicep.
output workerPrincipalId string = identities[7].properties.principalId

output identityPrincipalIds array = [for (service, i) in services: {
  service: service.name
  principalId: identities[i].properties.principalId
  clientId: identities[i].properties.clientId
}]
