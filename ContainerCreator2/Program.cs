using ContainerCreator2.Service;
using ContainerCreator2.Service.Abstract;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication()
    .ConfigureServices(services =>
    {
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();
        services.AddSingleton<IContainerManagerService, ContainerManagerService>();
        services.AddSingleton<IArmClientFactory, ArmClientFactory>();
    })
    .Build();

host.Run();
