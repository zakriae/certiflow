param name string
param location string
param tags object

/*
  Standard, not Basic: MassTransit's topology uses topics and subscriptions, and Basic has queues
  only. Not Premium either - Premium is priced for throughput this will never see.

  No SAS rules are created. Services connect with a managed identity and the Azure Service Bus Data
  Sender/Receiver roles, which is why nothing here emits a connection string with a key in it.
*/
resource namespace 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' = {
  name: name
  location: location
  tags: tags
  sku: {
    name: 'Standard'
    tier: 'Standard'
  }
  properties: {
    disableLocalAuth: true
    minimumTlsVersion: '1.2'
  }
}

output id string = namespace.id
output name string = namespace.name
output fullyQualifiedNamespace string = '${namespace.name}.servicebus.windows.net'
