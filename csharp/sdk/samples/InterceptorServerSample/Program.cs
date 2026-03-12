using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Interceptors;
using ModelContextProtocol.Interceptors.Server;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddMcpServer()
    .WithStdioServerTransport()
    .WithInterceptors<SampleInterceptors>();

var app = builder.Build();
await app.RunAsync();
