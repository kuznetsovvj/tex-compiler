namespace TexCompiler.Services
{
    /// <summary>
    /// Собирает аргументы docker run для одноразового контейнера компиляции.
    /// Отдельный тип намеренно: флаги изоляции покрываются unit-тестами, и здесь
    /// физически нет кода, который монтировал бы docker socket или общие тома
    /// приложения в контейнер компиляции.
    ///
    /// Контракт изоляции (менять только осознанно, с правкой тестов):
    /// --network none, --cap-drop ALL, no-new-privileges, read-only rootfs,
    /// отдельный ограниченный tmpfs, непривилегированный UID, лимиты памяти/CPU/PID,
    /// в качестве записываемого каталога - только bind-монтированный workspace задачи.
    /// </summary>
    internal static class ContainerRunCommandBuilder
    {
        /// <summary>UID/GID nobody: компиляция не от root даже внутри task-контейнера</summary>
        internal const string ContainerUser = "65534:65534";

        public static List<string> Build(
            string image,
            string taskContainerName,
            string workspaceHostPath,
            string workingSubdirectory,
            string memoryMegabytes,
            string cpuCount,
            string pidsLimit,
            string tmpSizeMegabytes,
            string command,
            IReadOnlyList<string> commandArguments)
        {
            var args = new List<string>
            {
                "run",
                "--rm",
                "--name", taskContainerName,
                "--network", "none",
                "--cap-drop", "ALL",
                "--security-opt", "no-new-privileges",
                "--read-only",
                "--tmpfs", $"/tmp:rw,exec,size={tmpSizeMegabytes}m",
                "--user", ContainerUser,
                "--memory", $"{memoryMegabytes}m",
                "--cpus", cpuCount,
                "--pids-limit", pidsLimit,
                // HOME и переменные TeX Live - в tmpfs, rootfs read-only
                "--env", "HOME=/tmp",
                "--env", "TEXMFVAR=/tmp/texmf-var",
                "--env", "TEXMFCONFIG=/tmp/texmf-config",
                "--env", "XDG_CACHE_HOME=/tmp/cache",
                // Записываемый каталог ровно один - workspace задачи
                "--volume", $"{workspaceHostPath}:/work",
                "--workdir", "/work/" + workingSubdirectory.TrimStart('/'),
                image,
                command
            };

            args.AddRange(commandArguments);
            return args;
        }
    }
}
