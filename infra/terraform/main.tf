# EventRelay on Azure: Container Apps + Service Bus + PostgreSQL Flexible Server.
#
# Example infrastructure for a learning/portfolio project. It is validated in CI
# (`terraform fmt` + `terraform validate`) but never applied from this repository:
# there is no backend, no state and no subscription id in the code. To try it, supply
# ARM_SUBSCRIPTION_ID and a remote backend of your own. The state will contain the
# generated secrets (database password, API keys), so keep it in a protected backend and
# never commit it.
#
# Network layout: PostgreSQL has no public endpoint. It sits in a delegated subnet of a
# virtual network and is reachable only from the Container Apps environment in the same
# network. Only the API's HTTPS ingress is public, and every call to it needs an API key.

terraform {
  required_version = ">= 1.6"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 5.14"
    }
    random = {
      source  = "hashicorp/random"
      version = "~> 3.6"
    }
  }
}

provider "azurerm" {
  features {}
  # The subscription comes from the ARM_SUBSCRIPTION_ID environment variable.
}

locals {
  name = "${var.name_prefix}-${var.environment}"

  tags = merge(var.tags, {
    project     = "event-relay"
    environment = var.environment
    managed_by  = "terraform"
  })

  servicebus_fqdn = "${azurerm_servicebus_namespace.main.name}.servicebus.windows.net"

  # VerifyFull: encrypted and the server certificate is validated against the image's CA store.
  postgres_connection_string = join(";", [
    "Host=${azurerm_postgresql_flexible_server.main.fqdn}",
    "Database=${azurerm_postgresql_flexible_server_database.eventrelay.name}",
    "Username=${azurerm_postgresql_flexible_server.main.administrator_login}",
    "Password=${random_password.postgres.result}",
    "SSL Mode=VerifyFull",
    "GSS Encryption Mode=Disable",
  ])

  # Settings shared by the API and the dispatcher.
  common_env = merge(
    {
      "ServiceBus__FullyQualifiedNamespace" = local.servicebus_fqdn
      "ServiceBus__TopicName"               = azurerm_servicebus_topic.events.name
      "Webhooks__RequireHttps"              = "true"
    },
    { for index, host in var.webhook_allowed_hosts : "Webhooks__AllowedHosts__${index}" => host },
  )
}

resource "random_string" "suffix" {
  length  = 6
  upper   = false
  special = false
}

resource "azurerm_resource_group" "main" {
  name     = "rg-${local.name}"
  location = var.location
  tags     = local.tags
}

resource "azurerm_log_analytics_workspace" "main" {
  name                = "log-${local.name}"
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
  sku                 = "PerGB2018"
  retention_in_days   = 30
  tags                = local.tags
}

# ---------------------------------------------------------------------------
# Virtual network: one subnet for the Container Apps environment, one for PostgreSQL
# ---------------------------------------------------------------------------

resource "azurerm_virtual_network" "main" {
  name                = "vnet-${local.name}"
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
  address_space       = ["10.20.0.0/16"]
  tags                = local.tags
}

resource "azurerm_subnet" "container_apps" {
  name                 = "snet-container-apps"
  resource_group_name  = azurerm_resource_group.main.name
  virtual_network_name = azurerm_virtual_network.main.name
  address_prefixes     = ["10.20.0.0/23"]

  delegation {
    name = "container-apps"

    service_delegation {
      name    = "Microsoft.App/environments"
      actions = ["Microsoft.Network/virtualNetworks/subnets/join/action"]
    }
  }
}

resource "azurerm_subnet" "postgres" {
  name                 = "snet-postgres"
  resource_group_name  = azurerm_resource_group.main.name
  virtual_network_name = azurerm_virtual_network.main.name
  address_prefixes     = ["10.20.2.0/24"]

  delegation {
    name = "postgres-flexible-server"

    service_delegation {
      name    = "Microsoft.DBforPostgreSQL/flexibleServers"
      actions = ["Microsoft.Network/virtualNetworks/subnets/join/action"]
    }
  }
}

# The server's host name resolves to its private address only inside the linked network.
resource "azurerm_private_dns_zone" "postgres" {
  name                = "${local.name}.private.postgres.database.azure.com"
  resource_group_name = azurerm_resource_group.main.name
  tags                = local.tags
}

