using Cotifly.Aplicacion.Agente.DTOs;
using Cotifly.Aplicacion.Agente.Exceptions;
using Cotifly.Aplicacion.Agente.Herramientas;
using Cotifly.Aplicacion.Agente.Services;
using Cotifly.Aplicacion.Clientes.DTOs;
using Cotifly.Aplicacion.Correo.DTOs;
using Cotifly.Aplicacion.Margen.DTOs;
using Cotifly.Aplicacion.Pdf.DTOs;
using Cotifly.Aplicacion.Precio.DTOs;
using Cotifly.Aplicacion.Tarifas.DTOs;
using Cotifly.Dominio.Agente;
using Cotifly.Dominio.Clientes;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Dominio.Margen;
using NSubstitute;

namespace Cotifly.Aplicacion.Tests.Agente;

/// <summary>
/// Correcciones de la validación y la auditoría de #12: costo manual escrito por el comercial, destinatarios del correo, candado por
/// sesión, nueva versión desde el frontend (<c>cotizacion_origen_id</c>) y cotización en curso descartada al cambiar de cliente, línea
/// o ruta.
/// </summary>
public sealed class clsCorreccionesAuditoriaTests
{
    private static readonly object _payload = new
    {
        cliente = "Cliente",
        linea_negocio = "Nacional",
        tarifa = new { titulo = "COTIZACIÓN", origen = "YUMBO", filas = new[] { new { destino = "BUN", vehiculo_peso = "Tractomula" } } },
    };

    private readonly clsArnesAgente _arnes = new();
    private readonly clsEstadoConversacionService _estado = new();

    // Riesgo 5: un costo que el comercial no escribió no llega a calcular_precio.
    [Fact]
    public async Task UnCostoQueElComercialNoEscribioSeRechaza()
    {
        _arnes.Modelo
            .Llamar("obtener_margen", new { lineaNegocio = "Nacional" })
            .Llamar("calcular_precio", new { costo = 1_900_000, margen = 0.15, metodo = "margen" })
            .Cerrar("¿Cuál es el costo?");

        var resultado = (await _arnes.TurnoAsync("Cotiza Nacional Yumbo a Buenaventura")).Valor;

        resultado.Salidas[^1].Error!.Codigo.Should().Be(clsCodigosError.PrecioInvalido);
        resultado.Salidas[^1].Error!.Mensaje.Should().Contain("comercial");
        _arnes.Estado().PrecioFinal.Should().BeNull();
    }

    [Fact]
    public async Task UnCostoEscritoEnMillonesSeAceptaYLuegoSeReutilizaSinRepetirlo()
    {
        _arnes.Modelo
            .Llamar("obtener_margen", new { lineaNegocio = "Nacional" })
            .Llamar("calcular_precio", new { costo = 2_850_000, margen = 0.15, metodo = "margen" })
            .Cerrar("Listo.")
            .Llamar("calcular_precio", new { costo = 2_850_000, margen = 0.15, metodo = "margen" })
            .Cerrar("Otra vez.");

        var primero = (await _arnes.TurnoAsync("Cotiza con costo de 2,85 millones")).Valor;
        var segundo = (await _arnes.TurnoAsync("recalcula por favor")).Valor;

        primero.UltimaSalida<clsSalidaPrecio>("calcular_precio")!.Costo.Should().Be(2_850_000m);
        segundo.UltimaSalida<clsSalidaPrecio>("calcular_precio").Should().NotBeNull("el costo ya lo dio el comercial en la conversación");
    }

    [Theory]
    [InlineData("costo 2.850.000", 2_850_000)]
    [InlineData("costo $2,850,000", 2_850_000)]
    [InlineData("costo 2850000 pesos", 2_850_000)]
    [InlineData("costo 2'850.000", 2_850_000)]
    [InlineData("costo 2.850.000,50", 2_850_000.50)]
    [InlineData("2,85 millones", 2_850_000)]
    [InlineData("3.5 M", 3_500_000)]
    public void LeeLosNumerosEnLosFormatosHabituales(string mensaje, double valor) =>
        clsNumerosDelMensaje.Contiene(mensaje, (decimal)valor).Should().BeTrue();

    [Fact]
    public void UnNumeroQueNoEstaEnElMensajeNoSeEncuentra() => clsNumerosDelMensaje.Contiene("peso 10000 kg", 2_850_000m).Should().BeFalse();

