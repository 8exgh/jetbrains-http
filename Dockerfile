# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

WORKDIR /src

# Restore first so dependency layers cache independently of source changes.
# global.json is intentionally not copied: it pins a feature band the SDK image may not carry.
COPY src/JetBrainsHttpDemo.Api/JetBrainsHttpDemo.Api.csproj src/JetBrainsHttpDemo.Api/
RUN dotnet restore src/JetBrainsHttpDemo.Api/JetBrainsHttpDemo.Api.csproj

COPY src/ src/
RUN dotnet publish src/JetBrainsHttpDemo.Api/JetBrainsHttpDemo.Api.csproj -c Release -o /app/publish

# Stage 2: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0

WORKDIR /app

COPY --from=build /app/publish ./

# SQLite database lives here; mounted as a volume in deployment
RUN mkdir -p /app/data

ENV ASPNETCORE_URLS=http://+:8080
ENV ConnectionStrings__Database="Data Source=/app/data/jetbrains-http-demo.db"

EXPOSE 8080

ENTRYPOINT ["dotnet", "JetBrainsHttpDemo.Api.dll"]
