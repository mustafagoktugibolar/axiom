namespace Gateway.Routing;

public sealed class Router(IReadOnlyDictionary<string, string> routes)
{
    public string Resolve(string path) =>
        routes.FirstOrDefault(r => path.StartsWith(r.Key, StringComparison.Ordinal)).Value ?? "http://not-found";
}
