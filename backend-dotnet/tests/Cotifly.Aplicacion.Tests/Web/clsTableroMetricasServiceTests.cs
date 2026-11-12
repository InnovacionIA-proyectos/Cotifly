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
using Cotifly.Aplicacion.Tests.Identidad;
using NSubstitute;

namespace Cotifly.Aplicacion.Tests.Web;

/// <summary>
/// Tablero de métricas (ADR-016): rol revalidado, periodo por defecto y validado en fechas de Bogotá, alcance del
/// comercial y del gerente (regional resuelta, filtro por comercial de la regional, 403/404/409/503).
/// </summary>
public sealed class clsTableroMetricasServiceTests
{
    private static readonly TimeSpan _bogota = TimeSpan.FromHours(-5);
    private readonly IClsUsuarioWebRepository _usuarios = Substitute.For<IClsUsuarioWebRepository>();
    private readonly IClsRegionalUsuario _regionales;
    private readonly IClsMetricasRepository _metricas = Substitute.For<IClsMetricasRepository>();
    private readonly IClsRolRepository _roles = Substitute.For<IClsRolRepository>();
    private readonly IClsReloj _reloj = Substitute.For<IClsReloj>();
    private readonly clsCalendarioBogota _calendario = new(new DateOnly(2026, 9, 29));

