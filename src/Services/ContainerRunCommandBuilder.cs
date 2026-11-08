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
        /// <summary>
        /// UID/GID процесса внутри task-контейнера. Совпадает с UID приложения на хосте:
        /// workspace задачи на bind-монте принадлежит uid 1000, и процесс компиляции
        /// обязан иметь право записи в него. nobody (65534) не имеет - pdflatex не
        /// смог бы создать ни .log, ни .pdf. На изоляцию uid не влияет: контейнер
        /// всё равно без сети, capabilities и записываемого rootfs.
        /// </summary>
        internal const string ContainerUser = "1000:1000";

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
