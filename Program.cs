using ReportsAutomationApp.Components;
using ReportsAutomationApp.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddHttpClient();
builder.Services.AddScoped<IExtractionStep, DaxExtractorService>();
builder.Services.AddScoped<IExtractionStep, SemanticMapperService>();
builder.Services.AddScoped<IExtractionStep, SqlGeneratorService>();
builder.Services.AddScoped<IExtractionStep, SnowflakeCortexService>(); 
builder.Services.AddScoped<ExtractionOrchestrator>();

var app = builder.Build();

// Ensure runtime-generated exports directory exists.
Directory.CreateDirectory(Path.Combine(app.Environment.WebRootPath, "Exports"));

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
// Required for runtime-generated files (e.g., /wwwroot/Exports/*.csv) after publish.
app.UseStaticFiles();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
