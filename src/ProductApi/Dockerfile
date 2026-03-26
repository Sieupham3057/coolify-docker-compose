# ── Stage 1: Build ────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy csproj trước để tận dụng layer cache
# Nếu chỉ đổi code, layer restore không chạy lại → build nhanh hơn
COPY src/ProductApi/ProductApi.csproj src/ProductApi/
RUN dotnet restore src/ProductApi/ProductApi.csproj

COPY src/ src/
WORKDIR /src/src/ProductApi
RUN dotnet publish -c Release -o /app/publish --no-restore /p:UseAppHost=false

# ── Stage 2: Runtime ──────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime

# Không chạy với root — giảm attack surface
RUN adduser --disabled-password --gecos "" appuser

WORKDIR /app
COPY --from=build /app/publish .

RUN mkdir -p logs && chown appuser:appuser logs
USER appuser

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD curl -f http://localhost:8080/health || exit 1

ENTRYPOINT ["dotnet", "ProductApi.dll"]