using System.Globalization;
using Cotifly.Aplicacion.Comun;
using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Identidad.Interfaces;
using Cotifly.Aplicacion.Identidad.Repositories;
using Cotifly.Aplicacion.Metricas.DTOs;
using Cotifly.Aplicacion.Metricas.Repositories;
using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Identidad;
using Cotifly.Dominio.Metricas;

namespace Cotifly.Aplicacion.Metricas.Services;

/// <summary>
/// Tablero de métricas agregadas (HU-010.1, ADR-016): <c>GET /web/metricas/comercial</c> y <c>/regional</c>. El rol sale del
/// token (claim <c>rol</c>) y se revalida aquí; el cálculo lo hace <see cref="clsCalculadoraMetricas"/>.
/// </summary>
public interface IClsTableroMetricasService
{
    /// <summary>Indicadores y serie de las cotizaciones propias (comercial o gerente).</summary>
    Task<clsResultado<clsMetricasComercial>> ObtenerComercialAsync(clsConsultaMetricas consulta, CancellationToken cancellationToken = default);

    /// <summary>
    /// Indicadores de la regional del gerente (o de un comercial suyo con <c>usuario_id</c>), lista de comerciales, serie y
    /// concentración por ciudad. Solo <c>gerente_comercial</c>.
    /// </summary>
    Task<clsResultado<clsMetricasRegional>> ObtenerRegionalAsync(clsConsultaMetricas consulta, CancellationToken cancellationToken = default);
}