    // Riesgo 5: con "Confirmo", solo destinatarios que el comercial escribió en la conversación, nunca de datos externos.
    [Fact]
    public async Task ConConfirmacionElCorreoSoloAceptaDestinatariosQueEscribioElComercial()
    {
        _arnes.OpcionesCorreo.Habilitado = true;
        _arnes.Correo.EnviarAsync(Arg.Any<clsEntradaEnviarCorreo>(), Arg.Any<CancellationToken>())
            .Returns(clsResultado<clsSalidaEnvioCorreo>.Exito(new clsSalidaEnvioCorreo(true, "<id@x>", true)));
        _arnes.Modelo
            .Llamar("obtener_margen", new { lineaNegocio = "Nacional" })
            .Llamar("calcular_precio", new { costo = 2_850_000, margen = 0.15, metodo = "margen" })
            .Cerrar("¿Confirmas?")
            .Llamar("enviar_correo", new { para = new[] { "externo@otra.com" } })
            .Llamar("enviar_correo", new { para = new[] { "compras@cliente.com" } })
            .Cerrar("Enviado.");
        var sesion = await _arnes.Sesiones.ObtenerOCrearAsync(clsArnesAgente.Hilo, clsArnesAgente.Hilo, _arnes.Usuario.Id, TestContext.Current.CancellationToken);
        sesion.ActualizarEstadoAgente(clsEstadoConversacion.Vacio with { CotizacionId = Guid.NewGuid() });

        await _arnes.TurnoAsync("Cotiza con costo 2.850.000 y luego envíala a compras@cliente.com");
        var confirmado = (await _arnes.TurnoAsync("Confirmo")).Valor;

        confirmado.Salidas[0].Error!.Codigo.Should().Be(clsCodigosError.ConfirmacionRequerida);
        confirmado.Salidas[1].EsExitoso.Should().BeTrue();
        await _arnes.Correo.Received(1).EnviarAsync(Arg.Is<clsEntradaEnviarCorreo>(e => e.Para.Single() == "compras@cliente.com"), Arg.Any<CancellationToken>());
    }

    // Riesgo 4: dos turnos de la misma sesión no corren a la vez.
    [Fact]
    public async Task ConOtroTurnoEnCursoNoSeLlamaAlModelo()
    {
        _arnes.Candado.Ocupado = true;

        var turno = async () => await _arnes.TurnoAsync("hola");

        await turno.Should().ThrowAsync<clsTurnoEnCursoException>();
        _arnes.Modelo.Llamadas.Should().BeEmpty();
        _arnes.Sesiones.Eventos.Should().BeEmpty();
        clsTextosAgente.ParaFallo(new clsTurnoEnCursoException()).Should().Be(clsTextosAgente.TurnoEnCurso);
    }

    [Fact]
    public async Task ElCandadoDeLaSesionSeTomaYSeLiberaEnCadaTurno()
    {
        _arnes.Modelo.Cerrar("uno");

        await _arnes.TurnoAsync("hola");

        _arnes.Candado.Tomados.Should().Equal($"{clsArnesAgente.Hilo}|{clsArnesAgente.Hilo}");
        _arnes.Candado.Liberados.Should().Be(1);
    }

