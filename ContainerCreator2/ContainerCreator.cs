using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
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

        [Function(nameof(CreateContainer))]
        public async Task<IActionResult> CreateContainer([HttpTrigger(AuthorizationLevel.Function, "get", "post")] HttpRequest req,
            [DurableClient] DurableTaskClient client,
            [FromQuery] string dnsNameLabel, [FromQuery] string urlToOpenEncoded, [FromQuery] string ownerId = "00000000-0000-0000-0000-000000000000")
        {
            var containerRequest = new ContainerRequest()
            {
                Id = Guid.NewGuid(),
                OwnerId = ownerId,
                DnsNameLabel = dnsNameLabel,
                UrlToOpenEncoded = urlToOpenEncoded
            };
            var containerInfo = await containerManagerService.CreateContainer(containerRequest);
            var entityId = new EntityInstanceId(nameof(ContainersDurableEntity), "containers");
            await client.Entities.SignalEntityAsync(entityId, nameof(ContainersDurableEntity.Add), containerInfo);

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
            [DurableClient] DurableTaskClient client, [FromQuery] string containerGroupName = "")
        {
            bool hasCompleted = false;
            var entityId = new EntityInstanceId(nameof(ContainersDurableEntity), "containers");

            if (string.IsNullOrEmpty(containerGroupName))
            {
                hasCompleted = await containerManagerService.DeleteAllContainerGroups();
                await client.Entities.SignalEntityAsync(entityId, nameof(ContainersDurableEntity.Reset));
            }
            else
            {
                hasCompleted = await containerManagerService.DeleteContainerGroup(containerGroupName);
                EntityMetadata<ContainersDurableEntity>? entity = await client.Entities.GetEntityAsync<ContainersDurableEntity>(entityId);
                var containerInfo = entity?.State?.Get()?.FirstOrDefault(c => c.ContainerGroupName == containerGroupName);
                await client.Entities.SignalEntityAsync(entityId, nameof(ContainersDurableEntity.Delete), containerInfo);
            }
            logger.LogInformation("C# HTTP trigger function processed a request.");
            return new OkObjectResult($"Deleted containers: {hasCompleted}");
        }

        [Function(nameof(DeleteAllContainersDaily))]
        [FixedDelayRetry(5, "00:00:10")]
        public async Task DeleteAllContainersDaily([TimerTrigger("0 0 1 * * *")] TimerInfo timerInfo, FunctionContext context,
            [DurableClient] DurableTaskClient client)
        {
            var hasCompleted = await containerManagerService.DeleteAllContainerGroups();
            var entityId = new EntityInstanceId(nameof(ContainersDurableEntity), "containers");
            await client.Entities.SignalEntityAsync(entityId, nameof(ContainersDurableEntity.Reset));
            logger.LogWarning($"Automatically deleted containers if any existed: {hasCompleted}");
        }

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
            catch (Exception ex)
            {
                return new OkObjectResult($"{ex.Message}");
            }

            logger.LogInformation("C# HTTP trigger function processed a request.");
            return new OkObjectResult($"value: {secret}");
        }
    }

}
