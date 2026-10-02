using Atm.Grpc;
using Grpc.Net.Client;
using Itmo.Dev.Platform.Persistence.Abstractions.Connections;
using Itmo.Dev.Platform.Testing.ApplicationFactories;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Testcontainers.PostgreSql;
using IInvoiceApprovalClient = Atm.Application.IInvoiceApprovalClient;

namespace Atm.IntegrationTests;

public sealed class AtmGrpcServiceFixture : IAsyncLifetime
{
    public static string AdminPassword => "integration-tests-admin-password";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine").Build();

    private WebApplicationFactory<Program>? _factory;
    private IServiceScope? _scope;
    private GrpcChannel? _channel;

    public SessionService.SessionServiceClient SessionClient => new(RequireChannel());

    public AccountService.AccountServiceClient AccountClient => new(RequireChannel());

    public InvoiceService.InvoiceServiceClient InvoiceClient => new(RequireChannel());

    public FakeInvoiceApprovalClient ApprovalClient { get; } = new();

    public string ConnectionString => _container.GetConnectionString();

    public Users.Grpc.Contracts.UserService.UserServiceClient UserClient => new(RequireChannel());

    public IServiceProvider Services => RequireFactory().Services;

    public IPersistenceConnectionProvider ConnectionProvider => RequireScope().ServiceProvider.GetRequiredService<IPersistenceConnectionProvider>();

    public async Task<long> CountOutboxMessagesAsync()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = "select count(*) from message_persistence.persisted_messages";

        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        var connectionString = new NpgsqlConnectionStringBuilder(_container.GetConnectionString());

        WebApplicationFactory<Program> factory = new PlatformWebApplicationBuilder<Program>()
            .AddConfigurationEntry("Infrastructure:Persistence:Postgres:Host", RequireValue(connectionString.Host, "Host"))
            .AddConfigurationEntry("Infrastructure:Persistence:Postgres:Port", connectionString.Port)
            .AddConfigurationEntry("Infrastructure:Persistence:Postgres:Database", RequireValue(connectionString.Database, "Database"))
            .AddConfigurationEntry("Infrastructure:Persistence:Postgres:Username", RequireValue(connectionString.Username, "Username"))
            .AddConfigurationEntry("Infrastructure:Persistence:Postgres:Password", RequireValue(connectionString.Password, "Password"))
            .AddConfigurationEntry("MessagePersistence:Postgres:Host", RequireValue(connectionString.Host, "Host"))
            .AddConfigurationEntry("MessagePersistence:Postgres:Port", connectionString.Port)
            .AddConfigurationEntry("MessagePersistence:Postgres:Database", RequireValue(connectionString.Database, "Database"))
            .AddConfigurationEntry("MessagePersistence:Postgres:Username", RequireValue(connectionString.Username, "Username"))
            .AddConfigurationEntry("MessagePersistence:Postgres:Password", RequireValue(connectionString.Password, "Password"))
            .AddConfigurationEntry("AdminPassword", AdminPassword)
            .Build()
            .WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IInvoiceApprovalClient>();
                services.AddSingleton<IInvoiceApprovalClient>(ApprovalClient);
            }));

        HttpClient client = factory.CreateDefaultClient(new ResponseVersionHandler());
        Uri baseAddress = client.BaseAddress ?? throw new InvalidOperationException("Test HttpClient has no base address");

        _factory = factory;
        _scope = factory.Services.CreateScope();
        _channel = GrpcChannel.ForAddress(baseAddress, new GrpcChannelOptions { HttpClient = client });
    }

    public async Task DisposeAsync()
    {
        _channel?.Dispose();
        _scope?.Dispose();
        _factory?.Dispose();
        await _container.DisposeAsync();
    }

    private static string RequireValue(string? value, string name)
    {
        return value ?? throw new InvalidOperationException($"Container connection string has no {name}");
    }

    private GrpcChannel RequireChannel()
    {
        return _channel ?? throw new InvalidOperationException("Fixture is not initialized yet");
    }

    private WebApplicationFactory<Program> RequireFactory()
    {
        return _factory ?? throw new InvalidOperationException("Fixture is not initialized yet");
    }

    private IServiceScope RequireScope()
    {
        return _scope ?? throw new InvalidOperationException("Fixture is not initialized yet");
    }

    private sealed class ResponseVersionHandler : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpResponseMessage response = await base.SendAsync(request, cancellationToken);
            response.Version = request.Version;
            return response;
        }
    }
}
