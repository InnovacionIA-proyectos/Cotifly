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
/// <c>ISJSON</c>) y enviarlo por MailSender falso (deduplicación con el reloj de la aplicación, <c>reenviada</c>, fila
/// <c>encolado</c> con el id de MailSender, el worker que la lleva a <c>enviado</c> o <c>fallido</c>, el reintento y el historial).
/// </summary>
[Collection(clsColeccionSqlServer.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsPdfCorreoIntegracionTests : clsPruebaIntegracion
{
    private static readonly clsMailSenderFalso _mailSender = new();

    public clsPdfCorreoIntegracionTests(clsBaseDatosPruebas baseDatos)
        : base(baseDatos,
            configurar: s => s.AddHttpClient(Infraestructura.Correo.Services.clsTransporteMailSender.NombreCliente).ConfigurePrimaryHttpMessageHandler(() => _mailSender),
            configuracion: new Dictionary<string, string?>
            {
                ["Correo:Habilitado"] = "true",
                ["Correo:MailSender:UrlBase"] = clsMailSenderFalso.UrlBase,
                ["Correo:Proteccion:BuzonPruebas"] = clsMailSenderFalso.BuzonPruebas,
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
        // El chat también crea una ruta (línea de negocio, margen y precio) y un camión con el flete.
        var ruta = cotizacion.Rutas.Should().ContainSingle().Subject;
        ruta.LineaNegocioId.Should().NotBeNull("'Distribución' con tilde se resuelve contra 'Distribucion' (issue #44)");
        ruta.OrigenMargen.Should().Be(OrigenMargen.PoliticaLinea);
        (ruta.CostoFinal, ruta.PrecioFinalLinea, cotizacion.PrecioFinal).Should().Be(((decimal?)2_550_000m, (decimal?)3_000_000m, (decimal?)3_000_000m));
        ruta.Vehiculos.Should().ContainSingle().Which.CostoFlete.Should().Be(2_550_000m);
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
        correos.Should().HaveCount(2).And.OnlyContain(c => c.Estado == EstadoCorreo.Encolado && c.ConfirmadoPor == usuario && c.IdMailSender != null);
        correos[0].Cc.Should().Equal(new[] { "jefe@empresa.com" }, "en la base quedan los destinatarios originales");
        correos[0].RedirigidoA.Should().Be(clsMailSenderFalso.BuzonPruebas);
        _mailSender.Recibidos.Where(r => r.CorrelationId!.StartsWith(folio + ":", StringComparison.Ordinal))
            .Should().HaveCount(2).And.OnlyContain(r => r.To.SequenceEqual(new[] { clsMailSenderFalso.BuzonPruebas }) && r.Cc.Count == 0 && r.Bcc.Count == 0);
    }

    // Carrera de estadoEnvio: dos envíos simultáneos en ámbitos distintos (dos peticiones) a destinatarios distintos.
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
            .Should().HaveCount(2).And.OnlyContain(c => c.Estado == EstadoCorreo.Encolado);
    }

    [Fact]
    public async Task UnRechazoDeMailSenderDejaLaFilaFallidaYElEstadoSinCambios()
    {
        var usuario = await CrearUsuarioAsync();
        var folio = await CotizacionConPdfAsync(usuario);
        _mailSender.AltaFalla = System.Net.HttpStatusCode.UnprocessableEntity;
        try
        {
            var resultado = await EnviarAsync(new clsEntradaEnviarCorreo(usuario, ["rechazado@empresa.com"], Folio: folio));

            resultado.Error.Codigo.Should().Be(clsCodigosError.CorreoEnvioFallido);
        }
        finally
        {
            _mailSender.AltaFalla = null;
        }

        var cotizacion = await ConsultarAsync(s => s.GetRequiredService<IClsCotizacionRepository>().ObtenerPorFolioAsync(folio, Ct));
        cotizacion!.EstadoEnvio.Should().Be(EstadoEnvio.SinEnviar);
        var correo = (await ConsultarAsync(s => s.GetRequiredService<IClsCorreoRepository>().ListarPorCotizacionAsync(cotizacion.Id, Ct))).Should().ContainSingle().Subject;
        correo.Estado.Should().Be(EstadoCorreo.Fallido);
        correo.Error.Should().Contain("HTTP 422");
        correo.IdMailSender.Should().BeNull();
        correo.SiguienteConsultaAt.Should().BeNull();
    }

    [Fact]
    public async Task ElWorkerLlevaElCorreoAEnviadoOFallidoYElReintentoLoVuelveAEncolar()
    {
        var usuario = await CrearUsuarioAsync();
        var folio = await CotizacionConPdfAsync(usuario);
        var entregado = (await EnviarAsync(new clsEntradaEnviarCorreo(usuario, ["uno@empresa.com"], Folio: folio))).Valor;
        var fallido = (await EnviarAsync(new clsEntradaEnviarCorreo(usuario, ["dos@empresa.com"], Folio: folio))).Valor;

        // MailSender todavía no lo entrega: la consulta deja el estado (solo anota los intentos, 0) y reprograma.
        Reloj.Avanzar(TimeSpan.FromMinutes(1));
        (await ProcesarCorreoAsync(entregado.CorreoId)).Should().NotBe(ResultadoConsultaCorreo.Error);
        var enCola = await LeerCorreoAsync(entregado.CorreoId);
        enCola.Estado.Should().Be(EstadoCorreo.Encolado);
        enCola.SiguienteConsultaAt.Should().Be(Reloj.AhoraUtc + TimeSpan.FromSeconds(30));
        (await ProcesarCorreoAsync(entregado.CorreoId)).Should().Be(ResultadoConsultaCorreo.SinCambio, "la misma foto de MailSender no cambia nada");

        _mailSender.Entregar(Guid.Parse(entregado.MessageId), intentos: 2);
        _mailSender.Fallar(Guid.Parse(fallido.MessageId), "550 5.7.1 Unable to relay");
        Reloj.Avanzar(TimeSpan.FromMinutes(1));
        (await ProcesarCorreoAsync(entregado.CorreoId)).Should().Be(ResultadoConsultaCorreo.Cambio);
        (await ProcesarCorreoAsync(fallido.CorreoId)).Should().Be(ResultadoConsultaCorreo.Cambio);

        var correoEntregado = await LeerCorreoAsync(entregado.CorreoId);
        correoEntregado.Estado.Should().Be(EstadoCorreo.Enviado);
        correoEntregado.IntentosEntrega.Should().Be(2);
        correoEntregado.EnviadoAt.Should().NotBeNull();
        correoEntregado.SiguienteConsultaAt.Should().BeNull("un estado final ya no se consulta");
        var correoFallido = await LeerCorreoAsync(fallido.CorreoId);
        correoFallido.Estado.Should().Be(EstadoCorreo.Fallido);
        correoFallido.Error.Should().Be("550 5.7.1 Unable to relay");
        correoFallido.EsReintentable.Should().BeTrue();

        // Historial para el frontend y reintento del fallido (solo el dueño).
        var historial = (await ConsultarAsync(s => s.GetRequiredService<IClsHistorialCorreosService>().ListarAsync(usuario, folio, Ct))).Valor;
        historial.Select(h => (h.Para[0], h.Estado, h.Reintentable)).Should().BeEquivalentTo([("uno@empresa.com", "enviado", false), ("dos@empresa.com", "fallido", true)]);
        (await ConsultarAsync(s => s.GetRequiredService<IClsHistorialCorreosService>().ReintentarAsync(Guid.NewGuid(), folio, fallido.CorreoId, Ct)))
            .Error.Codigo.Should().Be(clsCodigosError.CotizacionNoEncontrada, "otro comercial no ve ni reintenta");
        (await ConsultarAsync(s => s.GetRequiredService<IClsHistorialCorreosService>().ReintentarAsync(usuario, folio, entregado.CorreoId, Ct)))
            .Error.Codigo.Should().Be(clsCodigosError.CorreoNoReintentable, "solo un fallido");
        var reintentado = await ConsultarAsync(s => s.GetRequiredService<IClsHistorialCorreosService>().ReintentarAsync(usuario, folio, fallido.CorreoId, Ct));
        reintentado.Valor.Estado.Should().Be("encolado");
        (await LeerCorreoAsync(fallido.CorreoId)).SiguienteConsultaAt.Should().NotBeNull("el worker vuelve a seguirlo");

        _mailSender.Entregar(Guid.Parse(fallido.MessageId));
        Reloj.Avanzar(TimeSpan.FromMinutes(1));
        (await ProcesarCorreoAsync(fallido.CorreoId)).Should().Be(ResultadoConsultaCorreo.Cambio);
        (await LeerCorreoAsync(fallido.CorreoId)).Estado.Should().Be(EstadoCorreo.Enviado);
    }

    [Fact]
    public async Task SinConfirmacionDeMailSenderEnLaEdadMaximaElCorreoQuedaFallido()
    {
        var usuario = await CrearUsuarioAsync();
        var folio = await CotizacionConPdfAsync(usuario);
        var enviado = (await EnviarAsync(new clsEntradaEnviarCorreo(usuario, ["tres@empresa.com"], Folio: folio))).Valor;

        Reloj.Avanzar(TimeSpan.FromHours(25));
        (await ProcesarCorreoAsync(enviado.CorreoId)).Should().Be(ResultadoConsultaCorreo.Vencido);

        var correo = await LeerCorreoAsync(enviado.CorreoId);
        correo.Estado.Should().Be(EstadoCorreo.Fallido);
        correo.Error.Should().Contain("no confirmó la entrega");
        correo.EsReintentable.Should().BeTrue();
    }

    private async Task<ResultadoConsultaCorreo> ProcesarCorreoAsync(Guid correoId)
    {
        var correo = await LeerCorreoAsync(correoId);
        return await ConsultarAsync(s => s.GetRequiredService<IClsSeguimientoCorreosService>()
            .ProcesarAsync(new clsConsultaCorreoPendiente(correoId, correo.SiguienteConsultaAt!.Value), Ct));
    }

    private async Task<clsCorreo> LeerCorreoAsync(Guid correoId) =>
        (await ConsultarAsync(s => s.GetRequiredService<IClsCorreoRepository>().ObtenerAsync(correoId, Ct)))!;

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
