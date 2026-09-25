using System.Text.Json;
using Cotifly.Aplicacion.Agente.DTOs;
using Cotifly.Aplicacion.Auditoria.Services;
using Cotifly.Aplicacion.Clientes.DTOs;
using Cotifly.Aplicacion.Correo.DTOs;
using Cotifly.Aplicacion.Margen.DTOs;
using Cotifly.Aplicacion.Pdf.DTOs;
using Cotifly.Aplicacion.Precio.DTOs;
using Cotifly.Aplicacion.Tarifas.DTOs;
using Cotifly.Dominio.Clientes;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Margen;
using NSubstitute;

namespace Cotifly.Aplicacion.Tests.Agente;

/// <summary>
/// Filtros de invocación del agente (ADR-003/ADR-004): el orquestador propaga los identificadores y los números desde el estado,
/// nunca el LLM; confirmación explícita para el PDF y el correo; auditoría sin duplicar lo que auditan los casos de uso.
/// </summary>
public sealed class clsInyeccionEstadoTests
{
    private static readonly object _payloadDelLlm = new
    {
        referencia = "DG-24092026",
        cliente = "Longyin",
        cliente_id_sap = "99999",
        linea_negocio = "nacional",
        precio_final = 1,
        tarifa = new { titulo = "COTIZACIÓN TRANSPORTE NACIONAL", origen = "YUMBO", filas = new[] { new { destino = "BUENAVENTURA", vehiculo_peso = "Tractomula - 34 TON", tarifa = 999, moneda = "USD" } } },
    };

    private readonly clsArnesAgente _arnes = new();

    // EL HALLAZGO (ADR-004, chat_slot_filling.feature): el cliente del turno 1 llega al PDF del turno 2 sin que el LLM lo reenvíe.
    [Fact]
    public async Task ElClienteIdentificadoEnElTurnoUnoLlegaAlPdfDelTurnoDosSinQueElLlmLoMande()
    {
        _arnes.CatalogoConLongyin();
        _arnes.Modelo
            .Llamar("buscar_cliente", new { nombre = "Longyin" })
            .Llamar("obtener_margen", new { lineaNegocio = "Nacional", cliente = "Longyin" })
            .Llamar("calcular_precio", new { costo = 2_850_000, margen = 0.30, metodo = "markup" })
            .Cerrar("Previsualización: precio 3.238.636. ¿Confirmas?")
            .Llamar("generar_pdf", new { cotizacion = _payloadDelLlm })
            .Cerrar("Listo, el PDF está generado.");

        var turno1 = (await _arnes.TurnoAsync("Cotiza Nacional Yumbo a Buenaventura para Longyin, costo 2.850.000")).Valor;
        var turno2 = (await _arnes.TurnoAsync("Confirmo")).Valor;

        _arnes.EntradasMargen.Should().ContainSingle().Which.BpCliente.Should().Be(clsArnesAgente.BpLongyin, "SAP TM se consulta con el BP del estado, no con el texto");
        var precio = turno1.UltimaSalida<clsSalidaPrecio>("calcular_precio")!;
        precio.Margen.Should().Be(0.12m, "el margen sale de obtener_margen, no del LLM (0.30)");
        precio.Metodo.Should().Be("margen");
        precio.Precio.Should().Be(3_238_636m);

        turno2.HerramientasInvocadas.Should().Equal("generar_pdf");
        _arnes.NitsFolio.Should().Equal(clsArnesAgente.NitLongyin);
        var pdf = _arnes.EntradasPdf.Should().ContainSingle().Subject;
        pdf.Folio.Should().Be("PCN-900123456-24092026-001", "el folio lo genera el servidor, con el NIT del estado");
        pdf.UsuarioId.Should().Be(_arnes.Usuario.Id);
        pdf.SpaceId.Should().Be(clsArnesAgente.Hilo);
        pdf.Trazabilidad!.NitCliente.Should().Be(clsArnesAgente.NitLongyin);
        pdf.Trazabilidad.OrigenCliente.Should().Be("cliente");
        pdf.Trazabilidad.OrigenMargen.Should().Be("sap_tm");
        pdf.Trazabilidad.MargenAplicado.Should().Be(0.12m);
        pdf.Trazabilidad.MargenPolitica.Should().Be(0.135m);
        pdf.Trazabilidad.PrecioFinal.Should().Be(3_238_636m);
        pdf.Trazabilidad.Costo.Should().Be(2_850_000m);
        pdf.Cotizacion.Cliente.Should().Be("LONGYIN S.A.S.");
        pdf.Cotizacion.LineaNegocio.Should().Be("Nacional");
        var fila = pdf.Cotizacion.Tarifa!.Filas!.Single();
        fila.Tarifa.Should().Be(3_238_636m, "la tarifa impresa es el precio final del estado");
        fila.Moneda.Should().Be("COP");
        pdf.Cotizacion.Referencia.Should().BeNull();

        var estado = _arnes.Estado();
        estado.NitCliente.Should().Be(clsArnesAgente.NitLongyin);
        estado.BpCliente.Should().Be(clsArnesAgente.BpLongyin);
        estado.CotizacionId.Should().Be(_arnes.CotizacionGenerada);
        estado.FolioEnProgreso.Should().Be("PCN-900123456-24092026-001");
        estado.ConfirmacionPendiente.Should().BeFalse();
    }

