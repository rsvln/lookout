#See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

#FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
#USER root
#WORKDIR /app
#RUN apt-get update && apt-get upgrade -y && apt-get install -y  --no-install-recommends ffmpeg curl wget && rm -rf /var/lib/apt/lists/*

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS base
USER root
WORKDIR /app
RUN apk add --no-cache ffmpeg curl wget

FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
#FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["lookout/lookout.csproj", "lookout/"]
RUN dotnet restore "./lookout/lookout.csproj"
COPY . .
WORKDIR "/src/lookout"
RUN dotnet build "./lookout.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./lookout.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "lookout.dll"]
