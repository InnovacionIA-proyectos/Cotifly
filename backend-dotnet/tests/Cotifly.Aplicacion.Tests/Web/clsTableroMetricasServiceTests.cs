using Cotifly.Aplicacion.Comun;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Identidad.Interfaces;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Aplicacion.Metricas.DTOs;
using Cotifly.Aplicacion.Metricas.Repositories;
using Cotifly.Aplicacion.Metricas.Services;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Identidad;
using Cotifly.Dominio.Metricas;
using NSubstitute;

namespace Cotifly.Aplicacion.Tests.Web;

/// <summary>
/// Tablero de métricas (HU-010.1, ADR-016): rol revalidado, periodo por defecto y validado en fechas de Bogotá, alcance del
/// comercial y del gerente (regional resuelta, filtro por comercial de la regional, 403/404/409/503).
/// </summary>
public sealed class clsTableroMetricasServiceTests
{
    private static readonly TimeSpan _bogota = TimeSpan.FromHours(-5);
    private readonly IClsUsuarioWebRepository _usuarios = Substitute.For<IClsUsuarioWebRepository>();
    private readonly IClsRegionalUsuario _regionales = Substitute.For<IClsRegionalUsuario>();
    private readonly IClsMetricasRepository _metricas = Substitute.For<IClsMetricasRepository>();
    private readonly IClsRolRepository _roles = Substitute.For<IClsRolRepository>();
    private readonly IClsReloj _reloj = Substitute.For<IClsReloj>();
    private readonly clsCalendarioBogota _calendario = new(new DateOnly(2026, 9, 29));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ElPeriodoPorDefectoEsElAnioEnCursoHastaHoyEnBogota()
    {
        var comercial = Usuario(RolInterno.Comercial);
        SinFilas();

        var resultado = await Servicio(comercial).ObtenerComercialAsync(new clsConsultaMetricas(), Ct);

        resultado.Valor.Periodo.Should().Be(new clsPeriodoMetricas(new DateOnly(2026, 1, 1), new DateOnly(2026, 9, 29), "America/Bogota"));
        resultado.Valor.SerieMensual.Should().HaveCount(9, "de enero a septiembre, también los meses vacíos");
        await _metricas.Received(1).ListarCotizacionesAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(u => u.Single() == comercial.UsuarioId),
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, _bogota),
            new DateTimeOffset(2026, 9, 30, 0, 0, 0, _bogota),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("2026-13-01", null)]
    [InlineData("01/01/2026", null)]
    [InlineData(null, "ayer")]
    [InlineData("2026-05-01", "2026-04-30")]
    [InlineData("2020-01-01", "2023-01-02")]
    public async Task UnPeriodoInvalidoEsSolicitudInvalida(string? desde, string? hasta)
    {
        var resultado = await Servicio(Usuario(RolInterno.Comercial)).ObtenerComercialAsync(new clsConsultaMetricas(desde, hasta), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosAplicacion.SolicitudInvalida);
        await _metricas.DidNotReceiveWithAnyArgs().ListarCotizacionesAsync(default!, default, default, Ct);
    }

    [Fact]
    public async Task SinSesionNoHayMetricas()
    {
        var resultado = await Servicio(new clsUsuarioFalso(null, null)).ObtenerComercialAsync(new clsConsultaMetricas(), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosAplicacion.NoAutenticado);
    }

    [Fact]
    public async Task ElComercialVeSusIndicadoresCalculados()
    {
        var comercial = Usuario(RolInterno.Comercial);
        _metricas.ListarCotizacionesAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new clsDatosMetricas([Fila(comercial.UsuarioId!.Value, ganada: true), Fila(comercial.UsuarioId!.Value, ganada: false)], false));

        var resultado = await Servicio(comercial).ObtenerComercialAsync(new clsConsultaMetricas("2026-03-01", "2026-03-31"), Ct);

        resultado.Valor.UsuarioId.Should().Be(comercial.UsuarioId!.Value);
        resultado.Valor.Indicadores.Cantidad.Should().Be(2);
        resultado.Valor.Indicadores.Conversion.Should().Be(0.5m);
        resultado.Valor.SerieMensual.Should().ContainSingle().Which.Mes.Should().Be("2026-03");
    }

    [Fact]
    public async Task UnComercialNoVeLaRegionalAunqueLlegueAlCasoDeUso()
    {
        var resultado = await Servicio(Usuario(RolInterno.Comercial)).ObtenerRegionalAsync(new clsConsultaMetricas(), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosAplicacion.SinPermiso);
        await _regionales.DidNotReceiveWithAnyArgs().ResolverAsync(default!, Ct);
    }

    [Theory]
    [InlineData(clsCodigosAplicacion.RegionalNoAsignada)]
    [InlineData(clsCodigosAplicacion.RegionalNoDisponible)]
    public async Task SinRegionalElGerenteRecibeElErrorYNoSeConsultaNada(string codigo)
    {
        var gerente = Usuario(RolInterno.GerenteComercial);
        var usuarioGerente = UsuarioWeb("gloria", null);
        _usuarios.ObtenerPorIdAsync(gerente.UsuarioId!.Value, Arg.Any<CancellationToken>()).Returns(usuarioGerente);
        _regionales.ResolverAsync(usuarioGerente, Arg.Any<CancellationToken>()).Returns(clsResultado<clsRegionalResuelta>.Fallo(new clsErrorDominio(codigo, "sin regional")));

        var resultado = await Servicio(gerente).ObtenerRegionalAsync(new clsConsultaMetricas(), Ct);

        resultado.Error.Codigo.Should().Be(codigo);
        await _metricas.DidNotReceiveWithAnyArgs().ListarCotizacionesAsync(default!, default, default, Ct);
    }

    [Fact]
    public async Task ElGerenteVeElAgregadoDeLosMiembrosDeSuRegionalYLaListaDeComerciales()
    {
        var (gerente, ana, luis) = PrepararRegional();
        _metricas.ListarCotizacionesAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new clsDatosMetricas([Fila(ana.Id, ganada: true), Fila(ana.Id, ganada: null), Fila(luis.Id, ganada: true)], false));
        _metricas.ContarLineasNegocioActivasAsync(Arg.Any<CancellationToken>()).Returns(8);

        var resultado = (await Servicio(gerente).ObtenerRegionalAsync(new clsConsultaMetricas("2026-03-01", "2026-03-31"), Ct)).Valor;

        resultado.Regional.Should().Be("Occidente");
        resultado.FuenteRegional.Should().Be(clsRegionalResuelta.FuenteUsuariosWeb);
        resultado.UsuarioId.Should().BeNull();
        resultado.Indicadores.Cantidad.Should().Be(3);
        resultado.Indicadores.Ganadas.Should().Be(2);
        resultado.LineasNegocioActivas.Should().Be(8);
        resultado.Comerciales.Select(c => (c.NombreCompleto, c.Rol, c.Indicadores.Cantidad)).Should().Equal(("Ana", "comercial", 2), ("Gloria", "gerente_comercial", 0), ("Luis", (string?)null, 1));
        resultado.Indicadores.DeadlineVencidas.Should().BeNull();
        resultado.ConcentracionCiudades.Disponible.Should().BeFalse();
        await _metricas.Received(1).ListarCotizacionesAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(u => u.Count == 3 && u.Contains(ana.Id) && u.Contains(luis.Id)),
            Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ElFiltroPorComercialRecalculaLosIndicadoresPeroConservaLaLista()
    {
        var (gerente, ana, luis) = PrepararRegional();
        _metricas.ListarCotizacionesAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new clsDatosMetricas([Fila(ana.Id, ganada: true), Fila(ana.Id, ganada: null), Fila(luis.Id, ganada: true)], false));

        var resultado = (await Servicio(gerente).ObtenerRegionalAsync(new clsConsultaMetricas(null, null, luis.Id.ToString()), Ct)).Valor;

        resultado.UsuarioId.Should().Be(luis.Id);
        resultado.Indicadores.Cantidad.Should().Be(1);
        resultado.Comerciales.Should().HaveCount(3);
    }

    [Fact]
    public async Task FiltrarPorAlguienFueraDeLaRegionalEsNoEncontrado()
    {
        var (gerente, _, _) = PrepararRegional();

        var resultado = await Servicio(gerente).ObtenerRegionalAsync(new clsConsultaMetricas(null, null, Guid.NewGuid().ToString()), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosAplicacion.NoEncontrado);
        resultado.Error.Mensaje.Should().Be(clsTableroMetricasService.MensajeComercialAjeno);
        await _metricas.DidNotReceiveWithAnyArgs().ListarCotizacionesAsync(default!, default, default, Ct);
    }

    [Fact]
    public async Task UnUsuarioIdQueNoEsUuidEsSolicitudInvalida()
    {
        var resultado = await Servicio(Usuario(RolInterno.GerenteComercial)).ObtenerRegionalAsync(new clsConsultaMetricas(null, null, "luis"), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosAplicacion.SolicitudInvalida);
    }

    private (clsUsuarioFalso Gerente, clsUsuarioWeb Ana, clsUsuarioWeb Luis) PrepararRegional()
    {
        var usuarioGerente = UsuarioWeb("gloria", "Gloria");
        var gerente = new clsUsuarioFalso(usuarioGerente.Id, RolInterno.GerenteComercial);
        var ana = UsuarioWeb("ana", "Ana");
        var luis = UsuarioWeb("luis", "Luis");
        _usuarios.ObtenerPorIdAsync(usuarioGerente.Id, Arg.Any<CancellationToken>()).Returns(usuarioGerente);
        _regionales.ResolverAsync(usuarioGerente, Arg.Any<CancellationToken>())
            .Returns(clsResultado<clsRegionalResuelta>.Exito(new clsRegionalResuelta("Occidente", clsRegionalResuelta.FuenteUsuariosWeb)));
        _regionales.ListarMiembrosAsync("Occidente", Arg.Any<CancellationToken>())
            .Returns(clsResultado<IReadOnlyList<clsUsuarioWeb>>.Exito([luis, usuarioGerente, ana]));
        _roles.ListarRolesDeUsuariosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, RolInterno> { [ana.Id] = RolInterno.Comercial, [usuarioGerente.Id] = RolInterno.GerenteComercial });
        return (gerente, ana, luis);
    }

    private void SinFilas() =>
        _metricas.ListarCotizacionesAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new clsDatosMetricas([], false));

    private static clsCotizacionMetrica Fila(Guid usuarioId, bool? ganada) => new()
    {
        UsuarioId = usuarioId,
        CreatedAt = new DateTimeOffset(2026, 3, 10, 12, 0, 0, _bogota),
        PrecioFinal = 1_000_000m,
        Costo = 850_000m,
        Ganada = ganada,
    };

    private static clsUsuarioWeb UsuarioWeb(string nombre, string? nombreCompleto) =>
        clsUsuarioWeb.Crear(nombre, nombreCompleto, null, "Occidente", DateTimeOffset.UtcNow).Valor;

    private static clsUsuarioFalso Usuario(RolInterno rol) => new(Guid.NewGuid(), rol);

    private clsTableroMetricasService Servicio(IClsContextoUsuario usuario) => new(usuario, _calendario, _usuarios, _regionales, _roles, _reloj, _metricas);

    private sealed class clsUsuarioFalso(Guid? usuarioId, RolInterno? rol) : IClsContextoUsuario
    {
        public bool EstaAutenticado => UsuarioId is not null;

        public Guid? UsuarioId { get; } = usuarioId;

        public string? NombreUsuario => UsuarioId is null ? null : "usuario.prueba";

        public RolInterno? Rol { get; } = rol;
    }

    /// <summary>Calendario fijo en UTC−5 (Bogotá no tiene horario de verano).</summary>
    private sealed class clsCalendarioBogota(DateOnly hoy) : IClsCalendarioNegocio
    {
        public DateOnly Hoy { get; } = hoy;

        public string ZonaHoraria => "America/Bogota";

        public DateOnly FechaDe(DateTimeOffset instante) => DateOnly.FromDateTime(instante.ToOffset(_bogota).DateTime);

        public DateTimeOffset InicioDelDia(DateOnly fecha) => new(fecha.ToDateTime(TimeOnly.MinValue), _bogota);
    }
}