    // El filtro sobrescribe los números aunque el LLM mande otros (ADR-003 §2).
    [Fact]
    public async Task CalcularPrecioUsaElMargenYElMetodoDelEstadoAunqueElLlmMandeOtros()
    {
        _arnes.Modelo
            .Llamar("obtener_margen", new { lineaNegocio = "Nacional" })
            .Llamar("calcular_precio", new { costo = 2_850_000, margen = 0.5, metodo = "markup" })
            .Cerrar("ok");

        var precio = (await _arnes.TurnoAsync("cotiza")).Valor.UltimaSalida<clsSalidaPrecio>("calcular_precio")!;

        precio.Margen.Should().Be(0.135m);
        precio.Metodo.Should().Be("margen");
        precio.Precio.Should().Be(3_294_798m);
    }

    [Fact]
    public async Task SinMargenResueltoCalcularPrecioSeRechazaSinInventarlo()
    {
        _arnes.Modelo.Llamar("calcular_precio", new { costo = 2_850_000, margen = 0.2, metodo = "margen" }).Cerrar("Primero el margen.");

        var resultado = (await _arnes.TurnoAsync("cotiza")).Valor;

        resultado.Salidas.Single().Error!.Codigo.Should().Be(clsCodigosError.PrecioInvalido);
        resultado.Salidas.Single().Error!.Mensaje.Should().Contain("obtener_margen");
    }

    // Un costo de buscar_tarifa (Nexo) se impone sobre el que manda el LLM; uno escrito por el comercial sí llega del mensaje.
    [Fact]
    public async Task ElCostoDeLaFuenteSeImponeSobreElDelLlm()
    {
        _arnes.Tarifas.BuscarAsync(Arg.Any<clsEntradaBuscarTarifa>(), Arg.Any<CancellationToken>())
            .Returns(clsResultado<clsSalidaTarifa>.Exito(new clsSalidaTarifa(true, 2_000_000m, "COP", "nexo", "C-1")));
        _arnes.Modelo
            .Llamar("buscar_tarifa", new { lineaNegocio = "Nacional", origen = "YUMBO", destino = "BUN", pesoKg = 10000 })
            .Llamar("obtener_margen", new { lineaNegocio = "Nacional" })
            .Llamar("calcular_precio", new { costo = 999, margen = 0.135, metodo = "margen" })
            .Cerrar("ok");

        var precio = (await _arnes.TurnoAsync("cotiza")).Valor.UltimaSalida<clsSalidaPrecio>("calcular_precio")!;

        precio.Costo.Should().Be(2_000_000m);
        _arnes.Estado().CosteoId.Should().Be("C-1");
        _arnes.Estado().Origen.Should().Be("YUMBO");
        _arnes.Estado().PesoKg.Should().Be(10000m);
    }

    // Sin cliente confirmado no se consulta SAP TM aunque el LLM mande un BP (nunca con texto libre, ADR-005).
    [Fact]
    public async Task SinClienteConfirmadoObtenerMargenNoConsultaSapConLoQueMandeElLlm()
    {
        _arnes.Modelo
            .Llamar("buscar_cliente", new { nombre = "Longyin" })
            .Llamar("obtener_margen", new { lineaNegocio = "Nacional", cliente = clsArnesAgente.BpLongyin })
            .Cerrar("ok");

        var resultado = (await _arnes.TurnoAsync("cotiza para Longyin")).Valor;

        resultado.Salidas[0].Error!.Codigo.Should().Be(clsCodigosError.ClienteBusquedaNoDisponible);
        _arnes.EntradasMargen.Single().BpCliente.Should().BeNull();
        _arnes.Estado().ClienteNombre.Should().Be("Longyin", "el texto queda solo como referencia visible");
        _arnes.Estado().NitCliente.Should().BeNull();
    }

