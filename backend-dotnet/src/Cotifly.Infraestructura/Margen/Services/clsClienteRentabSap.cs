using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Cotifly.Infraestructura.Configuracion;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Margen.Services;

/// <summary>
/// Descarga del set completo de <c>RentabClienteSet</c> de SAP TM para la sincronización (HU-042.2, #243, ADR-027): un solo
/// <c>GET {SapTm:Url}/RentabClienteSet?$format=json</c> (más <c>&amp;sap-client=NNN</c> si hay mandante), SIN <c>$filter</c>: SAP lo ignora
/// y siempre devuelve todo (~1500 filas, confirmado por Datos con curl).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Autenticación Basic por petición con <c>SapTm:User</c>/<c>SapTm:Pass</c>; los logs de <c>IHttpClientFactory</c> ocultan las
/// cabeceras (<c>RedactLoggedHeaders</c>).</item>
/// <item><c>Accept-Encoding: gzip</c> con descompresión automática, sin cookies (<see cref="CrearManejadorPrimario"/>).</item>
/// <item><c>SapTm:TimeoutSegundos</c> cubre cabeceras y cuerpo; <c>SapTm:MaximoBytes</c> acota el cuerpo ya descomprimido.</item>
/// <item>Sin reintentos: si falla, la corrida queda en <c>error</c> y el worker vuelve a intentar en el chequeo siguiente.</item>
/// <item>Cualquier fallo sale como <see cref="clsSincronizacionMargenSapException"/> con un motivo corto, sin la URL ni las credenciales
/// (el detalle técnico queda en Debug), también un corte a mitad del cuerpo o un gzip corrupto.</item>
/// </list>
/// </remarks>
public sealed class clsClienteRentabSap(
    IHttpClientFactory fabrica,
    IOptions<clsOpcionesSapTmSincronizacion> opciones,
    ILogger<clsClienteRentabSap> logger)
{
    /// <summary>Nombre del cliente de <c>IHttpClientFactory</c> (las pruebas le cambian el manejador primario).</summary>
    public const string NombreCliente = "SapTmRentab";

    /// <summary>Conjunto de entidades de rentabilidad por cliente.</summary>
    public const string ConjuntoEntidades = "RentabClienteSet";

    /// <summary>Manejador primario: descompresión gzip/deflate automática y sin cookies (SAP abre sesión con cookies; no se reutiliza).</summary>
    public static HttpMessageHandler CrearManejadorPrimario() => new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        UseCookies = false,
        AllowAutoRedirect = false,
    };

    /// <summary>
    /// URL de la descarga: <c>{url}/RentabClienteSet?$format=json[&amp;sap-client=NNN]</c>, sin <c>$filter</c>. Una <paramref name="url"/> que ya
    /// termina en <c>/RentabClienteSet</c> no lo duplica.
    /// </summary>
    public static Uri ConstruirUrl(string url, string? sapClient)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        var mandante = string.IsNullOrWhiteSpace(sapClient) ? string.Empty : $"&sap-client={Uri.EscapeDataString(sapClient.Trim())}";
        return new Uri($"{clsOpcionesSapTmSincronizacion.NormalizarUrl(url)}/{ConjuntoEntidades}?$format=json{mandante}", UriKind.Absolute);
    }

    /// <summary>Descarga y valida el set completo (ver <see cref="clsLectorRentabClienteSap"/>).</summary>
    /// <exception cref="clsSincronizacionMargenSapException">SAP no respondió, rechazó las credenciales o respondió algo inválido.</exception>
    public async Task<IReadOnlyList<clsFilaMargenSap>> DescargarAsync(CancellationToken cancellationToken = default)
    {
        var valor = opciones.Value;
        if (!valor.Configurada)
        {
            throw new clsSincronizacionMargenSapException(
                valor.ErrorConfiguracion ?? "La sincronización con SAP TM no está configurada (SapTm:Url, SapTm:User, SapTm:Pass).");
        }

        var limite = TimeSpan.FromSeconds(valor.TimeoutSegundos);
        using var tiempo = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        tiempo.CancelAfter(limite);

        using var peticion = new HttpRequestMessage(HttpMethod.Get, ConstruirUrl(valor.UrlServicio!, valor.SapClientNormalizado));
        peticion.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        peticion.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        peticion.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{valor.User}:{valor.Pass}")));

        try
        {
            var http = fabrica.CreateClient(NombreCliente);
            using var respuesta = await http.SendAsync(peticion, HttpCompletionOption.ResponseHeadersRead, tiempo.Token);
            if (respuesta.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw Fallo(string.Create(CultureInfo.InvariantCulture,
                    $"SAP TM rechazó las credenciales de SapTm:User/SapTm:Pass (HTTP {(int)respuesta.StatusCode})."));
            }

            if (!respuesta.IsSuccessStatusCode)
            {
                throw Fallo(string.Create(CultureInfo.InvariantCulture, $"SAP TM respondió HTTP {(int)respuesta.StatusCode} ({respuesta.StatusCode})."));
            }

            await using var cuerpo = await respuesta.Content.ReadAsStreamAsync(tiempo.Token);
            await using var acotado = new clsFlujoAcotado(cuerpo, valor.MaximoBytes);
            return await clsLectorRentabClienteSap.LeerAsync(acotado, tiempo.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // la canceló quien llama (apagado de la API)
        }
        catch (OperationCanceledException ex)
        {
            throw Fallo(string.Create(CultureInfo.InvariantCulture, $"SAP TM no respondió en {valor.TimeoutSegundos} s (SapTm:TimeoutSegundos)."), ex);
        }
        catch (HttpRequestException ex)
        {
            throw Fallo("No se pudo conectar con SAP TM (error de red o de DNS).", ex);
        }
        catch (clsRespuestaDemasiadoGrandeException ex)
        {
            throw Fallo(string.Create(CultureInfo.InvariantCulture,
                $"La respuesta de SAP TM supera SapTm:MaximoBytes ({valor.MaximoBytes} bytes descomprimidos)."), ex);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            // Conexión cortada a mitad del cuerpo (HttpIOException) o gzip corrupto, como en clsCubicadorHttp y clsCosteoNexo.
            throw Fallo("SAP TM cortó la respuesta o la mandó corrupta (conexión interrumpida o gzip inválido).", ex);
        }
    }

    private clsSincronizacionMargenSapException Fallo(string mensaje, Exception? causa = null)
    {
        if (causa is not null)
        {
            logger.LogDebug(causa, "Detalle del fallo descargando RentabClienteSet de SAP TM.");
        }

        return causa is null ? new clsSincronizacionMargenSapException(mensaje) : new clsSincronizacionMargenSapException(mensaje, causa);
    }

    /// <summary>El cuerpo pasó el tope de bytes.</summary>
    private sealed class clsRespuestaDemasiadoGrandeException : IOException
    {
        public clsRespuestaDemasiadoGrandeException()
            : base("La respuesta supera el tope de bytes.")
        {
        }
    }

    /// <summary>Flujo de solo lectura que lanza al pasar de <c>maximo</c> bytes leídos.</summary>
    private sealed class clsFlujoAcotado(Stream interno, long maximo) : Stream
    {
        private long _leidos;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _leidos;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Contar(interno.Read(buffer, offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Contar(await interno.ReadAsync(buffer, cancellationToken));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                interno.Dispose();
            }

            base.Dispose(disposing);
        }

        private int Contar(int leidos)
        {
            _leidos += leidos;
            return _leidos > maximo ? throw new clsRespuestaDemasiadoGrandeException() : leidos;
        }
    }
}
