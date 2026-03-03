FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["src/McpServer/McpServer.csproj", "McpServer/"]
RUN dotnet restore "McpServer/McpServer.csproj"
COPY src/McpServer/ McpServer/
WORKDIR "/src/McpServer"
RUN dotnet build "McpServer.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "McpServer.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
# Copy sample schemas; production schemas mounted as volume
COPY src/McpServer/schemas/ ./schemas/
VOLUME /app/schemas
ENTRYPOINT ["dotnet", "McpServer.dll"]
