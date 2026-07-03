using WorkflowEngine.Ui.Components;
using WorkflowEngine.Ui.Clients;
using WorkflowEngine.Ui.Auth;

var builder = WebApplication.CreateBuilder(args);

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
