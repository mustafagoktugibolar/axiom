using Gateway.Routing;

// Transport-level routing only: the gateway forwards by path prefix and never looks at domain state.
var router = new Router(new Dictionary<string, string>
{
    ["/orders"] = "http://orders-service",
    ["/homepage"] = "http://homepage-service",
});

Console.WriteLine(router.Resolve("/orders/42"));
