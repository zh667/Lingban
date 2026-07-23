using Lingban.Shared;

namespace Lingban.TestAppHost;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = DistributedApplication.CreateBuilder(args);

        builder.AddPostgres(Services.DatabaseServer)
            .WithImage("pgvector/pgvector", "pg17")
            .AddDatabase(Services.Database);

        builder.Build().Run();
    }
}
