namespace Cotifly.Aplicacion.Metricas.DTOs;

/// <summary>
/// Fila de <c>vActividadPorComercial</c> con <c>nombre_completo</c> de <c>UsuariosWeb</c> (<c>ActividadComercial</c>, borrador
/// hasta HU-010.1). <see cref="NumCotizacionesEnviadas"/> cuenta <c>enviada</c> y <c>reenviada</c> (script 0003).
/// </summary>
public sealed record clsActividadComercial(
    Guid UsuarioId,
    string? NombreCompleto,
    int NumCotizaciones,
    int NumCotizacionesEnviadas,
    int NumAjustes,
    DateTimeOffset? UltimaCotizacion);

/// <summary>
/// Fila de <c>vAjustesMargenPorComercial</c> (una por comercial y línea) con <c>nombre_completo</c>
/// (<c>AjustesMargenComercial</c>, borrador hasta HU-010.1). <see cref="NumAjustesBajoSugerido"/> (script 0006, HU-013.2) cuenta
/// los ajustes que quedaron por debajo del margen sugerido (diferencia negativa).
/// </summary>
public sealed record clsAjustesMargenComercial(
    Guid? UsuarioId,
    string? NombreCompleto,
    string? LineaNegocio,
    int NumAjustes,
    decimal? DiferenciaPromedio,
    decimal? DiferenciaMin,
    decimal? DiferenciaMax,
    DateTimeOffset? UltimoAjuste,
    int NumAjustesBajoSugerido = 0);

/// <summary>
/// Qué filas de métricas puede ver el usuario: las de un comercial (<see cref="UsuarioId"/>) o las de todos los comerciales de
/// una regional (<see cref="Regional"/>, <c>UsuariosWeb.regional</c>). Siempre uno de los dos.
/// </summary>
public sealed record clsAlcanceMetricas
{
    private clsAlcanceMetricas(Guid? usuarioId, string? regional)
    {
        UsuarioId = usuarioId;
        Regional = regional;
    }

    public Guid? UsuarioId { get; }

    public string? Regional { get; }

    public static clsAlcanceMetricas DelUsuario(Guid usuarioId) => new(usuarioId, null);

    public static clsAlcanceMetricas DeLaRegional(string regional)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(regional);
        return new clsAlcanceMetricas(null, regional.Trim());
    }
}
