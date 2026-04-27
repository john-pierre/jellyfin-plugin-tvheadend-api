# Common Service

Shared JSON serialization settings used across all TVHeadend API services.

## Detailed Description

`JsonDefaults` is a static class that exposes a single `JsonSerializerOptions` instance (`Api`) configured with `PropertyNameCaseInsensitive = true`. This is the canonical options object for deserializing TVHeadend API JSON responses throughout the plugin, ensuring consistent behavior and avoiding repeated allocations.

## Domain Context

- **Use Case:** Consistent JSON deserialization across all TVHeadend API consumers
- **Module Type:** Infrastructure (Static)
- **Key Domain Entities:** JsonDefaults
