FROM mcr.microsoft.com/dotnet/core/sdk:3.1-bionic AS build-env
WORKDIR /build

# copy everything and build the project
COPY . ./
RUN dotnet restore TS3AudioBot/*.csproj
RUN dotnet publish TS3AudioBot/*.csproj -c Release -f netcoreapp3.1 -o out

# build runtime image
FROM mcr.microsoft.com/dotnet/core/runtime:3.1
WORKDIR /app
COPY --from=build-env /build/out ./
COPY WebInterface ../WebInterface/
RUN apt-get update && apt-get install -y \
      openssl \
      libopus-dev \
      opus-tools \
      ffmpeg \
      zip 

ARG TS3_AUDIOBOT_INCLUDE_YOUTUBE_DL="true"


RUN bash -c 'if [ "xy$TS3_AUDIOBOT_INCLUDE_YOUTUBE_DL" == "xytrue" ] ; then \
        apt-get update && apt-get install -y python3 \
        && update-alternatives --install /usr/bin/python python /usr/bin/python3 99 \
        && curl -L https://yt-dl.org/downloads/latest/youtube-dl -o /usr/local/bin/youtube-dl && chmod a+rx /usr/local/bin/youtube-dl ; \
    else \
        echo "skipping setup for youtube-dl"; \
    fi'


ENTRYPOINT ["dotnet", "TS3AudioBot.dll", "--non-interactive", "--config", "config/ts3audiobot.toml"]