resource "azurerm_private_dns_zone_virtual_network_link" "postgres" {
  name                  = "link-${local.name}"
  private_dns_zone_name = azurerm_private_dns_zone.postgres.name
  resource_group_name   = azurerm_resource_group.main.name
  virtual_network_id    = azurerm_virtual_network.main.id
  registration_enabled  = false
  tags                  = local.tags
}

# ---------------------------------------------------------------------------
# Service Bus: one topic, one subscription for the dispatcher.
# Local (SAS key) auth is disabled: the apps authenticate with managed identities.
# ---------------------------------------------------------------------------

resource "azurerm_servicebus_namespace" "main" {
  name                = "sb-${local.name}-${random_string.suffix.result}"
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
  sku                 = "Standard" # topics require Standard or Premium
  local_auth_enabled  = false
  minimum_tls_version = "1.2"
  tags                = local.tags
}

resource "azurerm_servicebus_topic" "events" {
  name         = "events"
  namespace_id = azurerm_servicebus_namespace.main.id

  # Broker-side de-duplication by MessageId as a first line of defence;
  # the dispatcher still de-duplicates itself because the window is finite.
  requires_duplicate_detection            = true
  duplicate_detection_history_time_window = "PT10M"
  default_message_ttl                     = "P7D"
}

resource "azurerm_servicebus_subscription" "dispatcher" {
  name                                 = "dispatcher"
  topic_id                             = azurerm_servicebus_topic.events.id
  max_delivery_count                   = 10
  lock_duration                        = "PT1M"
  dead_lettering_on_message_expiration = true
}

# ---------------------------------------------------------------------------
# PostgreSQL Flexible Server: private access only (VNet integration)
# ---------------------------------------------------------------------------

resource "random_password" "postgres" {
  length  = 32
  special = false # avoids escaping issues in the connection string; 32 alphanumerics is plenty
}

resource "azurerm_postgresql_flexible_server" "main" {
  name                          = "psql-${local.name}-${random_string.suffix.result}"
  location                      = azurerm_resource_group.main.location
  resource_group_name           = azurerm_resource_group.main.name
  version                       = "16"
  sku_name                      = var.postgres_sku
  storage_mb                    = 32768
  backup_retention_days         = 7
  zone                          = "1"
  administrator_login           = "eventrelay"
  administrator_password        = random_password.postgres.result
  delegated_subnet_id           = azurerm_subnet.postgres.id
  private_dns_zone_id           = azurerm_private_dns_zone.postgres.id
  public_network_access_enabled = false # no firewall rules: there is no public endpoint to open
  tags                          = local.tags

  depends_on = [azurerm_private_dns_zone_virtual_network_link.postgres]
}

resource "azurerm_postgresql_flexible_server_database" "eventrelay" {
  name      = "eventrelay"
  server_id = azurerm_postgresql_flexible_server.main.id
  charset   = "UTF8"
  collation = "en_US.utf8"
}

# ---------------------------------------------------------------------------
# API keys (X-Api-Key). Stored as Container Apps secrets; read them with
# `terraform output -raw admin_api_key`. Production: Key Vault references instead.
# ---------------------------------------------------------------------------

resource "random_password" "api_key" {
  for_each = toset(["publisher", "admin"])

  length  = 48
  special = false
}

# ---------------------------------------------------------------------------
# Identities and least-privilege access to Service Bus
# ---------------------------------------------------------------------------

resource "azurerm_user_assigned_identity" "api" {
  name                = "id-${local.name}-api"
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
  tags                = local.tags
}

resource "azurerm_user_assigned_identity" "dispatcher" {
  name                = "id-${local.name}-dispatcher"
  location            = azurerm_resource_group.main.location
  resource_group_name = azurerm_resource_group.main.name
  tags                = local.tags
}

# The API only publishes to the topic...
resource "azurerm_role_assignment" "api_sends_events" {
  scope                = azurerm_servicebus_topic.events.id
  role_definition_name = "Azure Service Bus Data Sender"
  principal_id         = azurerm_user_assigned_identity.api.principal_id
}

