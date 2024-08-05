using Azure.ResourceManager;

namespace ContainerCreator2.Service.Abstract
{
    public interface IArmClientFactory
    {
        ArmClient GetArmClient(string tenantId, string clientId, string clientSecret);
        ArmClient GetArmClient();
    }
}