    // ADR-003 §5: el PDF exige la confirmación explícita del comercial, en un turno posterior a la previsualización.
    [Fact]
    public async Task ElPdfSinConfirmacionRespondeConfirmacionRequeridaYNoSeGenera()
    {
        _arnes.Modelo
            .Llamar("obtener_margen", new { lineaNegocio = "Nacional" })
            .Llamar("calcular_precio", new { costo = 2_850_000, margen = 0.135, metodo = "margen" })
            .Llamar("generar_pdf", new { cotizacion = _payloadDelLlm })
            .Cerrar("Necesito tu confirmación.");

        var resultado = (await _arnes.TurnoAsync("cotiza y genera el PDF de una vez")).Valor;

        resultado.Salidas[^1].Error!.Codigo.Should().Be(clsCodigosError.ConfirmacionRequerida);
        await _arnes.Pdf.DidNotReceive().GenerarAsync(Arg.Any<clsEntradaGenerarPdf>(), Arg.Any<CancellationToken>());
        await _arnes.Auditoria.Received(1).RegistrarAsync("generar_pdf", Arg.Any<object?>(), Arg.Any<object?>(), false, Arg.Any<int>(), clsArnesAgente.Hilo);
    }

    [Fact]
    public async Task SinPrecioCalculadoElPdfNoSeGeneraAunqueConfirme()
    {
        _arnes.Modelo.Llamar("generar_pdf", new { cotizacion = _payloadDelLlm }).Cerrar("Falta el precio.");

        var resultado = (await _arnes.TurnoAsync("Confirmo")).Valor;

        resultado.Salidas.Single().Error!.Codigo.Should().Be(clsCodigosError.PdfPayloadInvalido);
        await _arnes.Pdf.DidNotReceive().GenerarAsync(Arg.Any<clsEntradaGenerarPdf>(), Arg.Any<CancellationToken>());
    }

    // ADR-004 §5: con candidatos por similitud, el PDF espera la confirmación del cliente; elegir por NIT lo confirma.
    [Fact]
    public async Task ConCandidatosPendientesElPdfEsperaYElegirPorNitConfirmaAlCliente()
    {
        _arnes.Buscador.BuscarAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(clsResultado<clsResultadoBusquedaClientes>.Exito(new clsResultadoBusquedaClientes(false,
            [
                new clsClienteEncontrado("900111222", "ACME S.A.S.", "cliente", "700"),
                new clsClienteEncontrado("800333444", "ACME LOGÍSTICA", "prospecto"),
            ])));
        _arnes.Modelo
            .Llamar("buscar_cliente", new { nombre = "acme" })
            .Cerrar("¿Cuál de los dos?")
            .Cerrar("Perfecto, ACME LOGÍSTICA.");

        await _arnes.TurnoAsync("cotiza para acme");
        _arnes.Estado().CandidatosCliente.Should().HaveCount(2);
        _arnes.Estado().ClienteConfirmado.Should().BeFalse();

        await _arnes.TurnoAsync("el de NIT 800.333.444");

        var estado = _arnes.Estado();
        estado.ClienteConfirmado.Should().BeTrue();
        estado.NitCliente.Should().Be("800333444");
        estado.OrigenCliente.Should().Be(OrigenCliente.Prospecto);
        estado.BpCliente.Should().BeNull();
        estado.CandidatosCliente.Should().BeEmpty();
    }

    // registrar_ajuste_margen: referencia, costo y método del estado; comercial del token.
    [Fact]
    public async Task ElAjusteNegociadoTomaLaReferenciaYElCostoDelEstado()
    {
        clsEntradaAjusteMargen? recibida = null;
        _arnes.Ajuste.RegistrarAsync(Arg.Do<clsEntradaAjusteMargen>(e => recibida = e), Arg.Any<CancellationToken>())
            .Returns(clsResultado<clsSalidaAjusteMargen>.Exito(new clsSalidaAjusteMargen(
                new clsSalidaMargen(0.11m, "negociado", null, false, "Ajuste"), true, -0.025m, Guid.NewGuid(), new clsSalidaPrecio(3_202_247m, 2_850_000m, 0.11m, "margen", 352_247m))));
        _arnes.Modelo
            .Llamar("obtener_margen", new { lineaNegocio = "Nacional" })
            .Llamar("calcular_precio", new { costo = 2_850_000, margen = 0.135, metodo = "margen" })
            .Llamar("registrar_ajuste_margen", new { margenAplicado = 0.11, motivo = "Negociación", margenPolitica = 0.5, comercial = "otro@x.co" })
            .Cerrar("Recalculé con 11%.");

        await _arnes.TurnoAsync("Ajusta el margen a 11%");

        recibida!.MargenPolitica.Should().Be(0.135m);
        recibida.MargenAplicado.Should().Be(0.11m);
        recibida.Costo.Should().Be(2_850_000m);
        recibida.UsuarioId.Should().Be(_arnes.Usuario.Id);
        recibida.ThreadId.Should().Be(clsArnesAgente.Hilo);
        var estado = _arnes.Estado();
        estado.OrigenMargen.Should().Be(OrigenMargen.Negociado);
        estado.MargenAplicado.Should().Be(0.11m);
        estado.PrecioFinal.Should().Be(3_202_247m);
        estado.ConfirmacionPendiente.Should().BeTrue();
    }

