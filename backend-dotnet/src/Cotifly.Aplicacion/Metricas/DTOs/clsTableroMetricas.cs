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

/// <summary>Un usuario de la regional con sus indicadores (esquema <c>ComercialRegional</c>).</summary>
public sealed record clsComercialRegional(Guid UsuarioId, string? NombreUsuario, string? NombreCompleto, clsIndicadoresMetricas Indicadores);

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
/// Filas de <c>Cotizaciones</c> de los comerciales pedidos creadas en el periodo (todas las versiones, marcada la vigente) y si
/// el esquema ya tiene las columnas de ciudad (<c>origen</c>/<c>destino</c> del script 0004).
/// </summary>
public sealed record clsDatosMetricas(IReadOnlyList<clsCotizacionMetrica> Filas, bool CiudadesDisponibles);
