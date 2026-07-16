using Serilog;
using Flowbit.Ui.Components;
using Flowbit.Ui.Clients;
using Flowbit.Ui.Auth;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    Log.Information("Starting UI host...");
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((context, services, configuration) => configuration
        .ReadFrom.Configuration(context.Configuration)
        .ReadFrom.Services(services));

    // Add services to the container.
    builder.Services.AddRazorComponents()
        .AddInteractiveServerComponents();

    builder.Services.AddSingleton<TokenState>();
    builder.Services.AddSingleton<DevTokenFactory>();
    builder.Services.AddTransient<AuthTokenHandler>();

    builder.Services.AddHttpClient<WorkflowApiClient>(client =>
    {
        var baseUrl = builder.Configuration["WorkflowApi:BaseUrl"]
            ?? "http://localhost:5017";
        client.BaseAddress = new Uri(baseUrl);
    })
    .AddHttpMessageHandler<AuthTokenHandler>();

    var app = builder.Build();

    app.UseSerilogRequestLogging();

    // Configure the HTTP request pipeline.
    if (!app.Environment.IsDevelopment())
    {
        app.UseExceptionHandler("/Error", createScopeForErrors: true);
        // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
        app.UseHsts();
    }
    app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
    app.UseHttpsRedirection();

    app.UseAntiforgery();

    app.MapStaticAssets();
    app.MapRazorComponents<App>()
        .AddInteractiveServerRenderMode();

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "UI Host terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