    // Auditoría: calcular_precio la audita el filtro; generar_pdf su caso de uso (decorador), sin duplicar.
    [Fact]
    public async Task LaAuditoriaDelFiltroNoDuplicaLaDeLosCasosDeUso()
    {
        _arnes.Modelo
            .Llamar("obtener_margen", new { lineaNegocio = "Nacional" })
            .Llamar("calcular_precio", new { costo = 2_850_000, margen = 0.135, metodo = "margen" })
            .Cerrar("¿Confirmas?")
            .Llamar("generar_pdf", new { cotizacion = _payloadDelLlm })
            .Cerrar("Listo.");

        await _arnes.TurnoAsync("cotiza");
        await _arnes.TurnoAsync("Confirmo");

        await _arnes.Auditoria.Received(1).RegistrarAsync("calcular_precio", Arg.Any<object?>(), Arg.Any<object?>(), true, Arg.Any<int>(), clsArnesAgente.Hilo);
        await _arnes.Auditoria.Received(1).RegistrarAsync("obtener_margen", Arg.Any<object?>(), Arg.Any<object?>(), true, Arg.Any<int>(), clsArnesAgente.Hilo);
        await _arnes.Auditoria.DidNotReceive().RegistrarAsync("generar_pdf", Arg.Any<object?>(), Arg.Any<object?>(), Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<string?>());
        clsAuditoriaService.HerramientasAuditadasPorCasoDeUso.Should().Contain("generar_pdf");
        await _arnes.Pdf.Received(1).GenerarAsync(Arg.Any<clsEntradaGenerarPdf>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LaEntradaAuditadaLlevaLosValoresQueInyectoElOrquestador()
    {
        object? entrada = null;
        await _arnes.Auditoria.RegistrarAsync("calcular_precio", Arg.Do<object?>(e => entrada = e), Arg.Any<object?>(), Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<string?>());
        _arnes.Modelo
            .Llamar("obtener_margen", new { lineaNegocio = "Nacional" })
            .Llamar("calcular_precio", new { costo = 2_850_000, margen = 0.9, metodo = "markup" })
            .Cerrar("ok");

        await _arnes.TurnoAsync("cotiza");

        var json = JsonSerializer.Serialize(entrada);
        json.Should().Contain("\"margen\":0.135").And.Contain("\"metodo\":\"margen\"").And.Contain(_arnes.Usuario.Id.ToString());
    }

    // enviar_correo: los destinatarios deben salir del mensaje del comercial (o una confirmación); la cotización, del estado.
    [Fact]
    public async Task ElCorreoExigeDestinatariosEscritosPorElComercialYUsaLaCotizacionDelEstado()
    {
        _arnes.OpcionesCorreo.Habilitado = true;
        _arnes.Correo.EnviarAsync(Arg.Any<clsEntradaEnviarCorreo>(), Arg.Any<CancellationToken>())
            .Returns(clsResultado<clsSalidaEnvioCorreo>.Exito(new clsSalidaEnvioCorreo(true, "<id@x>", true)));
        var sesion = await _arnes.Sesiones.ObtenerOCrearAsync(clsArnesAgente.Hilo, clsArnesAgente.Hilo, _arnes.Usuario.Id, TestContext.Current.CancellationToken);
        var cotizacionId = Guid.NewGuid();
        sesion.ActualizarEstadoAgente(Dominio.Agente.clsEstadoConversacion.Vacio with { CotizacionId = cotizacionId, FolioEnProgreso = "PCN-24092026-001" });
        _arnes.Modelo
            .Llamar("enviar_correo", new { para = new[] { "inventado@cliente.com" } })
            .Cerrar("¿A quién se lo envío?")
            .Llamar("enviar_correo", new { para = new[] { "compras@cliente.com" }, cotizacionId = Guid.NewGuid() })
            .Cerrar("Enviado.");

        var primero = (await _arnes.TurnoAsync("Envíalo al cliente")).Valor;
        await _arnes.TurnoAsync("Envíalo a compras@cliente.com por favor");

        primero.Salidas.Single().Error!.Codigo.Should().Be(clsCodigosError.ConfirmacionRequerida);
        await _arnes.Correo.Received(1).EnviarAsync(
            Arg.Is<clsEntradaEnviarCorreo>(e => e.CotizacionId == cotizacionId && e.UsuarioId == _arnes.Usuario.Id && e.Para.SequenceEqual(new[] { "compras@cliente.com" })),
            Arg.Any<CancellationToken>());
    }
}
