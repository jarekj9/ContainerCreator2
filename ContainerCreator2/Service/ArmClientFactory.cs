using ContainerCreator2.Service.Abstract;
using Azure.ResourceManager;
using Azure.Identity;

namespace ContainerCreator2.Service
{
    public class ArmClientFactory: IArmClientFactory
    {
        public ArmClientFactory() { }

        public ArmClient GetArmClient(string tenantId, string clientId, string clientSecret)
        {
            var clientSecretCredential = new ClientSecretCredential(tenantId, clientId, clientSecret);
            ArmClient armClient = new ArmClient(clientSecretCredential);
            return armClient;
        }

        public ArmClient GetArmClient()
        {
            ArmClient armClient = new ArmClient(new ManagedIdentityCredential());
            return armClient;
        }
    }
}
