using ContainerCreator2.Data;
using ContainerCreator2.Service.Abstract;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Client;
using Microsoft.DurableTask.Client.Entities;
using Microsoft.DurableTask.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ContainerCreator2
{

    public class ContainerCreatorOrchestrated
    {
        private readonly ILogger<ContainerCreatorOrchestrated> logger;
        private readonly IConfiguration configuration;
        private readonly IContainerManagerService containerManagerService;
        private readonly int containerLifeTimeMinutes;

        public ContainerCreatorOrchestrated(ILogger<ContainerCreatorOrchestrated> logger, IConfiguration configuration, IContainerManagerService containerManagerService)
        {
            this.logger = logger;
            this.configuration = configuration;
            this.containerManagerService = containerManagerService;
            this.containerLifeTimeMinutes = int.TryParse(this.configuration["ContainerLifeTimeMinutes"], out var parsed) ? parsed : 20;
        }

        [Function("Orchestrate")]
        public async Task<bool> Orchestrate(
            [OrchestrationTrigger] TaskOrchestrationContext context)
        {
            var containerRequest = context.GetInput<ContainerRequest>();
            var overLimitsError = await context.CallActivityAsync<string>(nameof(GetOverLimitsErrorActivity), containerRequest);
            if(!string.IsNullOrEmpty(overLimitsError))
            {
                context.SetCustomStatus($"Failed to start, {overLimitsError}, {DateTime.UtcNow} UTC");
                return false;
            }
            
            var containerInfo = await context.CallActivityAsync<ContainerInfo>(nameof(CreateContainerAsActivity), containerRequest);
            if (!containerInfo.IsDeploymentSuccesful)
            {
                context.SetCustomStatus($"Failed to start, {containerInfo.ProblemMessage}, {DateTime.UtcNow} UTC");
                return false;
            }

            var containerHost = !string.IsNullOrEmpty(containerInfo.Fqdn) ? containerInfo.Fqdn : containerInfo.Ip;
            context.SetCustomStatus(containerInfo);

            var waitDuration = TimeSpan.FromMinutes(this.containerLifeTimeMinutes);
            await context.CreateTimer(waitDuration, CancellationToken.None);

            var isDeleted = await context.CallActivityAsync<bool>(nameof(DeleteContainerAsActivity), containerInfo);
            context.SetCustomStatus($"Deleted:{DateTime.UtcNow} UTC,{containerHost},{containerInfo.Port}");
            return isDeleted;
        }

        [Function(nameof(GetOverLimitsErrorActivity))]
        public async Task<string> GetOverLimitsErrorActivity([ActivityTrigger] ContainerRequest containerRequest,
            [DurableClient] DurableTaskClient client
            //,TaskOrchestrationContext context
            )
        {
            var entityId = new EntityInstanceId(nameof(ContainersDurableEntity), "containers");
            EntityMetadata<ContainersDurableEntity>? entity = await client.Entities.GetEntityAsync<ContainersDurableEntity>(entityId);
            var activeContainers = await containerManagerService.GetContainers();

            while (containerManagerService.MaxConcurrentContainersPerUserReached(activeContainers, containerRequest.OwnerId))
            {
                var oldestUsersContainer = containerManagerService.GetOldestContainerForUser(activeContainers, containerRequest.OwnerId);
                await containerManagerService.DeleteContainerGroup(oldestUsersContainer.ContainerGroupName);

                await client.Entities.SignalEntityAsync(entityId, nameof(ContainersDurableEntity.Delete), oldestUsersContainer);
                //var deleteSuccess = await context.Entities.CallEntityAsync<ContainerInfo>(entityId, nameof(ContainersDurableEntity.Delete), oldestUsersContainer);
                
                activeContainers = await containerManagerService.GetContainers();
            }
            if (containerManagerService.MaxConcurrentContainersTotalReached(activeContainers))
            {
                logger.LogWarning($"Problem: {nameof(containerManagerService.MaxConcurrentContainersTotalReached)}");
                return nameof(containerManagerService.MaxConcurrentContainersTotalReached);
            }

            var containersFromMemory = entity?.State?.Get()?.ToList() ?? new List<ContainerInfo>();
            logger.LogInformation($"containersFromMemory: {containersFromMemory.Count}");

            // TODO: this IF may always be opened if deleting old users container does not propagate in durable entity
            if (containerManagerService.MaxConcurrentContainersPerUserReached(containersFromMemory, containerRequest.OwnerId))
            {
                Thread.Sleep(TimeSpan.FromSeconds(60)); 
                activeContainers = await containerManagerService.GetContainers();
                if (containerManagerService.MaxConcurrentContainersPerUserReached(activeContainers, containerRequest.OwnerId))
                {
                    logger.LogWarning($"Problem: {nameof(containerManagerService.MaxConcurrentContainersPerUserReached)}");
                    return nameof(containerManagerService.MaxConcurrentContainersPerUserReached);
                }

                // Problem occured, need to sync entity memory with current state
                foreach (var containerInfo in containersFromMemory.Where(c => c.OwnerId.ToString() == containerRequest.OwnerId))
                {
                    await client.Entities.SignalEntityAsync(entityId, nameof(ContainersDurableEntity.Delete), containerInfo);
                }
                foreach (var containerInfo in activeContainers.Where(c => c.OwnerId.ToString() == containerRequest.OwnerId))
                {
                    await client.Entities.SignalEntityAsync(entityId, nameof(ContainersDurableEntity.Add), containerInfo);
                }
            }

            return string.Empty;
        }

        [Function(nameof(CreateContainerAsActivity))]
        public async Task<ContainerInfo> CreateContainerAsActivity([ActivityTrigger] ContainerRequest containerRequest,
            [DurableClient] DurableTaskClient client)
        {
            var entityId = new EntityInstanceId(nameof(ContainersDurableEntity), "containers");
            var tempContainerInfo = new ContainerInfo()
            {
                OwnerId = Guid.TryParse(containerRequest.OwnerId, out var parsed) ? parsed : Guid.Empty,
                ContainerGroupName = containerRequest.OwnerId
            };

            await client.Entities.SignalEntityAsync(entityId, nameof(ContainersDurableEntity.Add), tempContainerInfo);
            var containerInfo = await containerManagerService.CreateContainer(containerRequest);
            if (containerInfo.IsDeploymentSuccesful)
            {
                await client.Entities.SignalEntityAsync(entityId, nameof(ContainersDurableEntity.Add), containerInfo);
            }
            await client.Entities.SignalEntityAsync(entityId, nameof(ContainersDurableEntity.Delete), tempContainerInfo);
            logger.LogInformation("C# HTTP trigger function processed a request.");
            return containerInfo;
        }

        [Function(nameof(DeleteContainerAsActivity))]
        public async Task<bool> DeleteContainerAsActivity([ActivityTrigger] ContainerInfo containerInfo,
            [DurableClient] DurableTaskClient client)
        {
            var hasCompleted = await containerManagerService.DeleteContainerGroup(containerInfo.ContainerGroupName);
            var entityId = new EntityInstanceId(nameof(ContainersDurableEntity), "containers");
            await client.Entities.SignalEntityAsync(entityId, nameof(ContainersDurableEntity.Delete), containerInfo);
            logger.LogInformation($"Deleted container group {containerInfo.ContainerGroupName}");
            return hasCompleted;
        }

        [Function(nameof(GetOrchestrationStatus))]
        public static async Task<IActionResult> GetOrchestrationStatus([DurableClient] DurableTaskClient client, [FromQuery] string instanceId,
            [HttpTrigger(AuthorizationLevel.Function, "get", "post")] HttpRequest req)
        {
            var orchestrationData = await client.GetInstanceAsync(instanceId);
            var status = orchestrationData?.RuntimeStatus.ToString();
            return new OkObjectResult(status);
        }

        [Function(nameof(CreateContainerInOrchestration))]
        public async Task<HttpResponseData> CreateContainerInOrchestration([HttpTrigger(AuthorizationLevel.Function, "get", "post")] HttpRequestData req,
            [DurableClient] DurableTaskClient client,
            [FromQuery] string dnsNameLabel, [FromQuery] string urlToOpenEncoded, [FromQuery] string ownerId = "00000000-0000-0000-0000-000000000000")
        {
            var containerInfo = new ContainerRequest()
            {
                Id = Guid.NewGuid(),
                DnsNameLabel = dnsNameLabel,
                UrlToOpenEncoded = urlToOpenEncoded,
                OwnerId = ownerId
            };
            string instanceId = await client.ScheduleNewOrchestrationInstanceAsync("Orchestrate", containerInfo);
            logger.LogInformation("Started orchestration with ID = '{instanceId}'.", instanceId);
            //return new RedirectResult($"/runtime/webhooks/durabletask/instances/{instanceId}");
            return await client.CreateCheckStatusResponseAsync(req, instanceId);
        }

        [Function(nameof(DeleteAllContainersDaily))]
        [FixedDelayRetry(5, "00:00:10")]
        public async Task DeleteAllContainersDaily([TimerTrigger("0 0 1 * * *")] TimerInfo timerInfo, FunctionContext context,
            [DurableClient] DurableTaskClient client)
        {
            var hasCompleted = await containerManagerService.DeleteAllContainerGroups();
            var entityId = new EntityInstanceId(nameof(ContainersDurableEntity), "containers");
            await client.Entities.SignalEntityAsync(entityId, nameof(ContainersDurableEntity.Reset));
            logger.LogInformation($"Automatically deleted containers if any existed: {hasCompleted}");
        }

        [Function(nameof(DeleteOldContainersAsFailSafe))]
        public async Task DeleteOldContainersAsFailSafe([TimerTrigger("0 10 * * * *")] TimerInfo timerInfo, FunctionContext context,
            [DurableClient] DurableTaskClient client)
        {
            var entityId = new EntityInstanceId(nameof(ContainersDurableEntity), "containers");
            var containerInfosToDelete = await containerManagerService.GetContainersOverTimeLimit(containerLifeTimeMinutes);
            foreach (var containerInfo in containerInfosToDelete)
            {
                await containerManagerService.DeleteContainerGroup(containerInfo.ContainerGroupName);
                await client.Entities.SignalEntityAsync(entityId, nameof(ContainersDurableEntity.Delete), containerInfo);
            }
            logger.LogDebug($"Automatically deleted containers over time limit if any existed");
        }
    }

    public class ContainersDurableEntity
    {
        public List<ContainerInfo> Containers { get; set; } = new List<ContainerInfo>();

        public void Add(ContainerInfo containerInfo) => this.Containers.Add(containerInfo);
        public void Delete(ContainerInfo containerInfo) => this.Containers.Remove(containerInfo);
        public void Reset() => this.Containers.Clear();

        public List<ContainerInfo> Get() => this.Containers ?? new List<ContainerInfo>();

        [Function(nameof(ContainersDurableEntity))]
        public static Task RunEntityAsync([EntityTrigger] TaskEntityDispatcher dispatcher)
        {
            return dispatcher.DispatchAsync<ContainersDurableEntity>();
        }
    }

}
