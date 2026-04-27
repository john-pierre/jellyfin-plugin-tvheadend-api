# Configuration Service

Lambda-based providers that decouple services from `Plugin.Instance` for configuration access and persistence.

## Detailed Description

`ConfigurationProvider` wraps a `Func<PluginConfiguration?>` delegate that resolves the current plugin configuration at call time. Services inject `ConfigurationProvider` instead of depending on the static `Plugin.Instance` singleton, enabling testability and clean DI registration. Returns `null` when the plugin instance is unavailable (e.g. during startup).

`ConfigurationSaver` wraps an `Action<Action<PluginConfiguration>>` delegate that applies a mutation to the configuration and persists it. Services call `Save(mutate)` with a lambda that modifies the configuration object; the delegate handles serialization and disk persistence.

Both classes are registered in DI with delegates pointing to `Plugin.Instance.Configuration` and `Plugin.Instance.UpdateConfiguration`, respectively.

## Domain Context

- **Use Case:** Indirection layer between services and plugin configuration lifecycle
- **Module Type:** Infrastructure
- **Key Domain Entities:** ConfigurationProvider, ConfigurationSaver, PluginConfiguration
