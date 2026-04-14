# ────────────────────────────────────────────────────────────────────────────
# Stage 1: Build
# ────────────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy the project file and restore dependencies first (Docker layer caching)
COPY src/FlashSaleApi/FlashSaleApi.csproj ./FlashSaleApi/
RUN dotnet restore ./FlashSaleApi/FlashSaleApi.csproj

# Copy all source code and build
COPY src/FlashSaleApi/ ./FlashSaleApi/
WORKDIR /src/FlashSaleApi
RUN dotnet publish FlashSaleApi.csproj -c Release -o /app/publish --no-restore

# ────────────────────────────────────────────────────────────────────────────
# Stage 2: Runtime (much smaller image — no SDK)
# ────────────────────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# Create log directory
RUN mkdir -p /app/logs

# Copy the published output from the build stage
COPY --from=build /app/publish .

# Expose port 8080 (ASP.NET Core default in containers)
EXPOSE 8080

# Set environment variables for container
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

ENTRYPOINT ["dotnet", "FlashSaleApi.dll"]
