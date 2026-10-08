using System.Net;
using System.Net.Sockets;
using Microsoft.Data.SqlClient;

namespace Cotifly.Pruebas.Comun;

/// <summary>
/// Proxy TCP de pruebas hacia el SQL Server del contenedor (#125). Con <see cref="Congelar"/> deja de reenviar en ambos sentidos
/// pero mantiene vivas las conexiones TCP: simula un servidor que no responde sin cortar la conexión. Si el cliente cierra su
/// lado, el proxy cierra el del servidor (así SQL Server ve la desconexión y deshace la transacción de esa sesión).
/// </summary>
public sealed class clsProxyTcpCongelable : IAsyncDisposable
{
    private readonly TcpListener _escucha = new(IPAddress.Loopback, 0);
    private readonly string _hostDestino;
    private readonly int _puertoDestino;
    private readonly CancellationTokenSource _fin = new();
    private readonly List<TcpClient> _sockets = [];
    private readonly Task _aceptar;
    private volatile bool _congelado;

    /// <param name="cadenaDestino">Cadena de conexión al servidor real; su <c>Data Source</c> es <c>host,puerto</c>.</param>
    public clsProxyTcpCongelable(string cadenaDestino)
    {
        var origen = new SqlConnectionStringBuilder(cadenaDestino).DataSource.Replace("tcp:", string.Empty, StringComparison.OrdinalIgnoreCase);
        var partes = origen.Split(',');
        _hostDestino = partes[0];
        _puertoDestino = partes.Length > 1 ? int.Parse(partes[1], System.Globalization.CultureInfo.InvariantCulture) : 1433;
        _escucha.Start();
        _aceptar = AceptarAsync();
    }

    /// <summary>Puerto local del proxy.</summary>
    public int Puerto => ((IPEndPoint)_escucha.LocalEndpoint).Port;

    /// <summary><paramref name="cadena"/> apuntando al proxy, sin pool.</summary>
    public string Cadena(string cadena) =>
        new SqlConnectionStringBuilder(cadena) { DataSource = $"127.0.0.1,{Puerto}", Pooling = false }.ConnectionString;

    /// <summary>Deja de reenviar tráfico (lo que llega se descarta); las conexiones TCP siguen abiertas.</summary>
    public void Congelar() => _congelado = true;

    public async ValueTask DisposeAsync()
    {
        await _fin.CancelAsync();
        _escucha.Stop();
        lock (_sockets)
        {
            foreach (var socket in _sockets)
            {
                socket.Dispose();
            }
        }

        try
        {
            await _aceptar;
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
            // Fin del proxy.
        }

        _fin.Dispose();
    }

    private async Task AceptarAsync()
    {
        while (!_fin.IsCancellationRequested)
        {
            var cliente = await _escucha.AcceptTcpClientAsync(_fin.Token);
            var servidor = new TcpClient();
            await servidor.ConnectAsync(_hostDestino, _puertoDestino, _fin.Token);
            lock (_sockets)
            {
                _sockets.Add(cliente);
                _sockets.Add(servidor);
            }

            _ = BombearAsync(cliente, servidor);
            _ = BombearAsync(servidor, cliente);
        }
    }

    private async Task BombearAsync(TcpClient desde, TcpClient hacia)
    {
        var bufer = new byte[16 * 1024];
        try
        {
            var entrada = desde.GetStream();
            var salida = hacia.GetStream();
            while (true)
            {
                var leidos = await entrada.ReadAsync(bufer, _fin.Token);
                if (leidos == 0)
                {
                    break;
                }

                if (!_congelado)
                {
                    await salida.WriteAsync(bufer.AsMemory(0, leidos), _fin.Token);
                }
            }
        }
#pragma warning disable CA1031 // Cualquier error de red termina el par de conexiones.
        catch (Exception)
#pragma warning restore CA1031
        {
            // Conexión cerrada o proxy detenido.
        }

        desde.Dispose();
        hacia.Dispose();
    }
}
