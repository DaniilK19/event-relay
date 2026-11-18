variable "location" {
  description = "Azure region for all resources."
  type        = string
  default     = "westeurope"
}

variable "name_prefix" {
  description = "Prefix for resource names. Lowercase letters and digits only."
  type        = string
  default     = "eventrelay"

  validation {
    condition     = can(regex("^[a-z][a-z0-9]{2,11}$", var.name_prefix))
    error_message = "name_prefix must be 3-12 lowercase letters or digits and start with a letter."
  }
}

variable "environment" {
  description = "Environment name used in resource names and tags, e.g. demo or dev."
  type        = string
  default     = "demo"

  validation {
    condition     = can(regex("^[a-z0-9]{2,8}$", var.environment))
    error_message = "environment must be 2-8 lowercase letters or digits."
  }
}

variable "api_image" {
  description = "Container image for EventRelay.Api, e.g. ghcr.io/<owner>/eventrelay-api:<tag>."
  type        = string
}

variable "dispatcher_image" {
  description = "Container image for EventRelay.Dispatcher, e.g. ghcr.io/<owner>/eventrelay-dispatcher:<tag>."
  type        = string
}

variable "webhook_allowed_hosts" {
  description = "Hosts webhooks may be delivered to (exact names or *.example.com). Empty blocks every webhook."
  type        = list(string)
  default     = []

  validation {
    condition     = alltrue([for host in var.webhook_allowed_hosts : can(regex("^(\\*\\.)?[a-z0-9.-]+$", host))])
    error_message = "Each allowed host must be a lowercase host name, optionally starting with '*.'."
  }
}

variable "postgres_sku" {
  description = "PostgreSQL Flexible Server SKU."
  type        = string
  default     = "B_Standard_B1ms"
}

variable "tags" {
  description = "Extra tags applied to every resource."
  type        = map(string)
  default     = {}
}
