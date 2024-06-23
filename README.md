# ACI container creator 
Azure function, that creates isolated Ubuntu docker container with firefox and noVNC access in predefined resource group.


There are 2 versions:
- ContainerCreator.cs uses simple azure functions
- ContainerCreatorOrchestrated.cs - uses azure durable functions (orchestration, activity + entity to keep state)


## Example url

```
http://localhost:7169/api/CreateContainer?dnsNameLabel=testingjj-08&urlToOpenEncoded=https%3A%2F%2Fgithub.com%2F
```

## Example url (with orchestration)

```
http://localhost:7110/api/CreateContainerInOrchestration?dnsNameLabel=testingjj-02&urlToOpenEncoded=https%3A%2F%2Fgithub.com%2F
```