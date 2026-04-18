# Diagnostic Service

Runs compatibility checks and produces a structured diagnostic report with a health score for the plugin's configuration and connectivity.

## Detailed Description

The Diagnostic Service analyzes plugin configuration, TVHeadend server info, streaming profile availability, and Jellyfin encoding options to produce a comprehensive compatibility report. It identifies misconfigurations, missing profiles, and connectivity issues, assigning a numeric score. `EncodingOptionsReader` extracts relevant Jellyfin server encoding settings for analysis.

## Domain Context

- **Use Case:** Admin troubleshooting via the plugin settings page diagnostic panel
- **Module Type:** Service
- **Key Domain Entities:** DiagnosticService, EncodingOptionsReader

## Internal Dependencies

- **`Profile`** — Reads profile availability for compatibility scoring
- **`Helper`** — ApiClient for serverinfo endpoint
- **`Configuration`** — Reads PluginConfiguration for analysis

