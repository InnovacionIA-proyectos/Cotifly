using System.Net;
using System.Text;
using System.Text.Json;
using Cotifly.Aplicacion.Margen.Interfaces;
using Cotifly.Infraestructura.Configuracion;
using Cotifly.Infraestructura.Margen.Services;
using Cotifly.Infraestructura.Tests.Configuracion;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cotifly.Infraestructura.Tests.Margen;

/// <summary>
/// HU-042.2 (#243, ADR-027): piezas sin base de la sincronización del margen de SAP TM: lectura de <c>RentabClienteSet</c>, guarda de mínimo
/// de filas, próxima corrida estimada, opciones, descarga (con el SAP falso) y el worker sin configuración. La corrida contra SQL Server está
/// en <c>clsSincronizacionMargenSapIntegracionTests</c>.
/// </summary>
public sealed class clsSincronizacionMargenSapTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void ElLectorNormalizaRedondeaYConservaLosCerosDelBp()
    {
        var json = clsSapRentabFalso.Cuerpo(
        [
            (" 50000005 ", "optn", " 0000015927 ", "13.592356781"),
            ("50000005", "OPIM", "777", "6.50005"),
            ("50000005", "OPEX", "880", "-19.2"),
        ]);

        var filas = Leer(json);

        filas.Should().Equal(
            new clsFilaMargenSap("50000005", "OPTN", "0000015927", 13.5924m),
            new clsFilaMargenSap("50000005", "OPIM", "777", 6.5001m),
            new clsFilaMargenSap("50000005", "OPEX", "880", -19.2m));
    }

    [Fact]
    public void ElValueTambienPuedeVenirComoNumero()
    {
        var filas = Leer("""{"d":{"results":[{"SalesOrgId":"50000005","TrqType":"OPTN","OrderPartyId":"1","Value":21.25}]}}""");

        filas.Should().ContainSingle().Which.Margen.Should().Be(21.25m);
    }

    [Theory]
    [InlineData("""{"d":{"results":[{"TrqType":"OPTN","OrderPartyId":"1","Value":"1"}]}}""", "no trae 'SalesOrgId' de texto")]
    [InlineData("""{"d":{"results":[{"SalesOrgId":"5","TrqType":"OPTN","OrderPartyId":"1"}]}}""", "no trae 'Value'")]
    [InlineData("""{"d":{"results":[{"SalesOrgId":"5","TrqType":"OPTN","OrderPartyId":"  ","Value":"1"}]}}""", "trae 'OrderPartyId' vacío")]
    [InlineData("""{"d":{"results":[{"SalesOrgId":"5","TrqType":"OPTN","OrderPartyId":1,"Value":"1"}]}}""", "no trae 'OrderPartyId' de texto")]
    [InlineData("""{"d":{"results":[{"SalesOrgId":"5","TrqType":"OPTN","OrderPartyId":"1","Value":"N/A"}]}}""", "trae 'Value' no numérico")]
    [InlineData("""{"d":{"results":[{"SalesOrgId":"5","TrqType":"OPTN","OrderPartyId":"1","Value":"13,5"}]}}""", "trae 'Value' no numérico")]
    [InlineData("""{"d":{"results":[{"SalesOrgId":"5","TrqType":"OPTN","OrderPartyId":"1","Value":"100000"}]}}""", "fuera del rango")]
    [InlineData("""{"d":{"results":[{"SalesOrgId":"5","TrqType":"OPTNXXXXX","OrderPartyId":"1","Value":"1"}]}}""", "más de 8 caracteres")]
    [InlineData("""{"d":{"results":["x"]}}""", "no es un objeto")]
    [InlineData("""{"d":{"results":[]}}""", "0 registros")]
    [InlineData("""{"d":{}}""", "no tiene la forma")]
    [InlineData("""[]""", "no tiene la forma")]
    public void UnaFilaInvalidaDescartaTodoElSet(string json, string motivo)
    {
        var leer = () => Leer(json);

        leer.Should().Throw<clsSincronizacionMargenSapException>().WithMessage($"*{motivo}*");
    }

    [Fact]
    public void UnaClaveRepetidaSinDistinguirMayusculasDescartaElSet()
    {
        var json = clsSapRentabFalso.Cuerpo([("50000005", "OPTN", "abc1", "1"), ("50000005", "optn", "ABC1", "2")]);

        var leer = () => Leer(json);

        leer.Should().Throw<clsSincronizacionMargenSapException>().WithMessage("*repitió la combinación*fila 2*");
    }

    [Fact]
    public async Task UnJsonInvalidoEsUnErrorDeSincronizacion()
    {
        await using var flujo = new MemoryStream(Encoding.UTF8.GetBytes("{\"d\": {\"results\": ["));

        var leer = () => clsLectorRentabClienteSap.LeerAsync(flujo, Ct);

        await leer.Should().ThrowAsync<clsSincronizacionMargenSapException>().WithMessage("*JSON inválido*");
    }

    [Theory]
    [InlineData(100, 80, 0, 100)]
    [InlineData(100, 80, 100, 100)]
    [InlineData(100, 80, 1500, 1200)]
    [InlineData(100, 80, 1501, 1201)] // ceil(1200,8)
    [InlineData(10, 0, 5000, 10)]
    [InlineData(1, 100, 7, 7)]
    public void ElMinimoEsElMayorEntreElAbsolutoYElPorcentajeDeLasActivas(int minimo, int porcentaje, int activas, int esperado)
    {
        clsGuardaMinimoFilasSap.MinimoExigido(minimo, porcentaje, activas).Should().Be(esperado);
    }

    [Fact]
    public void ElRechazoDiceCuantasLlegaronYCuantasSeExigian()
    {
        clsGuardaMinimoFilasSap.Rechazo(1200, 100, 80, 1500).Should().BeNull();
        clsGuardaMinimoFilasSap.Rechazo(1199, 100, 80, 1500).Should()
            .Be("SAP TM devolvió 1199 filas y el mínimo exigido es 1200 (máximo entre SapTm:MinimoFilas = 100 y el 80 % de las 1500 filas activas): no se tocó la tabla.");
    }

    [Fact]
    public void LaProximaCorridaEsElPrimerChequeoDespuesDeQueVenceElIntervalo()
    {
        var ahora = new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
        var hora = TimeSpan.FromHours(1);
        var dia = TimeSpan.FromDays(1);

        // Vence en 23 h 30 min; chequeos a las 12:20, 13:20... → el de las 12:20 del día siguiente.
        clsCoordinacionSincronizacionMargenSap.ProximaCorridaEstimada(ahora.AddMinutes(-30), dia, ahora.AddMinutes(20), hora, ahora)
            .Should().Be(ahora.AddDays(1).AddMinutes(20));
        // Sin corrida ok: el próximo chequeo.
        clsCoordinacionSincronizacionMargenSap.ProximaCorridaEstimada(null, dia, ahora.AddMinutes(20), hora, ahora).Should().Be(ahora.AddMinutes(20));
        // Vencida hace rato: el próximo chequeo.
        clsCoordinacionSincronizacionMargenSap.ProximaCorridaEstimada(ahora.AddDays(-3), dia, ahora.AddMinutes(5), hora, ahora).Should().Be(ahora.AddMinutes(5));
        // Sin worker: cuando vence, o ya.
        clsCoordinacionSincronizacionMargenSap.ProximaCorridaEstimada(ahora.AddHours(-1), dia, null, hora, ahora).Should().Be(ahora.AddHours(23));
        clsCoordinacionSincronizacionMargenSap.ProximaCorridaEstimada(null, dia, null, hora, ahora).Should().Be(ahora);
    }

    [Theory]
    [InlineData(null, null, null, false)]
    [InlineData("http://sap/odata", "u", null, false)]
    [InlineData("http://sap/odata", null, "clave-123456", false)]
    [InlineData(" ", "u", "clave-123456", false)]
    [InlineData("http://sap/odata", "u", "clave-123456", true)]
    public void SoloConUrlUsuarioYClaveQuedaConfigurada(string? url, string? usuario, string? clave, bool configurada)
    {
        var opciones = new clsOpcionesSapTmSincronizacion { SincronizacionHabilitada = true, Url = url, User = usuario, Pass = clave };

        opciones.Configurada.Should().Be(configurada);
        opciones.ErrorConfiguracion.Should().BeNull("que falte algo deja el worker inactivo, sin error de configuración");
    }

    [Theory]
    [InlineData("sap-tm-prd:8000/sap", null, "http o https absoluta")]
    [InlineData("ftp://sap/odata", null, "http o https absoluta")]
    [InlineData("http://usuario:clave@sap/odata", null, "no puede llevar usuario")]
    [InlineData("http://sap/odata?$filter=x", null, "sin consulta")]
    [InlineData("http://sap/odata", "10", "3 dígitos")]
    [InlineData("http://sap/odata", "abc", "3 dígitos")]
    public void UnaUrlOMandanteMalEscritosDejanLaSincronizacionSinConfigurarSinImpedirArrancar(string url, string? sapClient, string mensaje)
    {
        var opciones = new clsOpcionesSapTmSincronizacion { SincronizacionHabilitada = true, Url = url, User = "u", Pass = "clave-123456", SapClient = sapClient };

        opciones.TieneCredenciales.Should().BeTrue();
        opciones.Configurada.Should().BeFalse();
        opciones.ErrorConfiguracion.Should().Contain(mensaje).And.NotContain("clave-123456").And.NotContain("usuario:");
        using var proveedor = Proveedor(new clsSapRentabFalso(), new() { ["SapTm:Url"] = url, ["SapTm:SapClient"] = sapClient });
        var leer = () => proveedor.GetRequiredService<IOptions<clsOpcionesSapTmSincronizacion>>().Value;
        leer.Should().NotThrow("la URL de SAP no impide arrancar la API: el margen no depende de SAP al cotizar");
    }

    [Theory]
    [InlineData("http://sap/odata/RentabClienteSet", "http://sap/odata")]
    [InlineData(" http://sap/odata/rentabclienteset/ ", "http://sap/odata")]
    [InlineData("http://sap/odata/", "http://sap/odata")]
    public void UnaUrlConRentabClienteSetSeAceptaYSeNormaliza(string url, string servicio)
    {
        var opciones = new clsOpcionesSapTmSincronizacion { SincronizacionHabilitada = true, Url = url, User = "u", Pass = "clave-123456" };

        opciones.Configurada.Should().BeTrue();
        opciones.UrlServicio.Should().Be(servicio);
        clsClienteRentabSap.ConstruirUrl(url, null).AbsoluteUri.Should().Be($"{servicio}/RentabClienteSet?$format=json");
    }

    [Fact]
    public void LosValoresPorDefectoSonLosDelPlan()
    {
        var opciones = new clsOpcionesSapTmSincronizacion();

        (opciones.ChequeoMinutos, opciones.SincronizacionMinutos, opciones.TimeoutSegundos, opciones.MaximoBytes, opciones.MinimoFilas,
            opciones.MinimoPorcentajeActivas).Should().Be((60, 1440, 120, 20L * 1024 * 1024, 100, 80));
    }

    [Theory]
    [InlineData("http://sap:8000/sap/opu/odata/sap/ZGW_RENTAB_CLI_SRV/", null, "http://sap:8000/sap/opu/odata/sap/ZGW_RENTAB_CLI_SRV/RentabClienteSet?$format=json")]
    [InlineData("https://sap/srv", " 100 ", "https://sap/srv/RentabClienteSet?$format=json&sap-client=100")]
    public void LaUrlDeLaDescargaNoLlevaFilter(string url, string? sapClient, string esperada)
    {
        clsClienteRentabSap.ConstruirUrl(url, sapClient).AbsoluteUri.Should().Be(esperada);
    }

    [Fact]
    public void ElManejadorPrimarioDescomprimeGzipYNoGuardaCookies()
    {
        using var manejador = clsClienteRentabSap.CrearManejadorPrimario();

        var sockets = manejador.Should().BeOfType<SocketsHttpHandler>().Subject;
        sockets.AutomaticDecompression.Should().HaveFlag(DecompressionMethods.GZip);
        sockets.UseCookies.Should().BeFalse();
    }

    [Fact]
    public async Task LaDescargaPideElSetCompletoConBasicYGzipAunqueSapIgnoreElFilter()
    {
        var sap = new clsSapRentabFalso().ConFilas(clsSapRentabFalso.Set("1", 3));
        await using var proveedor = Proveedor(sap);

        var filas = await proveedor.GetRequiredService<clsClienteRentabSap>().DescargarAsync(Ct);

        filas.Should().HaveCount(3);
        var peticion = sap.Peticiones.Should().ContainSingle().Subject;
        peticion.Url.AbsoluteUri.Should().Be($"{clsSapRentabFalso.Url}/RentabClienteSet?$format=json");
        peticion.Autorizacion.Should().Be("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clsSapRentabFalso.Usuario}:{clsSapRentabFalso.Clave}")));
        peticion.AcceptEncoding.Should().Be("gzip");
    }

    [Fact]
    public async Task UnaRespuestaMasGrandeQueElTopeSeRechaza()
    {
        var grande = JsonSerializer.Serialize(new { d = new { results = Enumerable.Range(0, 30_000).Select(i => new { SalesOrgId = "50000005", TrqType = "OPTN", OrderPartyId = $"{i}", Value = "1.5" }) } });
        var sap = new clsSapRentabFalso().Responder(HttpStatusCode.OK, grande);
        await using var proveedor = Proveedor(sap, new() { ["SapTm:MaximoBytes"] = (1024 * 1024).ToString(System.Globalization.CultureInfo.InvariantCulture) });

        var descargar = () => proveedor.GetRequiredService<clsClienteRentabSap>().DescargarAsync(Ct);

        Encoding.UTF8.GetByteCount(grande).Should().BeGreaterThan(1024 * 1024);
        await descargar.Should().ThrowAsync<clsSincronizacionMargenSapException>().WithMessage("*supera SapTm:MaximoBytes*");
    }

    [Theory]
    [InlineData("corte")]
    [InlineData("gzip")]
    public async Task UnCorteAMitadDelCuerpoOUnGzipCorruptoEsUnErrorDeSincronizacion(string caso)
    {
        // H2 (#243): antes la IOException/InvalidDataException escapaba de la sincronización (sin fila 'error' y 500 en el POST).
        Exception error = caso == "corte"
            ? new HttpIOException(HttpRequestError.ResponseEnded, "The response ended prematurely.")
            : new InvalidDataException("The archive entry was compressed using an unsupported compression method.");
        var inicio = Encoding.UTF8.GetBytes("{\"d\":{\"results\":[{\"SalesOrgId\":\"50000005\",");
        await using var proveedor = Proveedor(new clsSapRentabFalso().ResponderFlujo(() => new clsFlujoQueFalla(inicio, error)));

        var descargar = () => proveedor.GetRequiredService<clsClienteRentabSap>().DescargarAsync(Ct);

        await descargar.Should().ThrowAsync<clsSincronizacionMargenSapException>().WithMessage("*SAP TM cortó la respuesta*");
    }

    [Fact]
    public async Task ConLaUrlMalEscritaElWorkerNoCorreYLoAnotaEnError()
    {
        var logger = new clsLoggerFalso<clsWorkerSincronizacionMargenSap>();
        await using var proveedor = Proveedor(new clsSapRentabFalso(), new() { ["SapTm:Url"] = "sap-tm-prd:8000/sin-esquema" });
        using var coordinacion = new clsCoordinacionSincronizacionMargenSap();
        using var worker = new clsWorkerSincronizacionMargenSap(
            proveedor.GetRequiredService<IServiceScopeFactory>(), proveedor.GetRequiredService<IOptions<clsOpcionesSapTmSincronizacion>>(),
            coordinacion, new clsEntornoFalso(Environments.Production), TimeProvider.System, logger);

        await worker.StartAsync(Ct);
        await worker.ExecuteTask!;

        logger.Registros.Should().ContainSingle().Which.Should().Match<clsRegistroLog>(r => r.Nivel == LogLevel.Error && r.Mensaje.Contains("SapTm:Url"));
        logger.Registros.Should().NotContain(r => r.Mensaje.Contains("sin-esquema") || r.Mensaje.Contains(clsSapRentabFalso.Clave));
        proveedor.GetRequiredService<IClsSincronizacionMargenSap>().Configurada.Should().BeFalse();
    }

    [Fact]
    public async Task DeshabilitadaElWorkerNoCorreAunqueHayaCredencialesYLoAnotaEnInformation()
    {
        var logger = new clsLoggerFalso<clsWorkerSincronizacionMargenSap>();
        await using var proveedor = Proveedor(new clsSapRentabFalso(), new() { ["SapTm:SincronizacionHabilitada"] = null });
        using var coordinacion = new clsCoordinacionSincronizacionMargenSap();
        using var worker = new clsWorkerSincronizacionMargenSap(
            proveedor.GetRequiredService<IServiceScopeFactory>(), proveedor.GetRequiredService<IOptions<clsOpcionesSapTmSincronizacion>>(),
            coordinacion, new clsEntornoFalso(Environments.Production), TimeProvider.System, logger);

        await worker.StartAsync(Ct);
        await worker.ExecuteTask!;

        logger.Registros.Should().ContainSingle().Which.Should().Match<clsRegistroLog>(r => r.Nivel == LogLevel.Information && r.Mensaje.Contains("deshabilitada"));
        var sincronizacion = proveedor.GetRequiredService<IClsSincronizacionMargenSap>();
        (sincronizacion.Habilitada, sincronizacion.Configurada).Should().Be((false, false));
        new clsOpcionesSapTmSincronizacion().SincronizacionHabilitada.Should().BeFalse("apagada por defecto");
    }

    [Fact]
    public async Task SinConfiguracionElWorkerNoCorreYLoAnotaEnInformation()
    {
        var logger = new clsLoggerFalso<clsWorkerSincronizacionMargenSap>();
        await using var proveedor = Proveedor(new clsSapRentabFalso(), new() { ["SapTm:Url"] = null });
        using var coordinacion = new clsCoordinacionSincronizacionMargenSap();
        using var worker = new clsWorkerSincronizacionMargenSap(
            proveedor.GetRequiredService<IServiceScopeFactory>(), proveedor.GetRequiredService<IOptions<clsOpcionesSapTmSincronizacion>>(),
            coordinacion, new clsEntornoFalso(Environments.Production), TimeProvider.System, logger);

        await worker.StartAsync(Ct);
        await worker.ExecuteTask!;

        logger.Registros.Should().ContainSingle().Which.Should().Match<clsRegistroLog>(r => r.Nivel == LogLevel.Information && r.Mensaje.Contains("inactiva"));
        coordinacion.ProximoChequeo.Should().BeNull();
        proveedor.GetRequiredService<IClsSincronizacionMargenSap>().Configurada.Should().BeFalse();
    }

    [Fact]
    public async Task EnProductionUnaUrlHttpSeAceptaConUnWarningYUnaRondaFallidaNoTumbaElWorker()
    {
        var logger = new clsLoggerFalso<clsWorkerSincronizacionMargenSap>();
        await using var proveedor = Proveedor(new clsSapRentabFalso(), configurar: s => s.AddSingleton<IClsSincronizacionMargenSap, clsSincronizacionQueFalla>());
        using var coordinacion = new clsCoordinacionSincronizacionMargenSap();
        using var worker = new clsWorkerSincronizacionMargenSap(
            proveedor.GetRequiredService<IServiceScopeFactory>(), proveedor.GetRequiredService<IOptions<clsOpcionesSapTmSincronizacion>>(),
            coordinacion, new clsEntornoFalso(Environments.Production), TimeProvider.System, logger);

        await worker.StartAsync(Ct);
        // En .NET 10 ExecuteAsync arranca en otro hilo: sin esperar, StopAsync podía cancelarlo antes de que escribiera el warning.
        await EsperarRegistroAsync(logger, r => r.Nivel == LogLevel.Information && r.Mensaje.Contains("activa"));
        var ronda = await worker.RondaAsync(Ct);
        await worker.StopAsync(Ct);

        ronda.Should().BeNull();
        logger.Registros.Should().Contain(r => r.Nivel == LogLevel.Warning && r.Mensaje.Contains("http"));
        logger.Registros.Should().Contain(r => r.Nivel == LogLevel.Error && r.Mensaje.Contains("InvalidOperationException"));
        logger.Registros.Should().NotContain(r => r.Mensaje.Contains(clsSapRentabFalso.Clave) || r.Mensaje.Contains("sap-tm-falso"));
    }

    /// <summary>Espera, como mucho 10 s, a que el worker escriba un registro que cumpla <paramref name="condicion"/>.</summary>
    private static async Task EsperarRegistroAsync<T>(clsLoggerFalso<T> logger, Func<clsRegistroLog, bool> condicion)
    {
        using var tope = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        tope.CancelAfter(TimeSpan.FromSeconds(10));
        while (!logger.Registros.Any(condicion))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20), tope.Token);
        }
    }

    private static IReadOnlyList<clsFilaMargenSap> Leer(string json)
    {
        using var documento = JsonDocument.Parse(json);
        return clsLectorRentabClienteSap.Leer(documento.RootElement);
    }

    private static ServiceProvider Proveedor(clsSapRentabFalso sap, Dictionary<string, string?>? extra = null, Action<IServiceCollection>? configurar = null)
    {
        var valores = new Dictionary<string, string?>
        {
            ["SapTm:SincronizacionHabilitada"] = "true",
            ["SapTm:Url"] = clsSapRentabFalso.Url,
            ["SapTm:User"] = clsSapRentabFalso.Usuario,
            ["SapTm:Pass"] = clsSapRentabFalso.Clave,
        };
        foreach (var (clave, valor) in extra ?? [])
        {
            valores[clave] = valor;
        }

        var servicios = new ServiceCollection();
        servicios.AddSingleton<IHostEnvironment>(new clsEntornoFalso(Environments.Development));
        servicios.AddLogging();
        servicios.AddInfraestructura(new ConfigurationBuilder().AddInMemoryCollection(valores).Build());
        servicios.AddHttpClient(clsClienteRentabSap.NombreCliente).ConfigurePrimaryHttpMessageHandler(() => sap);
        configurar?.Invoke(servicios);
        return servicios.BuildServiceProvider();
    }

    /// <summary>Flujo que entrega unos bytes y luego lanza (conexión cortada o gzip corrupto).</summary>
    private sealed class clsFlujoQueFalla(byte[] inicio, Exception error) : Stream
    {
        private int _posicion;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _posicion;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_posicion >= inicio.Length)
            {
                throw error;
            }

            var n = Math.Min(buffer.Length, inicio.Length - _posicion);
            inicio.AsSpan(_posicion, n).CopyTo(buffer);
            _posicion += n;
            return n;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(Read(buffer.AsSpan(offset, count)));

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Sincronización que lanza (una falla inesperada): el worker la anota y sigue.</summary>
    private sealed class clsSincronizacionQueFalla : IClsSincronizacionMargenSap
    {
        public bool Habilitada => true;

        public bool Configurada => true;

        public string? ErrorConfiguracion => null;

        public Task<Aplicacion.Margen.DTOs.clsEstadoSincronizacionMargenSap> EstadoAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("no se usa");

        public Task<Aplicacion.Margen.DTOs.clsResultadoSincronizacionMargenSap> SincronizarAsync(bool forzada, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("falla inesperada");
    }
}
