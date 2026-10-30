using Cotifly.Dominio.Cotizaciones;

namespace Cotifly.Dominio.Tests.Cotizaciones;

/// <summary>
/// HU-033 (#199, ADR-023, script 13): resultado ganada/perdida diferido a Nexo en el agregado. El resultado marcado antes de la entrega
/// queda <c>pendiente</c> con su comentario (recortado a 500), cada envío fallido suma un intento y guarda el error (recortado a 1000),
/// Nexo lo acepta (<c>sincronizado</c>) o ya no se puede enviar (<c>no_sincronizable</c>); y lo que ve el frontend
/// (<see cref="clsCotizacionSolicitud.EstadoSincronizacionResultado"/>) según el caso.
/// </summary>
public sealed class clsResultadoDiferidoTests
{
    private static readonly DateTimeOffset _ahora = new(2026, 10, 20, 10, 0, 0, TimeSpan.FromHours(-5));

    [Fact]
    public void ElResultadoPendienteGuardaElComentarioRecortadoYReiniciaIntentosYError()
    {
        var cotizacion = Enviada();
        cotizacion.RegistrarResultadoPendienteNexo(true, null, "primero", _ahora);
        cotizacion.RegistrarFalloSincronizacionResultado("Nexo no respondió");
        cotizacion.MarcarResultadoSincronizado(_ahora);

        cotizacion.RegistrarResultadoPendienteNexo(false, "Precio alto", "  " + new string('c', 600) + "  ", _ahora.AddHours(1));

        cotizacion.Ganada.Should().BeFalse();
        cotizacion.MotivoRechazo.Should().Be("Precio alto");
        cotizacion.MarcadoAt.Should().Be(_ahora.AddHours(1));
        cotizacion.SincronizacionResultado.Should().Be(SincronizacionResultadoNexo.Pendiente);
        cotizacion.ResultadoPendienteDeSincronizar.Should().BeTrue();
        cotizacion.ComentarioResultadoPendiente.Should().Be(new string('c', clsCotizacionSolicitud.LargoMaximoComentarioResultado),
            "se recorta (sin espacios) al largo de Nexo");
        cotizacion.ResultadoSincronizadoAt.Should().BeNull();
        cotizacion.ErrorSincronizacionResultado.Should().BeNull();
        cotizacion.IntentosSincronizacionResultado.Should().Be(0);
        cotizacion.EstadoSincronizacionResultado.Should().Be("pendiente");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void UnComentarioVacioQuedaNulo(string? comentario)
    {
        var cotizacion = Enviada();

        cotizacion.RegistrarResultadoPendienteNexo(true, null, comentario, _ahora);

        cotizacion.ComentarioResultadoPendiente.Should().BeNull();
    }

    [Fact]
    public void CadaFalloSumaUnIntentoYGuardaElErrorRecortadoSinDejarDeEstarPendiente()
    {
        var cotizacion = Enviada();
        cotizacion.RegistrarResultadoPendienteNexo(true, null, "Ganada por tarifa", _ahora);

        cotizacion.RegistrarFalloSincronizacionResultado("Nexo no respondió (HTTP 503)");
        cotizacion.RegistrarFalloSincronizacionResultado(" " + new string('e', 1500));

        cotizacion.IntentosSincronizacionResultado.Should().Be(2);
        cotizacion.ErrorSincronizacionResultado.Should().HaveLength(clsCotizacionSolicitud.LargoMaximoErrorSincronizacion)
            .And.EndWith("…").And.StartWith("eee");
        cotizacion.SincronizacionResultado.Should().Be(SincronizacionResultadoNexo.Pendiente);
        cotizacion.ComentarioResultadoPendiente.Should().Be("Ganada por tarifa", "el comentario se conserva para el siguiente intento");
        FluentActions.Invoking(() => cotizacion.RegistrarFalloSincronizacionResultado(" ")).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void AlSincronizarseBorraElComentarioPendienteYReiniciaLosIntentos()
    {
        var cotizacion = Enviada();
        cotizacion.RegistrarResultadoPendienteNexo(true, null, "Ganada por tarifa", _ahora);
        cotizacion.RegistrarFalloSincronizacionResultado("Nexo no respondió");

        cotizacion.MarcarResultadoSincronizado(_ahora.AddHours(2));

        cotizacion.SincronizacionResultado.Should().Be(SincronizacionResultadoNexo.Sincronizado);
        cotizacion.ResultadoPendienteDeSincronizar.Should().BeFalse();
        cotizacion.ResultadoSincronizadoAt.Should().Be(_ahora.AddHours(2));
        cotizacion.ComentarioResultadoPendiente.Should().BeNull();
        cotizacion.ErrorSincronizacionResultado.Should().BeNull();
        cotizacion.IntentosSincronizacionResultado.Should().Be(0);
        cotizacion.EstadoSincronizacionResultado.Should().Be("sincronizado");

        cotizacion.MarcarResultadoSincronizado(_ahora.AddHours(3), "  Nexo ya tenía el resultado  ");
        cotizacion.ErrorSincronizacionResultado.Should().Be("Nexo ya tenía el resultado", "la nota opcional se guarda limpia");
    }

    [Fact]
    public void NoSincronizableGuardaElMotivoYConservaLosIntentos()
    {
        var cotizacion = Enviada();
        cotizacion.RegistrarResultadoPendienteNexo(false, "Precio alto", null, _ahora);
        cotizacion.RegistrarFalloSincronizacionResultado("Nexo no respondió");

        cotizacion.MarcarResultadoNoSincronizable(new string('m', 1200));

        cotizacion.SincronizacionResultado.Should().Be(SincronizacionResultadoNexo.NoSincronizable);
        cotizacion.ResultadoPendienteDeSincronizar.Should().BeFalse();
        cotizacion.ErrorSincronizacionResultado.Should().HaveLength(clsCotizacionSolicitud.LargoMaximoErrorSincronizacion).And.EndWith("…");
        cotizacion.IntentosSincronizacionResultado.Should().Be(1);
        cotizacion.EstadoSincronizacionResultado.Should().Be("no_sincronizable");
        FluentActions.Invoking(() => cotizacion.MarcarResultadoNoSincronizable("")).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void SinResultadoNoHayEstadoDeSincronizacion()
    {
        Enviada().EstadoSincronizacionResultado.Should().BeNull();
        Chat().EstadoSincronizacionResultado.Should().BeNull();
    }

    [Fact]
    public void UnResultadoQueNuncaVaANexoEsSoloCotifly()
    {
        // Chat (fuera del flujo de costeo): resultado solo local.
        var chat = Chat();
        chat.MarcarSeguimiento(true, null, _ahora);

        // Costeo vencido: Nexo no acepta el resultado (HU-005.14).
        var vencida = Enviada();
        vencida.VencerPollingCosteo("72 h");
        vencida.EstadoCosteo.Should().Be(EstadoCosteo.CosteoVencido);
        vencida.MarcarSeguimiento(false, "Sin respuesta", _ahora);

        // HU-013.17: perdida antes del costeo, solo en Cotifly.
        var desistida = Enviada();
        desistida.RegistrarPerdidaSinCosteo("El cliente desistió", _ahora);

        foreach (var cotizacion in new[] { chat, vencida, desistida })
        {
            cotizacion.SincronizacionResultado.Should().BeNull();
            cotizacion.EstadoSincronizacionResultado.Should().Be(clsCotizacionSolicitud.CodigoSoloCotifly).And.Be("solo_cotifly");
        }
    }

    [Fact]
    public void UnResultadoAnteriorAlScript13MarcadoEnNexoSeVeSincronizado()
    {
        var cotizacion = Enviada();

        cotizacion.MarcarSeguimiento(true, null, _ahora);

        cotizacion.SincronizacionResultado.Should().BeNull("sin columna guardada (anterior al script 13)");
        cotizacion.EstadoSincronizacionResultado.Should().Be("sincronizado");
    }

    /// <summary>Cotización del formulario de costeo enviada a Nexo (<c>pendiente_costeo</c>, con <c>solicitudNexoId</c>).</summary>
    private static clsCotizacionSolicitud Enviada()
    {
        var cotizacion = Chat();
        cotizacion.RegistrarSolicitudCosteo(new clsSolicitudCosteo
        {
            Tipo = TipoSolicitud.Spot,
            Origen = "Buenaventura",
            Destino = "Bogotá D.C.",
            TipoVehiculo = "3S3",
            ClaseViaje = "Contenedor",
            ClaseContenedor = "CNT40.HC",
            HorasCargue = 4m,
            HorasDescargue = 6m,
            PesoKg = 28_500m,
            Posiciones = [new clsPosicionCarga("Estiba", 20, 120m, 100m, 150m, 1425m)],
        }, 3);
        cotizacion.RegistrarEnvioCosteo("sol-1", false, _ahora, _ahora);
        return cotizacion;
    }

    /// <summary>Cotización fuera del flujo de costeo (sin <c>estado_costeo</c> ni solicitud en Nexo).</summary>
    private static clsCotizacionSolicitud Chat() =>
        clsCotizacionSolicitud.Crear(new clsDatosCotizacion
        {
            Folio = "PCN-900555111-AP-20102026-001",
            NitCliente = "900555111",
            ClienteNombre = "CLIENTE S.A.S.",
            UsuarioId = Guid.NewGuid(),
        }).Valor;
}
