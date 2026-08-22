param suffix string

@description('Short environment name. Container App names have a 32-character limit.')
param environmentName string
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

/*
  Names are built from a deliberately short stem.

  Azure's name limits differ per resource type and two of them bit during the first real deployment:
  storage accounts cap at 24 characters and Container Apps at 32, while the descriptive
  'ca-certiflow-<service>-<env>-<13-char hash>' came to 41. Dropping 'certiflow' costs nothing - the
  resource group already says it - and truncating the hash to six characters keeps collisions
  implausible within one resource group while leaving room for the longest service name.

  3 + 12 ('verification') + 1 + 6 + 1 + 6 = 29.
*/
var stem = '${take(environmentName, 6)}-${take(uniqueString(resourceGroup().id), 6)}'

var gatewayAppName = 'ca-gateway-${stem}'
var registryAppName = 'ca-registry-${stem}'
var complianceAppName = 'ca-compliance-${stem}'

// Internal FQDNs for the services the gateway proxies to.
//
// These have to be supplied, and their absence is what made the first deployment look healthy while
// being useless: the YARP routes lived only in appsettings.Development.json, so in Production the
// gateway had no routes and answered 404 for everything - while /health and the OIDC discovery
// document, which are mapped in code, both returned 200. A smoke test that only checks the front
// door would have called that a success.
var internalSuffix = 'internal.${environment.properties.defaultDomain}'

var services = [
  { name: 'gateway',      image: 'gateway',      external: true,  storage: false, openAi: false }
  { name: 'registry',     image: 'registry',     external: false, storage: false, openAi: false }
  { name: 'intake',       image: 'intake',       external: false, storage: true,  openAi: false }
  { name: 'verification', image: 'verification', external: false, storage: false, openAi: false }
  { name: 'compliance',   image: 'compliance',   external: false, storage: false, openAi: false }
  { name: 'audit',        image: 'audit',        external: false, storage: false, openAi: false }
  { name: 'reporting',    image: 'reporting',    external: false, storage: true,  openAi: false }
  { name: 'notifications', image: 'notifications', external: false, storage: false, openAi: false }
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
  name: 'ca-${service.name}-${stem}'
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
            // Logging levels are NOT set here, and the attempt is worth recording: only
            // appsettings.Development.json turned EF command logging down, so Production logged
            // every outbox SELECT - one every two seconds, per service - which buried the actual
            // extraction failure this deployment was trying to diagnose.
            //
            // The obvious fix, an environment variable, cannot work: Container Apps runs on
            // Kubernetes, whose environment variable names may not contain a dot, and the key is
            // Microsoft.EntityFrameworkCore.Database.Command. `__` separates sections; it does not
            // escape dots within a key. The levels ship in each service's appsettings.json instead.
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
            { name: 'ConnectionStrings__NotificationsDatabase', value: sqlConnectionString }
            { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsightsConnectionString }
            { name: 'Auth__Authority', value: 'https://${gatewayAppName}.${environment.properties.defaultDomain}' }
            { name: 'Auth__Audience', value: 'certiflow-api' }
            { name: 'Auth__RequireHttpsMetadata', value: 'true' }
          ], service.storage ? [
            { name: 'Storage__ServiceUri', value: storageBlobEndpoint }
            { name: 'Storage__AccountName', value: storageAccountName }
            // Named to match the containers created in data.bicep. Without these the services fall
            // back to their defaults, which happen to be the same - but "happen to be" is not a
            // configuration strategy.
            { name: 'Storage__DocumentsContainer', value: 'documents' }
            { name: 'Storage__ReportsContainer', value: 'reports' }
          ] : [], service.openAi ? [
            { name: 'AzureOpenAI__Endpoint', value: openAiEndpoint }
            { name: 'AzureOpenAI__Deployment', value: openAiDeploymentName }
          ] : [], service.name == 'gateway' ? [
            { name: 'Auth__Issuer', value: 'https://${gatewayAppName}.${environment.properties.defaultDomain}' }
            { name: 'Cors__SpaOrigin', value: 'https://${gatewayAppName}.${environment.properties.defaultDomain}' }
            // One per cluster declared in the gateway's appsettings.json. Double underscore is the
            // configuration provider's section separator, so these bind straight onto
            // ReverseProxy:Clusters:<id>:Destinations:primary:Address.
            //
            // Their absence is what made the first deployment look healthy and be useless: the
            // routes lived only in appsettings.Development.json, so Production had no proxy config
            // and answered 404 for every API call while /health and OIDC discovery - both mapped in
            // code - returned 200.
            { name: 'ReverseProxy__Clusters__registry__Destinations__primary__Address', value: 'https://${registryAppName}.${internalSuffix}' }
            { name: 'ReverseProxy__Clusters__intake__Destinations__primary__Address', value: 'https://ca-intake-${stem}.${internalSuffix}' }
            { name: 'ReverseProxy__Clusters__verification__Destinations__primary__Address', value: 'https://ca-verification-${stem}.${internalSuffix}' }
            { name: 'ReverseProxy__Clusters__compliance__Destinations__primary__Address', value: 'https://${complianceAppName}.${internalSuffix}' }
            { name: 'ReverseProxy__Clusters__audit__Destinations__primary__Address', value: 'https://ca-audit-${stem}.${internalSuffix}' }
            { name: 'ReverseProxy__Clusters__reporting__Destinations__primary__Address', value: 'https://ca-reporting-${stem}.${internalSuffix}' }
            { name: 'ReverseProxy__Clusters__notifications__Destinations__primary__Address', value: 'https://ca-notifications-${stem}.${internalSuffix}' }
          ] : [], service.name == 'reporting' ? [
            { name: 'Services__Compliance', value: 'https://${complianceAppName}.${internalSuffix}' }
            { name: 'Services__Registry', value: 'https://${registryAppName}.${internalSuffix}' }
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
        /*
          One replica for anything that consumes messages; zero only for the gateway.

          This started at zero everywhere, and the deployment looked fine: health checks passed, the
          authorization matrix was perfect, and the audit ledger even filled - because restarting the
          revisions had briefly woken everything. Then a supplier was registered and Compliance never
          created its state. A scaled-to-zero container is not listening to Service Bus. HTTP traffic
          wakes an app; a message arriving does not.

          The proper fix is a KEDA azure-servicebus scale rule per subscription, which is how you
          keep scale-to-zero and still process events. It needs a scaler identity and one rule per
          queue this system has dozens of, and it buys nothing here: the environment exists for the
          length of a recording session (SRS §5.0), so 'idle' is a state it is barely in.

          The gateway keeps minReplicas: 0 because it is purely HTTP - a request wakes it, and §20 R8
          already accepts that first-request cold start.
        */
        minReplicas: service.name == 'gateway' ? 0 : 1
        maxReplicas: 2
      }
    }
  }
  dependsOn: [acrPull, serviceBusAccess]
}]

output gatewayUrl string = 'https://${apps[0].properties.configuration.ingress.fqdn}'
// The worker's principal, so the OpenAI role assignment can be made in the account's own resource
// group - which is deliberately not this one. See modules/openai-access.bicep.
//
// Found by index, not by position: this was identities[7] until a ninth service was inserted above
// the worker, at which point it would have granted OpenAI access to the notification service and
// left the only thing that calls OpenAI without it.
var workerIndex = indexOf(map(services, service => service.name), 'worker')

output workerPrincipalId string = identities[workerIndex].properties.principalId

output identityPrincipalIds array = [for (service, i) in services: {
  service: service.name
  principalId: identities[i].properties.principalId
  clientId: identities[i].properties.clientId
}]
