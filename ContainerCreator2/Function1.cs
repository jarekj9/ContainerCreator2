using ContainerCreator2.Data;
using ContainerCreator2.Service.Abstract;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask.Client;
using Microsoft.DurableTask.Client.Entities;
using Microsoft.DurableTask.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.Json;

namespace ContainerCreator2
{

    public class ContainerCreator
    {
        private readonly ILogger<ContainerCreator> logger;
        private readonly IConfiguration configuration;
        private readonly IContainerManagerService containerManagerService;

        public ContainerCreator(ILogger<ContainerCreator> logger, IConfiguration configuration, IContainerManagerService containerManagerService)
        {
            this.logger = logger;
            this.configuration = configuration;
            this.containerManagerService = containerManagerService;
        }

        [Function("CreateContainer")]
        public async Task<IActionResult> CreateContainer([HttpTrigger(AuthorizationLevel.Function, "get", "post")] HttpRequest req,
            [DurableClient] DurableTaskClient client,
            [FromQuery] string dnsNameLabel, [FromQuery] string urlToOpenEncoded, [FromQuery] string ownerId = "00000000-0000-0000-0000-000000000000")
        {
            var containerInfo = await containerManagerService.CreateContainer(ownerId, dnsNameLabel, urlToOpenEncoded);
            var entityId = new EntityInstanceId(nameof(ContainersDurableEntity), "containers");
            await client.Entities.SignalEntityAsync(entityId, "Add", containerInfo);

            logger.LogInformation("C# HTTP trigger function processed a request.");
            var response = new StringBuilder("Container created:\n");
            response.Append($"container group name: {containerInfo.ContainerGroupName}, url: {containerInfo.Fqdn}:{containerInfo.Port}\n");
            response.Append($"ip: {containerInfo.Ip}, password: {containerInfo.RandomPassword}");
            return new OkObjectResult(response.ToString());
        }

        [Function("ShowContainers")]
        public async Task<IActionResult> ShowContainers([HttpTrigger(AuthorizationLevel.Function, "get", "post")] HttpRequest req,
            [DurableClient] DurableTaskClient client)
        {
            var containerInfos = await containerManagerService.ShowContainers();
            var containersReadFromAzure = JsonSerializer.Serialize(containerInfos);

            var entityId = new EntityInstanceId(nameof(ContainersDurableEntity), "containers");
            EntityMetadata<ContainersDurableEntity>? entity = await client.Entities.GetEntityAsync<ContainersDurableEntity>(entityId);
            var containersReadFromDurableEntity = JsonSerializer.Serialize(entity?.State?.Get());

            logger.LogInformation($"C# HTTP trigger function processed a request. ");
            return new OkObjectResult($"{containersReadFromAzure}\n\n{containersReadFromDurableEntity}");
        }

        [Function("DeleteContainers")]
        public async Task<IActionResult> DeleteContainers([HttpTrigger(AuthorizationLevel.Function, "get", "post")] HttpRequest req,
            [DurableClient] DurableTaskClient client)
        {
            var hasCompleted = await containerManagerService.DeleteContainers();

            var entityId = new EntityInstanceId(nameof(ContainersDurableEntity), "containers");
            await client.Entities.SignalEntityAsync(entityId, "Reset");

            logger.LogInformation("C# HTTP trigger function processed a request.");
            return new OkObjectResult($"Deleted containers: {hasCompleted}");
        }


        // For orchestration:

        //[Function("Orchestrate")]
        //public async Task<List<string>> Run(
        //    [OrchestrationTrigger] TaskOrchestrationContext context)
        //{
        //    var outputs = new List<string>();
        //    outputs.Add(await context.CallActivityAsync<string>("Act", "Tokyo"));
        //    return outputs;
        //}

        //[Function(nameof(Act))]
        //public async Task<string> Act([ActivityTrigger] string name, [DurableClient] DurableTaskClient client)
        //{
        //    logger.LogInformation("Saying hello to {name}.", name);
        //    return $"Hello {name}!";
        //}

        //[Function("Function1_HttpStart")]
        //public async Task<IActionResult> HttpStart(
        //    [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post")] HttpRequestMessage req,
        //    [DurableClient] DurableTaskClient client)
        //{
        //    string instanceId = await client.ScheduleNewOrchestrationInstanceAsync("Orchestrate", null);
        //    logger.LogInformation("Started orchestration with ID = '{instanceId}'.", instanceId);
        //    return new OkObjectResult("ok");
        //}

    }


    public class ContainersDurableEntity
    {
        public List<ContainerInfo> Containers { get; set; } = new List<ContainerInfo>();

        public void Add(ContainerInfo newContainer) => this.Containers.Add(newContainer);

        public void Reset() => this.Containers.Clear();

        public List<ContainerInfo> Get() => this.Containers;

        [Function(nameof(ContainersDurableEntity))]
        public static Task RunEntityAsync([EntityTrigger] TaskEntityDispatcher dispatcher)
        {
            return dispatcher.DispatchAsync<ContainersDurableEntity>();
        }
    }

}
