namespace ContainerCreator2.Data
{
    public class ContainerInfo
    {
        public string ContainerGroupName { get; set; }
        public string Image { get; set; }
        public string Name { get; set; }
        public string Fqdn { get; set; }
        public string Ip { get; set; }
        public int Port { get; set; }
        public Guid OwnerId { get; set; }
    }
}
