FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore ProjectOpsExcelImporter.sln \
 && dotnet publish src/ProjectOpsExcelImporter/ProjectOpsExcelImporter.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "ProjectOpsExcelImporter.dll"]
