FROM texcompiler-base:latest

# Базовый образ - это TeX Live поверх Ubuntu 22.04, .NET в нем нет. Здесь ставится SDK,
# которым ниже публикуется приложение. Пакет packages-microsoft-prod.deb подключает
# репозиторий Microsoft: dotnet-sdk-8.0 в стандартных репозиториях Ubuntu 22.04 отсутствует
RUN apt-get update && \
    apt-get install -y wget && \
    wget https://packages.microsoft.com/config/ubuntu/22.04/packages-microsoft-prod.deb -O packages-microsoft-prod.deb && \
    dpkg -i packages-microsoft-prod.deb && \
    apt-get update && \
    apt-get install -y dotnet-sdk-8.0 && \
    rm -f packages-microsoft-prod.db && \
    rm -rf /var/lib/apt/lists/*

WORKDIR /src
COPY . .

RUN dotnet publish src/TexCompiler.csproj -c Release -o /app

WORKDIR /app
RUN mkdir -p artifacts/pdfs artifacts/logs storage 

ENV ASPNETCORE_URLS=http://0.0.0.0:5000

EXPOSE 5000
ENTRYPOINT ["dotnet", "TexCompiler.dll"]