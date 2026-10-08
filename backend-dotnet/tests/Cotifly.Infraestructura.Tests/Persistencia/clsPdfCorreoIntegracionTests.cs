using System.Text.Json;
using Cotifly.Aplicacion.Auditoria.Repositories;
using Cotifly.Aplicacion.Correo.DTOs;
using Cotifly.Aplicacion.Correo.Repositories;
using Cotifly.Aplicacion.Correo.Services;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Aplicacion.Pdf.DTOs;
using Cotifly.Aplicacion.Pdf.Interfaces;
using Cotifly.Aplicacion.Pdf.Services;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Correo;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Margen;
using Cotifly.Pruebas.Comun;
using Microsoft.Extensions.DependencyInjection;

namespace Cotifly.Infraestructura.Tests.Persistencia;

/// <summary>
/// Flujo completo con el registro real (AddAplicacion + AddInfraestructura) sobre SQL Server 2019: generar el PDF (render
/// MigraDoc, almacén en carpeta temporal, fila en <c>Cotizaciones</c>, evento en <c>EventosHerramienta</c> que pasa el CHECK
/// <c>ISJSON</c>) y enviarlo por el relay falso (deduplicación con el reloj de la aplicación, <c>reenviada</c>, fila
/// <c>fallido</c>).
/// </summary>
[Collection(clsColeccionSqlServer.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsPdfCorreoIntegracionTests : clsPruebaIntegracion
{
    private static readonly clsServidorSmtpFalso _relay = new();

    public clsPdfCorreoIntegracionTests(clsBaseDatosPruebas baseDatos)
        : base(baseDatos, configurar: null, configuracion: new Dictionary<string, string?>
        {
            ["Correo:Habilitado"] = "true",
            ["Correo:SmtpRelay:Host"] = clsServidorSmtpFalso.Host,
            ["Correo:SmtpRelay:Port"] = _relay.Puerto.ToString(System.Globalization.CultureInfo.InvariantCulture),
        })
    {
    }

    [Fact]
    public async Task GenerarElPdfRegistraLaCotizacionGuardaElArchivoYAudita()
    {
        var usuario = await CrearUsuarioAsync();
        var hilo = $"web/{Guid.NewGuid():N}";
        var folio = $"PCN-I-{Guid.NewGuid():N}"[..30];
        var payload = clsCotizacionPdfPayload.DesdeJson(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "cotizacion", "ejemplo-solicitud.json"))).Valor;
        payload.Tarifa!.Filas!.RemoveAt(1); // un solo precio: el de la trazabilidad (2.550.000 / 0,85)
        payload.LineaNegocio = "Distribución";

        var resultado = await ConsultarAsync(s => s.GetRequiredService<IClsGenerarPdfService>().GenerarAsync(
            new clsEntradaGenerarPdf(usuario, payload, folio, Trazabilidad: new clsTrazabilidadCotizacion(Costo: 2_550_000m, MargenAplicado: 0.15m, OrigenMargen: "politica_linea", PrecioFinal: 3_000_000m), ThreadId: hilo),
            Ct));

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallo ? resultado.Error.Mensaje : string.Empty);
        var cotizacion = await ConsultarAsync(s => s.GetRequiredService<IClsCotizacionRepository>().ObtenerPorFolioAsync(folio, Ct));
        cotizacion!.Id.Should().Be(resultado.Valor.CotizacionId);
        cotizacion.UsuarioId.Should().Be(usuario);
        cotizacion.LineaNegocioId.Should().NotBeNull("'Distribución' con tilde se resuelve contra 'Distribucion' (issue #44)");
        cotizacion.OrigenMargen.Should().Be(OrigenMargen.PoliticaLinea);
        cotizacion.EstadoEnvio.Should().Be(EstadoEnvio.SinEnviar);
        cotizacion.PdfPath.Should().EndWith(folio + ".pdf");

        var pdf = await ConsultarAsync(s => s.GetRequiredService<IClsAlmacenPdf>().LeerPorReferenciaAsync(cotizacion.PdfPath, Ct));
        clsLectorPdf.Leer(pdf!).Paginas.Should().HaveCount(3);

        var eventos = await ConsultarAsync(s => s.GetRequiredService<IClsAuditoriaRepository>().ListarPorThreadAsync(hilo, Ct));
        var evento = eventos.Should().ContainSingle().Subject;
        evento.Herramienta.Should().Be("generar_pdf");
        evento.Exito.Should().BeTrue();
        using var salida = JsonDocument.Parse(evento.OutputResumen!);
        salida.RootElement.GetProperty("folio").GetString().Should().Be(folio);
        JsonDocument.Parse(evento.Input!).RootElement.ValueKind.Should().Be(JsonValueKind.Object);
    }

    [Fact]
    public async Task ElEnvioDeduplicaConElRelojDeLaAplicacionYPasaAReenviada()
    {
        var usuario = await CrearUsuarioAsync();
        var folio = await CotizacionConPdfAsync(usuario);
        var entrada = new clsEntradaEnviarCorreo(usuario, ["Cliente@Empresa.com"], Folio: folio, Cc: ["jefe@empresa.com"]);

        var primero = await EnviarAsync(entrada);
        Reloj.Avanzar(TimeSpan.FromMinutes(4));
        var duplicado = await EnviarAsync(entrada with { Para = ["cliente@empresa.com"] });
        Reloj.Avanzar(TimeSpan.FromMinutes(2));
        var tercero = await EnviarAsync(entrada);

        primero.EsExitoso.Should().BeTrue(primero.EsFallo ? primero.Error.Mensaje : string.Empty);
        duplicado.Error.Codigo.Should().Be(clsCodigosError.CorreoDuplicado);
        tercero.EsExitoso.Should().BeTrue();
        var cotizacion = await ConsultarAsync(s => s.GetRequiredService<IClsCotizacionRepository>().ObtenerPorFolioAsync(folio, Ct));
        cotizacion!.EstadoEnvio.Should().Be(EstadoEnvio.Reenviada);
        var correos = await ConsultarAsync(s => s.GetRequiredService<IClsCorreoRepository>().ListarPorCotizacionAsync(cotizacion.Id, Ct));
        correos.Should().HaveCount(2).And.OnlyContain(c => c.Estado == EstadoCorreo.Enviado && c.ConfirmadoPor == usuario);
        correos[0].Cc.Should().Equal("jefe@empresa.com");
        _relay.Recibidos.Where(r => r.Destinatarios.Contains("jefe@empresa.com")).Should().HaveCountGreaterThanOrEqualTo(2);
    }

    // Carrera de estadoEnvio (#11): dos envíos simultáneos en ámbitos distintos (dos peticiones) a destinatarios distintos.
    [Fact]
    public async Task DosEnviosSimultaneosDejanUnoEnviadaYElSiguienteReenviada()
    {
        var usuario = await CrearUsuarioAsync();
        var folio = await CotizacionConPdfAsync(usuario);

        var resultados = await Task.WhenAll(
            EnviarAsync(new clsEntradaEnviarCorreo(usuario, ["uno@empresa.com"], Folio: folio)),
            EnviarAsync(new clsEntradaEnviarCorreo(usuario, ["dos@empresa.com"], Folio: folio)));

        resultados.Should().OnlyContain(r => r.EsExitoso);
        var cotizacion = await ConsultarAsync(s => s.GetRequiredService<IClsCotizacionRepository>().ObtenerPorFolioAsync(folio, Ct));
        cotizacion!.EstadoEnvio.Should().Be(EstadoEnvio.Reenviada, "el segundo envío recarga la cotización dentro del candado y ve 'enviada'");
        (await ConsultarAsync(s => s.GetRequiredService<IClsCorreoRepository>().ListarPorCotizacionAsync(cotizacion.Id, Ct)))
            .Should().HaveCount(2).And.OnlyContain(c => c.Estado == EstadoCorreo.Enviado);
    }

    [Fact]
    public async Task UnFalloDelRelayDejaLaFilaFallidaYElEstadoSinCambios()
    {
        var usuario = await CrearUsuarioAsync();
        var folio = await CotizacionConPdfAsync(usuario);
        _relay.RechazarDestinatarios = true;
        try
        {
            var resultado = await EnviarAsync(new clsEntradaEnviarCorreo(usuario, ["rechazado@empresa.com"], Folio: folio));

            resultado.Error.Codigo.Should().Be(clsCodigosError.CorreoEnvioFallido);
        }
        finally
        {
            _relay.RechazarDestinatarios = false;
        }

        var cotizacion = await ConsultarAsync(s => s.GetRequiredService<IClsCotizacionRepository>().ObtenerPorFolioAsync(folio, Ct));
        cotizacion!.EstadoEnvio.Should().Be(EstadoEnvio.SinEnviar);
        var correo = (await ConsultarAsync(s => s.GetRequiredService<IClsCorreoRepository>().ListarPorCotizacionAsync(cotizacion.Id, Ct))).Should().ContainSingle().Subject;
        correo.Estado.Should().Be(EstadoCorreo.Fallido);
        correo.Error.Should().Contain("rechazó los destinatarios");
    }

    [Fact]
    public async Task ElTelefonoConfirmadoQuedaEnUsuariosWeb()
    {
        var usuario = await CrearUsuarioAsync();

        await ConsultarAsync(s => s.GetRequiredService<Aplicacion.Identidad.Services.IClsTelefonoComercialService>().GuardarAsync(usuario, "+57 300 123 4567", Ct));

        var leido = await ConsultarAsync(s => s.GetRequiredService<IClsUsuarioWebRepository>().ObtenerPorIdAsync(usuario, Ct));
        leido!.Telefono.Should().Be("+57 300 123 4567");
    }

    private Task<clsResultado<clsSalidaEnvioCorreo>> EnviarAsync(clsEntradaEnviarCorreo entrada) =>
        ConsultarAsync(s => s.GetRequiredService<IClsCorreoService>().EnviarAsync(entrada, Ct));

    private async Task<string> CotizacionConPdfAsync(Guid usuario)
    {
        var folio = FolioUnico();
        var ruta = await ConsultarAsync(s => s.GetRequiredService<IClsAlmacenPdf>().GuardarNuevoAsync(folio + ".pdf", "%PDF-1.4 prueba"u8.ToArray(), Ct));
        var cotizacion = clsCotizacionSolicitud.Crear(new clsDatosCotizacion { Folio = folio, UsuarioId = usuario, PdfPath = ruta }).Valor;
        await GuardarAsync(cotizacion);
        return folio;
    }
}
