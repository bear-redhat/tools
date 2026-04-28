FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/Investigator.Contracts/Investigator.Contracts.csproj Investigator.Contracts/
COPY src/Investigator/Investigator.csproj Investigator/
RUN dotnet restore Investigator/Investigator.csproj

COPY src/ .
RUN dotnet publish Investigator/Investigator.csproj -c Release -o /app

# Download Playwright Chromium browser binary
ENV PLAYWRIGHT_BROWSERS_PATH=/ms-playwright
RUN dotnet exec \
    --runtimeconfig /app/Investigator.runtimeconfig.json \
    --depsfile /app/Investigator.deps.json \
    /app/Microsoft.Playwright.dll install chromium

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app

ENV PLAYWRIGHT_BROWSERS_PATH=/ms-playwright

RUN apt-get update && apt-get install -y --no-install-recommends \
    python3 python3-pip python3-yaml \
    jq curl gawk less file vim-tiny \
    dnsutils iputils-ping net-tools \
    openssl ca-certificates git \
    procps xz-utils unzip \
    libasound2t64 \
    libatk-bridge2.0-0t64 \
    libatk1.0-0t64 \
    libatspi2.0-0t64 \
    libcairo2 \
    libcups2t64 \
    libdbus-1-3 \
    libdrm2 \
    libgbm1 \
    libglib2.0-0t64 \
    libnspr4 \
    libnss3 \
    libpango-1.0-0 \
    libx11-6 \
    libxcb1 \
    libxcomposite1 \
    libxdamage1 \
    libxext6 \
    libxfixes3 \
    libxkbcommon0 \
    libxrandr2 \
    fonts-liberation \
    fonts-noto-color-emoji \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /ms-playwright /ms-playwright

# Install oc CLI
RUN curl -sL https://mirror.openshift.com/pub/openshift-v4/clients/ocp/stable/openshift-client-linux.tar.gz \
    | tar xz -C /usr/local/bin oc kubectl

# Install AWS CLI v2
RUN curl -sL "https://awscli.amazonaws.com/awscli-exe-linux-x86_64.zip" \
    -o /tmp/awscliv2.zip \
    && unzip -q /tmp/awscliv2.zip -d /tmp \
    && /tmp/aws/install \
    && rm -rf /tmp/aws /tmp/awscliv2.zip

# Install yq
RUN curl -sL https://github.com/mikefarah/yq/releases/latest/download/yq_linux_amd64 \
    -o /usr/local/bin/yq && chmod +x /usr/local/bin/yq

# Install Google Cloud CLI (gsutil, gcloud storage)
RUN curl -sL https://dl.google.com/dl/cloudsdk/channels/rapid/downloads/google-cloud-cli-linux-x86_64.tar.gz \
    | tar xz -C /opt \
    && /opt/google-cloud-sdk/install.sh --quiet --path-update true \
    && ln -s /opt/google-cloud-sdk/bin/gcloud /usr/local/bin/gcloud \
    && ln -s /opt/google-cloud-sdk/bin/gsutil /usr/local/bin/gsutil

# Install useful Python packages
RUN pip3 install --no-cache-dir --break-system-packages requests tabulate

# Create agent user for shell execution (non-root, no secret access)
RUN groupadd -g 1001 investigator \
    && useradd -u 1001 -g investigator -m agent

# Create data directories
RUN mkdir -p /data/investigator/workspaces /data/release-repo /data/ci-tools-repo /secrets \
    && chown root:investigator /data/investigator/workspaces \
    && chmod 2775 /data/investigator/workspaces \
    && chmod 0600 /secrets

COPY --from=build /app .

# Playwright Node driver needs execute permission for arbitrary UIDs (OpenShift restricted SCC)
RUN chmod -R o+rx /app/.playwright/

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

ENTRYPOINT ["dotnet", "Investigator.dll"]
