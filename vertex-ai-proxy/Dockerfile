FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
ARG TARGETARCH

RUN apt-get update && apt-get install -y --no-install-recommends \
        clang zlib1g-dev \
    && rm -rf /var/lib/apt/lists/*

RUN if [ "$TARGETARCH" = "arm64" ] && [ "$(dpkg --print-architecture)" != "arm64" ]; then \
        dpkg --add-architecture arm64 && \
        apt-get update && apt-get install -y --no-install-recommends \
            gcc-aarch64-linux-gnu zlib1g-dev:arm64 && \
        rm -rf /var/lib/apt/lists/*; \
    fi

WORKDIR /src
COPY vertex-ai-proxy.csproj .
RUN dotnet restore -r linux-${TARGETARCH/amd/x}

COPY . .
RUN dotnet publish -c Release \
    -r linux-${TARGETARCH/amd/x} \
    --no-restore \
    -o /app

# --- runtime ---
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled

EXPOSE 5000
ENV ASPNETCORE_URLS=http://+:5000
ENV DOTNET_RUNNING_IN_CONTAINER=true

WORKDIR /app
COPY --from=build /app .

USER $APP_UID
ENTRYPOINT ["./vertex-ai-proxy"]
