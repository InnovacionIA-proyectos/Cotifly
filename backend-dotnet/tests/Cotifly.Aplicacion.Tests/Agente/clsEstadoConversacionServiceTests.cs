using Cotifly.Aplicacion.Agente.DTOs;
using Cotifly.Aplicacion.Agente.Services;
using Cotifly.Aplicacion.Clientes.DTOs;
using Cotifly.Aplicacion.Margen.DTOs;
using Cotifly.Aplicacion.Pdf.DTOs;
using Cotifly.Aplicacion.Precio.DTOs;
using Cotifly.Dominio.Agente;
using Cotifly.Dominio.Clientes;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Margen;

namespace Cotifly.Aplicacion.Tests.Agente;

/// <summary>
/// <c>clsEstadoConversacionService</c> (ADR-004 §3): el estado solo cambia con salidas exitosas de herramientas y con una
/// confirmación explícita que reconoce el runner; un error no cambia nada.
/// </summary>
public sealed class clsEstadoConversacionServiceTests
{
    private static readonly Dictionary<string, object?> _sinArgumentos = [];
    private readonly clsEstadoConversacionService _servicio = new();

    [Fact]
    public void UnaCoincidenciaExactaLlenaElClienteConSuBp()
    {
        var busqueda = new clsResultadoBusquedaClientes(true, [new clsClienteEncontrado("900123456", "LONGYIN S.A.S.", "cliente", "15927")]);

        var estado = _servicio.Aplicar(clsEstadoConversacion.Vacio, clsResultadoHerramienta.Exito(clsNombresHerramientas.BuscarCliente, new { }, busqueda), _sinArgumentos);

        estado.NitCliente.Should().Be("900123456");
        estado.ClienteNombre.Should().Be("LONGYIN S.A.S.");
        estado.OrigenCliente.Should().Be(OrigenCliente.Cliente);
        estado.BpCliente.Should().Be("15927");
        estado.ClienteConfirmado.Should().BeTrue();
    }

    [Fact]
    public void VariasCoincidenciasQuedanComoCandidatasSinConfirmar()
    {
        var busqueda = new clsResultadoBusquedaClientes(false, [new clsClienteEncontrado("1", "A", "cliente"), new clsClienteEncontrado("2", "B", "prospecto")]);

        var estado = _servicio.Aplicar(clsEstadoConversacion.Vacio, clsResultadoHerramienta.Exito(clsNombresHerramientas.BuscarCliente, new { }, busqueda), _sinArgumentos);

        estado.ClienteConfirmado.Should().BeFalse();
        estado.NitCliente.Should().BeNull();
        estado.CandidatosCliente.Select(c => c.Nombre).Should().Equal("A", "B");
    }

    [Fact]
    public void SinCatalogoElTextoQuedaComoReferenciaSinNit()
    {
        var error = clsResultadoHerramienta.Fallo(clsNombresHerramientas.BuscarCliente, clsCodigosError.ClienteBusquedaNoDisponible, "no disponible");

        var estado = _servicio.Aplicar(clsEstadoConversacion.Vacio, error, new Dictionary<string, object?> { ["nombre"] = "Falabella" });

        estado.ClienteNombre.Should().Be("Falabella");
        estado.NitCliente.Should().BeNull();
        estado.ClienteConfirmado.Should().BeFalse();
    }

    [Fact]
    public void UnErrorDeOtraHerramientaNoCambiaElEstado()
    {
        var inicial = clsEstadoConversacion.Vacio with { MargenAplicado = 0.15m };

        var estado = _servicio.Aplicar(inicial, clsResultadoHerramienta.Fallo(clsNombresHerramientas.CalcularPrecio, clsCodigosError.PrecioInvalido, "x"), _sinArgumentos);

        estado.Should().Be(inicial);
    }

    [Fact]
    public void ElMargenLlenaLineaMetodoYReferenciasYAnulaUnPrecioAnterior()
    {
        var resuelto = new clsMargenResuelto(new clsSalidaMargen(0.12m, "sap_tm", "OPTN", false, "SAP"), "Nacional", 1, 0.15m, "margen");
        var inicial = clsEstadoConversacion.Vacio with { PrecioFinal = 1m, ConfirmacionPendiente = true };

        var estado = _servicio.Aplicar(inicial, clsResultadoHerramienta.Exito(clsNombresHerramientas.ObtenerMargen, resuelto.Salida, resuelto), _sinArgumentos);

        estado.LineaNegocio.Should().Be("Nacional");
        estado.LineaNegocioId.Should().Be(1);
        estado.MargenPolitica.Should().Be(0.15m);
        estado.MargenSugerido.Should().Be(0.12m);
        estado.MargenAplicado.Should().Be(0.12m);
        estado.OrigenMargen.Should().Be(OrigenMargen.SapTm);
        estado.MetodoCalculo.Should().Be("margen");
        estado.PrecioFinal.Should().BeNull();
        estado.ConfirmacionPendiente.Should().BeFalse();
    }