    // I2: nueva versión desde el frontend con cotizacion_origen_id.
    [Fact]
    public async Task LaCotizacionDeOrigenSiembraElEstadoYElPdfSaleComoNuevaVersion()
    {
        var origen = clsCotizacion.Crear(new clsDatosCotizacion
        {
            Folio = "PCN-NV-24092026-001",
            Version = 1,
            LineaNegocioId = 1,
            ClienteNombre = "LONGYIN S.A.S.",
            NitCliente = "900123456",
            OrigenCliente = OrigenCliente.Cliente,
            Costo = 2_850_000m,
            MargenPolitica = 0.15m,
            MargenAplicado = 0.15m,
            OrigenMargen = OrigenMargen.PoliticaLinea,
            MetodoCalculo = "margen",
            PrecioFinal = 3_352_941m,
            UsuarioId = _arnes.Usuario.Id,
        }).Valor;
        _arnes.Cotizaciones.ObtenerPorIdDelUsuarioAsync(origen.Id, _arnes.Usuario.Id, Arg.Any<CancellationToken>()).Returns(origen);
        _arnes.Politicas.ObtenerPorIdAsync(1, Arg.Any<CancellationToken>()).Returns(clsMargenPolitica.Crear("Nacional", 0.15m).Valor);
        _arnes.Modelo
            .Llamar("calcular_precio", new { costo = 2_850_000, margen = 0.15, metodo = "margen" })
            .Cerrar("Previsualización de la versión 2. ¿Confirmas?")
            .Llamar("generar_pdf", new { cotizacion = _payload })
            .Cerrar("Versión 2 generada.");

        await _arnes.TurnoAsync("Quiero una nueva versión de esta cotización", cotizacionOrigen: origen.Id);
        var sembrado = _arnes.Estado();
        await _arnes.TurnoAsync("Confirmo", cotizacionOrigen: origen.Id);

        sembrado.NitCliente.Should().Be("900123456");
        sembrado.LineaNegocio.Should().Be("Nacional");
        sembrado.CotizacionId.Should().Be(origen.Id);
        _arnes.OrigenesVersion.Should().Equal(origen.Id);
        _arnes.EntradasPdf.Single().Folio.Should().Be("PCN-NV-24092026-001-v2");
        _arnes.EntradasPdf.Single().Trazabilidad!.NitCliente.Should().Be("900123456");
        await _arnes.Folios.DidNotReceive().CrearConFolioNuevoAsync(Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<Func<clsFolio, CancellationToken, Task<clsResultado<clsSalidaPdf>>>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnaCotizacionDeOrigenAjenaNoSeUsa()
    {
        var resultado = await _arnes.TurnoAsync("nueva versión", cotizacionOrigen: Guid.NewGuid());

        resultado.Error.Codigo.Should().Be(clsCodigosError.CotizacionNoEncontrada);
        _arnes.Modelo.Llamadas.Should().BeEmpty();
    }

    // F3/I3: al cambiar de cliente, línea o ruta, la siguiente cotización es nueva (no una -vN de otra).
    private static clsEstadoConversacion ConCotizacion() => clsEstadoConversacion.Vacio with
    {
        CotizacionId = Guid.NewGuid(),
        FolioEnProgreso = "PCN-900123456-NV-24092026-001",
        Version = 1,
        NitCliente = "900123456",
        ClienteConfirmado = true,
        LineaNegocioId = 1,
        Origen = "YUMBO",
        Destino = "BUENAVENTURA",
    };

    private static void SinCotizacionEnCurso(clsEstadoConversacion estado)
    {
        estado.CotizacionId.Should().BeNull();
        estado.FolioEnProgreso.Should().BeNull();
        estado.Version.Should().BeNull();
    }

    [Fact]
    public void OtroClienteDescartaLaCotizacionEnCurso() =>
        SinCotizacionEnCurso(_estado.Aplicar(ConCotizacion(), clsResultadoHerramienta.Exito("buscar_cliente", new { },
            new clsResultadoBusquedaClientes(true, [new clsClienteEncontrado("800333444", "OTRO", "cliente")])), new Dictionary<string, object?>()));

    [Fact]
    public void OtraLineaDescartaLaCotizacionEnCurso()
    {
        var resuelto = new clsMargenResuelto(new clsSalidaMargen(0.15m, "politica_linea", "OPIM", false, "x"), "Importacion", 3, 0.15m, "margen");

        SinCotizacionEnCurso(_estado.Aplicar(ConCotizacion(), clsResultadoHerramienta.Exito("obtener_margen", resuelto.Salida, resuelto), new Dictionary<string, object?>()));
    }

    [Fact]
    public void OtraRutaDescartaLaCotizacionEnCurso() =>
        SinCotizacionEnCurso(_estado.Aplicar(ConCotizacion(), clsResultadoHerramienta.Exito("buscar_tarifa", new clsSalidaTarifa(false, null, "COP", "nexo")),
            new Dictionary<string, object?> { ["origen"] = "BOGOTA", ["destino"] = "BUENAVENTURA" }));

    [Fact]
    public void ElMismoClienteLaMismaLineaYLaMismaRutaConservanLaCotizacion()
    {
        var inicial = ConCotizacion();
        var resuelto = new clsMargenResuelto(new clsSalidaMargen(0.15m, "politica_linea", "OPTN", false, "x"), "Nacional", 1, 0.15m, "margen");

        var estado = _estado.Aplicar(inicial, clsResultadoHerramienta.Exito("buscar_cliente", new { },
            new clsResultadoBusquedaClientes(true, [new clsClienteEncontrado("900123456", "LONGYIN", "cliente")])), new Dictionary<string, object?>());
        estado = _estado.Aplicar(estado, clsResultadoHerramienta.Exito("obtener_margen", resuelto.Salida, resuelto), new Dictionary<string, object?>());
        estado = _estado.Aplicar(estado, clsResultadoHerramienta.Exito("buscar_tarifa", new clsSalidaTarifa(false, null, "COP", "nexo")),
            new Dictionary<string, object?> { ["origen"] = "Yumbo", ["destino"] = "Buenaventura" });

        estado.CotizacionId.Should().Be(inicial.CotizacionId);
        estado.FolioEnProgreso.Should().Be(inicial.FolioEnProgreso);
    }
}
