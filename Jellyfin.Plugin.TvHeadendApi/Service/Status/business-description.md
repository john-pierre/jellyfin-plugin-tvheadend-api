# Status Service

Reports TVHeadend server activity status and active client connections for the admin monitoring panel.

## Detailed Description

The Status Service queries TVHeadend's `/api/status/activity` and `/api/status/connections` endpoints to provide real-time information about server load and connected clients. This data powers the admin UI's status dashboard.

## Domain Context

- **Use Case:** Real-time server monitoring in admin panel
- **Module Type:** Service
- **Key Domain Entities:** StatusService

## Internal Dependencies

- **`Helper`** — ApiClient for status API calls

