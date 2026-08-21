param name string
param location string
param tags object

resource workspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: name
  location: location
  tags: tags
  properties: {
    sku: { name: 'PerGB2018' }
    // Thirty days is the minimum billable retention. An environment that lives for hours has no
    // use for ninety days of logs, and retention is a line on the bill.
    retentionInDays: 30
  }
}

resource insights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'appi-${name}'
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: workspace.id
  }
}

output customerId string = workspace.properties.customerId
@secure()
output primaryKey string = workspace.listKeys().primarySharedKey
output appInsightsConnectionString string = insights.properties.ConnectionString