    [Fact]
    public void ElPrecioDejaLaPrevisualizacionPendienteDeConfirmar()
    {
        var precio = new clsSalidaPrecio(3_294_798m, 2_850_000m, 0.135m, "margen", 444_798m);

        var estado = _servicio.Aplicar(clsEstadoConversacion.Vacio, clsResultadoHerramienta.Exito(clsNombresHerramientas.CalcularPrecio, precio), _sinArgumentos);

        estado.PrecioFinal.Should().Be(3_294_798m);
        estado.Costo.Should().Be(2_850_000m);
        estado.FuenteCosto.Should().Be(clsEstadoConversacion.FuenteCostoManual);
        estado.Moneda.Should().Be("COP");
        estado.ConfirmacionPendiente.Should().BeTrue();
    }

    [Fact]
    public void ElPdfGeneradoGuardaCotizacionYFolioYCierraLaConfirmacion()
    {
        var id = Guid.NewGuid();
        var inicial = clsEstadoConversacion.Vacio with { ConfirmacionPendiente = true };

        var estado = _servicio.Aplicar(inicial, clsResultadoHerramienta.Exito(clsNombresHerramientas.GenerarPdf, new clsSalidaPdf(id, "PCN-NV-24092026-001-v2", "/pdfs/x.pdf", true)), _sinArgumentos);

        estado.CotizacionId.Should().Be(id);
        estado.FolioEnProgreso.Should().Be("PCN-NV-24092026-001-v2");
        estado.Version.Should().Be(2);
        estado.ConfirmacionPendiente.Should().BeFalse();
    }

    [Theory]
    [InlineData("Confirmo", true)]
    [InlineData("confirmo.", true)]
    [InlineData("Sí, dale", true)]
    [InlineData("  ok  ", true)]
    [InlineData("Confirmar y generar PDF", true)]
    [InlineData("Genera el PDF", true)]
    [InlineData("no", false)]
    [InlineData("No confirmo todavía", false)]
    [InlineData("¿cuánto sale?", false)]
    [InlineData("Ajusta el margen a 11%", false)]
    public void ReconoceLasConfirmacionesExplicitas(string mensaje, bool esperado) =>
        clsEstadoConversacionService.EsConfirmacion(mensaje).Should().Be(esperado);

    [Fact]
    public void UnaConfirmacionSoloValeConUnaPrevisualizacionPendiente()
    {
        var sinPrevisualizacion = _servicio.ReconocerMensaje(clsEstadoConversacion.Vacio, "Confirmo");
        var conPrevisualizacion = _servicio.ReconocerMensaje(clsEstadoConversacion.Vacio with { ConfirmacionPendiente = true, PrecioFinal = 10m }, "Confirmo");

        sinPrevisualizacion.Confirmado.Should().BeFalse();
        conPrevisualizacion.Confirmado.Should().BeTrue();
    }

    [Theory]
    [InlineData("2", "800333444")]
    [InlineData("opción 1", "900111222")]
    [InlineData("el segundo", "800333444")]
    [InlineData("ACME Logística", "800333444")]
    [InlineData("NIT 900.111.222-1", "900111222")]
    public void ElComercialEligeUnCandidatoPorPosicionNombreONit(string mensaje, string nit)
    {
        var estado = clsEstadoConversacion.Vacio with
        {
            PrecioFinal = 10m,
            ConfirmacionPendiente = true,
            CandidatosCliente = [new clsCandidatoCliente("900111222", "ACME S.A.S.", OrigenCliente.Cliente, "700"), new clsCandidatoCliente("800333444", "ACME LOGÍSTICA", OrigenCliente.Prospecto)],
        };

        var reconocido = _servicio.ReconocerMensaje(estado, mensaje);

        reconocido.Estado.NitCliente.Should().Be(nit);
        reconocido.Estado.ClienteConfirmado.Should().BeTrue();
        reconocido.Confirmado.Should().BeFalse("elegir el cliente no confirma el PDF");
    }
}
