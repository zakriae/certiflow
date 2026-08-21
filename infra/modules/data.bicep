param suffix string

@description('Environment name, used where a resource has a tighter name limit than the full suffix.')
param environmentName string
param location string
param tags object
param sqlAdminObjectId string
param sqlAdminLogin string

/*
  SQL and Storage, both reachable only by identity.

  One database with a schema per bounded context (SRS §13.1). Eight databases would be the textbook
  microservices answer and would cost eight times as much for a system whose contexts already never
  read each other's tables; the trade is written up in the SRS and the migration history tables make
  the isolation real (each context tracks its own inside its own schema).
*/

resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: 'sql-certiflow-${suffix}'
  location: location
  tags: tags
  properties: {
    // No SQL login exists at all. There is no administratorLogin/administratorLoginPassword pair
    // here, so there is no password to put in a parameter file, a key vault, or a deployment log.
    administrators: {
      administratorType: 'ActiveDirectory'
      principalType: 'User'
      login: sqlAdminLogin
      sid: sqlAdminObjectId
      tenantId: subscription().tenantId
      azureADOnlyAuthentication: true
    }
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
  }
}

resource database 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
  parent: sqlServer
  name: 'certiflow'
  location: location
  tags: tags
  sku: {
    // Serverless, and the auto-pause is the point: an environment left up overnight by accident
    // stops billing compute after an hour instead of quietly running until someone notices (R9).
    name: 'GP_S_Gen5_1'
    tier: 'GeneralPurpose'
    family: 'Gen5'
    capacity: 1
  }
  properties: {
    autoPauseDelay: 60
    minCapacity: json('0.5')
    maxSizeBytes: 34359738368
    zoneRedundant: false
  }
}

// Container Apps have no fixed egress IP on a Consumption plan, so the alternative to this rule is
// a VNet-integrated environment - a real answer for production and a poor trade for an environment
// that exists for an afternoon. Entra-only auth means an open port is not an open door.
resource allowAzureServices 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = {
  parent: sqlServer
  name: 'AllowAzureServices'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

// Storage account names are capped at 24 characters, which is tighter than every other resource
// here - 'stcertiflow' plus the full suffix came to 28 and failed preflight. `take` bounds it for
// any environment name rather than working only for the short ones: 2 + 6 + 13 = 21.
resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: 'st${take(environmentName, 6)}${uniqueString(resourceGroup().id)}'
  location: location
  tags: tags
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
  properties: {
    allowBlobPublicAccess: false
    // Off, so a leaked account key is not a thing that can exist. Every caller uses its identity,
    // and the SAS URLs the APIs mint are user-delegation SAS signed by that identity (NFR-10).
    allowSharedKeyAccess: false
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: storage
  name: 'default'
  properties: {
    cors: {
      corsRules: [
        {
          // The PDF viewer fetches documents straight from storage by SAS, so the browser needs
          // CORS here or it fails with an opaque "Failed to fetch" (learned the hard way against
          // Azurite). Tightened to the SPA origin at deploy time in a real environment.
          allowedOrigins: ['*']
          allowedMethods: ['GET', 'HEAD', 'OPTIONS']
          allowedHeaders: ['*']
          exposedHeaders: ['*']
          maxAgeInSeconds: 3600
        }
      ]
    }
  }
}

resource documents 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'documents'
  properties: { publicAccess: 'None' }
}

// Separate from documents (SRS §13.2): reports are immutable once written and have a different
// retention story from the certificates they cite. Sharing a container would make "delete this
// supplier's uploads" quietly capable of deleting their attestations too.
resource reports 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'reports'
  properties: { publicAccess: 'None' }
}

output sqlServerName string = sqlServer.name
output sqlDatabaseName string = database.name
output sqlConnectionString string = 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Database=${database.name};Authentication=Active Directory Default;Encrypt=True;TrustServerCertificate=False;'
output storageAccountName string = storage.name
output storageAccountId string = storage.id
output blobEndpoint string = storage.properties.primaryEndpoints.blob
