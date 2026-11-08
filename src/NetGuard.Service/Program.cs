using NetGuard.Service;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "NetGuard");
builder.Services.AddSingleton<Guard>();
builder.Services.AddHostedService<GuardWorker>();
builder.Services.AddHostedService<PipeServer>();
builder.Build().Run();
