using DeezSpoTag.Services.Authentication;
using DeezSpoTag.Services.Extensions;
using DeezSpoTag.Services.Download;

var builder = WebApplication.CreateBuilder(args);

// Register services for dependency injection
builder.Services.AddScoped<DeezSpoTag.Services.Authentication.IDeezerAuthenticationService, DeezSpoTag.Services.Authentication.DeezerAuthenticationService>();
builder.Services.AddSingleton<ILoginStorageService, LoginStorageService>();

// Register Deezer SDK services (DeezerApiClient removed - was just a wrapper)
builder.Services.AddScoped<DeezSpoTag.Integrations.Deezer.DeezerSessionManager>(sp =>
    new DeezSpoTag.Integrations.Deezer.DeezerSessionManager(
        sp.GetRequiredService<ILogger<DeezSpoTag.Integrations.Deezer.DeezerSessionManager>>(),
        () => sp.GetRequiredService<DeezSpoTag.Services.Settings.DeezSpoTagSettingsService>().LoadSettings()));
builder.Services.AddScoped<DeezSpoTag.Integrations.Deezer.DeezerApiService>();
builder.Services.AddScoped<DeezSpoTag.Integrations.Deezer.DeezerGatewayService>();
builder.Services.AddScoped<DeezSpoTag.Integrations.Deezer.DeezerClient>();
builder.Services.AddScoped<DeezSpoTag.Services.Download.AuthenticatedDeezerService>();

// Register Settings service (handled in AddDownloadEngine -> AddDeezSpoTagQueue)


// Register DeezSpoTag services (download engine, crypto, etc.)
builder.Services.AddDeezSpoTagServices();
// Register the full download engine and deezspotag queue (object generator, queue, background worker)
builder.Services.AddDownloadEngine();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<DeezSpoTag.API.Services.DeezSpoTagSearchProxyService>();

// Enable CORS for frontend
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend",
        policy => policy.WithOrigins("http://localhost:8668")
                        .AllowAnyHeader()
                        .AllowAnyMethod()
                        .AllowCredentials());
});

// Add services to the container
builder.Services.AddControllers();
builder.Services.AddSignalR();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddDistributedMemoryCache();
builder.Services.AddSession(options =>
{
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.IdleTimeout = TimeSpan.FromHours(12);
});

var app = builder.Build();

// Use CORS
app.UseCors("AllowFrontend");

// Use session
app.UseSession();

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

Console.WriteLine("🚀 DeezSpoTag API is running!");
Console.WriteLine("📖 Swagger available at: /swagger");

await app.RunAsync();
