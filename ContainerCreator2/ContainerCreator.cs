using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using ContainerCreator2.Data;
using ContainerCreator2.Service.Abstract;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask;
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

        [Function(nameof(CreateContainer))]
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

        [Function(nameof(ShowContainers))]
        public async Task<IActionResult> ShowContainers([HttpTrigger(AuthorizationLevel.Function, "get", "post")] HttpRequest req,
            [DurableClient] DurableTaskClient client)
        {
            string containersReadFromAzure, containersReadFromDurableEntity;
            try
            {
                var containerInfos = await containerManagerService.ShowContainers();
                containersReadFromAzure = JsonSerializer.Serialize(containerInfos);

                var entityId = new EntityInstanceId(nameof(ContainersDurableEntity), "containers");
                EntityMetadata<ContainersDurableEntity>? entity = await client.Entities.GetEntityAsync<ContainersDurableEntity>(entityId);
                containersReadFromDurableEntity = JsonSerializer.Serialize(entity?.State?.Get());
            }
            catch (Exception ex)
            {
                return new OkObjectResult($"{ex.Message}");
            }

            logger.LogInformation($"C# HTTP trigger function processed a request. ");
            return new OkObjectResult($"{containersReadFromAzure}\n\n{containersReadFromDurableEntity}");
        }

        [Function(nameof(DeleteContainers))]
        public async Task<IActionResult> DeleteContainers([HttpTrigger(AuthorizationLevel.Function, "get", "post")] HttpRequest req,
            [DurableClient] DurableTaskClient client)
        {
            var hasCompleted = await containerManagerService.DeleteContainers();

            var entityId = new EntityInstanceId(nameof(ContainersDurableEntity), "containers");
            await client.Entities.SignalEntityAsync(entityId, "Reset");

            logger.LogInformation("C# HTTP trigger function processed a request.");
            return new OkObjectResult($"Deleted containers: {hasCompleted}");
        }

        [Function(nameof(ShutAllContainersDaily))]
        [FixedDelayRetry(5, "00:00:10")]
        public async Task ShutAllContainersDaily([TimerTrigger("0 0 1 * * *")] TimerInfo timerInfo, FunctionContext context,
            [DurableClient] DurableTaskClient client)
        {
            var hasCompleted = await containerManagerService.DeleteContainers();
            var entityId = new EntityInstanceId(nameof(ContainersDurableEntity), "containers");
            await client.Entities.SignalEntityAsync(entityId, "Reset");
            logger.LogWarning($"Automatically deleted containers if any existed: {hasCompleted}");
        }


        // As orchestration:////////////////////////////////////////////////////////////////////////////////////////

        [Function("Orchestrate")]
        public async Task<List<string>> Orchestrate(
            [OrchestrationTrigger] TaskOrchestrationContext context)
        {
            var containerRequestData = context.GetInput<ContainerRequestData>();
            var outputs = new List<string>();
            outputs.Add(await context.CallActivityAsync<string>(nameof(CreateContainerAsActivity), containerRequestData));
            context.SetCustomStatus($"Container Created {DateTime.UtcNow} UTC");

            var waitDuration = TimeSpan.FromMinutes(10);
            await context.CreateTimer(waitDuration, CancellationToken.None);

            outputs.Add(await context.CallActivityAsync<string>(nameof(StopContainerAsActivity), containerRequestData));

            return outputs;
        }

        [Function(nameof(CreateContainerAsActivity))]
        public async Task<string> CreateContainerAsActivity([ActivityTrigger] ContainerRequestData containerRequestData,
            [DurableClient] DurableTaskClient client)
        {
            var containerInfo = await containerManagerService.CreateContainer(
                containerRequestData.OwnerId, containerRequestData.DnsNameLabel, containerRequestData.UrlToOpenEncoded
            );
            var entityId = new EntityInstanceId(nameof(ContainersDurableEntity), "containers");
            await client.Entities.SignalEntityAsync(entityId, "Add", containerInfo);

            logger.LogInformation("C# HTTP trigger function processed a request.");
            var response = new StringBuilder("Container created:\n");
            response.Append($"container group name: {containerInfo.ContainerGroupName}, url: {containerInfo.Fqdn}:{containerInfo.Port}\n");
            response.Append($"ip: {containerInfo.Ip}, password: {containerInfo.RandomPassword}");
            return response.ToString();
        }

        [Function(nameof(StopContainerAsActivity))]
        public async Task<string> StopContainerAsActivity([ActivityTrigger] ContainerRequestData containerRequestData,
            [DurableClient] DurableTaskClient client)
        {
            var hasCompleted = await containerManagerService.DeleteContainers();
            var entityId = new EntityInstanceId(nameof(ContainersDurableEntity), "containers");
            await client.Entities.SignalEntityAsync(entityId, "Reset");
            logger.LogInformation("Deleting containers");
            return $"Deleted containers: {hasCompleted}";
        }

        [Function(nameof(GetOrchestrationStatus))]
        public static async Task<IActionResult> GetOrchestrationStatus([DurableClient] DurableTaskClient client, [FromQuery] string instanceId,
            [HttpTrigger(AuthorizationLevel.Function, "get", "post")] HttpRequest req)
        {
            var orchestrationData = await client.GetInstanceAsync(instanceId);
            var status = orchestrationData.RuntimeStatus.ToString();
            return new OkObjectResult(status);
        }

        [Function(nameof(CreateContainerInOrchestration))]
        public async Task<RedirectResult> CreateContainerInOrchestration([HttpTrigger(AuthorizationLevel.Function, "get", "post")] HttpRequestData req,
            [DurableClient] DurableTaskClient client,
            [FromQuery] string dnsNameLabel, [FromQuery] string urlToOpenEncoded, [FromQuery] string ownerId = "00000000-0000-0000-0000-000000000000")
        {
            var containerInfo = new ContainerRequestData()
            {
                DnsNameLabel = dnsNameLabel,
                UrlToOpenEncoded = urlToOpenEncoded,
                OwnerId = ownerId
            };
            string instanceId = await client.ScheduleNewOrchestrationInstanceAsync("Orchestrate", containerInfo);
            logger.LogInformation("Started orchestration with ID = '{instanceId}'.", instanceId);
            return new RedirectResult($"/runtime/webhooks/durabletask/instances/{instanceId}");
        }

        ////////////////////////////////////////////////////////////////////////////////////////////////////////////

        [Function("TestKeyVault")]
        public async Task<IActionResult> Test([HttpTrigger(AuthorizationLevel.Function, "get", "post")] HttpRequest req,
        [DurableClient] DurableTaskClient client)
        {
            const string secretName = "Test";
            var keyVaultName = "KeyVaultJJ-09";
            var kvUri = $"https://{keyVaultName}.vault.azure.net";
            var secret = "";
            try
            {
                var vaultClient = new SecretClient(new Uri(kvUri), new DefaultAzureCredential());
                secret = (await vaultClient.GetSecretAsync(secretName)).Value.Value;
            }
            catch(Exception ex)
            {
                return new OkObjectResult($"{ex.Message}");
            }

            logger.LogInformation("C# HTTP trigger function processed a request.");
            return new OkObjectResult($"value: {secret}");
        }
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
