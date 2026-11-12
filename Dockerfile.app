FROM texcompiler-base:latest

# Базовый образ - это TeX Live поверх Ubuntu 22.04, .NET в нем нет. Здесь ставится SDK,
# которым ниже публикуется приложение. Пакет packages-microsoft-prod.deb подключает
# репозиторий Microsoft: dotnet-sdk-10.0 в стандартных репозиториях Ubuntu 22.04 отсутствует.
# Целевая версия - .NET 10 LTS: поддержка net8 закончилась в ноябре 2026.
RUN apt-get update && \
    apt-get install -y wget ca-certificates curl gnupg && \
    wget https://packages.microsoft.com/config/ubuntu/22.04/packages-microsoft-prod.deb -O packages-microsoft-prod.deb && \
    dpkg -i packages-microsoft-prod.deb && \
    rm -f packages-microsoft-prod.db && \
    apt-get update && \
    apt-get install -y dotnet-sdk-10.0 && \
    rm -rf /var/lib/apt/lists/*

# Клиент docker: приложение запускает компиляцию в одноразовых контейнерах через
# смонтированный /var/run/docker.sock. Нужен только клиент, демон в образе не нужен.
# Репозиторий официальный - см. https://docs.docker.com/engine/install/ubuntu
RUN install -m 0755 -d /etc/apt/keyrings && \
    curl -fsSL https://download.docker.com/linux/ubuntu/gpg | gpg --dearmor -o /etc/apt/keyrings/docker.gpg && \
    chmod a+r /etc/apt/keyrings/docker.gpg && \
    echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.gpg] https://download.docker.com/linux/ubuntu jammy stable" > /etc/apt/sources.list.d/docker.list && \
    apt-get update && \
    apt-get install -y docker-ce-cli && \
    rm -rf /var/lib/apt/lists/*

WORKDIR /src
COPY . .

RUN dotnet publish src/TexCompiler.csproj -c Release -o /app

WORKDIR /app
RUN mkdir -p artifacts/pdfs artifacts/logs storage && \
    useradd --uid 1000 --create-home app && \
    chown -R app:app /app

# Приложение не под root: ущерб от компрометации ASP.NET меньше. Компиляция
# пользовательских документов и так выполняется не здесь, а в одноразовых
# контейнерах (см. CompilationContainer), texlive в образе оставлен как запасной
# режим EnableIsolation=false.
USER app

ENV ASPNETCORE_URLS=http://0.0.0.0:5000

EXPOSE 5000
ENTRYPOINT ["dotnet", "TexCompiler.dll"]
