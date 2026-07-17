using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Testcontainers.PostgreSql;
using Flowbit.Infrastructure.Data;
using Flowbit.Service.Abstractions;
using Xunit;

namespace Flowbit.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PostgresApiCollection : ICollectionFixture<PostgresApiFixture>
{
    public const string Name = "postgres-api";
}

public sealed class PostgresApiFixture : IAsyncLifetime
{
    private readonly Dictionary<string, string?> _originalEnvironment = new(StringComparer.Ordinal);
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("workflow_tests")
        .WithUsername("postgres")
        .WithPassword("workflow-tests")
        .Build();

    public WebApplicationFactory<Program> Factory { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;
    public string ConnectionString => _postgres.GetConnectionString();
    public NpgsqlDataSource DataSource { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        SetEnvironment("ASPNETCORE_ENVIRONMENT", "Testing");
        SetEnvironment("ConnectionStrings__Flowbit", _postgres.GetConnectionString());
        SetEnvironment("Jwt__Issuer", ApiTestAuth.Issuer);
        SetEnvironment("Jwt__Audience", ApiTestAuth.Audience);
        SetEnvironment("Jwt__Key", ApiTestAuth.Key);
        SetEnvironment("WorkflowContext__Config__taskDistributionClientId", "config-distributor");
        SetEnvironment("WorkflowContext__Config__taskDistributionClientSecret", "config-distributor-secret");

        // Program reads the process-latched actor identity setting during startup,
        // so the schema must exist before WebApplicationFactory starts the host.
        var migrationDataSourceBuilder = new NpgsqlDataSourceBuilder(_postgres.GetConnectionString());
        migrationDataSourceBuilder.EnableDynamicJson();
        await using (var migrationDataSource = migrationDataSourceBuilder.Build())
        {
            var migrationOptions = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(migrationDataSource)
                .Options;
            await using var migrationDb = new AppDbContext(migrationOptions);
            await migrationDb.Database.MigrateAsync();
        }

        Factory = new WorkflowApiFactory(_postgres.GetConnectionString());
        Client = Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        await using var scope = Factory.Services.CreateAsyncScope();
        DataSource = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
    }

    public async Task DisposeAsync()
    {
        Client.Dispose();
        await Factory.DisposeAsync();
        await _postgres.DisposeAsync();
        foreach (var pair in _originalEnvironment)
        {
            Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
    }

    public AppDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(DataSource)
            .Options;
        return new AppDbContext(options);
    }

    private void SetEnvironment(string key, string value)
    {
        _originalEnvironment.TryAdd(key, Environment.GetEnvironmentVariable(key));
        Environment.SetEnvironmentVariable(key, value);
    }

    private sealed class WorkflowApiFactory(string connectionString) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) =>
            {
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Flowbit"] = connectionString,
                    ["Jwt:Issuer"] = ApiTestAuth.Issuer,
                    ["Jwt:Audience"] = ApiTestAuth.Audience,
                    ["Jwt:Key"] = ApiTestAuth.Key,
                    ["Serilog:WriteTo:0:Name"] = "Console"
                });
            });
            builder.ConfigureTestServices(services =>
            {
                services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = TestAuthenticationHandler.SchemeName;
                        options.DefaultChallengeScheme = TestAuthenticationHandler.SchemeName;
                    })
                    .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                        TestAuthenticationHandler.SchemeName,
                        _ => { });
                services.RemoveAll<IServiceTaskInvoker>();
                services.AddSingleton<IServiceTaskInvoker, DeterministicServiceTaskInvoker>();
            });
        }
    }

    private sealed class DeterministicServiceTaskInvoker : IServiceTaskInvoker
    {
        public Task<ServiceTaskResult> InvokeAsync(
            ServiceTaskRequest request,
            CancellationToken cancellationToken)
        {
            var body = request.Url switch
            {
                var url when url.EndsWith("/typed-output-success", StringComparison.Ordinal) =>
                    """{"result":{"decision":"approved","score":12},"tags":["safe","priority"],"businessDate":"2026-07-15","approved":true,"receivedAt":"2026-07-15T10:30:00+03:00","metadata":{"source":"service"},"ratings":[1.5,2.5]}""",
                var url when url.EndsWith("/typed-output-invalid", StringComparison.Ordinal) =>
                    """{"result":{"decision":"approved","score":"12"},"tags":["safe"],"businessDate":"2026-07-15","approved":true,"receivedAt":"2026-07-15T10:30:00Z","metadata":{},"ratings":[1]}""",
                var url when url.EndsWith("/typed-output-blocked", StringComparison.Ordinal) =>
                    """{"result":{"decision":"blocked","score":12},"tags":["safe"],"businessDate":"2026-07-15","approved":true,"receivedAt":"2026-07-15T10:30:00Z","metadata":{},"ratings":[1]}""",
                var url when url.EndsWith("/nullable-output", StringComparison.Ordinal) =>
                    """{"value":null}""",
                _ => null
            };
            return Task.FromResult(body is null
                ? new ServiceTaskResult(true, 404, null, "No deterministic test response configured.")
                : new ServiceTaskResult(true, 200, body, null));
        }
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "FlowbitTests";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var user = Request.Headers["X-Test-User"].FirstOrDefault();
            if (string.IsNullOrWhiteSpace(user))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var claims = new List<Claim>
            {
                new(ClaimTypes.Name, user),
                new(ClaimTypes.NameIdentifier, user)
            };
            var roles = Request.Headers["X-Test-Roles"].FirstOrDefault()
                ?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                ?? [];
            foreach (var role in roles.Append("admin").Distinct(StringComparer.OrdinalIgnoreCase))
            {
                claims.Add(new Claim(ClaimTypes.Role, role));
                claims.Add(new Claim("role", role));
            }
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(principal, SchemeName)));
        }
    }
}
