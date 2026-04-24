FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS build
WORKDIR /src

# Restore layer — cached unless .csproj changes
COPY CoWildfireApi/CoWildfireApi.csproj CoWildfireApi/
RUN dotnet restore CoWildfireApi/CoWildfireApi.csproj

COPY CoWildfireApi/ CoWildfireApi/
RUN dotnet publish CoWildfireApi/CoWildfireApi.csproj \
    -c Release \
    -o /app/publish \
    --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0-alpine AS runtime
WORKDIR /app

COPY --from=build /app/publish .

ENV ASPNETCORE_ENVIRONMENT=Production

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=10s --start-period=45s --retries=3 \
    CMD wget -qO- http://localhost:8080/api/health || exit 1

ENTRYPOINT ["dotnet", "CoWildfireApi.dll"]
