# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY AutoTestAi.sln ./
COPY src/AutoTestAi.Domain/AutoTestAi.Domain.csproj src/AutoTestAi.Domain/
COPY src/AutoTestAi.Application/AutoTestAi.Application.csproj src/AutoTestAi.Application/
COPY src/AutoTestAi.Infrastructure/AutoTestAi.Infrastructure.csproj src/AutoTestAi.Infrastructure/
COPY src/AutoTestAi.Workflows/AutoTestAi.Workflows.csproj src/AutoTestAi.Workflows/
COPY src/AutoTestAi.Api/AutoTestAi.Api.csproj src/AutoTestAi.Api/
RUN dotnet restore src/AutoTestAi.Api/AutoTestAi.Api.csproj
COPY src ./src
RUN dotnet publish src/AutoTestAi.Api/AutoTestAi.Api.csproj -c Release -o /app/publish --no-restore

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
RUN apt-get update && apt-get install -y --no-install-recommends curl \
  && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/publish ./
USER app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s \
  CMD curl -f http://localhost:8080/health/live || exit 1
ENTRYPOINT ["dotnet", "AutoTestAi.Api.dll"]
