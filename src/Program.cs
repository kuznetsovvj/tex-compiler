using Microsoft.AspNetCore.Mvc;
using TexCompiler.Filters;
using TexCompiler.Models;
using TexCompiler.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();

builder.Services.AddScoped<UploadSizeLimitFilter>();

// [ApiController] отклоняет запрос с невалидной моделью сам, до входа в метод действия, и
// по умолчанию отвечает документом ProblemDetails. Интерфейс такой ответ прочитать не умеет:
// он ждёт { success, error } и на всё остальное показывает "HTTP error! status: 400".
// Поэтому ответ приводится к общей форме — иначе серверная проверка расширения дала бы
// технический текст вместо объяснения.
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
                : "Запрос не прошёл проверку"
        });
    };
});

builder.Services.AddSingleton<ITaskStorageService, TaskStorageService>();
builder.Services.AddSingleton<CompilationManagerService>();
builder.Services.AddSingleton<ICompilationService, CompilationService>();
builder.Services.AddHostedService<CleanupService>();

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.All;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
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

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.MapControllers(); 

app.Run();