FROM mcr.microsoft.com/dotnet/sdk:9.0 AS builder

WORKDIR /src

COPY BBDown.Core/ BBDown.Core/
COPY BBDown/ BBDown/
COPY BBDown.sln .

RUN dotnet restore BBDown.sln
RUN dotnet publish BBDown -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0

WORKDIR /app

# install ffmpeg
RUN apt-get update && \
    apt-get install -y ffmpeg && \
    rm -rf /var/lib/apt/lists/*

COPY --from=builder /app/publish .

EXPOSE 23333

ENTRYPOINT ["dotnet", "BBDown.dll", "serve", "-l", "http://0.0.0.0:23333"]