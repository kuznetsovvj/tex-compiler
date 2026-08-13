namespace TexCompiler.Services
{
    /// <summary>
    /// Единственное место, где вычисляются пути к каталогам с результатами компиляции.
    /// 
    /// Раньше PDF и логи лежали в wwwroot, то есть внутри корня статики: UseStaticFiles
    /// раздавал их анонимно, в обход контроллера, который проверяет существование задачи
    /// и ее статус. Имя PDF при этом складывалось из метки времени и имени файла пользователя,
    /// то есть подбиралось. Каталоги вынесены за пределы wwwroot, и единственная дорога
    /// к файлу теперь идет через ApiController
    /// </summary>
    public static class ArtifactPath
    {
        public const string RootDirectoryName = "artifacts";

        public static string GetPdfDirectory(IWebHostEnvironment environment) =>
            Path.Combine(environment.ContentRootPath, RootDirectoryName, "pdfs");

        public static string GetLogDirectory(IWebHostEnvironment environment) =>
            Path.Combine(environment.ContentRootPath, RootDirectoryName, "logs");
    }
}
