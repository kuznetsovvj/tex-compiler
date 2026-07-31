using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Filters;
using TexCompiler.Services;

namespace TexCompiler.Filters
{
    /// <summary>
    /// Делает то же, что штатный [RequestSizeLimit], но берёт значение из конфигурации.
    ///
    /// Атрибут [RequestSizeLimit] принимает только константу времени компиляции, а лимит
    /// должен меняться без пересборки образа. Ограничение выставляется здесь, а не глобально
    /// в Program.cs, чтобы оно относилось к тому единственному эндпоинту, у которого есть
    /// тело запроса: глобальная настройка молча распространилась бы на любой будущий метод.
    ///
    /// Фильтр авторизации выбран потому, что он выполняется до привязки модели — то есть до
    /// того, как ASP.NET начнёт вычитывать тело и складывать файл во временный каталог.
    /// </summary>
    public sealed class UploadSizeLimitFilter : IAuthorizationFilter
    {
        private readonly IConfiguration _configuration;
        private readonly ILogger<UploadSizeLimitFilter> _logger;

        public UploadSizeLimitFilter(IConfiguration configuration, ILogger<UploadSizeLimitFilter> logger)
        {
            _configuration = configuration;
            _logger = logger;
        }

        public void OnAuthorization(AuthorizationFilterContext context)
        {
            var sizeFeature = context.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();

            if (sizeFeature == null)
            {
                // Сервер не поддерживает ограничение на запрос — остаётся только проверка
                // в контроллере, поздняя, но работающая.
                _logger.LogWarning("Request body size limit is not supported by the server");
                return;
            }

            if (sizeFeature.IsReadOnly)
            {
                // Чтение тела уже началось, менять лимит поздно.
                _logger.LogWarning("Request body size limit is already locked, upload limit not applied");
                return;
            }

            sizeFeature.MaxRequestBodySize = UploadLimits.GetMaxRequestBodySizeBytes(_configuration);
        }
    }
}
