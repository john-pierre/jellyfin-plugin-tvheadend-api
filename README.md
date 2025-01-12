# Jellyfin Plugin: TVHeadend API Integration

[![Release Version](https://img.shields.io/github/v/release/john-pierre/jellyfin-plugin-tvheadend-api)](https://github.com/john-pierre/jellyfin-plugin-tvheadend-api/releases)
[![Minimum Jellyfin Version](https://img.shields.io/badge/Jellyfin-10.10.3%2B-blue)](https://jellyfin.org)
[![Minimum TVHeadend Version](https://img.shields.io/badge/TVHeadend-4.3%2B-green)](https://tvheadend.org)
[![License](https://img.shields.io/github/license/john-pierre/jellyfin-plugin-tvheadend-api)](LICENSE)
[![Issues](https://img.shields.io/github/issues/john-pierre/jellyfin-plugin-tvheadend-api)](https://github.com/john-pierre/jellyfin-plugin-tvheadend-api/issues)
[![Target Framework](https://img.shields.io/badge/.NET-8.0-purple)](https://dotnet.microsoft.com/en-us/)
[![Conventional Commits](https://img.shields.io/badge/Conventional%20Commits-1.0.0-yellow.svg)](https://conventionalcommits.org)

This repository contains the `jellyfin-plugin-tvheadend-api`, a Jellyfin plugin that integrates with the TVHeadend API to provide seamless live TV, EPG (Electronic Program Guide), and recording functionality. The plugin is built to leverage the powerful TVHeadend backend while ensuring a smooth experience within Jellyfin.

**Note**: This plugin exclusively uses the TVHeadend API and does not support the custom HTSP protocol.

## Table of Contents

- [Features](#features)
- [Requirements](#requirements)
- [Installation](#installation)
- [Usage](#usage)
- [Configuration](#configuration)
    - [Connection Settings](#connection-settings)
    - [Authentication](#authentication)
    - [Streaming Settings](#streaming-settings)
    - [Recording Settings](#recording-settings)
- [Developer Guide](#developer-guide)
    - [Running with Docker](#running-with-docker)
- [License](#license)
- [Contributing](#contributing)
- [Acknowledgments](#acknowledgments)
- [Contact](#contact)

## Features

- **Live TV Support**: Fetch and stream live TV channels from TVHeadend.
- **EPG Integration**: Display an electronic program guide with detailed program metadata.
- **Recording Management**: Schedule, update, and manage recordings directly from Jellyfin.
- **Automatic Series Timers**: Create automatic recording rules for series.
- **Content Filtering**: Leverage TVHeadend content types and tags for advanced filtering.

## Requirements

- **Jellyfin 10.10 or later**
- **TVHeadend 4.3 or later**
- A configured TVHeadend server with accessible API endpoints.

## Installation

1. Add the following repository to your Jellyfin plugin settings:
   ```
   https://raw.githubusercontent.com/john-pierre/jellyfin-plugin-tvheadend-api/refs/heads/main/manifest.json
   ```
2. Install the `TVHeadend API` plugin from the repository.
3. Set up your TVHeadend server, ensuring the API is enabled and accessible.
4. Configure the plugin via the Jellyfin admin interface:
    - Navigate to `Dashboard > Plugins > TVHeadend API`.
    - Provide your TVHeadend server details (host, port, credentials, etc.).
    - Click `Save` and ensure the status reports `OK`.

## Usage

1. Ensure your TVHeadend instance is set up and running.
2. Fill in the URL to your TVHeadend instance in the plugin settings.
3. Provide additional configuration, such as:
    - **Username** and **Password** for TVHeadend API access.
    - **Auth Token** for accessing images (not streams).
4. Click `Save` and confirm that the plugin status reports `OK`.
5. Navigate to the Jellyfin live TV section or EPG to access your channels and recordings.

**Note**: All configuration changes are applied at runtime without restarting the plugin.

## Configuration

The following settings are configurable within the plugin:

### Connection Settings
- **Host**: TVHeadend server hostname or IP address.
- **Port**: TVHeadend API port.
- **Use SSL**: Enable for HTTPS connections.
- **Ignore Certificate Errors**: Useful for self-signed certificates.
- **Webroot**: Specify the webroot path.

### Authentication
- **Allow Anonymous Access**: If enabled, no authentication is required.
- **Username**: TVHeadend username.
- **Password**: TVHeadend password.
- **Auth Token**: Required only for accessing channel images.

**Note**:
- API requests use Basic Authentication via HTTP headers.
- Streams use Basic Authentication embedded in the URL.

### Streaming Settings
- **Streaming Profile**: Profile for streaming.
- **BufferMs**: Buffer size in milliseconds.
- **FallbackMaxStreamingBitrate**: Maximum streaming bitrate in kbps.
- **AnalyzeDurationMs**: Stream analysis duration in milliseconds.
- **Supports Transcoding**: Enable transcoding support.
- **Supports Probing**: Enable stream probing.
- **Supports Direct Play**: Enable direct playback.
- **Supports Direct Stream**: Enable direct streaming.
- **Is Infinite Stream**: Treat stream as infinite.
- **Ignore DTS**: Ignore DTS timestamps.

### Recording Settings
- **Priority**: Priority for recordings.
- **PrePaddingSeconds**: Pre-padding time in seconds.
- **PostPaddingSeconds**: Post-padding time in seconds.
- **Recording Profile**: Profile for recordings.

## Developer Guide

### Running with Docker

To simplify the development process, you can use Docker to set up Jellyfin with the TVHeadend plugin.

1. Clone the repository:
   ```bash
   git clone https://github.com/john-pierre/jellyfin-plugin-tvheadend-api.git
   cd jellyfin-plugin-tvheadend-api
   ```

2. Use the provided `docker-compose.yml` file to build and start the environment:
   ```bash
   docker-compose -p jellyfin-plugin-tvheadend-api up --always-recreate-deps --renew-anon-volumes --force-recreate -d --build
   ```

3. Access Jellyfin at `http://localhost:8096`.

## Logs and Security

- Logs automatically filter sensitive information, such as passwords and authentication tokens, to ensure data security.
- Ensure debug logs do not expose unnecessary details in production environments.

## License

This project is licensed under the GNU General Public License v3.0 (GPL-3.0). See the [LICENSE](LICENSE) file for details.

## Contributing

Contributions are welcome! Please submit issues, feature requests, or pull requests to improve the plugin.

## Acknowledgments

- [Jellyfin](https://jellyfin.org): The open-source media server.
- [TVHeadend](https://tvheadend.org): A powerful TV streaming backend.

---

### Contact
For questions or support, please open an issue or contact the repository maintainers.
