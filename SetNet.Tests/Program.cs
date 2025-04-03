// See https://aka.ms/new-console-template for more information

using SetNet.Config;
using SetNet.Tests;

var config = new Configuration();
config.Port = 7777;
config.Host = "127.0.0.1";

MainServer server = new MainServer(config);
server.StartAsync();

Console.ReadLine();

MainClient client = new MainClient(config);
await client.ConnectAsync();

Console.ReadLine();

client.DisconnectFromServer();
server.StopAsync();

