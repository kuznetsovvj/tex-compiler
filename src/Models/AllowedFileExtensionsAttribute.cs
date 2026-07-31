using System.ComponentModel.DataAnnotations;

namespace TexCompiler.Models
{
    /// <summary>
    /// Серверная проверка расширения загружаемого файла.
    ///
    /// Раньше расширение проверялось только в браузере (site.js и accept=".tex,.zip"),
    /// то есть не проверялось вовсе: HTTP-запрос формируется и без страницы. Проверка
    /// оформлена атрибутом, чтобы её подхватила уже написанная в ApiController обработка
    /// ModelState — она собирает сообщения и возвращает 400 до входа в тело метода.
    /// </summary>
    [AttributeUsage(AttributeTargets.Property)]
    public sealed class AllowedFileExtensionsAttribute : ValidationAttribute
    {
        private readonly string[] _extensions;

        public AllowedFileExtensionsAttribute(params string[] extensions)
        {
            _extensions = extensions;
        }

        public override bool IsValid(object? value)
        {
            // Отсутствие файла — забота проверки на пустоту, не этой.
            if (value is not IFormFile file)
            {
                return true;
            }

            var extension = Path.GetExtension(file.FileName);

            return _extensions.Contains(extension, StringComparer.OrdinalIgnoreCase);
        }

        public override string FormatErrorMessage(string name) =>
            $"Разрешены только файлы {string.Join(" и ", _extensions)}";
    }
}
