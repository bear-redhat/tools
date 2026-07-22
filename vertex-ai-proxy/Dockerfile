FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
ARG TARGETARCH

RUN apt-get update && apt-get install -y --no-install-recommends \
        clang zlib1g-dev \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /src
COPY vertex-ai-proxy.csproj .

ARG DOTNET_RID=linux-x64
RUN if [ "$TARGETARCH" = "arm64" ]; then DOTNET_RID=linux-arm64; fi && \
    dotnet restore -r $DOTNET_RID

COPY . .
RUN if [ "$TARGETARCH" = "arm64" ]; then DOTNET_RID=linux-arm64; fi && \
    dotnet publish -c Release -r $DOTNET_RID --no-restore -o /app

# --- runtime ---
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled

EXPOSE 5000
ENV ASPNETCORE_URLS=http://+:5000
ENV DOTNET_RUNNING_IN_CONTAINER=true

WORKDIR /app
COPY --from=build /app .

USER $APP_UID
ENTRYPOINT ["./vertex-ai-proxy"]
