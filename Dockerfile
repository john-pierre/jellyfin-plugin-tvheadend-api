# Jellyfin Base Image
FROM jellyfin/jellyfin:10.10.3

# Set timezone
ENV TZ=Europe/Berlin

# Install required dependencies and add Microsoft package repository for .NET
RUN apt-get update && apt-get install -y \
    wget \
    gnupg \
    apt-transport-https \
    software-properties-common \
    jq && \
    wget https://packages.microsoft.com/config/debian/12/packages-microsoft-prod.deb && \
    dpkg -i packages-microsoft-prod.deb && \
    apt-get update && apt-get install -y \
    dotnet-sdk-8.0 \
    unzip && \
    rm -rf /var/lib/apt/lists/*

# Create necessary directories for plugins and configuration
RUN mkdir -p /config/plugins /cache /media /build

# Set the working directory to /build for plugin compilation
WORKDIR /build

# Copy plugin source code and manifest.json
COPY ./Jellyfin.Plugin.TvHeadendApi/ /build/
COPY ./manifest.json /build/

# Set a fixed version for the development build
ENV VERSION=1.0.0.0

# Dynamically create meta.json without the 'versions' field and build the plugin
RUN TIMESTAMP=$(date -u +"%Y-%m-%dT%H:%M:%SZ") && \
    echo "Building plugin version $VERSION" && \
    mkdir -p /config/plugins/tvheadend_api_$VERSION && \
    dotnet build -c Release -o /config/plugins/tvheadend_api_$VERSION && \
    jq 'del(.versions) | . + {"timestamp": "'$TIMESTAMP'", "version": "'$VERSION'"}' manifest.json > /config/plugins/tvheadend_api_$VERSION/meta.json

# Clean up build directory
WORKDIR /
RUN rm -rf /build

# Expose Jellyfin ports
EXPOSE 8096 8920

# Start Jellyfin server
CMD ["/usr/lib/jellyfin/bin/jellyfin", "--datadir", "/config", "--cachedir", "/cache"]
