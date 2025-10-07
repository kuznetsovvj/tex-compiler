FROM texcompiler-base:latest

# Установка .NET SDK для сборки
RUN apt-get update && \
    apt-get install -y wget && \
    wget https://packages.microsoft.com/config/ubuntu/22.04/packages-microsoft-prod.deb -O packages-microsoft-prod.deb && \
    dpkg -i packages-microsoft-prod.deb && \
    apt-get update && \
    apt-get install -y dotnet-sdk-8.0

WORKDIR /src
COPY . .

RUN dotnet publish src/TexCompiler.csproj -c Release -o /app

WORKDIR /app
RUN mkdir -p wwwroot/pdfs wwwroot/logs storage 

ENV ASPNETCORE_URLS=http://0.0.0.0:5000

EXPOSE 5000
ENTRYPOINT ["dotnet", "TexCompiler.dll"]