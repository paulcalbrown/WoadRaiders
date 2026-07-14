# The dedicated server, containerized ready to deploy. tools/release.ps1 builds
# this with the staged linux-x64 self-contained publish as the (tiny) build
# context, so the image carries the exact bytes shipped in
# WoadRaiders-Server-linux-x64.zip. Works with docker and podman alike.
#
#   docker run -d -p 9050:9050/udp ghcr.io/paulcalbrown/woadraiders-server:v13
#
# (or `docker load` the WoadRaiders-Server-image.tar.gz release asset first,
# for deployments that don't pull from a registry).
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0

WORKDIR /app
# The context is staged on Windows, where there is no execute bit — set it here.
COPY --chmod=755 WoadRaiders.Server /app/WoadRaiders.Server
COPY maps/ /app/maps/

# LiteNetLib reliable UDP; the game's only port.
EXPOSE 9050/udp

# The non-root user the .NET base images provide.
USER $APP_UID
ENTRYPOINT ["/app/WoadRaiders.Server"]
