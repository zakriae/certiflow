param name string
param location string
param tags object

/*
  Basic tier. Premium buys geo-replication and private endpoints, neither of which matters for an
  environment that exists for the length of a recording session.

  Admin user is disabled: Container Apps pulls with a managed identity instead (see apps.bicep).
  Enabling it would hand out a registry password that would then live in a deployment parameter.
*/
resource acr 'Microsoft.ContainerRegistry/registries@2023-11-01-preview' = {
  name: name
  location: location
  tags: tags
  sku: { name: 'Basic' }
  properties: {
    adminUserEnabled: false
    anonymousPullEnabled: false
  }
}

output id string = acr.id
output loginServer string = acr.properties.loginServer
output name string = acr.name
