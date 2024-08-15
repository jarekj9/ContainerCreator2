using Azure.ResourceManager.ContainerInstance;
using ContainerCreator2.Data;
using ContainerCreator2.Service;
using ContainerCreator2.Service.Abstract;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;

namespace ContainerCreator2.Test
{
    public class ContainerManagerServiceTest
    {
        private const string ContainerLifeTimeMinutes = "10";
        private const int OneTimePasswordLength = 15;

        private IContainerManagerService containerManagerService;
        private Mock<IAzureAciService> azureAciServiceMock;

        private ContainerRequest containerRequest;
        private ContainerInfo containerInfoReturnedByAzureAciService;

        public ContainerManagerServiceTest()
        {
            containerRequest = new ContainerRequest()
            {
                Id = Guid.NewGuid(),
                OwnerId = Guid.NewGuid().ToString(),
                DnsNameLabel = "dnsname",
                UrlToOpenEncoded = "https%3A%2F%2Fgithub.com%2F"
            };

            containerInfoReturnedByAzureAciService = new ContainerInfo()
            {
                Id = Guid.NewGuid(),
                ContainerGroupName = "containerGroupName",
                Name = "name",
                Fqdn = "fqdn",
                Ip = "ip",
                Port = 443,
                CreatedTime = DateTime.UtcNow,
                IsDeploymentSuccesful = true
            };
        }

        [SetUp]
        public void Setup()
        {
            var resourceGroupName = "resourceGroupName";
            var loggerMock = new Mock<ILogger<ContainerManagerService>>();
            var configurationMock = new Mock<IConfiguration>();
            configurationMock.Setup(c => c["ContainerImage"]).Returns("container-image");
            configurationMock.Setup(c => c["ResourceGroupName"]).Returns(resourceGroupName);
            configurationMock.Setup(c => c["ContainerPorts"]).Returns("443,80");
            configurationMock.Setup(c => c["ContainerLifeTimeMinutes"]).Returns(ContainerLifeTimeMinutes);
            configurationMock.Setup(c => c["RegistryCredentialsServer"]).Returns("RegistryCredentialsServer");
            configurationMock.Setup(c => c["RegistryCredentialsUserName"]).Returns("RegistryCredentialsUserName");
            configurationMock.Setup(c => c["RegistryCredentialsPassword"]).Returns("RegistryCredentialsPassword");
            configurationMock.Setup(c => c["TenantId"]).Returns("TenantId");
            configurationMock.Setup(c => c["SubscriptionId"]).Returns("SubscriptionId");

            azureAciServiceMock = new Mock<IAzureAciService>();
            azureAciServiceMock.Setup(a => a.CreateOrUpdateAsync(It.IsAny<string>(), It.IsAny<ContainerGroupData>())).ReturnsAsync(containerInfoReturnedByAzureAciService);
            containerManagerService = new ContainerManagerService(loggerMock.Object, configurationMock.Object, azureAciServiceMock.Object);
        }

        [Test]
        public async Task CreateOrUpdateAsync_Sets_Correct_Result_Parameters()
        {
            var containerInfoResult = await containerManagerService.CreateContainer(containerRequest);

            Assert.IsTrue(containerInfoResult.OwnerId.ToString() == containerRequest.OwnerId);
            Assert.IsTrue(containerInfoResult.Image == "container-image");
            Assert.IsTrue(containerInfoResult.RandomPassword.Length == OneTimePasswordLength);
        }

        [Test]
        public async Task CreateOrUpdateAsync_Sets_Basic_Container_EnvVars()
        {
            var containerInfoResult = await containerManagerService.CreateContainer(containerRequest);

            azureAciServiceMock.Verify(m => m.CreateOrUpdateAsync(
                It.Is<string>(s => s.StartsWith("containergroup-")), 
                It.Is<ContainerGroupData>(
                        c => c.IPAddress.DnsNameLabel == containerRequest.DnsNameLabel 
                        && c.Containers.First().EnvironmentVariables.Any(e => e.Name == "MAXMINUTES" && e.Value == ContainerLifeTimeMinutes)
                        && c.Containers.First().EnvironmentVariables.Any(e => e.Name == "URL_TO_OPEN" && e.Value == "https://github.com/")
                        && c.Containers.First().EnvironmentVariables.Any(e => e.Name == "VNCPASS" && e.SecureValue.Length == OneTimePasswordLength)
                    )
                ),
                Times.Once
            );
        }

        [Test]
        public async Task CreateOrUpdateAsync_Uses_Correct_Registry_Credentials()
        {
            var containerInfoResult = await containerManagerService.CreateContainer(containerRequest);

            azureAciServiceMock.Verify(m => m.CreateOrUpdateAsync(
                It.Is<string>(s => s.StartsWith("containergroup-")),
                It.Is<ContainerGroupData>(
                        c => c.ImageRegistryCredentials.First().Username == "RegistryCredentialsUserName"
                        && c.ImageRegistryCredentials.First().Password == "RegistryCredentialsPassword"
                        && c.ImageRegistryCredentials.First().Server == "RegistryCredentialsServer"
                    )
                ),
                Times.Once
            );
        }
    }
}



