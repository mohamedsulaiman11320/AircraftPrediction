# syntax=docker/dockerfile:1
# ASP.NET Core API (AFP.Api). Configuration comes from environment variables (.env.prod via docker-compose.prod.yml);
# no .env file is baked into the image.

# ---- Stage 1: restore and publish ----
FROM mcr.microsoft.com/dotnet/sdk:10.0-preview AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/AFP.Domain/AFP.Domain.csproj src/AFP.Domain/
COPY src/AFP.Application/AFP.Application.csproj src/AFP.Application/
COPY src/AFP.Infrastructure/AFP.Infrastructure.csproj src/AFP.Infrastructure/
COPY src/AFP.Api/AFP.Api.csproj src/AFP.Api/
RUN dotnet restore src/AFP.Api/AFP.Api.csproj
COPY src/ src/
RUN dotnet publish src/AFP.Api/AFP.Api.csproj -c Release -o /app/publish --no-restore

# ---- Stage 2: runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0-preview
# Npgsql probes the Kerberos/GSSAPI library at connect; without it every start logs a (harmless) load error.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_HTTP_PORTS=5080
EXPOSE 5080
ENTRYPOINT ["dotnet", "AFP.Api.dll"]
