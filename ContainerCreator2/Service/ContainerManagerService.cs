using Azure.Core;
using Azure.ResourceManager.ContainerInstance;
using Azure.ResourceManager.ContainerInstance.Models;
using ContainerCreator2.Data;
using ContainerCreator2.Service.Abstract;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Web;

namespace ContainerCreator2.Service
{
    public class ContainerManagerService : IContainerManagerService
    {
        private readonly ILogger<ContainerManagerService> logger;
        private readonly IConfiguration configuration;
        private readonly IAzureAciService azureAciService;
        private readonly string containerImage;
        private readonly int maxConcurrentContainersPerUser;
        private readonly int maxConcurrentContainersTotal;
        private readonly List<int> containerPorts;
        private readonly int containerLifeTimeMinutes;
        public ContainerManagerService(ILogger<ContainerManagerService> logger, IConfiguration configuration, IAzureAciService azureAciService)
        {
            this.logger = logger;
            this.configuration = configuration;
            this.containerImage = this.configuration["ContainerImage"] ?? "";
            this.maxConcurrentContainersPerUser = int.TryParse(this.configuration["MaxConcurrentContainersPerUser"], out var parsed) ? parsed : 1;
            this.maxConcurrentContainersTotal = int.TryParse(this.configuration["MaxConcurrentContainersTotal"], out var parsedTotal) ? parsedTotal : 1;
            var ports = this.configuration["ContainerPorts"]?.Split(",") ?? new string[0];
            this.containerPorts = ports.Select(p => int.TryParse(p, out var parsed) ? parsed : 80).ToList();
            this.containerLifeTimeMinutes = int.TryParse(this.configuration["ContainerLifeTimeMinutes"], out var parsedMinutes) ? parsedMinutes : 20;
            this.azureAciService = azureAciService;
        }

        public async Task<ContainerInfo> CreateContainer(ContainerRequest containerRequest)
        {
            containerRequest.RandomPassword = RandomPasswordGenerator.CreateRandomPassword();
            var containerGroupData = GetContainerGroupData(containerRequest);
            var containerGroupName = $"containergroup-{RandomPasswordGenerator.CreateRandomPassword(8, useSpecialChars: false)}";

            var containerInfo = await azureAciService.CreateOrUpdateAsync(containerGroupName, containerGroupData);
            containerInfo.RandomPassword = containerRequest.RandomPassword;
            containerInfo.Image = this.containerImage;
            containerInfo.OwnerId = Guid.TryParse(containerRequest.OwnerId, out var parsedId) ? parsedId : Guid.Empty;

            return containerInfo;
        }

        private ContainerGroupData GetContainerGroupData(ContainerRequest containerRequest)
        {
            var containerResource = new ContainerResourceRequestsContent(1, 1);
            var requirements = new ContainerResourceRequirements(containerResource);
            var containerGroupPorts = this.containerPorts.Select(p => new ContainerGroupPort(p)).ToList();
            var ipAddress = new ContainerGroupIPAddress(containerGroupPorts, ContainerGroupIPAddressType.Public)
            {
                DnsNameLabel = containerRequest.DnsNameLabel,
            };

            var container = new ContainerInstanceContainer("container", this.containerImage, requirements)
            {
                EnvironmentVariables = {
                    new ContainerEnvironmentVariable("VNCPASS") { SecureValue = containerRequest.RandomPassword },
                    new ContainerEnvironmentVariable("MAXMINUTES") { Value = this.containerLifeTimeMinutes.ToString() }
                }
            };
            this.containerPorts.Select(p => new ContainerPort(p)).ToList().ForEach(p => container.Ports.Add(p));
            if (!string.IsNullOrEmpty(containerRequest.UrlToOpenEncoded))
            {
                container.EnvironmentVariables.Add(new ContainerEnvironmentVariable("URL_TO_OPEN")
                {
                    Value = HttpUtility.UrlDecode(containerRequest.UrlToOpenEncoded)
                });
            }
            var containers = new List<ContainerInstanceContainer> { container };
            var containerGroupData = new ContainerGroupData(AzureLocation.NorthEurope, containers, ContainerInstanceOperatingSystemType.Linux)
            {
                IPAddress = ipAddress,
                Tags = {
                    new KeyValuePair<string, string>("OwnerId", containerRequest.OwnerId),
                    new KeyValuePair<string, string>("CreatedTime", DateTime.UtcNow.ToString())
                }
            };
            var registryCredentials = CreateRegistryCredentials();
            if(!string.IsNullOrEmpty(registryCredentials.Server))
            {
                containerGroupData.ImageRegistryCredentials.Add(registryCredentials);
            }

            return containerGroupData;
        }

        private ContainerGroupImageRegistryCredential CreateRegistryCredentials()
        {
            if(new List<string?>() {
                configuration["RegistryCredentialsServer"],
                configuration["RegistryCredentialsUserName"],
                configuration["RegistryCredentialsPassword"]
            }
            .Any(s => string.IsNullOrEmpty(s)))
            {
                new ContainerGroupImageRegistryCredential("");
            }

            var registryCredentials = new ContainerGroupImageRegistryCredential(configuration["RegistryCredentialsServer"]);
            registryCredentials.Username = configuration["RegistryCredentialsUserName"];
            registryCredentials.Password = configuration["RegistryCredentialsPassword"];
            return registryCredentials;
        }

