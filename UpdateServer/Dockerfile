#See https://aka.ms/containerfastmode to understand how Visual Studio uses this Dockerfile to build your images for faster debugging.

FROM mcr.microsoft.com/dotnet/aspnet:6.0-alpine3.15 AS base

WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://*:8080

FROM mcr.microsoft.com/dotnet/sdk:5.0 AS build
WORKDIR /src
COPY ["UpdateServer.csproj", "."]
RUN dotnet restore "./UpdateServer.csproj"
COPY . .
WORKDIR "/src/."
RUN dotnet build "UpdateServer.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "UpdateServer.csproj" -c Release -o /app/publish -r alpine-x64 --no-self-contained

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "UpdateServer.dll"]