# Build
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY TwilioCaller.Api/TwilioCaller.Api.csproj TwilioCaller.Api/
RUN dotnet restore TwilioCaller.Api/TwilioCaller.Api.csproj

COPY TwilioCaller.Api/ TwilioCaller.Api/
RUN dotnet publish TwilioCaller.Api/TwilioCaller.Api.csproj -c Release -o /app

# Run
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .

# The SQLite file holds the encrypted Twilio credentials, so it must outlive the
# container. Mount a volume here or every restart forces a fresh sign-in.
RUN mkdir -p /data
ENV ConnectionStrings__Default="Data Source=/data/twilio-caller.db"
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "TwilioCaller.Api.dll"]
