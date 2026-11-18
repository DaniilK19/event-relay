# syntax=docker/dockerfile:1
#
# One Dockerfile for every .NET service in the repo; docker-compose.yml picks the project:
#   docker build --build-arg PROJECT=src/EventRelay.Api/EventRelay.Api.csproj -t eventrelay-api .

ARG DOTNET_VERSION=10.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
ARG PROJECT
WORKDIR /src

# Restore first, from project files only, so the layer is cached until a dependency changes.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/EventRelay.Core/EventRelay.Core.csproj src/EventRelay.Core/
COPY src/EventRelay.Infrastructure/EventRelay.Infrastructure.csproj src/EventRelay.Infrastructure/
COPY src/EventRelay.Api/EventRelay.Api.csproj src/EventRelay.Api/
COPY src/EventRelay.Dispatcher/EventRelay.Dispatcher.csproj src/EventRelay.Dispatcher/
COPY samples/FlightStatus.Simulator/FlightStatus.Simulator.csproj samples/FlightStatus.Simulator/
COPY samples/WebhookReceiver/WebhookReceiver.csproj samples/WebhookReceiver/
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages,sharing=locked \
    dotnet restore "$PROJECT"

COPY src/ src/
COPY samples/ samples/
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages,sharing=locked \
    dotnet publish "$PROJECT" -c Release -o /app --no-restore -p:UseAppHost=false \
 && basename "$PROJECT" .csproj > /app/.entrypoint

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS final
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_NOLOGO=1
EXPOSE 8080
# Built-in non-root user of the official .NET images.
USER $APP_UID
ENTRYPOINT ["/bin/sh", "-c", "exec dotnet \"$(cat /app/.entrypoint).dll\" \"$@\"", "--"]
