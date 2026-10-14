# WhyLine: multi-stage build for Azure Container Apps (or any container host)
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props WhyLine.slnx nuget.config global.json ./
COPY src/ src/
RUN dotnet publish src/WhyLine.Web/WhyLine.Web.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "WhyLine.Web.dll"]