/// <summary>Implementación de <see cref="IClsTableroMetricasService"/>.</summary>
public sealed class clsTableroMetricasService(
    IClsContextoUsuario usuario,
    IClsCalendarioNegocio calendario,
    IClsUsuarioWebRepository usuarios,
    IClsRegionalUsuario regionales,
    IClsRolRepository roles,
    IClsReloj reloj,
    IClsMetricasRepository metricas) : IClsTableroMetricasService
{
    /// <summary>Rango máximo del periodo (la serie mensual queda acotada a 37 puntos).</summary>
    public const int AniosMaximos = 3;

    public const string FormatoFecha = "yyyy-MM-dd";

    /// <summary>Primer año que acepta <c>anio</c> (HU-010.5); el último es el año en curso más uno (en Bogotá).</summary>
    public const int AnioMinimo = 2020;
    public const string MensajeComercialAjeno = "Comercial no encontrado en tu regional.";

    public async Task<clsResultado<clsMetricasComercial>> ObtenerComercialAsync(clsConsultaMetricas consulta, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(consulta);
        if (usuario.UsuarioId is not { } usuarioId || usuario.Rol is null)
        {
            return ErrorNoAutenticado();
        }

        var periodo = ResolverPeriodo(consulta);
        if (periodo.EsFallo)
        {
            return periodo.Error;
        }

        var datos = await ListarAsync([usuarioId], periodo.Valor, cancellationToken);
        return new clsMetricasComercial(
            periodo.Valor,
            usuarioId,
            Indicadores(datos.Filas, datos),
            Serie(datos.Filas, periodo.Valor),
            clsCalculadoraMetricas.PorLineaNegocio(datos.Filas));
    }

    public async Task<clsResultado<clsMetricasRegional>> ObtenerRegionalAsync(clsConsultaMetricas consulta, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(consulta);
        if (usuario.UsuarioId is not { } usuarioId || usuario.Rol is not { } rol)
        {
            return ErrorNoAutenticado();
        }

        if (rol != RolInterno.GerenteComercial)
        {
            return new clsErrorDominio(clsCodigosAplicacion.SinPermiso, "Solo el gerente comercial ve las métricas de la regional.");
        }

        var periodo = ResolverPeriodo(consulta);
        if (periodo.EsFallo)
        {
            return periodo.Error;
        }

        Guid? filtro = null;
        if (!string.IsNullOrWhiteSpace(consulta.UsuarioId))
        {
            if (!Guid.TryParse(consulta.UsuarioId.Trim(), out var id))
            {
                return Invalida("El parámetro 'usuario_id' debe ser un UUID.");
            }

            filtro = id;
        }

        var gerente = await usuarios.ObtenerPorIdAsync(usuarioId, cancellationToken);
        if (gerente is null)
        {
            return ErrorNoAutenticado();
        }

        var regional = await regionales.ResolverAsync(gerente, cancellationToken);
        if (regional.EsFallo)
        {
            return regional.Error;
        }

        var miembros = await regionales.ListarMiembrosAsync(regional.Valor.Regional, cancellationToken);
        if (miembros.EsFallo)
        {
            return miembros.Error;
        }

        if (filtro is { } filtrado && miembros.Valor.All(m => m.Id != filtrado))
        {
            return new clsErrorDominio(clsCodigosAplicacion.NoEncontrado, MensajeComercialAjeno);
        }

        var datos = await ListarAsync([.. miembros.Valor.Select(m => m.Id)], periodo.Valor, cancellationToken);
        var porUsuario = datos.Filas.ToLookup(f => f.UsuarioId);
        IReadOnlyList<clsCotizacionMetrica> seleccion = filtro is { } uno ? [.. porUsuario[uno]] : datos.Filas;

        var rolesMiembros = await roles.ListarRolesDeUsuariosAsync([.. miembros.Valor.Select(m => m.Id)], cancellationToken);
        var comerciales = miembros.Valor
            .OrderBy(m => m.NombreCompleto ?? m.NombreUsuario ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(m => m.Id)
            .Select(m => new clsComercialRegional(
                m.Id,
                m.NombreUsuario,
                m.NombreCompleto,
                rolesMiembros.TryGetValue(m.Id, out var rolMiembro) ? clsCodigoEnum.ACodigo(rolMiembro) : null,
                Indicadores(porUsuario[m.Id], datos)))
            .ToList();

        return new clsMetricasRegional(
            periodo.Valor,
            regional.Valor.Regional,
            regional.Valor.Fuente,
            filtro,
            Indicadores(seleccion, datos),
            await metricas.ContarLineasNegocioActivasAsync(cancellationToken),
            comerciales,
            Serie(seleccion, periodo.Valor),
            clsCalculadoraMetricas.Concentracion(seleccion, datos.CiudadesDisponibles),
            clsCalculadoraMetricas.PorLineaNegocio(seleccion));
    }

    /// <summary>
    /// Periodo de la consulta en fechas de negocio: por defecto del 1 de enero del año en curso a hoy. 400 si una fecha no es
    /// <c>AAAA-MM-DD</c>, si <c>hasta</c> es anterior a <c>desde</c> o si el rango supera <see cref="AniosMaximos"/> años.
    /// HU-010.5 (#187): con <c>anio</c> el periodo es ese año completo y, con <c>mes</c>, ese mes completo (fechas de Bogotá); reemplaza a
    /// <c>desde</c>/<c>hasta</c>, que se ignoran. 400 si <c>mes</c> llega sin <c>anio</c>, si no es un entero de 1 a 12 o si <c>anio</c>
    /// no es un entero de <see cref="AnioMinimo"/> al año en curso más uno. Solo la regional los pasa: en el comercial siempre son nulos.
    /// </summary>
    public clsResultado<clsPeriodoMetricas> ResolverPeriodo(clsConsultaMetricas consulta)
    {
        ArgumentNullException.ThrowIfNull(consulta);
        var hoy = calendario.Hoy;

        if (!string.IsNullOrWhiteSpace(consulta.Anio) || !string.IsNullOrWhiteSpace(consulta.Mes))
        {
            return PeriodoAnioMes(consulta.Anio, consulta.Mes, hoy);
        }

        if (!LeerFecha(consulta.Desde, "desde", new DateOnly(hoy.Year, 1, 1), out var desde, out var errorDesde))
        {
            return errorDesde!;
        }

        if (!LeerFecha(consulta.Hasta, "hasta", hoy, out var hasta, out var errorHasta))
        {
            return errorHasta!;
        }

        if (hasta < desde)
        {
            return Invalida("El parámetro 'hasta' no puede ser anterior a 'desde'.");
        }

        if (hasta > desde.AddYears(AniosMaximos))
        {
            return Invalida($"El periodo no puede superar {AniosMaximos} años.");
        }

        return new clsPeriodoMetricas(desde, hasta, calendario.ZonaHoraria);
    }

    /// <summary>El año completo, o el mes completo de ese año (HU-010.5).</summary>
    private clsResultado<clsPeriodoMetricas> PeriodoAnioMes(string? textoAnio, string? textoMes, DateOnly hoy)
    {
        if (string.IsNullOrWhiteSpace(textoAnio))
        {
            return Invalida("El parámetro 'mes' requiere 'anio'.");
        }

        var anioMaximo = hoy.Year + 1;
        if (!LeerEntero(textoAnio, out var anio) || anio < AnioMinimo || anio > anioMaximo)
        {
            return Invalida($"El parámetro 'anio' debe ser un año entre {AnioMinimo} y {anioMaximo}.");
        }

        if (string.IsNullOrWhiteSpace(textoMes))
        {
            return new clsPeriodoMetricas(new DateOnly(anio, 1, 1), new DateOnly(anio, 12, 31), calendario.ZonaHoraria);
        }

        if (!LeerEntero(textoMes, out var mes) || mes is < 1 or > 12)
        {
            return Invalida("El parámetro 'mes' debe ser un número de 1 a 12.");
        }

        var inicio = new DateOnly(anio, mes, 1);
        return new clsPeriodoMetricas(inicio, inicio.AddMonths(1).AddDays(-1), calendario.ZonaHoraria);
    }

    /// <summary>Entero sin signo ni separadores (<c>2026</c>, <c>04</c>); nada de <c>+4</c>, <c>4.0</c> ni <c>2 026</c>.</summary>
    private static bool LeerEntero(string texto, out int valor) =>
        int.TryParse(texto.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out valor);

    private Task<clsDatosMetricas> ListarAsync(IReadOnlyCollection<Guid> ids, clsPeriodoMetricas periodo, CancellationToken cancellationToken) =>
        metricas.ListarCotizacionesAsync(ids, calendario.InicioDelDia(periodo.Desde), calendario.InicioDelDia(periodo.Hasta.AddDays(1)), cancellationToken);

    private clsIndicadoresMetricas Indicadores(IEnumerable<clsCotizacionMetrica> filas, clsDatosMetricas datos) =>
        clsCalculadoraMetricas.Indicadores(filas, reloj.AhoraUtc, datos.DeadlineDisponible);

    private IReadOnlyList<clsPuntoSerieMensual> Serie(IEnumerable<clsCotizacionMetrica> filas, clsPeriodoMetricas periodo) =>
        clsCalculadoraMetricas.SerieMensual(filas, periodo.Desde, periodo.Hasta, calendario.FechaDe);

    private static bool LeerFecha(string? texto, string parametro, DateOnly porDefecto, out DateOnly fecha, out clsErrorDominio? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(texto))
        {
            fecha = porDefecto;
            return true;
        }

        if (DateOnly.TryParseExact(texto.Trim(), FormatoFecha, CultureInfo.InvariantCulture, DateTimeStyles.None, out fecha))
        {
            return true;
        }

        error = Invalida($"El parámetro '{parametro}' debe ser una fecha AAAA-MM-DD.");
        return false;
    }

    private static clsErrorDominio Invalida(string mensaje) => new(clsCodigosAplicacion.SolicitudInvalida, mensaje);

    private static clsErrorDominio ErrorNoAutenticado() => new(clsCodigosAplicacion.NoAutenticado, "Inicia sesión de nuevo.");
}
