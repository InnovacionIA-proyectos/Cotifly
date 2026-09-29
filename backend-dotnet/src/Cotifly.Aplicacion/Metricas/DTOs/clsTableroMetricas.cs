using Cotifly.Dominio.Metricas;

namespace Cotifly.Aplicacion.Metricas.DTOs;

/// <summary>Parámetros de consulta del tablero tal como llegan (<c>desde</c>, <c>hasta</c>, <c>usuario_id</c>); los valida el caso de uso.</summary>
public sealed record clsConsultaMetricas(string? Desde = null, string? Hasta = null, string? UsuarioId = null);

/// <summary>Periodo aplicado (esquema <c>PeriodoMetricas</c>).</summary>
public sealed record clsPeriodoMetricas(DateOnly Desde, DateOnly Hasta, string ZonaHoraria);

/// <summary><c>GET /web/metricas/comercial</c> (esquema <c>MetricasComercial</c>).</summary>
public sealed record clsMetricasComercial(
    clsPeriodoMetricas Periodo,
    Guid UsuarioId,
    clsIndicadoresMetricas Indicadores,
    IReadOnlyList<clsPuntoSerieMensual> SerieMensual);

/// <summary>Un usuario de la regional con su rol y sus indicadores (esquema <c>ComercialRegional</c>).</summary>
/// <param name="Rol"><c>comercial</c>, <c>gerente_comercial</c> o <c>null</c>: rol interno asignado en <c>UsuariosRoles</c> (ADR-009).</param>
public sealed record clsComercialRegional(Guid UsuarioId, string? NombreUsuario, string? NombreCompleto, string? Rol, clsIndicadoresMetricas Indicadores);

/// <summary><c>GET /web/metricas/regional</c> (esquema <c>MetricasRegional</c>).</summary>
public sealed record clsMetricasRegional(
    clsPeriodoMetricas Periodo,
    string Regional,
    string FuenteRegional,
    Guid? UsuarioId,
    clsIndicadoresMetricas Indicadores,
    int LineasNegocioActivas,
    IReadOnlyList<clsComercialRegional> Comerciales,
    IReadOnlyList<clsPuntoSerieMensual> SerieMensual,
    clsConcentracionCiudades ConcentracionCiudades);

/// <summary>
/// Cotizaciones (una por cadena de versiones) de los comerciales pedidos cuya primera creación cae en el periodo, y si el esquema
/// ya tiene las columnas opcionales del script 0004: <c>origen</c>/<c>destino</c> y <c>deadline</c>.
/// </summary>
public sealed record clsDatosMetricas(IReadOnlyList<clsCotizacionMetrica> Filas, bool CiudadesDisponibles, bool DeadlineDisponible = false);
