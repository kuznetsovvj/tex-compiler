using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Filters;
using TexCompiler.Services;

namespace TexCompiler.Filters
{
    /// <summary>
    /// Делает то же, что штатный [RequestSizeLimit], но берет значение из конфигурации
    /// 
    /// Атрибут [RequestSizeLimit] принимает только константу времени компиляции, а лимит
    /// должен меняться без пересборки образа. Ограничение выставляется здесь, а не глобально
    /// в Program.cs, чтобы оно относилось к тому единственному endpoint, у которого есть
    /// тело запроса: глобальная настройка молча распространилась бы на любой будущий метод.
    /// 
    /// Фильтр авторизации выбран потому, что он выполняется до привязки модели - то есть до того,
    /// как ASP.NET начнет вычитывать тело и складывать файл во временный каталог.
    /// </summary>
    public class UploadSizeLimitFilter : IAuthorizationFilter
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
                _logger.LogWarning("Request body size limit is not supported by the server");
                return;
            }

            if (sizeFeature.IsReadOnly)
            {
                _logger.LogWarning($"Request body size limit is already locked, upload limit not applied");
                return;
            }

            sizeFeature.MaxRequestBodySize = UploadLimits.GetMaxRequestBodySizeBytes(_configuration);
        }
    }
}
