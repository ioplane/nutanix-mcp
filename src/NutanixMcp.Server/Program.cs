namespace NutanixMcp.Server;

// The Server assembly is hosted via NutanixMcp.Cli; this entrypoint only exists to
// satisfy the Web SDK. Use `nutanix-mcp serve --transport stdio|http` instead.
internal static class Program
{
    public static int Main(string[] args)
    {
        _ = args;
        Console.Error.WriteLine("Run via: nutanix-mcp serve --transport stdio|http");
        return 0;
    }
}
