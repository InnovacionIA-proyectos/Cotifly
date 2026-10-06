using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Cotifly.Pruebas.Comun;

/// <summary>
/// Servidor SMTP falso en proceso (127.0.0.1, puerto libre, sin red externa) en el papel del relay interno: ESMTP sin AUTH
/// ni STARTTLS, como el relay de Botero Soto. Guarda el envelope (<c>MAIL FROM</c>, <c>RCPT TO</c>) y los bytes del mensaje
/// (<c>DATA</c>). Con <see cref="RechazarDestinatarios"/> responde <c>550 5.7.1 Unable to relay</c> a cada <c>RCPT TO</c>.
/// </summary>
public sealed class clsServidorSmtpFalso : IAsyncDisposable
{
    private readonly TcpListener _escucha = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cancelacion = new();
    private readonly ConcurrentQueue<clsCorreoRecibido> _recibidos = new();
    private readonly Task _bucle;

    public clsServidorSmtpFalso()
    {
        _escucha.Start();
        Puerto = ((IPEndPoint)_escucha.LocalEndpoint).Port;
        _bucle = AceptarAsync(_cancelacion.Token);
    }

    public const string Host = "127.0.0.1";

    public int Puerto { get; }

    /// <summary>Si es true, cada <c>RCPT TO</c> recibe <c>550 5.7.1 Unable to relay</c>.</summary>
    public bool RechazarDestinatarios { get; set; }

    /// <summary>Correos aceptados (terminaron <c>DATA</c>), en orden.</summary>
    public IReadOnlyList<clsCorreoRecibido> Recibidos => [.. _recibidos];

    public async ValueTask DisposeAsync()
    {
        await _cancelacion.CancelAsync();
        _escucha.Stop();
        try
        {
            await _bucle;
        }
        catch (OperationCanceledException)
        {
        }

        _cancelacion.Dispose();
    }

    private async Task AceptarAsync(CancellationToken cancelacion)
    {
        while (!cancelacion.IsCancellationRequested)
        {
            TcpClient cliente;
            try
            {
                cliente = await _escucha.AcceptTcpClientAsync(cancelacion);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _ = AtenderAsync(cliente, cancelacion);
        }
    }

    private async Task AtenderAsync(TcpClient cliente, CancellationToken cancelacion)
    {
        using (cliente)
        {
            try
            {
                var flujo = cliente.GetStream();
                // Latin-1 conserva cada byte tal cual (el mensaje puede traer UTF-8 en 8bit).
                using var lector = new StreamReader(flujo, Encoding.Latin1, false, 4096, leaveOpen: true);
                await using var escritor = new StreamWriter(flujo, Encoding.Latin1, 4096, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };

                await escritor.WriteLineAsync("220 relay-falso ESMTP");
                string? remitente = null;
                var destinatarios = new List<string>();
                while (await lector.ReadLineAsync(cancelacion) is { } linea)
                {
                    var comando = linea.Length >= 4 ? linea[..4].ToUpperInvariant() : linea.ToUpperInvariant();
                    switch (comando)
                    {
                        case "EHLO":
                            await escritor.WriteLineAsync("250-relay-falso");
                            await escritor.WriteLineAsync("250 8BITMIME");
                            break;
                        case "HELO":
                            await escritor.WriteLineAsync("250 relay-falso");
                            break;
                        case "MAIL":
                            remitente = Direccion(linea);
                            destinatarios.Clear();
                            await escritor.WriteLineAsync("250 2.1.0 OK");
                            break;
                        case "RCPT":
                            if (RechazarDestinatarios)
                            {
                                await escritor.WriteLineAsync("550 5.7.1 Unable to relay");
                            }
                            else
                            {
                                destinatarios.Add(Direccion(linea));
                                await escritor.WriteLineAsync("250 2.1.5 OK");
                            }

                            break;
                        case "DATA":
                            await escritor.WriteLineAsync("354 Start mail input; end with <CRLF>.<CRLF>");
                            var datos = new StringBuilder();
                            while (await lector.ReadLineAsync(cancelacion) is { } renglon && renglon != ".")
                            {
                                datos.Append(renglon.StartsWith("..", StringComparison.Ordinal) ? renglon[1..] : renglon).Append("\r\n");
                            }

                            _recibidos.Enqueue(new clsCorreoRecibido(remitente, [.. destinatarios], Encoding.Latin1.GetBytes(datos.ToString())));
                            await escritor.WriteLineAsync("250 2.0.0 Queued");
                            break;
                        case "RSET":
                            remitente = null;
                            destinatarios.Clear();
                            await escritor.WriteLineAsync("250 2.0.0 OK");
                            break;
                        case "NOOP":
                            await escritor.WriteLineAsync("250 2.0.0 OK");
                            break;
                        case "QUIT":
                            await escritor.WriteLineAsync("221 2.0.0 Bye");
                            return;
                        default:
                            await escritor.WriteLineAsync("502 5.5.2 Command not implemented");
                            break;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
            {
                // Conexión cerrada por el cliente o servidor detenido.
            }
        }
    }

    private static string Direccion(string linea)
    {
        var inicio = linea.IndexOf('<', StringComparison.Ordinal);
        var fin = linea.IndexOf('>', StringComparison.Ordinal);
        return inicio >= 0 && fin > inicio ? linea[(inicio + 1)..fin] : linea[(linea.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim();
    }
}

/// <summary>Correo recibido por <see cref="clsServidorSmtpFalso"/>: envelope y bytes del mensaje (RFC 5322).</summary>
public sealed record clsCorreoRecibido(string? Remitente, IReadOnlyList<string> Destinatarios, byte[] Datos);