    public clsTableroMetricasServiceTests()
    {
        // La regla real del alcance por rol sobre la regional simulada.
        _regionales = clsRegionalSustituta.Crear(_usuarios);
    }

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
    public async Task ConAnioElPeriodoEsElAnioCompletoEnBogotaAunqueTermineEnElFuturo()
    {
        var gerente = PrepararRegional().Gerente;
        SinFilas();

        var resultado = await Servicio(gerente).ObtenerRegionalAsync(new clsConsultaMetricas(Anio: "2026"), Ct);

        resultado.Valor.Periodo.Should().Be(new clsPeriodoMetricas(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), "America/Bogota"));
        resultado.Valor.SerieMensual.Should().HaveCount(12);
        await _metricas.Received(1).ListarCotizacionesAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(),
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, _bogota),
            new DateTimeOffset(2027, 1, 1, 0, 0, 0, _bogota),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("2026", "2", 2026, 2, 28)]
    [InlineData("2024", "2", 2024, 2, 29)]
    [InlineData("2026", "04", 2026, 4, 30)]
    [InlineData(" 2025 ", "12", 2025, 12, 31)]
    [InlineData("2027", "1", 2027, 1, 31)]
    [InlineData("2020", "1", 2020, 1, 31)]
    public async Task ConAnioYMesElPeriodoEsElMesCompletoEnBogota(string anio, string mes, int a, int m, int ultimoDia)
    {
        var gerente = PrepararRegional().Gerente;
        SinFilas();

        var resultado = await Servicio(gerente).ObtenerRegionalAsync(new clsConsultaMetricas(Anio: anio, Mes: mes), Ct);

        resultado.Valor.Periodo.Should().Be(new clsPeriodoMetricas(new DateOnly(a, m, 1), new DateOnly(a, m, ultimoDia), "America/Bogota"));
        resultado.Valor.SerieMensual.Should().ContainSingle().Which.Mes.Should().Be($"{a:D4}-{m:D2}");
        var inicio = new DateTimeOffset(a, m, 1, 0, 0, 0, _bogota);
        await _metricas.Received(1).ListarCotizacionesAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(), inicio, inicio.AddMonths(1), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ElAnioReemplazaADesdeYHastaSinCombinarse()
    {
        var gerente = PrepararRegional().Gerente;
        SinFilas();

        // desde/hasta inválidos entre sí no importan: con anio se ignoran.
        var resultado = await Servicio(gerente).ObtenerRegionalAsync(
            new clsConsultaMetricas("2026-05-01", "2026-04-01", null, "2025", "3"), Ct);

        resultado.Valor.Periodo.Should().Be(new clsPeriodoMetricas(new DateOnly(2025, 3, 1), new DateOnly(2025, 3, 31), "America/Bogota"));
    }

    [Theory]
    [InlineData(null, "4")]
    [InlineData(" ", "4")]
    [InlineData("2026", "0")]
    [InlineData("2026", "13")]
    [InlineData("2026", "-1")]
    [InlineData("2026", "+4")]
    [InlineData("2026", "4.0")]
    [InlineData("2026", "abril")]
    [InlineData("2019", null)]
    [InlineData("2028", null)]
    [InlineData("26", null)]
    [InlineData("dosmil", null)]
    [InlineData("2026.5", "4")]
    [InlineData("99999999999", null)]
    public async Task UnAnioOMesInvalidoEsSolicitudInvalidaSinConsultar(string? anio, string? mes)
    {
        var gerente = PrepararRegional().Gerente;

        var resultado = await Servicio(gerente).ObtenerRegionalAsync(new clsConsultaMetricas(Anio: anio, Mes: mes), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosAplicacion.SolicitudInvalida);
        await _metricas.DidNotReceiveWithAnyArgs().ListarCotizacionesAsync(default!, default, default, Ct);
    }

    [Fact]
    public async Task MesSinAnioTieneSuPropioMensajeYElAnioMaximoEsElEnCursoMasUno()
    {
        var gerente = PrepararRegional().Gerente;
        SinFilas();

        (await Servicio(gerente).ObtenerRegionalAsync(new clsConsultaMetricas(Mes: "4"), Ct)).Error.Mensaje
            .Should().Be("El parámetro 'mes' requiere 'anio'.");
        (await Servicio(gerente).ObtenerRegionalAsync(new clsConsultaMetricas(Anio: "2027"), Ct)).EsExitoso.Should().BeTrue();
        (await Servicio(gerente).ObtenerRegionalAsync(new clsConsultaMetricas(Anio: "2028"), Ct)).Error.Mensaje
            .Should().Be("El parámetro 'anio' debe ser un año entre 2020 y 2027.");
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LaSerieDeLaRegionalTraeGananciasYCosteoPorMesYCuadraConLosIndicadores(bool filtrarPorLuis)
    {
        var (gerente, ana, luis) = PrepararRegional();
        var abril = new DateTimeOffset(2026, 4, 2, 12, 0, 0, _bogota);
        _metricas.ListarCotizacionesAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new clsDatosMetricas(
                [Fila(ana.Id, ganada: true), Fila(ana.Id, ganada: false), Fila(luis.Id, ganada: true), Fila(luis.Id, ganada: true) with { CreatedAt = abril }],
                false));

        var resultado = (await Servicio(gerente).ObtenerRegionalAsync(
            new clsConsultaMetricas("2026-03-01", "2026-04-30", filtrarPorLuis ? luis.Id.ToString() : null), Ct)).Valor;

        var marzo = filtrarPorLuis ? ("2026-03", 850_000m, 850_000m, 150_000m) : ("2026-03", 2_550_000m, 1_700_000m, 300_000m);
        resultado.SerieMensual.Select(p => (p.Mes, p.CosteoTotal, p.CosteoGanadas, p.Ganancias))
            .Should().Equal(marzo, ("2026-04", 850_000m, 850_000m, 150_000m));
        (resultado.SerieMensual.Sum(p => p.CosteoTotal), resultado.SerieMensual.Sum(p => p.CosteoGanadas), resultado.SerieMensual.Sum(p => p.Ganancias))
            .Should().Be((resultado.Indicadores.CosteoTotal, resultado.Indicadores.CosteoGanadas, resultado.Indicadores.Ganancias));
    }

    [Fact]
    public async Task ConMesLaSerieTieneSoloEseMesYCuadraConLosIndicadores()
    {
        // anio=2026&mes=3 -> un solo punto, igual a los indicadores del periodo.
        var (gerente, ana, luis) = PrepararRegional();
        _metricas.ListarCotizacionesAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new clsDatosMetricas([Fila(ana.Id, ganada: true), Fila(ana.Id, ganada: false), Fila(luis.Id, ganada: true)], false));

        var resultado = (await Servicio(gerente).ObtenerRegionalAsync(new clsConsultaMetricas(Anio: "2026", Mes: "3"), Ct)).Valor;

        var punto = resultado.SerieMensual.Should().ContainSingle().Subject;
        punto.Should().Be(new clsPuntoSerieMensual("2026-03", 3, 2, 1, 3_000_000m, 2_000_000m, 2_550_000m, 1_700_000m, 300_000m));
        var indicadores = resultado.Indicadores;
        (punto.Cantidad, punto.ValorCotizadoCop, punto.ValorGanadoCop, punto.CosteoTotal, punto.CosteoGanadas, punto.Ganancias).Should().Be(
            (indicadores.Cantidad, indicadores.ValorCotizadoCop, indicadores.ValorGanadoCop, indicadores.CosteoTotal, indicadores.CosteoGanadas, indicadores.Ganancias));
    }

    [Fact]
    public async Task UnComercialSinCotizacionesTieneTodosLosMesesEnCero()
    {
        var comercial = Usuario(RolInterno.Comercial);
        SinFilas();

        var resultado = (await Servicio(comercial).ObtenerComercialAsync(new clsConsultaMetricas("2025-11-01", "2026-02-28"), Ct)).Valor;

        resultado.SerieMensual.Select(p => p.Mes).Should().Equal("2025-11", "2025-12", "2026-01", "2026-02");
        resultado.SerieMensual.Should().OnlyContain(p => p == new clsPuntoSerieMensual(p.Mes, 0, 0, 0, 0m, 0m, 0m, 0m, 0m));
        (resultado.Indicadores.CosteoTotal, resultado.Indicadores.CosteoGanadas, resultado.Indicadores.Ganancias).Should().Be((0m, 0m, 0m));
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
    public async Task ElDirectorSinRegionalVeATodosYFiltraUnComercialDeCualquierRegional()
    {
        // Todas las regionales; regional y fuente en null.
        var dora = UsuarioWeb("dora", "Dora");
        var ana = UsuarioWeb("ana", "Ana");
        var marta = clsUsuarioWeb.Crear("marta", "Marta", null, "Norte", DateTimeOffset.UtcNow).Valor;
        _usuarios.ObtenerPorIdAsync(dora.Id, Arg.Any<CancellationToken>()).Returns(dora);
        _usuarios.ListarTodosAsync(Arg.Any<CancellationToken>()).Returns([dora, ana, marta]);
        _roles.ListarRolesDeUsuariosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, RolInterno> { [dora.Id] = RolInterno.Director, [marta.Id] = RolInterno.Comercial });
        SinFilas();

        var tablero = (await Servicio(new clsUsuarioFalso(dora.Id, RolInterno.Director))
            .ObtenerRegionalAsync(new clsConsultaMetricas(UsuarioId: marta.Id.ToString()), Ct)).Valor;

        (tablero.Regional, tablero.FuenteRegional, tablero.UsuarioId).Should().Be(((string?)null, (string?)null, marta.Id));
        tablero.Comerciales.Select(c => c.UsuarioId).Should().BeEquivalentTo([dora.Id, ana.Id, marta.Id]);
        tablero.Comerciales.Single(c => c.UsuarioId == dora.Id).Rol.Should().Be("director");
        await _regionales.DidNotReceiveWithAnyArgs().ResolverAsync(default!, Ct);
    }

    [Fact]
    public async Task ElGerenteQuePideOtraRegionalEnElTableroRecibeRegionalNoPermitida()
    {
        var (gerente, _, _) = PrepararRegional();

        var resultado = await Servicio(gerente).ObtenerRegionalAsync(new clsConsultaMetricas(Regional: "Norte"), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosAplicacion.RegionalNoPermitida);
        await _metricas.DidNotReceiveWithAnyArgs().ListarCotizacionesAsync(default!, default, default, Ct);
    }

    [Fact]
    public async Task ElGerenteQuePideSuRegionalConOtraEscrituraNoFiltraUnComercialDeOtraRegional()
    {
        // ?regional= con la suya no amplía el alcance; un usuario_id de otra regional sigue siendo 404.
        var (gerente, _, _) = PrepararRegional();
        var marta = clsUsuarioWeb.Crear("marta", "Marta", null, "Norte", DateTimeOffset.UtcNow).Valor;

        var resultado = await Servicio(gerente).ObtenerRegionalAsync(
            new clsConsultaMetricas(UsuarioId: marta.Id.ToString(), Regional: "  oCCIDENTE "), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosAplicacion.NoEncontrado);
        await _metricas.DidNotReceiveWithAnyArgs().ListarCotizacionesAsync(default!, default, default, Ct);
    }

    [Fact]
    public async Task ElDirectorQueEligeUnaRegionalNoFiltraUnComercialDeOtra()
    {
        // Con ?regional= el alcance del director es esa regional; un comercial de otra es 404.
        PrepararRegional();
        var dora = UsuarioWeb("dora", "Dora");
        var marta = clsUsuarioWeb.Crear("marta", "Marta", null, "Norte", DateTimeOffset.UtcNow).Valor;
        _usuarios.ObtenerPorIdAsync(dora.Id, Arg.Any<CancellationToken>()).Returns(dora);

        var resultado = await Servicio(new clsUsuarioFalso(dora.Id, RolInterno.Director)).ObtenerRegionalAsync(
            new clsConsultaMetricas(UsuarioId: marta.Id.ToString(), Regional: "Occidente"), Ct);

        resultado.Error.Codigo.Should().Be(clsCodigosAplicacion.NoEncontrado);
        await _usuarios.DidNotReceiveWithAnyArgs().ListarTodosAsync(Ct);
        await _metricas.DidNotReceiveWithAnyArgs().ListarCotizacionesAsync(default!, default, default, Ct);
    }

    [Fact]
    public async Task ElDirectorQuePideUnaRegionalSinUsuariosRecibeElTableroVacio()
    {
        var dora = UsuarioWeb("dora", "Dora");
        _usuarios.ObtenerPorIdAsync(dora.Id, Arg.Any<CancellationToken>()).Returns(dora);
        _regionales.ListarMiembrosAsync("Inexistente", Arg.Any<CancellationToken>())
            .Returns(clsResultado<IReadOnlyList<clsUsuarioWeb>>.Exito([]));
        _roles.ListarRolesDeUsuariosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, RolInterno>());
        SinFilas();

        var tablero = (await Servicio(new clsUsuarioFalso(dora.Id, RolInterno.Director))
            .ObtenerRegionalAsync(new clsConsultaMetricas(Regional: " Inexistente "), Ct)).Valor;

        tablero.Regional.Should().Be("Inexistente");
        tablero.Comerciales.Should().BeEmpty();
        tablero.Indicadores.Cantidad.Should().Be(0);
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
