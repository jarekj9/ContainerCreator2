namespace ContainerCreator2.Data
{
    public class ContainerRequest
    {
        public Guid Id { get; set; }
        public string OwnerId { get; set; }
        public string DnsNameLabel { get; set; }
        public string UrlToOpenEncoded { get; set; }
        public string RandomPassword { get; set; }
    }
}