# ...and the dispatcher only receives from it.
resource "azurerm_role_assignment" "dispatcher_receives_events" {
  scope                = azurerm_servicebus_topic.events.id
  role_definition_name = "Azure Service Bus Data Receiver"
  principal_id         = azurerm_user_assigned_identity.dispatcher.principal_id
}

# ---------------------------------------------------------------------------
# Container Apps
# ---------------------------------------------------------------------------

resource "azurerm_container_app_environment" "main" {
  name                       = "cae-${local.name}"
  location                   = azurerm_resource_group.main.location
  resource_group_name        = azurerm_resource_group.main.name
  log_analytics_workspace_id = azurerm_log_analytics_workspace.main.id
  infrastructure_subnet_id   = azurerm_subnet.container_apps.id
  tags                       = local.tags

  workload_profile {
    name                  = "Consumption"
    workload_profile_type = "Consumption"
  }
}

resource "azurerm_container_app" "api" {
  name                         = "ca-${local.name}-api"
  container_app_environment_id = azurerm_container_app_environment.main.id
  resource_group_name          = azurerm_resource_group.main.name
  revision_mode                = "Single"
  workload_profile_name        = "Consumption"
  tags                         = local.tags

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.api.id]
  }

  secret {
    name  = "postgres-connection"
    value = local.postgres_connection_string
  }

  secret {
    name  = "publisher-api-key"
    value = random_password.api_key["publisher"].result
  }

  secret {
    name  = "admin-api-key"
    value = random_password.api_key["admin"].result
  }

  ingress {
    external_enabled = true
    target_port      = 8080

    traffic_weight {
      latest_revision = true
      percentage      = 100
    }
  }

  template {
    min_replicas = 1
    max_replicas = 3

    container {
      name   = "api"
      image  = var.api_image
      cpu    = 0.5
      memory = "1Gi"

      env {
        name        = "ConnectionStrings__Postgres"
        secret_name = "postgres-connection"
      }

      env {
        name        = "ApiKeys__Publisher__0"
        secret_name = "publisher-api-key"
      }

      env {
        name        = "ApiKeys__Admin__0"
        secret_name = "admin-api-key"
      }

      env {
        # Tells DefaultAzureCredential which user-assigned identity to use.
        name  = "AZURE_CLIENT_ID"
        value = azurerm_user_assigned_identity.api.client_id
      }

      env {
        # Kept simple for the example; EF Core locks the database while migrating.
        # A dedicated migration job would be the production choice.
        name  = "Database__MigrateOnStartup"
        value = "true"
      }

      dynamic "env" {
        for_each = local.common_env
        content {
          name  = env.key
          value = env.value
        }
      }

      liveness_probe {
        transport = "HTTP"
        port      = 8080
        path      = "/health"
      }

      readiness_probe {
        transport = "HTTP"
        port      = 8080
        path      = "/health"
      }
    }
  }

  depends_on = [azurerm_role_assignment.api_sends_events]
}

resource "azurerm_container_app" "dispatcher" {
  name                         = "ca-${local.name}-dispatcher"
  container_app_environment_id = azurerm_container_app_environment.main.id
  resource_group_name          = azurerm_resource_group.main.name
  revision_mode                = "Single"
  workload_profile_name        = "Consumption"
  tags                         = local.tags

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.dispatcher.id]
  }

  secret {
    name  = "postgres-connection"
    value = local.postgres_connection_string
  }

  # No ingress: the dispatcher only makes outbound calls (Service Bus, webhooks).
  template {
    min_replicas = 1
    max_replicas = 2

    container {
      name   = "dispatcher"
      image  = var.dispatcher_image
      cpu    = 0.5
      memory = "1Gi"

      env {
        name        = "ConnectionStrings__Postgres"
        secret_name = "postgres-connection"
      }

      env {
        name  = "AZURE_CLIENT_ID"
        value = azurerm_user_assigned_identity.dispatcher.client_id
      }

      env {
        name  = "ServiceBus__SubscriptionName"
        value = azurerm_servicebus_subscription.dispatcher.name
      }

      dynamic "env" {
        for_each = local.common_env
        content {
          name  = env.key
          value = env.value
        }
      }
    }
  }

  depends_on = [azurerm_role_assignment.dispatcher_receives_events]
}
