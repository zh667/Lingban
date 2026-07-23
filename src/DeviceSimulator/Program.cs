using Lingban.DeviceSimulator;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.AddApplicationServices();
builder.AddInfrastructureServices();

builder.Services.Configure<SimulatorOptions>(builder.Configuration.GetSection(SimulatorOptions.SectionName));
builder.Services.AddHostedService<SimulatorWorker>();

var host = builder.Build();
host.Run();
