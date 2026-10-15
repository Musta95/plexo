using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

internal static class Program
{
    private const int IpUnicastIf = 31;
    private const int MaxHeader = 4096;
    private record RouteRequest(string Token, string RemoteAddress, int RemotePort, string LocalAddress, int InterfaceIndex);
    private static async Task Main(string[] args)
    {
        if (args.Length != 1 || args[0].Length < 32) throw new ArgumentException("Session token required");
        using var shutdown = new CancellationTokenSource();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        Console.WriteLine($"READY {((IPEndPoint)listener.LocalEndpoint).Port}");
        Console.Out.Flush();
        while (true)
        {
            var client = await listener.AcceptTcpClientAsync(shutdown.Token);
            _ = Handle(client, args[0]);
        }
    }

    private static async Task Handle(TcpClient client, string token)
    {
        using (client)
        {
            try
            {
                using var inbound = client.GetStream();
                using var header = new MemoryStream();
                var one = new byte[1];
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (header.Length < MaxHeader)
                {
                    if (await inbound.ReadAsync(one, timeout.Token) == 0) return;
                    if (one[0] == (byte)'\n') break;
                    header.WriteByte(one[0]);
                }
                if (header.Length >= MaxHeader) return;
                var request = JsonSerializer.Deserialize<RouteRequest>(header.ToArray(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (request == null || request.Token != token) return;
                if (request.RemotePort is < 1 or > 65535 || request.InterfaceIndex < 1) return;
                var remote = IPAddress.Parse(request.RemoteAddress);
                var local = IPAddress.Parse(request.LocalAddress);
                if (remote.AddressFamily != AddressFamily.InterNetwork || local.AddressFamily != AddressFamily.InterNetwork) return;
                using var outbound = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                outbound.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)IpUnicastIf, IPAddress.HostToNetworkOrder(request.InterfaceIndex));
                outbound.Bind(new IPEndPoint(local, 0));
                await outbound.ConnectAsync(new IPEndPoint(remote, request.RemotePort), timeout.Token);
                await inbound.WriteAsync(Encoding.ASCII.GetBytes("OK\n"), timeout.Token);
                using var outgoing = new NetworkStream(outbound, ownsSocket: false);
                var upload = inbound.CopyToAsync(outgoing);
                var download = outgoing.CopyToAsync(inbound);
                await Task.WhenAny(upload, download);
            }
            catch (Exception) { /* Socket failure closes only this tunnel. */ }
        }
    }
}
