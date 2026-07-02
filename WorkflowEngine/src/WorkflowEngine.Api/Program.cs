using WorkflowEngine.Api.Endpoints;
using WorkflowEngine.Infrastructure.Data;
using WorkflowEngine.Infrastructure.DependencyInjection;
using WorkflowEngine.Service.DependencyInjection;
using WorkflowEngine.Service.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services
    .AddServiceLayer()
    .AddInfrastructure(builder.Configuration);

var app = builder.Build();

app.Use(async (context, next) =>
{
    try
    {
        await next(context);
    }
    catch (WorkflowDomainException ex)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new { error = ex.Message });
    }
});

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/openapi/v1.json", "Workflow Engine API v1");
    });

    using var scope = app.Services.CreateScope();
    var initializer = scope.ServiceProvider.GetRequiredService<DatabaseInitializer>();
    var seedPath = Path.GetFullPath(Path.Combine(
        app.Environment.ContentRootPath,
        "..",
        "..",
        "..",
        "workflow.json"));
    await initializer.ApplyMigrationsAndSeedAsync(seedPath);
}

app.UseHttpsRedirection();

app.MapGet("/", () => Results.Redirect("/swagger"));
app.MapWorkflowDefinitionEndpoints();
app.MapWorkflowInstanceEndpoints();

app.Run();
