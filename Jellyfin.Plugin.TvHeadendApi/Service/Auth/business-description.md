# Auth Service

Generates and validates authentication tokens for secure, password-less access to TVHeadend stream and image URLs.

## Detailed Description

The Auth Service creates persistent alphanumeric auth tokens via TVHeadend's user idnode API. These tokens are appended as `?auth=<token>` query parameters to stream and image URLs, enabling Jellyfin clients to access resources without transmitting credentials. TokenValidator ensures tokens contain only safe alphanumeric characters.

## Domain Context

- **Use Case:** Secure stream/image URL authentication without exposing passwords
- **Module Type:** Service
- **Key Domain Entities:** Auth Token, TokenValidator

## Internal Dependencies

- **`Helper`** — ApiClient for TVHeadend user API calls, IdNodeValueHelper for extracting token values

