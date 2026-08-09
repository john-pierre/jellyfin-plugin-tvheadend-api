# Input Monitor Service

Monitors TV tuner/input hardware status including signal strength, bit error rate, signal-to-noise ratio, and bitrate.

## Detailed Description

The Input Monitor Service queries TVHeadend's `/api/status/inputs` endpoint to retrieve real-time tuner signal quality metrics. This information helps administrators identify reception problems and hardware issues.

## Domain Context

- **Use Case:** Tuner health monitoring in admin panel
- **Module Type:** Service
- **Key Domain Entities:** InputMonitorService

## Internal Dependencies

- **`Backend`** — ApiClient for input status API calls

