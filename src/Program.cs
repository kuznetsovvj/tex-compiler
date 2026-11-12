using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using System.Text.Json;
using System.Threading.RateLimiting;
using TexCompiler.Filters;
using TexCompiler.Models;
using TexCompiler.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();
builder.Services.AddScoped<UploadSizeLimitFilter>();
builder.Services.Configure<ApiBehaviorOptions>(options =>
{
    options.InvalidModelStateResponseFactory = context =>
    {
        var errors = context.ModelState.Values
            .SelectMany(state => state.Errors)
            .Select(error => error.ErrorMessage)
            .Where(message => !string.IsNullOrWhiteSpace(message))
            .ToList();

        return new BadRequestObjectResult(new ApiResponse<object?>
        {
            Success = false,
            Error = errors.Count > 0
                ? $"Ошибка валидации: {string.Join(", ", errors)}"
                : "Запрос не прошел проверку"
        });
    };
});


builder.Services.AddSingleton<ITaskStorageService, TaskStorageService>();
builder.Services.AddSingleton<CompilationQueue>();
builder.Services.AddSingleton<CompilationManagerService>();
builder.Services.AddSingleton<ICompilationService, CompilationService>();
builder.Services.AddHostedService<CompilationWorker>();
builder.Services.AddHostedService<CleanupService>();

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.All;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

// Rate limiting на загрузку: анонимный endpoint без квот превращался в канал
// бесконечного заполнения storage одним клиентом. Фиксированное окно по IP;
// RemoteIpAddress корректен, пока forwarded-заголовки обрабатываются (см. выше).
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.HttpContext.Response.ContentType = "application/json";

        var response = JsonSerializer.Serialize(new ApiResponse<object?>
        {
            Success = false,
            Error = "Слишком много загрузок за короткое время. Подождите несколько минут."
        });

        await context.HttpContext.Response.WriteAsync(response, cancellationToken);
    };

    options.AddPolicy("upload", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = builder.Configuration.GetValue("RateLimiting:UploadPermitLimit", 20),
                Window = TimeSpan.FromMinutes(
                    builder.Configuration.GetValue("RateLimiting:UploadWindowMinutes", 5)),
                // Без внутренней очереди: при переполнении - сразу отказ
                QueueLimit = 0
            }));
});

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();
builder.Logging.SetMinimumLevel(LogLevel.Information);

var app = builder.Build();

app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

// После UseRouting: политики читаются из метаданных endpoint-а
app.UseRateLimiter();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.MapControllers(); 

app.Run();