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
        private IContainerManagerService containerManagerService;
        private Mock<IAzureAciService> azureAciServiceMock;

        [SetUp]
        public void Setup()
        {
            var resourceGroupName = "resourceGroupName";
            var loggerMock = new Mock<ILogger<ContainerManagerService>>();
            var configurationMock = new Mock<IConfiguration>();
            configurationMock.Setup(c => c["ContainerImage"]).Returns("container-image");
            configurationMock.Setup(c => c["ResourceGroupName"]).Returns(resourceGroupName);
            configurationMock.Setup(c => c["ContainerPorts"]).Returns("443,80");
            configurationMock.Setup(c => c["ContainerLifeTimeMinutes"]).Returns("10");
            configurationMock.Setup(c => c["RegistryCredentialsServer"]).Returns("RegistryCredentialsServer");
            configurationMock.Setup(c => c["RegistryCredentialsUserName"]).Returns("RegistryCredentialsUserName");
            configurationMock.Setup(c => c["RegistryCredentialsPassword"]).Returns("");
            configurationMock.Setup(c => c["TenantId"]).Returns("TenantId");
            configurationMock.Setup(c => c["SubscriptionId"]).Returns("SubscriptionId");

            azureAciServiceMock = new Mock<IAzureAciService>();
            containerManagerService = new ContainerManagerService(loggerMock.Object, configurationMock.Object, azureAciServiceMock.Object);
        }

        [Test]
        public async Task TestCreateOrUpdateAsync()
        {
            var containerInfo = new ContainerInfo()
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
            azureAciServiceMock.Setup(a => a.CreateOrUpdateAsync(It.IsAny<string>(), It.IsAny<ContainerGroupData>())).ReturnsAsync(containerInfo);

            var containerRequest = new ContainerRequest()
            {
                Id = Guid.NewGuid(),
                OwnerId = Guid.NewGuid().ToString(),
                DnsNameLabel = "dnsname",
            };
           
            var containerInfoResult = await containerManagerService.CreateContainer(containerRequest);

            Assert.IsTrue(containerInfoResult.OwnerId.ToString() == containerRequest.OwnerId);
            Assert.IsTrue(containerInfoResult.Image == "container-image");
            Assert.IsTrue(containerInfoResult.RandomPassword.Length == 15);
            azureAciServiceMock.Verify(m => m.CreateOrUpdateAsync(
                It.Is<string>(s => s.StartsWith("containergroup-")), 
                It.Is<ContainerGroupData>(c => c.IPAddress.DnsNameLabel == containerRequest.DnsNameLabel)),
                Times.Once
            );
        }
    }
}



