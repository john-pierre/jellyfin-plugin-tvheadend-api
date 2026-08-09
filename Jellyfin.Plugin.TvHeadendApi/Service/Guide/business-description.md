# Guide Service

Provides the electronic program guide (EPG) and channel listing for Jellyfin's Live TV interface by querying TVHeadend's channel and EPG grid APIs.

## Detailed Description

The Guide Service fetches all available TV channels, their associated EPG programs, content type classifications, and channel tag groupings from TVHeadend. It transforms TVHeadend's native data structures into Jellyfin's `ChannelInfo` and `ProgramInfo` models, handling genre mapping, image URL construction, and time zone normalization.

## Domain Context

- **Use Case:** Channel browsing and EPG grid display in Jellyfin clients
- **Module Type:** Service
- **Key Domain Entities:** Channel Grid, EPG Event, Content Type, Channel Tag

## Internal Dependencies

- **`Backend`** — Uses GridFetcher for paginated channel/EPG fetching, UrlBuilder for image URLs, ApiClient for HTTP calls
- **`Auth`** — Auth tokens appended to channel icon URLs

