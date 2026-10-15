using Microsoft.AspNetCore.Http.Features;
using StudentIdPhotoApp.Components;
using StudentIdPhotoApp.Services;

var builder = WebApplication.CreateBuilder(args);

// Increase request size limit for large batch mobile photo uploads (up to 500 MB)
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = 524_288_000;
    options.ValueLengthLimit = 524_288_000;
    options.MultipartHeadersLengthLimit = 1_048_576;
});

builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 524_288_000;
});

// Configure default listening URL for standalone server/desktop deployments
if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")) && !args.Any(a => a.StartsWith("--urls", StringComparison.OrdinalIgnoreCase)))
{
    builder.WebHost.UseUrls("http://0.0.0.0:5209");
}

// Add MVC Controllers & HttpClient
builder.Services.AddControllers();
builder.Services.AddHttpClient();

// Add Blazor interactive server components
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Register AI ONNX Model Bootstrapper (Singleton & HostedService)
builder.Services.AddSingleton<OnnxModelBootstrapper>();
builder.Services.AddSingleton<IOnnxModelBootstrapper>(sp => sp.GetRequiredService<OnnxModelBootstrapper>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<OnnxModelBootstrapper>());

// Register Core Image Processing & AI Services
builder.Services.AddSingleton<IFaceCropService, FaceCropService>();
builder.Services.AddSingleton<IBackgroundRemovalService, BackgroundRemovalService>();
builder.Services.AddSingleton<IPhotoPipelineService, PhotoPipelineService>();

// Register Batch Processing Queue & Background Worker
builder.Services.AddSingleton<BatchProcessingWorker>();
builder.Services.AddSingleton<IBatchProcessingService>(sp => sp.GetRequiredService<BatchProcessingWorker>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<BatchProcessingWorker>());

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAntiforgery();

app.MapControllers();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
