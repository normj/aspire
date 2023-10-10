using Aspire.Hosting.Azure;
using Aspire.Hosting.Postgres;
using Aspire.Hosting.Redis;
using Projects = AppHost.Projects;

var builder = DistributedApplication.CreateBuilder(args);

builder.AddAzureProvisioning();

var grafana = builder.AddContainer("grafana", "grafana/grafana")
       .WithServiceBinding(containerPort: 3000, name: "grafana-http", scheme: "http");

var catalogdb = builder.AddPostgresContainer("postgres").AddDatabase("catalog");

var redis = builder.AddRedisContainer("basketCache");

var catalog = builder.AddProject<Projects.CatalogService>()
                     .WithPostgres(catalogdb)
                     .WithReplicas(2);

var serviceBus = builder.AddAzureServiceBus("messaging", queueNames: ["orders"]);

var basket = builder.AddProject<Projects.BasketService>()
                    .WithRedis(redis)
                    .WithReference(serviceBus, optional: true);

builder.AddProject<Projects.MyFrontend>()
       .WithServiceReference(basket)
       .WithServiceReference(catalog, bindingName: "http")
       .WithEnvironment("GRAFANA_URL", () => grafana.GetEndpoint("grafana-http")?.UriString ?? $"{{{grafana.Component.Name}.bindings.grafana-http}}");

builder.AddProject<Projects.OrderProcessor>()
       .WithAzureServiceBus(serviceBus)
       .WithLaunchProfile("OrderProcessor");

builder.AddProject<Projects.ApiGateway>()
       .WithServiceReference(basket)
       .WithServiceReference(catalog);

builder.AddContainer("prometheus", "prom/prometheus")
       .WithVolumeMount("../prometheus", "/etc/prometheus")
       .WithServiceBinding(9090);

builder.Build().Run();