        public async Task<List<ContainerInfo>> GetContainers()
        {
            var containerGroupCollection = await azureAciService.GetContainerGroupsFromResourceGroup();

            var containerInfos = new List<ContainerInfo>();
            foreach (var containerGroup in containerGroupCollection)
            {
                var ownerIdfromTags = (containerGroup.Data?.Tags?.TryGetValue("OwnerId", out var ownerId) ?? false) ?
                    (Guid.TryParse(ownerId, out var parsed) ? parsed : Guid.Empty) : Guid.Empty;

                var createdTimefromTags = (containerGroup.Data?.Tags?.TryGetValue("CreatedTime", out var createdTime) ?? false) ?
                    (DateTime.TryParse(createdTime, out var parsedTime) ? parsedTime : DateTime.MinValue) : DateTime.MinValue;

                containerInfos.Add(new ContainerInfo()
                {
                    ContainerGroupName = containerGroup?.Data?.Name ?? "",
                    Image = containerGroup.Data?.Containers?.FirstOrDefault()?.Image ?? "",
                    Name = containerGroup.Data?.Containers?.FirstOrDefault()?.Name ?? "",
                    Fqdn = containerGroup.Data?.IPAddress?.Fqdn ?? "",
                    Ip = containerGroup.Data?.IPAddress?.IP?.ToString() ?? "",
                    Port = containerGroup.Data?.IPAddress?.Ports?.FirstOrDefault()?.Port ?? 0,
                    OwnerId = ownerIdfromTags,
                    CreatedTime = createdTimefromTags
                });
            }
            return containerInfos;
        }

        public async Task<bool> DeleteAllContainerGroups()
        {
            var containerGroupCollection = await azureAciService.GetContainerGroupsFromResourceGroup();
            bool hasCompleted = true;
            foreach (var containerGroup in containerGroupCollection)
            {
                var containerGroupName = containerGroup?.Data?.Name;
                if (containerGroupName == null)
                {
                    continue;
                }
                bool result;
                try
                {
                    result = await azureAciService.DeleteAsync(containerGroupName);
                }
                catch (Exception e)
                {
                    logger.LogError(e, $"Cannot delete container group containerGroupName");
                    result = false;
                }
                hasCompleted = hasCompleted && result;
            }
            return hasCompleted;
        }

        public async Task<bool> DeleteContainerGroup(string containerGroupName)
        {
            try
            {
                var hasCompleted = await azureAciService.DeleteAsync(containerGroupName);
                return hasCompleted;
            }
            catch (Exception e)
            {
                logger.LogWarning(e, $"Cannot delete containerGroupName");
                return false;
            }
        }

        public async Task<List<ContainerInfo>> GetContainersOverTimeLimit(int maxMinutes)
        {
            var activeContainers = await GetContainers();
            var containersOverTimeLimit = activeContainers.Where(c => c.CreatedTime.AddMinutes(maxMinutes + 3) < DateTime.UtcNow).ToList();
            return containersOverTimeLimit;
        }

        public ContainerInfo GetOldestContainerForUser(List<ContainerInfo> activeContainers, string ownerId)
        {
            var oldestContainer = activeContainers.Where(c => c.OwnerId.ToString() == ownerId).OrderBy(c => c.CreatedTime).FirstOrDefault();
            if(oldestContainer != null)
            {
                return oldestContainer;
            }
            return new ContainerInfo();
        }

        public ContainerInfo GetNewestContainer(List<ContainerInfo> activeContainers)
        {
            var newestContainer = activeContainers.OrderByDescending(c => c.CreatedTime).FirstOrDefault();
            if (newestContainer != null)
            {
                return newestContainer;
            }
            return new ContainerInfo();
        }

        public bool UsersContainersLimitReachedOrExceeded(List<ContainerInfo> activeContainers, string ownerId)
        {
            var usersActiveContainers = activeContainers.Where(c => c.OwnerId.ToString() == ownerId).Count();
            if(usersActiveContainers >= this.maxConcurrentContainersPerUser)
            {
                return true;
            }
            return false;
        }

        public bool UsersContainersCountEqualsLimit(List<ContainerInfo> activeContainers, string ownerId)
        {
            var usersActiveContainers = activeContainers.Where(c => c.OwnerId.ToString() == ownerId).Count();
            if (usersActiveContainers == this.maxConcurrentContainersPerUser)
            {
                return true;
            }
            return false;
        }

        public bool MaxConcurrentContainersTotalReached(List<ContainerInfo> activeContainers)
        {
            if (activeContainers.Count() >= this.maxConcurrentContainersTotal)
            {
                return true;
            }
            return false;
        }

        public bool MaxConcurrentContainersTotalEqualsLimit(List<ContainerInfo> activeContainers)
        {
            if (activeContainers.Count() == this.maxConcurrentContainersTotal)
            {
                return true;
            }
            return false;
        }
    }
}
