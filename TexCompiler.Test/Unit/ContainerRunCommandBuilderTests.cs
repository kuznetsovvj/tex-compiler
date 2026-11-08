using TexCompiler.Services;

namespace TexCompiler.UnitTests.Services
{
    /// <summary>
    /// Контракт изоляции одноразовых контейнеров компиляции. Регрессия здесь
    /// критичнее удобства: флаг, выпавший из списка, молча ослабляет изоляцию.
    /// </summary>
    public class ContainerRunCommandBuilderTests
    {
        private static List<string> Build(
            string workspace = "/tasks/tex_1",
            string subdirectory = ".",
            string command = "pdflatex",
            IReadOnlyList<string>? commandArguments = null)
        {
            return ContainerRunCommandBuilder.Build(
                image: "texcompiler-base:latest",
                taskContainerName: "tex-task-abc",
                workspaceHostPath: workspace,
                workingSubdirectory: subdirectory,
                memoryMegabytes: "1024",
                cpuCount: "1",
                pidsLimit: "256",
                tmpSizeMegabytes: "64",
                command: command,
                commandArguments: commandArguments ?? new List<string> { "-interaction=nonstopmode", "-no-shell-escape", "main.tex" });
        }

        [Fact]
        public void Build_IsolationFlags_AreAllPresent()
        {
            var args = Build();
            var line = string.Join(" ", args);

            Assert.Contains("--network none", line);
            Assert.Contains("--cap-drop ALL", line);
            Assert.Contains("--security-opt no-new-privileges", line);
            Assert.Contains("--read-only", line);
            Assert.Contains("--tmpfs /tmp:rw,exec,size=64m", line);
            Assert.Contains("--user 65534:65534", line);
            Assert.Contains("--memory 1024m", line);
            Assert.Contains("--cpus 1", line);
            Assert.Contains("--pids-limit 256", line);

            // Переменные окружения уводят кэши TeX Live и fontconfig в tmpfs:
            // rootfs контейнера только на чтение
            Assert.Contains("--env HOME=/tmp", line);
            Assert.Contains("--env TEXMFVAR=/tmp/texmf-var", line);
            Assert.Contains("--env TEXMFCONFIG=/tmp/texmf-config", line);
        }

        [Fact]
        public void Build_DockerSocketAndAppVolumes_AreNeverMounted()
        {
            var line = string.Join(" ", Build());

            Assert.DoesNotContain("docker.sock", line);
            Assert.DoesNotContain("/var/run", line);
            // Общие тома приложения контейнеру компиляции недоступны
            Assert.DoesNotContain("storage", line);
            Assert.DoesNotContain("artifacts", line);
        }

        [Fact]
        public void Build_Workspace_IsTheOnlyWritableMount()
        {
            var args = Build(workspace: "/tasks/tex_42");

            Assert.Single(args.Where(a => a == "--volume"));
            Assert.Contains("--volume /tasks/tex_42:/work", string.Join(" ", args));
        }

        [Fact]
        public void Build_Container_IsDisposableAndNamed()
        {
            var line = string.Join(" ", Build());

            Assert.Contains("--rm", line);
            Assert.Contains("--name tex-task-abc", line);
        }

        [Fact]
        public void Build_Workdir_UsesLinuxSeparatorAndStaysUnderWork()
        {
            var line = string.Join(" ", Build(subdirectory: "chapters/intro"));

            Assert.Contains("--workdir /work/chapters/intro", line);
        }

        [Fact]
        public void Build_CommandAndArguments_AreAppendedAfterImage()
        {
            var args = Build(command: "asy", commandArguments: new List<string> { "pic.asy", "other.asy" });

            var imageIndex = args.IndexOf("texcompiler-base:latest");
            var commandIndex = args.IndexOf("asy");

            Assert.True(imageIndex > 0, "образ должен присутствовать в аргументах");
            Assert.True(commandIndex > imageIndex, "команда идет после образа");
            Assert.Equal(new[] { "asy", "pic.asy", "other.asy" }, args.Skip(imageIndex + 1).ToArray());
        }

        [Fact]
        public void Build_WorkspaceWithSpaces_GoesAsSingleArgument()
        {
            var args = Build(workspace: "/tasks/my dir/tex_1");

            // ArgumentList-совместимость: путь с пробелом - один элемент,
            // docker получит его как отдельный аргумент
            Assert.Contains("/tasks/my dir/tex_1:/work", args);
        }
    }
}
