output "resource_group_name" {
  description = "Resource group that contains everything."
  value       = azurerm_resource_group.main.name
}

output "api_url" {
  description = "Public URL of the EventRelay API."
  value       = "https://${azurerm_container_app.api.ingress[0].fqdn}"
}

output "servicebus_namespace" {
  description = "Fully qualified Service Bus namespace (identity-based access only)."
  value       = local.servicebus_fqdn
}

output "postgres_fqdn" {
  description = "PostgreSQL host name. Resolves to a private address inside the virtual network only."
  value       = azurerm_postgresql_flexible_server.main.fqdn
}

output "publisher_api_key" {
  description = "API key for POST /events. Read with `terraform output -raw publisher_api_key`."
  value       = random_password.api_key["publisher"].result
  sensitive   = true
}

output "admin_api_key" {
  description = "API key for every endpoint, including /subscriptions."
  value       = random_password.api_key["admin"].result
  sensitive   = true
}
