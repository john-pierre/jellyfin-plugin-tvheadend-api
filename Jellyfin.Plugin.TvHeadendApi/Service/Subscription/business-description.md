# Subscription Service

Lists active streaming subscriptions in TVHeadend to show which streams are currently being consumed.

## Detailed Description

The Subscription Service queries TVHeadend's `/api/status/subscriptions` endpoint to enumerate all active streaming sessions. This provides visibility into current resource usage and helps correlate client playback with TVHeadend-side resource allocation.

## Domain Context

- **Use Case:** Active stream monitoring in admin panel
- **Module Type:** Service
- **Key Domain Entities:** SubscriptionService

## Internal Dependencies

- **`Helper`** — ApiClient for subscription status API calls

