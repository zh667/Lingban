using Microsoft.Extensions.DependencyInjection;

namespace Lingban.Application.FunctionalTests;

[SetUpFixture]
public class FunctionalTestSetup
{
    internal static IServiceScopeFactory ScopeFactory { get; private set; } = null!;
    internal static string DatabaseConnectionString { get; private set; } = null!;
    internal static DatabaseResetter? DbResetter { get; private set; }

    private static WebApiFactory? _factory;
    private static DistributedApplication? _app;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var cancellationToken = cts.Token;

        var builder = await DistributedApplicationTestingBuilder
            .CreateAsync<Projects.TestAppHost>(
                args: [],
                configureBuilder: (options, _) =>
                {
                    options.DisableDashboard = true;
                });

        builder.Configuration["ASPIRE_ALLOW_UNSECURED_TRANSPORT"] = "true";

        _app = await builder
            .BuildAsync(cancellationToken)
            .WaitAsync(cancellationToken);

        await _app
            .StartAsync(cancellationToken)
            .WaitAsync(cancellationToken);

        await _app.ResourceNotifications.WaitForResourceHealthyAsync(
            Services.Database, cancellationToken);

        var connectionString = (await _app.GetConnectionStringAsync(Services.Database))!;
        DatabaseConnectionString = connectionString;

        _factory = new WebApiFactory(connectionString);
        ScopeFactory = _factory.Services.GetRequiredService<IServiceScopeFactory>();

        // WebApplicationFactory(minimal hosting)在 Build() 后即返回,Program 里的
        // EnsureDeleted/EnsureCreated 在后台线程竞跑——显式等 schema 就绪,不赌时序。
        await WaitForSchemaAsync(connectionString, cancellationToken);
        DbResetter = await CreateResetterWithRetryAsync(connectionString, cancellationToken);
    }

    private static async Task WaitForSchemaAsync(string connectionString, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (true)
        {
            try
            {
                await using var connection = new Npgsql.NpgsqlConnection(connectionString);
                await connection.OpenAsync(cancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_name = 'WorkOrders')
                    """;
                if ((bool)(await command.ExecuteScalarAsync(cancellationToken))!)
                {
                    return;
                }
            }
            catch (Npgsql.PostgresException)
            {
                // 初始化窗口内的 3D000/57P01 等瞬态,继续等。
            }
            catch (Npgsql.NpgsqlException)
            {
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("数据库 schema 在 90 秒内未就绪。");
            }

            await Task.Delay(500, cancellationToken);
        }
    }

    private static async Task<DatabaseResetter> CreateResetterWithRetryAsync(
        string connectionString, CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await DatabaseResetter.CreateAsync(connectionString);
            }
            catch (Npgsql.PostgresException) when (attempt < 4)
            {
                await Task.Delay(1000, cancellationToken);
            }
        }
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (DbResetter is not null) await DbResetter.DisposeAsync();
        if (_app is not null) await _app.DisposeAsync();
        if (_factory is not null) await _factory.DisposeAsync();
    }
}
